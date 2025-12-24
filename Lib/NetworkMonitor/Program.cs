/// <summary>
/// Network Monitor - Real-time packet capture using SharpPcap/PacketDotNet
/// 
/// This application implements the EXACT same packet capture approach as MiddlewareSniffPort
/// to ensure consistent behavior between testkit generation and grading.
/// 
/// ARCHITECTURE:
/// - Runs in a Docker container with --net=container:{unifiedContainer}
/// - Captures on loopback interface (lo) to see localhost traffic between client/server
/// - Uses SharpPcap's event-driven OnPacketArrival (real-time, no buffering issues)
/// - Writes captured packets to JSON lines file for reading by grading service
/// 
/// KEY FEATURES (matching MiddlewareSniffPort exactly):
/// 1. Real-time parsing - packets are parsed immediately on arrival
/// 2. TCP flag format: comma-separated (e.g., "FIN, ACK") not hyphenated
/// 3. Flag order: FIN, SYN, RST, PSH, ACK, URG (matching TCP header bit order)
/// 4. Role detection based on port (server port is typically 4000-4010)
/// 5. Payload extraction for PSH packets
/// 
/// USAGE:
///   NetworkMonitor <port> <outputPath>
///   
/// SIGNALS:
///   SIGTERM/SIGINT - Graceful shutdown, flush remaining packets
///   SIGUSR1        - Flush output buffer (for snapshot reads)
/// </summary>

using System.Text;
using System.Text.Json;
using SharpPcap;
using PacketDotNet;

namespace NetworkMonitor;

class Program
{
    // Configuration
    private static int _targetPort = 4000;
    private static string _outputPath = "/data/packets.jsonl";
    
