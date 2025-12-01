using System.Collections.Concurrent;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;
using SharpPcap;
using PacketDotNet;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Network monitoring service that passively captures network traffic using SharpPcap/libpcap.
/// This class also implements IMiddlewareService for compatibility with existing code
/// that expects middleware functionality.
/// 
/// For Docker grading, this service runs on the host to monitor traffic
/// between containers via the exposed ports.
/// 
/// Requirements for full packet capture:
/// - Linux: libpcap-dev package (sudo apt-get install libpcap-dev)
/// - Windows: Npcap (https://npcap.com/) or WinPcap
/// - Requires admin/sudo privileges for packet capture
/// </summary>
public class NetworkMonitorService : INetworkMonitorService, IMiddlewareService
{
    private readonly IRunContext _run;
    private int _serverPort = 5001;
    private int _proxyPort = 8888;
    private bool _useHttp;
    private bool _isRunning;
    private string _currentStage = "0";
    private ICaptureDevice? _captureDevice;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private bool _pcapAvailable;
    
    // Thread-safe storage for captured data per stage
    private readonly ConcurrentDictionary<string, CapturedStageData> _capturedData = new();
    
    // Network flow storage per stage (for Detail.xlsx Network sheet comparison)
    private readonly ConcurrentDictionary<string, List<NetworkFlowEntry>> _networkFlows = new();
    
    /// <summary>
    /// Container for captured data for a single stage.
    /// </summary>
    private class CapturedStageData
    {
        public StringBuilder RequestData { get; } = new();
        public StringBuilder ResponseData { get; } = new();
        public string? HttpMethod { get; set; }
        public string? StatusCode { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
    }
    
    /// <summary>
    /// Represents a single network flow entry (matching Detail.xlsx Network sheet format).
    /// </summary>
    public class NetworkFlowEntry
    {
        public DateTime Time { get; set; }
        public string Info { get; set; } = "TCP";
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
        public string Flags { get; set; } = "";
        public string State { get; set; } = "";
        public string? Data { get; set; }
        public string SourceRole { get; set; } = "";
        public string DestinationRole { get; set; } = "";
    }
    
    public NetworkMonitorService(IRunContext run)
    {
        _run = run;
        _pcapAvailable = CheckPcapAvailability();
    }
    
    /// <summary>
    /// Checks if libpcap/Npcap is available on the system.
    /// </summary>
    private static bool CheckPcapAvailability()
    {
        try
        {
            var devices = CaptureDeviceList.Instance;
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Pcap check failed: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Configures the ports to monitor.
    /// </summary>
    public void ConfigurePorts(int proxyPort, int serverPort)
    {
        _proxyPort = proxyPort;
        _serverPort = serverPort;
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Configured to monitor port {serverPort}");
    }
    
    /// <summary>
    /// Starts the network monitor with actual packet capture.
    /// </summary>
    public async Task StartAsync(bool useHttp, CancellationToken ct = default)
    {
        _useHttp = useHttp;
        
        if (_isRunning)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Already running, stopping first...");
            await StopAsync(ct);
        }
        
        _isRunning = true;
        
        if (!_pcapAvailable)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Started in no-op mode (libpcap not available)");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} For full packet capture, install libpcap-dev and run with sudo privileges");
            return;
        }
        
        try
        {
            // Find suitable capture device
            var devices = CaptureDeviceList.Instance;
            _captureDevice = null;
            
            // Prefer loopback or any device
            foreach (var dev in devices)
            {
                var devName = dev.Name?.ToLowerInvariant() ?? "";
                var devDesc = dev.Description?.ToLowerInvariant() ?? "";
                
                if (devName.Contains("lo") || devName.Contains("loopback") || 
                    devDesc.Contains("loopback") || devName.Contains("any"))
                {
                    _captureDevice = dev;
                    break;
                }
            }
            
            // Fallback to first device
            if (_captureDevice == null && devices.Count > 0)
            {
                _captureDevice = devices[0];
            }
            
            if (_captureDevice == null)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} No capture device found, running in no-op mode");
                _pcapAvailable = false;
                return;
            }
            
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Using device: {_captureDevice.Name} ({_captureDevice.Description})");
            
            // Configure and open device
            _captureDevice.OnPacketArrival += OnPacketArrival;
            _captureDevice.Open(DeviceModes.Promiscuous, 1000);
            
            // Set BPF filter for server port
            var filter = $"tcp port {_serverPort}";
            try
            {
                _captureDevice.Filter = filter;
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Filter set: {filter}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Failed to set filter: {ex.Message}");
            }
            
