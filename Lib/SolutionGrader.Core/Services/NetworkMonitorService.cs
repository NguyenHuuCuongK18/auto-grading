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
    
    // Track the known client ephemeral port - once we see the first SYN from client,
    // we lock onto that port and filter out any other connections (Windows/Docker health checks)
    private int _knownClientPort = 0;
    private readonly object _clientPortLock = new();
    
    // Track when captures started - used to filter out pre-existing connections from Windows/Docker
    private DateTime _captureStartTime = DateTime.UtcNow;
    
    // Flag to indicate if we've seen the first SYN packet (the real client-server connection)
    // This is the most reliable indicator that our client has started connecting
    private bool _firstSynSeen = false;
    
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
        
        // Reset known client port - will be set when we see the first SYN from actual client
        // or when explicitly set via SetKnownClientPort() or DetectClientPortFromConnections()
        lock (_clientPortLock)
        {
            _knownClientPort = 0;
            _firstSynSeen = false;
        }
        
        // Record the time when captures were cleared - used to filter out pre-existing connections
        _captureStartTime = DateTime.UtcNow;
        Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Captures cleared, waiting for first SYN to identify client-server connection");
    }
    
    /// <summary>
    /// Sets the known client port explicitly. Call this if you know the client port in advance.
    /// This allows filtering out Windows/Docker health check traffic to the server port.
    /// </summary>
    public void SetKnownClientPort(int clientPort)
    {
        lock (_clientPortLock)
        {
            _knownClientPort = clientPort;
            _firstSynSeen = true; // Consider connection identified if port is set explicitly
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Known client port explicitly set to {clientPort}");
        }
    }
    
    /// <summary>
    /// Detects the client's ephemeral port by querying the OS TCP connections table.
    /// This is more reliable than detecting from SYN packets because it directly queries
    /// the OS for established connections to the server port.
    /// 
    /// Call this after the client process has started and connected to the server.
    /// Returns the client's ephemeral port, or 0 if not found.
    /// </summary>
    public int DetectClientPortFromConnections()
    {
        try
        {
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnections = ipGlobalProperties.GetActiveTcpConnections();
            
            // Find connections where the remote port is our server port (client connecting to server)
            // or where the local port is our server port (connection from server's perspective)
            foreach (var connection in tcpConnections)
            {
                // Client -> Server: local port is ephemeral, remote port is server port
                if (connection.RemoteEndPoint.Port == _serverPort && 
                    connection.LocalEndPoint.Address.ToString() == "127.0.0.1" &&
                    connection.State == System.Net.NetworkInformation.TcpState.Established)
                {
                    var clientPort = connection.LocalEndPoint.Port;
                    Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Detected client port from TCP table: {clientPort}");
                    
                    lock (_clientPortLock)
                    {
                        _knownClientPort = clientPort;
                    }
                    _portRoleMap.TryAdd(clientPort, NetworkKeywords.Role_Client);
                    return clientPort;
                }
            }
            
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} No established connection to server port {_serverPort} found in TCP table");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Error detecting client port from TCP table: {ex.Message}");
            return 0;
        }
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
            
            // ===================================================================================
            // HEALTH CHECK FILTERING LOGIC
            // ===================================================================================
            // After ClearCaptures(), we wait for the FIRST SYN packet. This first SYN establishes
            // which ephemeral port is our real client. From that point on:
            // - We only accept traffic between the known client port and server port
            // - Any traffic from/to other ports = Windows/Docker health checks → filtered out
            //
            // This is deterministic because:
            // 1. Containers are fresh after disposal, so no pre-existing connections
            // 2. The client MUST send a SYN to connect to the server
            // 3. That first SYN identifies our client's ephemeral port
            // ===================================================================================
            
            int knownClientPort;
            bool firstSynSeen;
            lock (_clientPortLock)
            {
                knownClientPort = _knownClientPort;
                firstSynSeen = _firstSynSeen;
            }
            
            // Check if this is a SYN packet (start of a new TCP connection)
            bool isSynPacket = tcpPacket.Synchronize && !tcpPacket.Acknowledgment;
            
            // If this is the FIRST SYN we see after ClearCaptures(), it's our real client connecting
            if (isSynPacket && !firstSynSeen)
            {
                // The port that ISN'T the server port is the client's ephemeral port
                int detectedClientPort = (dstPort == _serverPort) ? srcPort : dstPort;
                
                lock (_clientPortLock)
                {
                    if (!_firstSynSeen)
                    {
                        _knownClientPort = detectedClientPort;
                        _firstSynSeen = true;
                        knownClientPort = detectedClientPort;
                        firstSynSeen = true;
                        Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} First SYN detected! Client port: {detectedClientPort}, Server port: {_serverPort}");
                        Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Traffic between {detectedClientPort} <-> {_serverPort} will be logged for grading");
                    }
                }
            }
            
            // Once we know the client port, exclude traffic that doesn't involve our client-server pair from grading/logging
            // We still receive all traffic, but only log traffic between our known client and server
            if (knownClientPort > 0)
            {
                bool involvesKnownClient = (srcPort == knownClientPort || dstPort == knownClientPort);
                bool involvesServer = (srcPort == _serverPort || dstPort == _serverPort);
                
                // Skip logging/grading for traffic that doesn't involve our client-server pair
                // This filters out Windows/Docker health check traffic from the grading results
                if (!involvesKnownClient || !involvesServer)
                {
                    // This traffic doesn't involve our client-server pair - skip logging for grading
                    if (!tcpPacket.Reset) // Don't spam logs with RST packets
                    {
                        Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Skipped non-graded traffic: {srcPort}->{dstPort} (grading only: {knownClientPort} <-> {_serverPort})");
                    }
                    return;
                }
            }
            
            // Determine roles based on ports
            DetermineRoles(srcPort, dstPort, out var srcRole, out var dstRole);
            
            // Extract TCP flags
            var flags = ExtractTcpFlags(tcpPacket);
            
            // Determine connection state based on flags
            var state = DetermineConnectionState(flags, srcRole);
            
            // Extract payload data if available (PSH packets)
            string? payload = null;
            if (tcpPacket.Push && tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
            {
                payload = Encoding.UTF8.GetString(tcpPacket.PayloadData);
            }
            
            // Create captured packet record
            var capturedPacket = new CapturedNetworkPacket
            {
                Timestamp = rawPacket.Timeval.Date,
                Flags = flags,
                State = state,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Data = payload,
                SourcePort = srcPort,
                DestinationPort = dstPort
            };
            
            // Store the captured packet for grading
            _run.AddCapturedNetworkPacket(_currentQuestionCode, _currentStage, capturedPacket);
            
            // Log captured packet summary
            var logMessage = $"{NetworkKeywords.LOG_PREFIX_CAPTURE} {srcRole}->{dstRole} [{flags}] {state}";
            if (!string.IsNullOrEmpty(payload))
            {
                var payloadPreview = payload.Length > PortKeywords.PACKET_PAYLOAD_PREVIEW_MAX_CHARS 
                    ? payload.Substring(0, PortKeywords.PACKET_PAYLOAD_PREVIEW_MAX_CHARS) + "..." 
                    : payload;
                logMessage += $" Data: {payloadPreview.Replace("\n", "\\n").Replace("\r", "")}";
            }
            Console.WriteLine(logMessage);
            
            // Also store payload in RunContext for backward compatibility (for PSH packets)
            if (!string.IsNullOrEmpty(payload))
            {
                StoreInRunContext(srcRole, payload);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_CAPTURE} Error capturing packet: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Extracts TCP flags from a TCP packet and returns a human-readable string.
    /// </summary>
    private static string ExtractTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Urgent) flags.Add("URG");
        
        return string.Join(", ", flags);
    }
    
    /// <summary>
    /// Determines the connection state description based on TCP flags and source role.
    /// State descriptions are informational and NOT used for grading validation.
    /// Only TCP flags and roles are compared for grading purposes.
    /// </summary>
    private static string DetermineConnectionState(string flags, string srcRole)
    {
        // Standard TCP handshake states
        // SYN only (no ACK) from client = connection initiation
        if (flags == "SYN" && srcRole == NetworkKeywords.Role_Client)
            return "Client connecting to server (SYN)";
        // SYN, ACK from server = server acknowledging connection
        if ((flags == "SYN, ACK" || flags == "ACK, SYN") && srcRole == NetworkKeywords.Role_Server)
            return "Server responding (SYN-ACK)";
        // ACK only (no SYN, no PSH, no FIN) = connection established acknowledgment
        if (flags == "ACK")
            return "Connection established";
        // PSH, ACK = data transfer
        if (flags.Contains("PSH"))
            return "Data transfer in progress";
        // FIN = connection closing
        if (flags.Contains("FIN"))
            return "Closing connection (FIN-ACK)";
        // RST = connection reset - indicates error condition (crash, abnormal termination)
        if (flags.Contains("RST"))
            return "Connection reset (RST) - Error occurred";
        
        return "TCP packet";
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
        // Parse HTTP data if this is HTTP protocol
        if (ProtocolType.Equals(NetworkKeywords.Protocol_HTTP, StringComparison.OrdinalIgnoreCase))
        {
            var httpData = ParseHttpData(payload);
            
            // Client -> Server is a request
            if (srcRole == NetworkKeywords.Role_Client)
            {
                // Store the full request payload
                _run.SetServerRequest(_currentQuestionCode, _currentStage, payload);
                
                if (!string.IsNullOrEmpty(httpData.Method))
                {
                    // Store method and request body separately for easier comparison
                    _run.SetHttpMetadata(_currentQuestionCode, _currentStage, httpData.Method, 0, 
                        Encoding.UTF8.GetByteCount(payload));
                    
                    // Store HTTP body if present (for request payload comparison)
                    if (!string.IsNullOrEmpty(httpData.Body))
                    {
                        _run.SetCapturedOutput($"network.{_currentStage}.req.body", httpData.Body);
                    }
                }
            }
            // Server -> Client is a response
            else if (srcRole == NetworkKeywords.Role_Server)
            {
                // Store the full response payload
                _run.SetServerResponse(_currentQuestionCode, _currentStage, payload);
                
                if (!string.IsNullOrEmpty(httpData.Status))
                {
                    // Parse status code from status line (e.g., "200 OK" -> 200)
                    var statusCode = ExtractStatusCode(httpData.Status);
                    _run.SetHttpMetadata(_currentQuestionCode, _currentStage, "", statusCode,
                        Encoding.UTF8.GetByteCount(payload));
                    
                    // Store HTTP body separately for response payload comparison
                    // Logging removed to reduce verbosity - body storage is silent now
                    if (!string.IsNullOrEmpty(httpData.Body))
                    {
                        _run.SetCapturedOutput($"network.{_currentStage}.res.body", httpData.Body);
                    }
                }
            }
        }
        else
        {
            // TCP protocol - store raw data
            if (srcRole == NetworkKeywords.Role_Client)
            {
                _run.SetServerRequest(_currentQuestionCode, _currentStage, payload);
                _run.SetCapturedOutput($"network.{_currentStage}.req.data", payload);
            }
            else if (srcRole == NetworkKeywords.Role_Server)
            {
                _run.SetServerResponse(_currentQuestionCode, _currentStage, payload);
                _run.SetCapturedOutput($"network.{_currentStage}.res.data", payload);
            }
        }
    }
    
    // Regex patterns for HTTP parsing (same as NetworkMonitor library)
    private static readonly Regex HttpRequestRegex = new(@"^(\S+)\s+(\S+)\s+HTTP/([0-9.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HttpResponseRegex = new(@"^HTTP/([0-9.]+)\s+(\d+)\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    /// <summary>
    /// Parses HTTP request/response data to extract method, status, headers, and body.
    /// Uses the same logic as NetworkMonitor.Services.NetworkFlowConverter.
    /// </summary>
    private static HttpData ParseHttpData(string? payload)
    {
        var httpData = new HttpData();
        
        if (string.IsNullOrEmpty(payload))
            return httpData;
        
        try
        {
            // Split into lines
            var lines = payload.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return httpData;
            
            var firstLine = lines[0];
            
            // Check if it's a request or response
            if (firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP Response: HTTP/VERSION STATUS_CODE STATUS_MESSAGE
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
                // HTTP Request: METHOD URI HTTP/VERSION
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
        catch
        {
            // If parsing fails, return what we have
        }
        
        return httpData;
    }
    
    /// <summary>
    /// Parses HTTP headers and body from lines.
    /// </summary>
    private static void ParseHeadersAndBody(string[] lines, HttpData httpData)
    {
        var headerLines = new List<string>();
        var bodyLines = new List<string>();
        bool inBody = false;
        
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            
            if (!inBody)
            {
                // Empty line indicates end of headers
                if (string.IsNullOrWhiteSpace(line))
                {
                    inBody = true;
                    continue;
                }
                
                headerLines.Add(line);
                
                // Extract Host header if present
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
        
        if (headerLines.Count > 0)
        {
            httpData.Headers = string.Join("; ", headerLines);
        }
        
        if (bodyLines.Count > 0)
        {
            httpData.Body = string.Join("\n", bodyLines).Trim();
        }
    }
    
    /// <summary>
    /// Extracts numeric status code from status line (e.g., "200 OK" -> 200)
    /// </summary>
    private static int ExtractStatusCode(string? status)
    {
        if (string.IsNullOrEmpty(status)) return 0;
        
        var match = Regex.Match(status, @"^(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var code))
        {
            return code;
        }
        return 0;
    }
    
    /// <summary>
    /// Internal class to hold parsed HTTP data.
    /// </summary>
    private class HttpData
    {
        public string? Method { get; set; }
        public string? Uri { get; set; }
        public string? Status { get; set; }
        public string? HttpVersion { get; set; }
        public string? Host { get; set; }
        public string? Headers { get; set; }
        public string? Body { get; set; }
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
