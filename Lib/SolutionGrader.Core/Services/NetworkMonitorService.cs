using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Network monitor service that captures network traffic using SharpPcap/PacketDotNet.
/// This replaces the middleware proxy approach by passively sniffing packets on the loopback interface.
/// 
/// Key differences from middleware proxy:
/// - Does not intercept or modify traffic
/// - Captures actual raw packets for analysis
/// - Both client and server connect directly to each other on the configured port
/// - Monitor only observes and records the communication
/// 
/// Requires: NPcap to be installed on the machine (Windows) or libpcap (Linux/Mac).
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitorService
{
    private readonly IRunContext _run;
    private readonly object _lock = new();
    private ICaptureDevice? _device;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _isCapturing;
    
    // Current context for associating packets with stages
    private string _currentQuestionCode = "";
    private string _currentStage = "0";
    
    // Track which port belongs to server (the listening port)
    private int _serverPort;
    private readonly ConcurrentDictionary<int, string> _portRoleMap = new();
    
    public int MonitorPort { get; set; }
    public string ProtocolType { get; set; } = NetworkKeywords.Protocol_TCP;
    public bool IsCapturing => _isCapturing;
    
    public NetworkMonitorService(IRunContext run)
    {
        _run = run;
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_isCapturing) return;
            
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} {string.Format(NetworkKeywords.MSG_MONITOR_STARTING, MonitorPort, ProtocolType)}");
            
            _serverPort = MonitorPort;
            _portRoleMap[_serverPort] = NetworkKeywords.Role_Server;
            
            // Find a suitable capture device (loopback interface)
            _device = FindLoopbackDevice();
            if (_device == null)
            {
                Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} No suitable capture device found - network capture will be skipped");
                // Continue without capture - this allows the system to work even if packet capture is unavailable
                return;
            }
            
            try
            {
                // Open the device for capture
                if (_device is LibPcapLiveDevice libPcapDevice)
                {
                    libPcapDevice.Open(DeviceModes.Promiscuous, 100);
                }
                else
                {
                    _device.Open(DeviceModes.Promiscuous);
                }
                
                // Set capture filter for the port we're monitoring (both directions)
                _device.Filter = $"port {MonitorPort}";
                
                // Create internal cancellation token source - NOT linked to external ct
                // This prevents step-level timeouts from stopping the network monitor prematurely
                _cts = new CancellationTokenSource();
                _isCapturing = true;
                
                // Start capture in background
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);
                
                Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} {NetworkKeywords.MSG_MONITOR_STARTED}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Error starting capture: {ex.Message}");
                _device?.Close();
                _device = null;
            }
        }
        
        await Task.CompletedTask;
    }
    
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task? taskToWait;
        
        lock (_lock)
        {
            if (!_isCapturing)
            {
                return;
            }
            
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} {NetworkKeywords.MSG_MONITOR_STOPPING}");
            
            _isCapturing = false;
            taskToWait = _captureTask;
            
            try
            {
                _cts?.Cancel();
            }
            catch { }
            
            try
            {
                _device?.StopCapture();
            }
            catch { }
        }
        
        // Wait for capture task to complete
        if (taskToWait != null)
        {
            try
            {
                await Task.WhenAny(taskToWait, Task.Delay(2000, ct));
            }
            catch { }
        }
        
        lock (_lock)
        {
            try
            {
                _device?.Close();
            }
            catch { }
            
            _device = null;
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
            
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} {NetworkKeywords.MSG_MONITOR_STOPPED}");
        }
    }
    
    public void SetCurrentContext(string questionCode, string stage)
    {
        _currentQuestionCode = questionCode ?? "";
        _currentStage = stage ?? "0";
    }
    
    public void ClearCaptures()
    {
        _portRoleMap.Clear();
        _portRoleMap[_serverPort] = NetworkKeywords.Role_Server;
    }
    
    private void CaptureLoop(CancellationToken ct)
    {
        try
        {
            _device!.OnPacketArrival += OnPacketArrival;
            _device.StartCapture();
            
            // Keep running until cancelled
            while (!ct.IsCancellationRequested && _isCapturing)
            {
                Thread.Sleep(10);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Capture loop error: {ex.Message}");
        }
        finally
        {
            if (_device != null)
            {
                _device.OnPacketArrival -= OnPacketArrival;
            }
        }
    }
    
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            
            // Extract TCP packet
            var tcpPacket = packet.Extract<TcpPacket>();
            if (tcpPacket == null) return;
            
            // Get IP packet for source/destination addresses
            var ipPacket = packet.Extract<IPPacket>();
            if (ipPacket == null) return;
            
            var srcPort = tcpPacket.SourcePort;
            var dstPort = tcpPacket.DestinationPort;
            
            // Determine roles based on ports
            DetermineRoles(srcPort, dstPort, out var srcRole, out var dstRole);
            
            // Only process packets with data (PSH flag)
            if (!tcpPacket.Push || tcpPacket.PayloadData == null || tcpPacket.PayloadData.Length == 0)
            {
                return;
            }
            
            // Extract payload data
            var payload = Encoding.UTF8.GetString(tcpPacket.PayloadData);
            
            // Store in RunContext for comparison
            StoreInRunContext(srcRole, payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_CAPTURE} Error capturing packet: {ex.Message}");
        }
    }
    
    private void DetermineRoles(int srcPort, int dstPort, out string srcRole, out string dstRole)
    {
        // Server always listens on _serverPort
        // Client uses ephemeral ports
        if (srcPort == _serverPort)
        {
            srcRole = NetworkKeywords.Role_Server;
            dstRole = NetworkKeywords.Role_Client;
            _portRoleMap.TryAdd(dstPort, NetworkKeywords.Role_Client);
        }
        else if (dstPort == _serverPort)
        {
            srcRole = NetworkKeywords.Role_Client;
            dstRole = NetworkKeywords.Role_Server;
            _portRoleMap.TryAdd(srcPort, NetworkKeywords.Role_Client);
        }
        else
        {
            // Try to use cached role mapping
            _portRoleMap.TryGetValue(srcPort, out srcRole!);
            _portRoleMap.TryGetValue(dstPort, out dstRole!);
            srcRole ??= "Unknown";
            dstRole ??= "Unknown";
        }
    }
    
    private void StoreInRunContext(string srcRole, string payload)
    {
        // Client -> Server is a request
        if (srcRole == NetworkKeywords.Role_Client)
        {
            _run.SetServerRequest(_currentQuestionCode, _currentStage, payload);
            
            // Parse HTTP method if this is HTTP protocol
            if (ProtocolType.Equals(NetworkKeywords.Protocol_HTTP, StringComparison.OrdinalIgnoreCase))
            {
                var method = ExtractHttpMethod(payload);
                if (!string.IsNullOrEmpty(method))
                {
                    _run.SetHttpMetadata(_currentQuestionCode, _currentStage, method, 0, 
                        Encoding.UTF8.GetByteCount(payload));
                }
            }
        }
        // Server -> Client is a response
        else if (srcRole == NetworkKeywords.Role_Server)
        {
            _run.SetServerResponse(_currentQuestionCode, _currentStage, payload);
            
            // Parse HTTP status if this is HTTP protocol
            if (ProtocolType.Equals(NetworkKeywords.Protocol_HTTP, StringComparison.OrdinalIgnoreCase))
            {
                var status = ExtractHttpStatus(payload);
                _run.SetHttpMetadata(_currentQuestionCode, _currentStage, "", status,
                    Encoding.UTF8.GetByteCount(payload));
            }
        }
    }
    
    private static string? ExtractHttpMethod(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        
        // HTTP methods are at the start: GET, POST, PUT, DELETE, etc.
        var match = Regex.Match(payload, @"^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s");
        return match.Success ? match.Groups[1].Value : null;
    }
    
    private static int ExtractHttpStatus(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return 0;
        
        // HTTP response starts with HTTP/X.X STATUS
        var match = Regex.Match(payload, @"^HTTP/\d\.\d\s+(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var status))
        {
            return status;
        }
        return 0;
    }
    
    private static ICaptureDevice? FindLoopbackDevice()
    {
        try
        {
            var devices = CaptureDeviceList.Instance;
            
            // First, look for loopback device
            foreach (var dev in devices)
            {
                var description = dev.Description?.ToLowerInvariant() ?? "";
                var name = dev.Name?.ToLowerInvariant() ?? "";
                
                if (description.Contains("loopback") || 
                    name.Contains("loopback") ||
                    name.Contains("npcap loopback") ||
                    description.Contains("npcap loopback") ||
                    name.Contains("\\device\\npf_loopback"))
                {
                    Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Found loopback device: {dev.Name} ({dev.Description})");
                    return dev;
                }
            }
            
            // On Linux, look for 'lo' interface
            foreach (var dev in devices)
            {
                if (dev.Name == "lo" || dev.Name?.Contains("lo") == true)
                {
                    Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Found loopback device: {dev.Name}");
                    return dev;
                }
            }
            
            // If no loopback found, try to use first available device as fallback
            if (devices.Count > 0)
            {
                Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Using first available device as fallback: {devices[0].Name}");
                return devices[0];
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Error finding capture device: {ex.Message}");
            return null;
        }
    }
}
