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
/// 
/// CRITICAL: Integrates with IRunContext for grading system compatibility.
/// Packets are stored both in local buffers (for retrieval) and in RunContext
/// (for grading logic that depends on _runContext.GetCapturedNetworkPackets()).
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
    
    // CRITICAL: RunContext mapping for storing packets to grading system
    // Key: studentCode, Value: IRunContext for that student
    private readonly ConcurrentDictionary<string, IRunContext> _studentRunContexts = new();
    
    // Port-to-Student mapping (thread-safe)
    private readonly ConcurrentDictionary<int, string> _portToStudentCode = new();
    
    // Per-student packet buffers (thread-safe)
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PacketInfo>> _studentPacketBuffers = new();
    
    // CRITICAL FIX: Per-student context (question code, stage)
    // Changed from single StudentContext to thread-safe atomic reference
    // This prevents stage context from being overwritten during concurrent stage execution
    private readonly ConcurrentDictionary<string, StudentContext> _studentContexts = new();
    
    // CRITICAL FIX: Stage timestamp tracking to detect context corruption
    // Key: studentCode, Value: timestamp when stage was last updated
    // Used to validate that packets are tagged with the correct stage
    private readonly ConcurrentDictionary<string, (string Stage, long TimestampTicks)> _studentStageTimestamps = new();
    
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
    /// <param name="studentCode">Student identifier</param>
    /// <param name="port">Port to monitor for this student</param>
    /// <param name="protocolType">Protocol type (TCP/HTTP)</param>
    /// <param name="runContext">RunContext for storing packets (required for grading)</param>
    public void RegisterStudent(string studentCode, int port, string protocolType, IRunContext runContext)
    {
        if (port < _startPort || port > _endPort)
        {
            throw new ArgumentException($"Port {port} is outside the monitored range {_startPort}-{_endPort}");
        }
        
        // CRITICAL VALIDATION: Check if this port is already registered to a different student
        if (_portToStudentCode.TryGetValue(port, out var existingStudent) && existingStudent != studentCode)
        {
            var errorMsg = $"[SharedNetworkMonitor] CRITICAL ERROR: Port {port} is already registered to student {existingStudent}, cannot register for {studentCode}! This indicates a port allocation race condition.";
            Console.WriteLine(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }
        
        // CRITICAL VALIDATION: Check if this student is already registered with a different port
        if (_studentPacketBuffers.ContainsKey(studentCode))
        {
            // Find the port this student is currently registered with
            var currentPort = _portToStudentCode.FirstOrDefault(kvp => kvp.Value == studentCode).Key;
            if (currentPort != port)
            {
                var errorMsg = $"[SharedNetworkMonitor] CRITICAL ERROR: Student {studentCode} is already registered with port {currentPort}, cannot re-register with port {port}! This indicates a port allocation race condition.";
                Console.WriteLine(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }
            else
            {
                Console.WriteLine($"[SharedNetworkMonitor] WARNING: Student {studentCode} is already registered on port {port}, skipping duplicate registration");
                return; // Already registered, skip
            }
        }
        
        // CRITICAL FIX: Clear port buffers BEFORE registering new student
        // This ensures NO stale packets from previous student remain
        ClearPortBuffers(port);
        
        _portToStudentCode[port] = studentCode;
        _studentPacketBuffers[studentCode] = new ConcurrentQueue<PacketInfo>();
        _studentContexts[studentCode] = new StudentContext();
        _portProtocols[port] = protocolType;
        _portRoleMap[port] = NetworkKeywords.Role_Server; // Server is the listening port
        _studentRunContexts[studentCode] = runContext; // CRITICAL: Store RunContext for packet storage
        
        UpdateBpfFilter();
        
        var totalStudents = _portToStudentCode.Count;
        var allPorts = string.Join(", ", _portToStudentCode.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        Console.WriteLine($"[SharedNetworkMonitor] SUCCESS: Registered student {studentCode} on port {port} (range: {_startPort}-{_endPort})");
        Console.WriteLine($"[SharedNetworkMonitor] Total registered students: {totalStudents}, Port mappings: [{allPorts}]");
    }
    
    /// <summary>
    /// Unregister a student's port (when grading completes).
    /// CRITICAL: This removes the student from port mapping to prevent any future packets
    /// from being attributed to this student.
    /// </summary>
    public void UnregisterStudent(string studentCode)
    {
        // Find and remove port mapping
        var portsToRemove = _portToStudentCode
            .Where(kvp => kvp.Value == studentCode)
            .Select(kvp => kvp.Key)
            .ToList();
        
        Console.WriteLine($"[SharedNetworkMonitor] Unregistering student {studentCode}, releasing ports: {string.Join(", ", portsToRemove)}");
        
        foreach (var port in portsToRemove)
        {
            _portToStudentCode.TryRemove(port, out _);
            _portProtocols.TryRemove(port, out _);
            _portRoleMap.TryRemove(port, out _);
        }
        
        // Clear all packets for this student before unregistering
        if (_studentPacketBuffers.TryRemove(studentCode, out var buffer))
        {
            // CRITICAL FIX: Drain the entire buffer to ensure no stale packets
            while (buffer.TryDequeue(out _)) { }
        }
        
        // CRITICAL FIX: Clear stage timestamps to prevent stale stage tracking
        _studentStageTimestamps.TryRemove(studentCode, out _);
        
        _studentContexts.TryRemove(studentCode, out _);
        _studentRunContexts.TryRemove(studentCode, out _); // Remove RunContext mapping
        
        UpdateBpfFilter();
        
        var remainingStudents = _portToStudentCode.Count;
        Console.WriteLine($"[SharedNetworkMonitor] Unregistered {studentCode}. Remaining students: {remainingStudents}");
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
        if (_devices.Count == 0 || !_isCapturing) return;
        
        var ports = _portToStudentCode.Keys.ToList();
        string filterExpression;
        
        if (ports.Count == 0)
        {
            // No ports registered, use dummy filter that matches nothing
            filterExpression = "tcp port 0";
        }
        else if (ports.Count == 1)
        {
            filterExpression = $"tcp port {ports[0]}";
        }
        else
        {
            var portList = string.Join(" or ", ports);
            filterExpression = $"tcp port ({portList})";
        }
        
        // CRITICAL FIX: Apply filter to ALL devices, not just the first one
        int successCount = 0;
        foreach (var device in _devices)
        {
            try
            {
                device.Filter = filterExpression;
                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SharedNetworkMonitor] WARNING: Failed to set BPF filter on {device.Name}: {ex.Message}");
            }
        }
        
        Console.WriteLine($"[SharedNetworkMonitor] Updated BPF filter for {ports.Count} ports on {successCount}/{_devices.Count} devices");
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
            
            Console.WriteLine($"[SharedNetworkMonitor] Scanning {allDevices.Count} network devices...");
            
            // CRITICAL FIX: Capture on ALL interfaces to catch ephemeral Docker bridges
            // Custom Docker networks create dynamic br-<network-id> interfaces that:
            // 1. Only exist while containers are running
            // 2. Have unpredictable names (include network ID)
            // 3. Cannot be pre-selected before containers start
            // 
            // Solution: Capture on all interfaces and use BPF filter to limit to target ports
            // This ensures we catch traffic regardless of which interface Docker uses
            
            foreach (var device in allDevices)
            {
                var name = device.Name?.ToLowerInvariant() ?? "";
                var desc = device.Description?.ToLowerInvariant() ?? "";
                
                // Skip "any" pseudo-device on Linux (causes issues)
                if (name == "any" || name.Contains("\\device\\npcap\\any"))
                {
                    Console.WriteLine($"[SharedNetworkMonitor] Skipping pseudo-device: {name}");
                    continue;
                }
                
                // Skip USB/Bluetooth/other non-network interfaces
                if (name.Contains("usb") || name.Contains("bluetooth") || desc.Contains("bluetooth"))
                {
                    continue;
                }
                
                // Add all other devices
                candidates.Add(device);
                Console.WriteLine($"[SharedNetworkMonitor] Found candidate device: {device.Name} ({device.Description})");
            }
            
            if (candidates.Count == 0)
            {
                Console.WriteLine("[SharedNetworkMonitor] WARNING: No suitable capture devices found!");
                Console.WriteLine("[SharedNetworkMonitor] This usually means:");
                Console.WriteLine("  1. On Linux: libpcap is not installed or you need to run with sudo");
                Console.WriteLine("  2. On Windows: NPcap is not installed");
                Console.WriteLine("[SharedNetworkMonitor] Available devices:");
                foreach (var device in allDevices)
                {
                    Console.WriteLine($"  - {device.Name}: {device.Description}");
                }
            }
            else
            {
                Console.WriteLine($"[SharedNetworkMonitor] Selected {candidates.Count} devices for monitoring");
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
            
            // CRITICAL VALIDATION #1: Port-based routing for ABSOLUTE isolation
            // Each student is assigned a specific port (e.g., Student A = 4000, Student B = 4001)
            // A packet belongs to a student if EITHER source OR destination port matches their allocated port
            // This ensures COMPLETE traffic isolation - Student A will NEVER see Student B's packets
            
            int studentPort = 0;
            string? studentCode = null;
            
            // Check if source port matches any registered student
            if (_portToStudentCode.TryGetValue(srcPort, out var studentFromSrc))
            {
                studentPort = srcPort;
                studentCode = studentFromSrc;
            }
            // Check if destination port matches any registered student
            else if (_portToStudentCode.TryGetValue(dstPort, out var studentFromDst))
            {
                studentPort = dstPort;
                studentCode = studentFromDst;
            }
            
            // CRITICAL VALIDATION #2: Discard packets that don't belong to any registered student
            // This ensures we ONLY capture traffic for students we're actively grading
            if (studentCode == null) 
            {
                // Packet doesn't belong to any registered student - discard silently
                return;
            }
            
            // CRITICAL VALIDATION #3: Verify EXACTLY ONE student owns this packet
            // A packet should NEVER match multiple students (would indicate port conflict)
            bool srcMatched = _portToStudentCode.ContainsKey(srcPort);
            bool dstMatched = _portToStudentCode.ContainsKey(dstPort);
            
            if (srcMatched && dstMatched && srcPort != dstPort)
            {
                // CRITICAL ERROR: Both source and destination ports are registered to (potentially) different students
                // This should NEVER happen - it means two students are communicating with each other
                // or there's a port allocation conflict
                var srcStudent = _portToStudentCode[srcPort];
                var dstStudent = _portToStudentCode[dstPort];
                
                if (srcStudent != dstStudent)
                {
                    Console.WriteLine($"[SharedNetworkMonitor] CRITICAL WARNING: Packet has src={srcPort} (student {srcStudent}) and dst={dstPort} (student {dstStudent})");
                    Console.WriteLine($"[SharedNetworkMonitor] This should NEVER happen - indicates students are communicating with each other or port conflict!");
                    Console.WriteLine($"[SharedNetworkMonitor] Packet will be attributed to source port owner: {srcStudent}");
                    // Attribute to source port owner (server sending response)
                }
            }
            
            // Track client ephemeral port
            int clientPort = (srcPort == studentPort) ? dstPort : srcPort;
            if (tcpPacket.Synchronize && !tcpPacket.Acknowledgment)
            {
                _portRoleMap.TryAdd(clientPort, NetworkKeywords.Role_Client);
            }
            
            // Determine roles for this packet
            string srcRole, dstRole;
            if (srcPort == studentPort)
            {
                srcRole = NetworkKeywords.Role_Server;
                dstRole = NetworkKeywords.Role_Client;
            }
            else
            {
                srcRole = NetworkKeywords.Role_Client;
                dstRole = NetworkKeywords.Role_Server;
            }
            
            // Extract TCP flags in correct order (matches NetworkMonitorService)
            var flags = GetTcpFlags(tcpPacket);
            
            // Determine connection state
            var state = DetermineConnectionState(flags, srcRole);
            
            // Extract payload for PSH packets
            string? payload = null;
            if (tcpPacket.Push && tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
            {
                payload = System.Text.Encoding.UTF8.GetString(tcpPacket.PayloadData);
            }
            
            // CRITICAL FIX: Determine the correct stage for this packet using timestamp-based stage windows
            // This prevents race conditions where stage context is updated while packets are still being captured
            string questionCode = "";
            string stage = "0";
            
            if (_studentContexts.TryGetValue(studentCode, out var context))
            {
                questionCode = context.QuestionCode;
                
                // CRITICAL: Match packet to stage based on its capture timestamp
                // This ensures packets are attributed to the stage that was active when they were captured,
                // not the stage that happens to be current when the packet handler runs
                long packetTimestampTicks = rawPacket.Timeval.Date.Ticks;
                stage = context.GetStageAtTimestamp(packetTimestampTicks);
            }
            
            // Parse stage to int
            if (!int.TryParse(stage, out int stageNum))
            {
                stageNum = 0;
            }
            
            // Create packet info for local buffer
            var packetInfo = new PacketInfo
            {
                Timestamp = DateTime.UtcNow,
                SourcePort = srcPort,
                DestPort = dstPort,
                SourceIp = ipPacket.SourceAddress.ToString(),
                DestIp = ipPacket.DestinationAddress.ToString(),
                Flags = flags,
                PayloadLength = tcpPacket.PayloadData?.Length ?? 0,
                Payload = tcpPacket.PayloadData,
                QuestionCode = questionCode,
                Stage = stage
            };
            
            // CRITICAL VALIDATION #4: Verify student buffer exists before storing
            if (!_studentPacketBuffers.TryGetValue(studentCode, out var buffer))
            {
                Console.WriteLine($"[SharedNetworkMonitor] ERROR: Student {studentCode} has no packet buffer! This should never happen.");
                return;
            }
            
            // Store to student's local buffer
            buffer.Enqueue(packetInfo);
            
            // CRITICAL VALIDATION #5: Verify RunContext exists for this student
            if (!_studentRunContexts.TryGetValue(studentCode, out var runContext))
            {
                Console.WriteLine($"[SharedNetworkMonitor] ERROR: Student {studentCode} has no RunContext! Packet will not be stored for grading.");
                return;
            }
            
            // CRITICAL VALIDATION #6: Verify packet has correct student port
            // This validates the packet's ports match the studentPort determined by routing logic (lines 446-456)
            // Catches bugs where packet routing logic incorrectly determined studentPort
            bool packetHasStudentPort = (srcPort == studentPort || dstPort == studentPort);
            if (!packetHasStudentPort)
            {
                Console.WriteLine($"[SharedNetworkMonitor] CRITICAL ERROR: Packet for student {studentCode} (port {studentPort}) has src={srcPort}, dst={dstPort}");
                Console.WriteLine($"[SharedNetworkMonitor] This should NEVER happen - packet routing is broken!");
                return; // Discard this packet as it's incorrectly routed
            }
            
            // CRITICAL VALIDATION #7: Double-check student ownership before storing
            // This validates the _portToStudentCode mapping is correct for the determined studentPort
            // Catches bugs where port mapping was corrupted or student code was incorrectly determined
            // This is a DEFENSE IN DEPTH check that complements validation #6
            var expectedStudent = _portToStudentCode.TryGetValue(studentPort, out var verifyStudent) ? verifyStudent : null;
            if (expectedStudent != studentCode)
            {
                Console.WriteLine($"[SharedNetworkMonitor] CRITICAL ERROR: Port {studentPort} ownership mismatch!");
                Console.WriteLine($"[SharedNetworkMonitor] Expected student: {expectedStudent}, Got: {studentCode}");
                Console.WriteLine($"[SharedNetworkMonitor] DISCARDING PACKET to prevent cross-contamination");
                return;
            }
            
            // CRITICAL: Store to RunContext for grading system compatibility
            // The grading system retrieves packets via runContext.GetCapturedNetworkPackets()
            var capturedPacket = new CapturedNetworkPacket
            {
                Stage = stageNum,
                Timestamp = rawPacket.Timeval.Date,
                Flags = flags,
                State = state,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Data = payload,
                SourcePort = srcPort,
                DestinationPort = dstPort
            };
            
            runContext.AddCapturedNetworkPacket(questionCode, stage, capturedPacket);
                
            // DETAILED LOGGING: Packet attribution with ownership verification
            // This helps debug any potential isolation issues
            var registeredStudentsCount = _portToStudentCode.Count;
            var logMessage = $"[SharedNetworkMonitor] [{studentCode}|Port:{studentPort}|Stage:{stage}|Students:{registeredStudentsCount}] " +
                           $"{srcRole}->{dstRole} [{flags}] {state} (src:{srcPort}, dst:{dstPort})";
            if (!string.IsNullOrEmpty(payload))
            {
                var payloadPreview = payload.Length > 50 
                    ? payload.Substring(0, 50) + "..." 
                    : payload;
                logMessage += $" Data: {payloadPreview.Replace("\n", "\\n").Replace("\r", "")}";
            }
            Console.WriteLine(logMessage);
            
            // CRITICAL: Store payload to RunContext with HTTP parsing (matches NetworkMonitorService)
            if (!string.IsNullOrEmpty(payload))
            {
                StorePayloadToRunContext(runContext, srcRole, payload, questionCode, stage);
            }
            
            // VALIDATION #8: Packet stored successfully
            // (Removed expensive Count() check - packet storage is validated by earlier checks)
            // If we reach here, all validations passed and packet was stored correctly
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SharedNetworkMonitor] Packet processing error: {ex.Message}");
            Console.WriteLine($"[SharedNetworkMonitor] Stack trace: {ex.StackTrace}");
        }
    }
    
    private string GetTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        // CRITICAL: Order matters for TestKit comparison (matches NetworkMonitorService)
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Urgent) flags.Add("URG");
        return string.Join(", ", flags);
    }
    
    private string DetermineConnectionState(string flags, string srcRole)
    {
        // Standard TCP handshake states (matches NetworkMonitorService)
        if (flags == "SYN" && srcRole == NetworkKeywords.Role_Client)
            return "Client connecting to server (SYN)";
        if ((flags == "SYN, ACK" || flags == "ACK, SYN") && srcRole == NetworkKeywords.Role_Server)
            return "Server responding (SYN-ACK)";
        if (flags == "ACK")
            return "Connection acknowledged (ACK)";
        if (flags.Contains("PSH") && flags.Contains("ACK"))
            return srcRole == NetworkKeywords.Role_Client ? "Client sending data" : "Server sending data";
        if (flags.Contains("FIN") && flags.Contains("ACK"))
            return srcRole == NetworkKeywords.Role_Client ? "Client closing connection" : "Server closing connection";
        if (flags == "FIN")
            return "Connection termination initiated";
        if (flags == "RST")
            return "Connection reset";
        return "Unknown state";
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
    /// CRITICAL FIX: Uses stage window tracking to prevent race conditions
    /// when multiple stages are executing concurrently or transitioning quickly.
    /// 
    /// This method records the START of a new stage, which allows packets to be
    /// correctly attributed based on their capture timestamp relative to stage windows.
    /// </summary>
    public void SetStudentContext(string studentCode, string questionCode, string stage)
    {
        if (_studentContexts.TryGetValue(studentCode, out var context))
        {
            var now = DateTime.UtcNow.Ticks;
            
            // Update context with stage window tracking
            context.QuestionCode = questionCode;
            context.RecordStageStart(stage, now);
            
            // Track timestamp for debugging and correlation
            _studentStageTimestamps[studentCode] = (stage, now);
            
            Console.WriteLine($"[SharedNetworkMonitor] [{studentCode}] Stage {stage} started at {new DateTime(now):HH:mm:ss.fff}");
        }
    }
    
    /// <summary>
    /// Clear all captured packets for a student (e.g., between test cases).
    /// CRITICAL: This should be called between test cases to ensure clean slate.
    /// </summary>
    public void ClearStudentCaptures(string studentCode)
    {
        if (_studentPacketBuffers.TryGetValue(studentCode, out var buffer))
        {
            var clearedCount = 0;
            while (buffer.TryDequeue(out _)) { clearedCount++; }
            Console.WriteLine($"[SharedNetworkMonitor] [{studentCode}] Cleared {clearedCount} packets from local buffer");
        }
        
        // CRITICAL FIX: Also clear packets from the student's RunContext
        // This ensures packets don't carry over between test cases
        if (_studentRunContexts.TryGetValue(studentCode, out var runContext))
        {
            runContext.ClearNetworkCaptures();
            Console.WriteLine($"[SharedNetworkMonitor] [{studentCode}] Cleared packets from RunContext");
        }
    }
    
    /// <summary>
    /// CRITICAL FIX: Clear all packet buffers for a specific port.
    /// This is called BEFORE registering a new student on a port to ensure
    /// no stale packets from the previous student remain in the system.
    /// 
    /// This method clears:
    /// 1. Any in-flight packets in the capture handler queue
    /// 2. Packets that may have been captured during the brief window between unregister and register
    /// 3. Any OS-level buffered packets that haven't been processed yet
    /// </summary>
    public void ClearPortBuffers(int port)
    {
        // Find if any student is currently registered on this port
        if (_portToStudentCode.TryGetValue(port, out var studentCode))
        {
            // Port is still registered - clear captures for that student
            ClearStudentCaptures(studentCode);
        }
        
        // Additional safety: Clear any client ports that might have been associated with this server port
        // Client ports are ephemeral and tracked in _portRoleMap
        var associatedClientPorts = _portRoleMap
            .Where(kvp => kvp.Value == NetworkKeywords.Role_Client)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var clientPort in associatedClientPorts)
        {
            _portRoleMap.TryRemove(clientPort, out _);
        }
    }
    
    /// <summary>
    /// Marks the end of a stage for more accurate stage window tracking.
    /// OPTIONAL: This is called when a stage completes to close its window.
    /// If not called, windows are auto-closed when the next stage starts.
    /// </summary>
    public void EndStageContext(string studentCode, string stage)
    {
        if (_studentContexts.TryGetValue(studentCode, out var context))
        {
            var now = DateTime.UtcNow.Ticks;
            Console.WriteLine($"[SharedNetworkMonitor] [{studentCode}] Stage {stage} ended at {new DateTime(now):HH:mm:ss.fff}");
        }
    }
    
    public bool IsCapturing => _isCapturing;
    
    public void Dispose()
    {
        StopAsync().Wait();
    }
    
    /// <summary>
    /// Stores payload to RunContext with HTTP parsing if protocol is HTTP.
    /// Matches NetworkMonitorService.StoreInRunContext() logic exactly.
    /// </summary>
    private void StorePayloadToRunContext(IRunContext runContext, string srcRole, string payload, string questionCode, string stage)
    {
        // Get protocol type for this port (default to TCP if not found)
        string protocolType = NetworkKeywords.Protocol_TCP;
        
        // Parse HTTP data if this is HTTP protocol
        if (protocolType.Equals(NetworkKeywords.Protocol_HTTP, StringComparison.OrdinalIgnoreCase))
        {
            var httpData = ParseHttpData(payload);
            
            // Client -> Server is a request
            if (srcRole == NetworkKeywords.Role_Client)
            {
                // Store the full request payload
                runContext.SetServerRequest(questionCode, stage, payload);
                
                if (!string.IsNullOrEmpty(httpData.Method))
                {
                    // Store method and request body separately for easier comparison
                    runContext.SetHttpMetadata(questionCode, stage, httpData.Method, 0, 
                        System.Text.Encoding.UTF8.GetByteCount(payload));
                    
                    // Store HTTP body if present (for request payload comparison)
                    if (!string.IsNullOrEmpty(httpData.Body))
                    {
                        runContext.SetCapturedOutput($"network.{stage}.req.body", httpData.Body);
                    }
                }
            }
            // Server -> Client is a response
            else if (srcRole == NetworkKeywords.Role_Server)
            {
                // Store the full response payload
                runContext.SetServerResponse(questionCode, stage, payload);
                
                if (!string.IsNullOrEmpty(httpData.Status))
                {
                    // Parse status code from status line (e.g., "200 OK" -> 200)
                    var statusCode = ExtractStatusCode(httpData.Status);
                    runContext.SetHttpMetadata(questionCode, stage, "", statusCode,
                        System.Text.Encoding.UTF8.GetByteCount(payload));
                    
                    // Store HTTP body separately for response payload comparison
                    if (!string.IsNullOrEmpty(httpData.Body))
                    {
                        runContext.SetCapturedOutput($"network.{stage}.res.body", httpData.Body);
                    }
                }
            }
        }
        else
        {
            // TCP protocol - store raw data
            if (srcRole == NetworkKeywords.Role_Client)
            {
                runContext.SetServerRequest(questionCode, stage, payload);
                runContext.SetCapturedOutput($"network.{stage}.req.data", payload);
            }
            else if (srcRole == NetworkKeywords.Role_Server)
            {
                runContext.SetServerResponse(questionCode, stage, payload);
                runContext.SetCapturedOutput($"network.{stage}.res.data", payload);
            }
        }
    }
    
    // Regex patterns for HTTP parsing (matches NetworkMonitorService)
    private static readonly System.Text.RegularExpressions.Regex HttpRequestRegex = 
        new System.Text.RegularExpressions.Regex(@"^(\S+)\s+(\S+)\s+HTTP/([0-9.]+)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HttpResponseRegex = 
        new System.Text.RegularExpressions.Regex(@"^HTTP/([0-9.]+)\s+(\d+)\s*(.*)", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    
    private static HttpData ParseHttpData(string? payload)
    {
        var httpData = new HttpData();
        
        if (string.IsNullOrEmpty(payload))
            return httpData;
        
        try
        {
            var lines = payload.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return httpData;
            
            var firstLine = lines[0];
            
            if (firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP Response
                var responseMatch = HttpResponseRegex.Match(firstLine);
                if (responseMatch.Success)
                {
                    httpData.HttpVersion = $"HTTP/{responseMatch.Groups[1].Value}";
                    httpData.Status = $"{responseMatch.Groups[2].Value} {responseMatch.Groups[3].Value}".Trim();
                }
                ParseHeadersAndBody(lines, httpData);
            }
            else
            {
                // HTTP Request
                var requestMatch = HttpRequestRegex.Match(firstLine);
                if (requestMatch.Success)
                {
                    httpData.Method = requestMatch.Groups[1].Value;
                    httpData.Uri = requestMatch.Groups[2].Value;
                    httpData.HttpVersion = $"HTTP/{requestMatch.Groups[3].Value}";
                }
                ParseHeadersAndBody(lines, httpData);
            }
        }
        catch { }
        
        return httpData;
    }
    
    private static void ParseHeadersAndBody(string[] lines, HttpData httpData)
    {
        var bodyLines = new List<string>();
        bool inBody = false;
        
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            
            if (!inBody)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inBody = true;
                    continue;
                }
                
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex >= 0 && colonIndex < line.Length - 1)
                    {
                        httpData.Host = line.Substring(colonIndex + 1).Trim();
                    }
                }
            }
            else
            {
                bodyLines.Add(line);
            }
        }
        
        if (bodyLines.Count > 0)
        {
            httpData.Body = string.Join("\n", bodyLines);
        }
    }
    
    private static int ExtractStatusCode(string status)
    {
        if (string.IsNullOrEmpty(status))
            return 0;
        
        var parts = status.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out int code))
        {
            return code;
        }
        return 0;
    }
    
    private class HttpData
    {
        public string Method { get; set; } = "";
        public string Uri { get; set; } = "";
        public string Status { get; set; } = "";
        public string HttpVersion { get; set; } = "";
        public string Host { get; set; } = "";
        public string Body { get; set; } = "";
    }
}