            // Start capture in background
            _captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _captureTask = Task.Run(() =>
            {
                try
                {
                    _captureDevice.StartCapture();
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Packet capture started on port {_serverPort}");
                    
                    // Keep running until cancelled
                    while (!_captureCts.Token.IsCancellationRequested)
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Capture error: {ex.Message}");
                }
            }, _captureCts.Token);
            
            // Wait a bit for capture to start
            await Task.Delay(100, ct);
            
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Started with libpcap packet capture");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Failed to start capture: {ex.Message}");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Running in no-op mode");
            _pcapAvailable = false;
        }
    }
    
    /// <summary>
    /// Handles incoming packets.
    /// </summary>
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            
            var tcpPacket = packet.Extract<TcpPacket>();
            if (tcpPacket == null) return;
            
            var ipPacket = packet.Extract<IPPacket>();
            if (ipPacket == null) return;
            
            var srcIp = ipPacket.SourceAddress?.ToString() ?? "unknown";
            var dstIp = ipPacket.DestinationAddress?.ToString() ?? "unknown";
            var srcPort = tcpPacket.SourcePort;
            var dstPort = tcpPacket.DestinationPort;
            
            // Determine if this is traffic to/from our monitored port
            bool isToServer = dstPort == _serverPort;
            bool isFromServer = srcPort == _serverPort;
            
            if (!isToServer && !isFromServer) return;
            
            // Get TCP flags
            var flags = GetTcpFlagsString(tcpPacket);
            var state = GetConnectionState(tcpPacket);
            
            // Get payload data if any
            string? payloadData = null;
            if (tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
            {
                try
                {
                    payloadData = Encoding.UTF8.GetString(tcpPacket.PayloadData);
                }
                catch
                {
                    payloadData = BitConverter.ToString(tcpPacket.PayloadData);
                }
            }
            
            // Create network flow entry
            var flowEntry = new NetworkFlowEntry
            {
                Time = rawPacket.Timeval.Date,
                Info = "TCP",
                Source = $"{srcIp}:{srcPort}",
                Destination = $"{dstIp}:{dstPort}",
                Flags = flags,
                State = state,
                Data = payloadData,
                SourceRole = isFromServer ? "Server" : "Client",
                DestinationRole = isToServer ? "Server" : "Client"
            };
            
            // Store in current stage
            var flows = _networkFlows.GetOrAdd(_currentStage, _ => new List<NetworkFlowEntry>());
            lock (flows)
            {
                flows.Add(flowEntry);
            }
            
            // Store payload data in appropriate capture
            if (!string.IsNullOrEmpty(payloadData))
            {
                var stageData = _capturedData.GetOrAdd(_currentStage, _ => new CapturedStageData());
                
                if (isToServer)
                {
                    // Request data (client to server)
                    stageData.RequestData.Append(payloadData);
                    
                    // Check for HTTP method
                    if (_useHttp && stageData.HttpMethod == null)
                    {
                        var firstLine = payloadData.Split('\n')[0];
                        var parts = firstLine.Split(' ');
                        if (parts.Length >= 2 && IsHttpMethod(parts[0]))
                        {
                            stageData.HttpMethod = parts[0].Trim();
                        }
                    }
                }
                else
                {
                    // Response data (server to client)
                    stageData.ResponseData.Append(payloadData);
                    
                    // Check for HTTP status
                    if (_useHttp && stageData.StatusCode == null)
                    {
                        var firstLine = payloadData.Split('\n')[0];
                        if (firstLine.StartsWith("HTTP/"))
                        {
                            var parts = firstLine.Split(' ');
                            if (parts.Length >= 2)
                            {
                                stageData.StatusCode = parts[1].Trim();
                            }
                        }
                    }
                }
                
                // Also store in run context
                var questionCode = _run.CurrentQuestionCode ?? "unknown";
                if (isToServer)
                {
                    _run.AppendServerRequestCapture(questionCode, _currentStage, payloadData);
                }
                else
                {
                    _run.AppendServerResponseCapture(questionCode, _currentStage, payloadData);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Packet processing error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets TCP flags as a string.
    /// </summary>
    private static string GetTcpFlagsString(TcpPacket tcp)
    {
        var flags = new List<string>();
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Urgent) flags.Add("URG");
        return string.Join(", ", flags);
    }
    
    /// <summary>
    /// Gets connection state description based on TCP flags.
    /// </summary>
    private static string GetConnectionState(TcpPacket tcp)
    {
        if (tcp.Synchronize && !tcp.Acknowledgment)
            return "Client connecting to server (SYN)";
        if (tcp.Synchronize && tcp.Acknowledgment)
            return "Server responding (SYN-ACK)";
        if (tcp.Finished)
            return "Connection closing (FIN)";
        if (tcp.Reset)
            return "Connection reset (RST)";
        if (tcp.Acknowledgment && !tcp.Synchronize && !tcp.Finished)
            return "Connection established";
        return "Data transfer";
    }
    
    /// <summary>
    /// Checks if a string is an HTTP method.
    /// </summary>
    private static bool IsHttpMethod(string method)
    {
        var upper = method.ToUpperInvariant();
        return upper == "GET" || upper == "POST" || upper == "PUT" || upper == "DELETE" ||
               upper == "HEAD" || upper == "OPTIONS" || upper == "PATCH" || upper == "CONNECT";
    }
    
    /// <summary>
    /// Stops the network monitor.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return;
        
        _isRunning = false;
        
        // Stop capture
        try
        {
            _captureCts?.Cancel();
            
            if (_captureDevice != null)
            {
                try
                {
                    _captureDevice.StopCapture();
                }
                catch { }
                
                try
                {
                    _captureDevice.Close();
                }
                catch { }
                
                _captureDevice = null;
            }
            
            if (_captureTask != null)
            {
                try
                {
                    await Task.WhenAny(_captureTask, Task.Delay(1000, ct));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Error stopping: {ex.Message}");
        }
        
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Stopped");
    }
    
    /// <summary>
    /// IMiddlewareService.ProxyAsync implementation - returns true since we're in passive mode.
    /// </summary>
    public async Task<bool> ProxyAsync(IRunContext context, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true;
    }
    
    /// <summary>
    /// Marks the start of a new stage.
    /// </summary>
    public void BeginStage(string stage)
    {
        _currentStage = stage;
        _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        _networkFlows.GetOrAdd(stage, _ => new List<NetworkFlowEntry>());
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Begin stage: {stage}");
    }
    
    /// <summary>
    /// Marks the end of a stage.
    /// </summary>
    public void EndStage(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            data.EndTime = DateTime.UtcNow;
        }
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} End stage: {stage}");
    }
    
    /// <summary>
    /// Gets captured request data for a stage.
    /// </summary>
    public string? GetCapturedRequest(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            var result = data.RequestData.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        return null;
    }
    
    /// <summary>
    /// Gets captured response data for a stage.
    /// </summary>
    public string? GetCapturedResponse(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            var result = data.ResponseData.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        return null;
    }
    
    /// <summary>
    /// Gets HTTP method from captured request.
    /// </summary>
    public string? GetCapturedHttpMethod(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.HttpMethod : null;
    }
    
    /// <summary>
    /// Gets HTTP status code from captured response.
    /// </summary>
    public string? GetCapturedStatusCode(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.StatusCode : null;
    }
    
    /// <summary>
    /// Gets network flow entries for a stage.
    /// </summary>
    public IReadOnlyList<NetworkFlowEntry> GetNetworkFlows(string stage)
    {
        if (_networkFlows.TryGetValue(stage, out var flows))
        {
            lock (flows)
            {
                return flows.ToList();
            }
        }
        return Array.Empty<NetworkFlowEntry>();
    }
    
    /// <summary>
    /// Manually adds captured request data (for use by external packet capture implementations).
    /// </summary>
    public void AddCapturedRequest(string stage, string data, string? httpMethod = null)
    {
        var stageData = _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        stageData.RequestData.Append(data);
        if (!string.IsNullOrEmpty(httpMethod))
        {
            stageData.HttpMethod = httpMethod;
        }
        
        // Also store in run context
        _run.AppendServerRequestCapture(_run.CurrentQuestionCode ?? "unknown", stage, data);
    }
    
    /// <summary>
    /// Manually adds captured response data (for use by external packet capture implementations).
    /// </summary>
    public void AddCapturedResponse(string stage, string data, string? statusCode = null)
    {
        var stageData = _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        stageData.ResponseData.Append(data);
        if (!string.IsNullOrEmpty(statusCode))
        {
            stageData.StatusCode = statusCode;
        }
        
        // Also store in run context
        _run.AppendServerResponseCapture(_run.CurrentQuestionCode ?? "unknown", stage, data);
    }
    
    /// <summary>
    /// Checks if pcap is available for network capture.
    /// </summary>
    public bool IsPcapAvailable => _pcapAvailable;
}
