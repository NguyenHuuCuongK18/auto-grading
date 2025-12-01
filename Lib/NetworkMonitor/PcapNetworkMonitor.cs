using System.Collections.Concurrent;
using System.Text;
using SharpPcap;
using PacketDotNet;

namespace SolutionGrader.NetworkMonitor;

/// <summary>
/// Full libpcap-based network monitoring implementation using SharpPcap.
/// 
/// This class provides actual packet capture functionality for environments
/// where libpcap/Npcap is installed and admin/sudo privileges are available.
/// 
/// Usage:
/// 1. Create instance and call Initialize() with ports
/// 2. Call StartCapture() to begin monitoring
/// 3. Call BeginStage()/EndStage() to mark capture boundaries
/// 4. Use GetCapturedRequest()/GetCapturedResponse() to retrieve data
/// 5. Call StopCapture() when done
/// 
/// Requirements:
/// - Linux: libpcap-dev package (sudo apt-get install libpcap-dev)
/// - Windows: Npcap (https://npcap.com/) or WinPcap
/// - Requires admin/sudo privileges
/// </summary>
public class PcapNetworkMonitor : IDisposable
{
    private ICaptureDevice? _device;
    private int _serverPort;
    private bool _useHttp;
    private bool _isRunning;
    private string _currentStage = "0";
    
    // Captured data storage
    private readonly ConcurrentDictionary<string, CapturedData> _capturedData = new();
    
    /// <summary>
    /// Event raised when a packet is captured.
    /// </summary>
    public event EventHandler<PacketCapturedEventArgs>? OnPacketCaptured;
    
    /// <summary>
    /// Container for captured stage data.
    /// </summary>
    public class CapturedData
    {
        public StringBuilder RequestData { get; } = new();
        public StringBuilder ResponseData { get; } = new();
        public string? HttpMethod { get; set; }
        public string? StatusCode { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
    }
    
    /// <summary>
    /// Event args for packet capture events.
    /// </summary>
    public class PacketCapturedEventArgs : EventArgs
    {
        public string Stage { get; set; } = "0";
        public bool IsRequest { get; set; }
        public string Data { get; set; } = "";
        public string? HttpMethod { get; set; }
        public string? StatusCode { get; set; }
    }
    
    /// <summary>
    /// Initializes the network monitor with the specified ports.
    /// </summary>
    /// <param name="serverPort">The server port to monitor.</param>
    /// <param name="useHttp">Whether to parse HTTP headers.</param>
    public void Initialize(int serverPort, bool useHttp = false)
    {
        _serverPort = serverPort;
        _useHttp = useHttp;
    }
    
    /// <summary>
    /// Starts packet capture.
    /// </summary>
    /// <returns>True if capture started successfully, false otherwise.</returns>
    public bool StartCapture()
    {
        if (_isRunning)
        {
            Console.WriteLine("[PcapNetworkMonitor] Already running");
            return true;
        }
        
        try
        {
            var devices = CaptureDeviceList.Instance;
            if (devices.Count == 0)
            {
                Console.WriteLine("[PcapNetworkMonitor] No capture devices found");
                return false;
            }
            
            // Find suitable device
            _device = null;
            foreach (var dev in devices)
            {
                // Prefer any device that's available
                if (dev.Name.Contains("lo") || dev.Name.Contains("Loopback") ||
                    dev.Description?.Contains("Loopback") == true ||
                    dev.Name.Contains("any"))
                {
                    _device = dev;
                    break;
                }
            }
            
            // Fallback to first device
            if (_device == null)
            {
                _device = devices[0];
            }
            
            Console.WriteLine($"[PcapNetworkMonitor] Using device: {_device.Name}");
            
            _device.OnPacketArrival += OnPacketArrival;
            _device.Open(DeviceModes.Promiscuous, 1000);
            _device.Filter = $"tcp port {_serverPort}";
            _device.StartCapture();
            _isRunning = true;
            
            Console.WriteLine($"[PcapNetworkMonitor] Started capturing on port {_serverPort}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PcapNetworkMonitor] Failed to start: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Stops packet capture.
    /// </summary>
    public void StopCapture()
    {
        if (!_isRunning)
            return;
        
        try
        {
            if (_device != null)
            {
                _device.StopCapture();
                _device.Close();
                _device = null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PcapNetworkMonitor] Error stopping: {ex.Message}");
        }
        
        _isRunning = false;
        Console.WriteLine("[PcapNetworkMonitor] Stopped");
    }
    
    /// <summary>
    /// Marks the beginning of a capture stage.
    /// </summary>
    public void BeginStage(string stage)
    {
        _currentStage = stage;
        _capturedData.GetOrAdd(stage, _ => new CapturedData());
    }
    
    /// <summary>
    /// Marks the end of a capture stage.
    /// </summary>
    public void EndStage(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            data.EndTime = DateTime.UtcNow;
        }
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
    public string? GetHttpMethod(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.HttpMethod : null;
    }
    
    /// <summary>
    /// Gets HTTP status code from captured response.
    /// </summary>
    public string? GetStatusCode(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.StatusCode : null;
    }
    
    /// <summary>
    /// Packet arrival handler.
    /// </summary>
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            
            var tcpPacket = packet.Extract<TcpPacket>();
            if (tcpPacket == null || tcpPacket.PayloadData == null || tcpPacket.PayloadData.Length == 0)
                return;
            
            var stageData = _capturedData.GetOrAdd(_currentStage, _ => new CapturedData());
            var payload = Encoding.UTF8.GetString(tcpPacket.PayloadData);
            
            bool isRequest = tcpPacket.DestinationPort == _serverPort;
            bool isResponse = tcpPacket.SourcePort == _serverPort;
            
            var eventArgs = new PacketCapturedEventArgs
            {
                Stage = _currentStage,
                IsRequest = isRequest,
                Data = payload
            };
            
            if (_useHttp)
            {
                if (isRequest)
                {
                    stageData.RequestData.Append(payload);
                    if (stageData.HttpMethod == null)
                    {
                        var firstLine = payload.Split('\n')[0];
                        var parts = firstLine.Split(' ');
                        if (parts.Length >= 2)
                        {
                            stageData.HttpMethod = parts[0].Trim();
                            eventArgs.HttpMethod = stageData.HttpMethod;
                        }
                    }
                }
                else if (isResponse)
                {
                    stageData.ResponseData.Append(payload);
                    if (stageData.StatusCode == null)
                    {
                        var firstLine = payload.Split('\n')[0];
                        var parts = firstLine.Split(' ');
                        if (parts.Length >= 2 && parts[0].StartsWith("HTTP"))
                        {
                            stageData.StatusCode = parts[1].Trim();
                            eventArgs.StatusCode = stageData.StatusCode;
                        }
                    }
                }
            }
            else
            {
                if (isRequest)
                    stageData.RequestData.Append(payload);
                else if (isResponse)
                    stageData.ResponseData.Append(payload);
            }
            
            OnPacketCaptured?.Invoke(this, eventArgs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PcapNetworkMonitor] Packet error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        StopCapture();
        GC.SuppressFinalize(this);
    }
}
