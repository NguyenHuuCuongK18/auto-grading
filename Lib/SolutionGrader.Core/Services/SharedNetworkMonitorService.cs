using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Shared network monitor service that captures traffic for multiple ports simultaneously.
/// 
/// OPTIMIZATION: Instead of creating one NetworkMonitorService per student (expensive),
/// this service monitors multiple ports with a single capture device, dramatically
/// reducing resource usage (97% reduction in capture instances for 32 students).
/// 
/// Architecture:
/// - Single capture device with BPF filter: "tcp port (4000 or 4001 or 4002 or ...)"
/// - Port-based packet routing to ensure per-student isolation
/// - Student A only sees port 4000 traffic, Student B only sees port 4001, etc.
/// - Thread-safe concurrent access for parallel grading
/// 
/// Port Allocation Strategy (per user request):
/// - Pre-allocate ports for all selected students + 10-20% buffer
/// - Only create new monitor instance when exceeding upper port limit
/// - Example: 50 students → allocate ports 4000-4059 (50 + 20% buffer)
/// </summary>
public sealed class SharedNetworkMonitorService : IDisposable
{
    private readonly object _lock = new();
    private ICaptureDevice? _device;
    private readonly List<ICaptureDevice> _devices = new();
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _isCapturing;
    
    // Port range covered by this monitor instance
    private readonly int _startPort;
    private readonly int _endPort;
    
    // Port-to-Student mapping (thread-safe)
    private readonly ConcurrentDictionary<int, string> _portToStudentCode = new();
    
    // Per-student packet buffers (thread-safe)
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PacketInfo>> _studentPacketBuffers = new();
    
    // Per-student context (question code, stage)
    private readonly ConcurrentDictionary<string, StudentContext> _studentContexts = new();
    
    // Protocol type per port
    private readonly ConcurrentDictionary<int, string> _portProtocols = new();
    
    // Track port roles (server vs client ephemeral)
    private readonly ConcurrentDictionary<int, string> _portRoleMap = new();
    
    /// <summary>
    /// Creates a shared network monitor for a range of ports.
    /// </summary>
    /// <param name="startPort">Starting port of the range</param>
    /// <param name="endPort">Ending port of the range (inclusive)</param>
    public SharedNetworkMonitorService(int startPort, int endPort)
    {
        _startPort = startPort;
        _endPort = endPort;
        Console.WriteLine($"[SharedNetworkMonitor] Created for port range {startPort}-{endPort}");
    }
    
    /// <summary>
    /// Register a student's port for monitoring.
    /// This student will receive all packets involving their port.
    /// </summary>
    public void RegisterStudent(string studentCode, int port, string protocolType = "TCP")
    {
        if (port < _startPort || port > _endPort)
        {
            throw new ArgumentException($"Port {port} is outside the monitored range {_startPort}-{_endPort}");
        }
        
        _portToStudentCode[port] = studentCode;
        _studentPacketBuffers[studentCode] = new ConcurrentQueue<PacketInfo>();
        _studentContexts[studentCode] = new StudentContext();
        _portProtocols[port] = protocolType;
        _portRoleMap[port] = NetworkKeywords.Role_Server; // Server is the listening port
        
        UpdateBpfFilter();
        
        Console.WriteLine($"[SharedNetworkMonitor] Registered {studentCode} on port {port}");
    }
    
    /// <summary>
    /// Unregister a student's port (when grading completes).
    /// </summary>
    public void UnregisterStudent(string studentCode)
    {
        // Find and remove port mapping
        var portsToRemove = _portToStudentCode
            .Where(kvp => kvp.Value == studentCode)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var port in portsToRemove)
        {
            _portToStudentCode.TryRemove(port, out _);
            _portProtocols.TryRemove(port, out _);
            _portRoleMap.TryRemove(port, out _);
        }
        
        _studentPacketBuffers.TryRemove(studentCode, out _);
        _studentContexts.TryRemove(studentCode, out _);
        
        UpdateBpfFilter();
        