    // Capture state
    private static ICaptureDevice? _device;
    private static StreamWriter? _outputWriter;
    private static readonly object _writeLock = new();
    private static volatile bool _isRunning = true;
    private static int _packetCount = 0;
    
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"[NetworkMonitor] Starting at {DateTime.UtcNow:O}");
        Console.WriteLine($"[NetworkMonitor] Args: {string.Join(" ", args)}");
        
        // Parse arguments
        if (args.Length >= 1 && int.TryParse(args[0], out var port))
        {
            _targetPort = port;
        }
        
        if (args.Length >= 2)
        {
            _outputPath = args[1];
        }
        
        Console.WriteLine($"[NetworkMonitor] Target port: {_targetPort}");
        Console.WriteLine($"[NetworkMonitor] Output path: {_outputPath}");
        
        // Setup signal handlers for graceful shutdown
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            _isRunning = false;
            Console.WriteLine("[NetworkMonitor] Received SIGINT, stopping...");
        };
        
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            _isRunning = false;
            FlushOutput();
            Console.WriteLine("[NetworkMonitor] Received SIGTERM, stopping...");
        };
        
        try
        {
            return await RunCapture();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NetworkMonitor] FATAL: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
    
    /// <summary>
    /// Main capture loop - matches MiddlewareSniffPort's StartCapture
    /// </summary>
    private static async Task<int> RunCapture()
    {
        // Step 1: Find capture device (loopback)
        var devices = CaptureDeviceList.Instance;
        Console.WriteLine($"[NetworkMonitor] Found {devices.Count} capture devices:");
        
        foreach (var dev in devices)
        {
            Console.WriteLine($"  - {dev.Name}: {dev.Description ?? "(no description)"}");
        }
        
        // Find loopback interface (lo on Linux, Loopback on Windows)
        _device = devices.FirstOrDefault(d =>
            d.Name == "lo" ||
            d.Name.Equals("lo", StringComparison.OrdinalIgnoreCase) ||
            (d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false));
        
        if (_device == null)
        {
            // Fallback: try "any" device which captures on all interfaces
            _device = devices.FirstOrDefault(d => d.Name == "any");
        }
        
        if (_device == null)
        {
            // Last resort: use first available device
            _device = devices.FirstOrDefault();
        }
        
        if (_device == null)
        {
            Console.Error.WriteLine("[NetworkMonitor] ERROR: No capture devices available");
            Console.Error.WriteLine("[NetworkMonitor] Ensure libpcap is installed and container has NET_ADMIN/NET_RAW capabilities");
            return 1;
        }
        
        Console.WriteLine($"[NetworkMonitor] Using device: {_device.Name}");
        
        // Step 2: Open output file
        var outputDir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        
        _outputWriter = new StreamWriter(_outputPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true // Ensure immediate writes like MiddlewareSniffPort
        };
        
        Console.WriteLine($"[NetworkMonitor] Output file opened: {_outputPath}");
        
        // Step 3: Setup packet capture (matching MiddlewareSniffPort)
        _device.OnPacketArrival += OnPacketArrival;
        
        // CRITICAL FIX: Use Promiscuous + MaxResponsiveness mode to minimize buffering and 
        // ensure packets are delivered in the correct order. On Linux, libpcap uses a ring 
        // buffer that can cause packets to be delivered out of order when they arrive very 
        // close together (e.g., FIN packets within milliseconds of each other).
        // 
        // Also use a much smaller read timeout (10ms instead of 1000ms) to reduce buffering.
        // This ensures packets are processed immediately rather than being batched.
        _device.Open(DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness, 10);
        
        // Set BPF filter for TCP only (like MiddlewareSniffPort with targeted ports)
        try
        {
            _device.Filter = "tcp";
            Console.WriteLine("[NetworkMonitor] Filter applied: tcp");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkMonitor] WARNING: Could not set filter: {ex.Message}");
        }
        
        // Step 4: Start capture
        _device.StartCapture();
        Console.WriteLine($"[NetworkMonitor] Capture started, listening for packets on port {_targetPort}...");
        
        // Step 5: Run until signaled to stop
        while (_isRunning)
        {
            await Task.Delay(100);
        }
        
        // Step 6: Cleanup
        Console.WriteLine($"[NetworkMonitor] Stopping capture. Total packets captured: {_packetCount}");
        
        try
        {
            _device.StopCapture();
            _device.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NetworkMonitor] Warning during device cleanup: {ex.Message}");
        }
        
        FlushOutput();
        
        Console.WriteLine("[NetworkMonitor] Capture stopped successfully");
        return 0;
    }
    
    /// <summary>
    /// Handle packet arrival - EXACTLY matching MiddlewareSniffPort's Device_OnPacketArrival
    /// </summary>
    private static void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            
            // Extract IP packet
            var ipPacket = packet.Extract<IPPacket>();
            if (ipPacket == null) return;
            
            string srcIp = ipPacket.SourceAddress?.ToString() ?? "(unknown)";
            string dstIp = ipPacket.DestinationAddress?.ToString() ?? "(unknown)";
            
            // Extract TCP packet
            var tcpPacket = packet.Extract<TcpPacket>();
            if (tcpPacket == null) return; // Skip non-TCP
            
            int srcPort = tcpPacket.SourcePort;
            int dstPort = tcpPacket.DestinationPort;
            
            // Check if this packet is related to our target port
            // Accept ports in range 4000-4010 to handle dynamic port allocation
            bool srcIsServer = (srcPort == _targetPort) || (srcPort >= 4000 && srcPort <= 4010 && srcPort < dstPort);
            bool dstIsServer = (dstPort == _targetPort) || (dstPort >= 4000 && dstPort <= 4010 && dstPort < srcPort);
            
            if (!srcIsServer && !dstIsServer)
            {
                // Packet not related to our target - skip
                return;
            }
            
            // Determine roles (matching MiddlewareSniffPort logic)
            string srcRole, dstRole;
            if (srcIsServer)
            {
                srcRole = "Server";
                dstRole = "Client";
            }
            else
            {
                srcRole = "Client";
                dstRole = "Server";
            }
            
            // Extract TCP flags - EXACTLY matching MiddlewareSniffPort's ExtractTcpFlags
            string flags = ExtractTcpFlags(tcpPacket);
            
            // Determine connection state
            string state = DetermineConnectionState(tcpPacket, flags, srcRole);
            
            // Extract payload data (for PSH packets)
            string? payload = null;
            if (tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
            {
                try
                {
                    payload = Encoding.UTF8.GetString(tcpPacket.PayloadData);
                    payload = CleanPayload(payload);
                }
                catch
                {
                    // Binary data - use hex representation
                    payload = BitConverter.ToString(tcpPacket.PayloadData);
                }
            }
            
            // Create packet record
            var record = new CapturedPacket
            {
                Timestamp = rawPacket.Timeval.Date.ToString("O"),
                SourceIp = srcIp,
                SourcePort = srcPort,
                DestinationIp = dstIp,
                DestinationPort = dstPort,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Flags = flags,
                State = state,
                Data = string.IsNullOrWhiteSpace(payload) ? null : payload,
                PayloadLength = tcpPacket.PayloadData?.Length ?? 0
            };
            
            // Write to output file (thread-safe)
            WritePacket(record);
            
            _packetCount++;
            
            // Log to console for debugging
            var dataPreview = string.IsNullOrEmpty(record.Data) ? string.Empty : $" Data={record.Data.Substring(0, Math.Min(50, record.Data.Length))}...";
            Console.WriteLine($"[Packet #{_packetCount}] {srcRole}:{srcPort} -> {dstRole}:{dstPort} [{flags}] {state}{dataPreview}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NetworkMonitor] Error processing packet: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Extract TCP flags - EXACTLY matching MiddlewareSniffPort's ExtractTcpFlags method
    /// Order: FIN, SYN, RST, PSH, ACK, URG (comma-separated)
    /// </summary>
    private static string ExtractTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        
        // CRITICAL: Order matches MiddlewareSniffPort exactly
        // This is the TCP header bit order
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Urgent) flags.Add("URG");
        
        // CRITICAL: Use comma + space separator like MiddlewareSniffPort
        return flags.Count > 0 ? string.Join(", ", flags) : "NONE";
    }
    
    /// <summary>
    /// Determine connection state based on TCP flags - matching MiddlewareSniffPort's DetermineConnectionState
    /// </summary>
    private static string DetermineConnectionState(TcpPacket tcp, string flags, string srcRole)
    {
        if (tcp.Reset)
            return "Connection reset";
        
        if (tcp.Synchronize && tcp.Acknowledgment)
            return "Server responding (SYN-ACK)";
        
        if (tcp.Synchronize)
            return "Client connecting to server (SYN)";
        
        if (tcp.Finished && tcp.Acknowledgment)
            return srcRole == "Client" ? "Client closing connection" : "Server closing connection";
        
        if (tcp.Finished)
            return "Connection termination initiated";
        
        if (tcp.Push && tcp.Acknowledgment)
            return "Data transfer in progress";
        
        // Pure ACK (no other flags)
        if (tcp.Acknowledgment && !tcp.Synchronize && !tcp.Finished && !tcp.Reset && !tcp.Push)
            return "Connection established";
        
        return string.Empty;
    }
    
    /// <summary>
    /// Clean payload data - remove non-printable characters
    /// </summary>
    private static string CleanPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return payload;
        
        var sb = new StringBuilder();
        foreach (var c in payload)
        {
            if (char.IsLetterOrDigit(c) ||
                " \t\r\n{}[]\"':,;.!?@#$%^&*()_+-=<>/\\|~`".Contains(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
    
    /// <summary>
    /// Write packet record to output file (thread-safe)
    /// </summary>
    private static void WritePacket(CapturedPacket packet)
    {
        lock (_writeLock)
        {
            if (_outputWriter != null)
            {
                var json = JsonSerializer.Serialize(packet, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                _outputWriter.WriteLine(json);
            }
        }
    }
    
    /// <summary>
    /// Flush output buffer
    /// </summary>
    private static void FlushOutput()
    {
        lock (_writeLock)
        {
            try
            {
                _outputWriter?.Flush();
                _outputWriter?.Dispose();
                _outputWriter = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkMonitor] Warning during output flush: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Captured packet record - matches the format expected by SharpPcapParsingService
/// </summary>
public class CapturedPacket
{
    public string Timestamp { get; set; } = "";
    public string SourceIp { get; set; } = "";
    public int SourcePort { get; set; }
    public string DestinationIp { get; set; } = "";
    public int DestinationPort { get; set; }
    public string SourceRole { get; set; } = "";
    public string DestinationRole { get; set; } = "";
    public string Flags { get; set; } = "";
    public string State { get; set; } = "";
    public string? Data { get; set; }
    public int PayloadLength { get; set; }
}