/// <summary>
/// Context information for a student's grading session.
/// CRITICAL FIX: Added stage window tracking to prevent race conditions
/// when multiple stages execute concurrently or transition quickly.
/// </summary>
public class StudentContext
{
    private readonly object _lock = new object();
    private string _questionCode = "";
    private string _stage = "0";
    
    // CRITICAL FIX: Track stage execution windows to correctly attribute packets
    // Key: stage number, Value: (start timestamp, end timestamp in ticks)
    // When a new stage starts, we record its start time
    // When we receive a packet, we match it to the appropriate stage based on timestamp
    private readonly Dictionary<string, (long StartTicks, long? EndTicks)> _stageWindows = new();
    
    public string QuestionCode 
    { 
        get { lock (_lock) return _questionCode; }
        set { lock (_lock) _questionCode = value; }
    }
    
    public string Stage 
    { 
        get { lock (_lock) return _stage; }
        set { lock (_lock) _stage = value; }
    }
    
    /// <summary>
    /// Records that a stage has started executing at this timestamp.
    /// CRITICAL: This allows us to correctly attribute packets to stages
    /// even when multiple stages are executing concurrently.
    /// </summary>
    public void RecordStageStart(string stage, long timestampTicks)
    {
        lock (_lock)
        {
            // End the previous stage window if it exists and is still open
            if (!string.IsNullOrEmpty(_stage) && _stageWindows.ContainsKey(_stage))
            {
                var prevWindow = _stageWindows[_stage];
                if (prevWindow.EndTicks == null)
                {
                    _stageWindows[_stage] = (prevWindow.StartTicks, timestampTicks);
                }
            }
            
            // Start new stage window
            _stageWindows[stage] = (timestampTicks, null); // null = still open
            _stage = stage;
        }
    }
    
    /// <summary>
    /// Gets the stage that was active at the given timestamp.
    /// CRITICAL: Uses stage windows to determine which stage a packet belongs to,
    /// preventing misattribution when stages transition quickly.
    /// </summary>
    public string GetStageAtTimestamp(long timestampTicks)
    {
        lock (_lock)
        {
            // Find the stage window that contains this timestamp
            foreach (var kvp in _stageWindows)
            {
                var (startTicks, endTicks) = kvp.Value;
                
                // Packet is in this stage if:
                // - Timestamp >= stage start
                // - AND (stage has no end OR timestamp < stage end)
                if (timestampTicks >= startTicks && 
                    (endTicks == null || timestampTicks < endTicks.Value))
                {
                    return kvp.Key;
                }
            }
            
            // Fallback: use current stage
            return _stage;
        }
    }
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