        Console.WriteLine($"[SharedNetworkMonitor] Unregistered {studentCode}");
    }
    
    /// <summary>
    /// Start capturing network traffic for all registered ports.
    /// Must be called before students start grading.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_isCapturing) return;
            
            Console.WriteLine($"[SharedNetworkMonitor] Starting capture for port range {_startPort}-{_endPort}");
            
            // Find suitable capture devices
            _devices.Clear();
            var found = FindCandidateDevices();
            foreach (var dev in found)
            {
                _devices.Add(dev);
            }
            
            if (_devices.Count == 0)
            {
                var errorMsg = "[SharedNetworkMonitor] CRITICAL: No suitable capture device found! " +
                              "On Linux, ensure libpcap is installed and run with sudo. On Windows, ensure NPcap is installed.";
                Console.WriteLine(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            
            try
            {
                // Open each device
                foreach (var dev in _devices)
                {
                    try
                    {
                        dev.Open(DeviceModes.Promiscuous, 1000);
                        Console.WriteLine($"[SharedNetworkMonitor] Successfully opened device: {dev.Name}");
                    }
                    catch (Exception openEx)
                    {
                        Console.WriteLine($"[SharedNetworkMonitor] WARNING: Failed to open device {dev.Name}: {openEx.Message}");
                    }
                }
                
                // Keep reference to first device
                _device = _devices.FirstOrDefault();
                
                if (_device == null)
                {
                    var errorMsg = "[SharedNetworkMonitor] CRITICAL: Failed to open any capture device.";
                    Console.WriteLine(errorMsg);
                    throw new InvalidOperationException(errorMsg);
                }
                
                // Apply initial BPF filter
                UpdateBpfFilter();
                
                _cts = new CancellationTokenSource();
                _isCapturing = true;
                
                // Start capture in background
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);
                
                Console.WriteLine("[SharedNetworkMonitor] Capture started");
            }
            catch (Exception ex)
            {
                var errorMsg = $"[SharedNetworkMonitor] CRITICAL: Failed to start capture: {ex.Message}";
                Console.WriteLine(errorMsg);
                foreach (var d in _devices)
                {
                    try { d.Close(); } catch { }
                }
                _devices.Clear();
                throw new InvalidOperationException(errorMsg, ex);
            }
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Stop capturing network traffic.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task? taskToWait;
        
        lock (_lock)
        {
            if (!_isCapturing) return;
            
            Console.WriteLine("[SharedNetworkMonitor] Stopping capture");
            
            _isCapturing = false;
            taskToWait = _captureTask;
            
            try { _cts?.Cancel(); } catch { }
            
            try
            {
                foreach (var dev in _devices)
                {
                    try { dev.StopCapture(); } catch { }
                }
            }
            catch { }
        }
        
        if (taskToWait != null)
        {
            try { await taskToWait; } catch { }
        }
        
        lock (_lock)
        {
            try
            {
                foreach (var dev in _devices)
                {
                    try { dev.Close(); } catch { }
                }
                _device?.Close();
            }
            catch { }
            
            _devices.Clear();
            _device = null;
            _cts?.Dispose();
            _cts = null;
        }
        
        Console.WriteLine("[SharedNetworkMonitor] Capture stopped");
    }
    
    /// <summary>
    /// Update BPF filter to include all registered ports.
    /// Example: "tcp port (4000 or 4001 or 4002 or 4003)"
    /// </summary>
    private void UpdateBpfFilter()
    {
        if (_device == null || !_isCapturing) return;
        
        var ports = _portToStudentCode.Keys.ToList();
        if (ports.Count == 0)
        {
            // No ports registered, use dummy filter that matches nothing
            try { _device.Filter = "tcp port 0"; } catch { }
            return;
        }
        
        try
        {
            if (ports.Count == 1)
            {
                _device.Filter = $"tcp port {ports[0]}";
            }
            else
            {
                var portList = string.Join(" or ", ports);
                _device.Filter = $"tcp port ({portList})";
            }
            
            Console.WriteLine($"[SharedNetworkMonitor] Updated BPF filter for {ports.Count} ports");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SharedNetworkMonitor] WARNING: Failed to update BPF filter: {ex.Message}");
        }
    }
    
    private List<ICaptureDevice> FindCandidateDevices()
    {
        var candidates = new List<ICaptureDevice>();
        
        try
        {
            var allDevices = CaptureDeviceList.Instance;
            
            if (allDevices.Count == 0)
            {
                Console.WriteLine("[SharedNetworkMonitor] No capture devices found");
                return candidates;
            }
            
            foreach (var device in allDevices)
            {
                var name = device.Name?.ToLowerInvariant() ?? "";
                var desc = device.Description?.ToLowerInvariant() ?? "";
                
                // Look for loopback devices
                bool isLoopback = name.Contains("loopback") || desc.Contains("loopback") ||
                                 name.Contains("lo0") || name.Contains("\\device\\npcap_loopback");
                
                if (isLoopback)
                {
                    candidates.Add(device);
                    Console.WriteLine($"[SharedNetworkMonitor] Found candidate device: {device.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SharedNetworkMonitor] Error finding devices: {ex.Message}");
        }
        
        return candidates;
    }
    
    private async Task CaptureLoop(CancellationToken ct)
    {
        try
        {
            // Attach handler and start capture on all devices
            foreach (var dev in _devices)
            {
                dev.OnPacketArrival += OnPacketArrival;
                try { dev.StartCapture(); } catch (Exception ex)
                {
                    Console.WriteLine($"[SharedNetworkMonitor] WARNING: Failed to start capture on {dev.Name}: {ex.Message}");
                }
            }
            
            // Keep running until cancelled
            while (!ct.IsCancellationRequested && _isCapturing)
            {
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SharedNetworkMonitor] Capture loop error: {ex.Message}");
        }
        finally
        {
            // Detach handlers
            ICaptureDevice[] devicesSnapshot;
            lock (_lock)
            {
                devicesSnapshot = _devices.ToArray();
            }
            
            foreach (var dev in devicesSnapshot)
            {
                try { dev.OnPacketArrival -= OnPacketArrival; } catch { }
            }
        }
    }
    
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
            
            var srcPort = tcpPacket.SourcePort;
            var dstPort = tcpPacket.DestinationPort;
            
            // Determine which port belongs to our monitored students
            // Traffic flow: client → server port (student's allocated port)
            // Traffic flow: server port (student's allocated port) → client
            int studentPort = 0;
            string? studentCode = null;
            
            if (_portToStudentCode.TryGetValue(srcPort, out var studentFromSrc))
            {
                studentPort = srcPort;
                studentCode = studentFromSrc;
            }
            else if (_portToStudentCode.TryGetValue(dstPort, out var studentFromDst))
            {
                studentPort = dstPort;
                studentCode = studentFromDst;
            }
            
            if (studentCode == null) return; // Not for any registered student
            
            // Track client ephemeral port
            int clientPort = (srcPort == studentPort) ? dstPort : srcPort;
            if (tcpPacket.Synchronize && !tcpPacket.Acknowledgment)
            {
                _portRoleMap.TryAdd(clientPort, NetworkKeywords.Role_Client);
            }
            
            // Create packet info
            var packetInfo = new PacketInfo
            {
                Timestamp = DateTime.UtcNow,
                SourcePort = srcPort,
                DestPort = dstPort,
                SourceIp = ipPacket.SourceAddress.ToString(),
                DestIp = ipPacket.DestinationAddress.ToString(),
                Flags = GetTcpFlags(tcpPacket),
                PayloadLength = tcpPacket.PayloadData?.Length ?? 0,
                Payload = tcpPacket.PayloadData
            };
            
            // Add context if available
            if (_studentContexts.TryGetValue(studentCode, out var context))
            {
                packetInfo.QuestionCode = context.QuestionCode;
                packetInfo.Stage = context.Stage;
            }
            
            // Route to student's buffer
            if (_studentPacketBuffers.TryGetValue(studentCode, out var buffer))
            {
                buffer.Enqueue(packetInfo);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SharedNetworkMonitor] Packet processing error: {ex.Message}");
        }
    }
    
    private string GetTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        return flags.Count > 0 ? string.Join(",", flags) : "";
    }
    
    /// <summary>
    /// Get all packets captured for a specific student.
    /// </summary>
    public List<PacketInfo> GetStudentPackets(string studentCode)
    {
        if (_studentPacketBuffers.TryGetValue(studentCode, out var buffer))
        {
            return buffer.ToList();
        }
        return new List<PacketInfo>();
    }
    
    /// <summary>
    /// Set the current context (question code, stage) for a student.
    /// </summary>
    public void SetStudentContext(string studentCode, string questionCode, string stage)
    {
        if (_studentContexts.TryGetValue(studentCode, out var context))
        {
            context.QuestionCode = questionCode;
            context.Stage = stage;
        }
    }
    
    /// <summary>
    /// Clear all captured packets for a student (e.g., between test cases).
    /// </summary>
    public void ClearStudentCaptures(string studentCode)
    {
        if (_studentPacketBuffers.TryGetValue(studentCode, out var buffer))
        {
            while (buffer.TryDequeue(out _)) { }
        }
    }
    
    public bool IsCapturing => _isCapturing;
    
    public void Dispose()
    {
        StopAsync().Wait();
    }
}

/// <summary>
/// Context information for a student's grading session.
/// </summary>
public class StudentContext
{
    public string QuestionCode { get; set; } = "";
    public string Stage { get; set; } = "0";
}

/// <summary>
/// Information about a captured network packet.
/// </summary>
public class PacketInfo
{
    public DateTime Timestamp { get; set; }
    public int SourcePort { get; set; }
    public int DestPort { get; set; }
    public string SourceIp { get; set; } = "";
    public string DestIp { get; set; } = "";
    public string Flags { get; set; } = "";
    public int PayloadLength { get; set; }
    public byte[]? Payload { get; set; }
    public string QuestionCode { get; set; } = "";
    public string Stage { get; set; } = "";
}
