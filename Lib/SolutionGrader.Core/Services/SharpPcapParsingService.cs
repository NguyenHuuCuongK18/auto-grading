using System.Text;
using System.Text.RegularExpressions;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using Domain.Models;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service responsible for parsing PCAP files using SharpPcap library.
/// This is much more robust than parsing tcpdump text output.
/// Works cross-platform (Windows with Npcap, Linux with libpcap).
/// Supports both TCP and HTTP protocol parsing.
/// </summary>
public class SharpPcapParsingService
{
    /// <summary>
    /// Parse a PCAP file and extract TCP/HTTP packets with their payload data.
    /// Protocol detection is automatic based on packet content (HTTP headers in payload).
    /// </summary>
    /// <param name="pcapFilePath">Path to the .pcap file</param>
    /// <param name="stage">Current test stage number</param>
    /// <param name="expectedPort">Port number to identify client/server roles</param>
    /// <param name="protocol">Expected protocol type (TCP or HTTP) from Header.xlsx</param>
    /// <returns>List of captured network packets</returns>
    public List<CapturedNetworkPacket> ParsePcapFile(string pcapFilePath, int stage, int expectedPort, string protocol = "TCP")
    {
        var packets = new List<CapturedNetworkPacket>();
        
        if (!File.Exists(pcapFilePath))
        {
            return packets; // Empty list if file doesn't exist
        }

        try
        {
            // Create a device from the .pcap file
            using var device = new CaptureFileReaderDevice(pcapFilePath);
            
            // Open the device for reading
            device.Open();
            
            // Read all packets from the file
            PacketCapture e;
            while (device.GetNextPacket(out e) == SharpPcap.GetPacketStatus.PacketRead)
            {
                try
                {
                    var rawCapture = e.GetPacket();
                    // Parse the raw packet using PacketDotNet
                    var packet = Packet.ParsePacket(rawCapture.LinkLayerType, rawCapture.Data);
                    
                    // Extract TCP packet
                    var tcpPacket = packet.Extract<TcpPacket>();
                    if (tcpPacket == null) continue; // Skip non-TCP packets
                    
                    // Extract IP packet for source/destination addresses
                    var ipPacket = packet.Extract<IPv4Packet>();
                    if (ipPacket == null) continue; // Skip non-IPv4 packets
                    
                    var srcPort = tcpPacket.SourcePort;
                    var dstPort = tcpPacket.DestinationPort;
                    
                    // Determine roles based on port
                    // CRITICAL FIX: Be lenient with port matching since students may hardcode ports
                    // The grading system assigns different ports (4000, 4001, 4002...) to avoid conflicts,
                    // but students often hardcode port 4000. We should accept ANY port in the 4000-4010 range
                    // and use heuristics to determine client vs server based on connection direction.
                    string srcRole, dstRole;
                    
                    // Check if either port matches expected port OR is in the common range (4000-4010)
                    bool srcIsServerPort = (srcPort == expectedPort) || (srcPort >= 4000 && srcPort <= 4010 && srcPort < dstPort);
                    bool dstIsServerPort = (dstPort == expectedPort) || (dstPort >= 4000 && dstPort <= 4010 && dstPort < srcPort);
                    
                    if (srcIsServerPort)
                    {
                        srcRole = "Server";
                        dstRole = "Client";
                    }
                    else if (dstIsServerPort)
                    {
                        srcRole = "Client";
                        dstRole = "Server";
                    }
                    else
                    {
                        // Neither port looks like a server port - skip
                        // This happens with unrelated traffic
                        continue;
                    }
                    
                    // Parse TCP flags
                    string flags = ParseTcpFlags(tcpPacket);
                    string state = DetermineState(flags);
                    
                    // Extract payload data (application layer)
                    string payloadData = "";
                    if (tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
                    {
                        // Convert bytes to ASCII for analysis
                        payloadData = Encoding.ASCII.GetString(tcpPacket.PayloadData);
                    }
                    
                    // Detect if this is HTTP traffic by checking payload for HTTP signatures
                    bool isHttp = protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase) || IsHttpPacket(payloadData);
                    
                    // Create captured packet
                    var capturedPacket = new CapturedNetworkPacket
                    {
                        Timestamp = rawCapture.Timeval.Date,
                        Stage = stage,
                        Source = $"{ipPacket.SourceAddress}:{srcPort}",
                        Destination = $"{ipPacket.DestinationAddress}:{dstPort}",
                        SourcePort = srcPort,
                        DestinationPort = dstPort,
                        Flags = flags,
                        State = state,
                        SourceRole = srcRole,
                        DestinationRole = dstRole,
                        Protocol = isHttp ? "HTTP" : "TCP",
                        Length = tcpPacket.PayloadData?.Length ?? 0
                    };
                    
                    // Parse HTTP-specific fields if this is HTTP traffic
                    if (isHttp && !string.IsNullOrWhiteSpace(payloadData))
                    {
                        ParseHttpFields(capturedPacket, payloadData);
                    }
                    else
                    {
                        // For non-HTTP TCP packets, store cleaned payload data
                        var cleanedData = CleanPayloadData(payloadData);
                        capturedPacket.Data = string.IsNullOrWhiteSpace(cleanedData) ? null : cleanedData;
                    }
                    
                    packets.Add(capturedPacket);
                }
                catch
                {
                    // Skip malformed packets
                    continue;
                }
            }
            
            device.Close();
        }
        catch (Exception ex)
        {
            // Log error but don't throw - return empty list
            Console.WriteLine($"[SharpPcap] Error parsing {pcapFilePath}: {ex.Message}");
        }
        
        return packets;
    }
    
    /// <summary>
    /// Parse TCP flags into human-readable format.
    /// 
    /// CRITICAL FIX: Output comma-separated format (e.g., "SYN, ACK", "FIN, ACK")
    /// to match the testkit Detail.xlsx format. The previous hyphenated format
    /// (e.g., "SYN-ACK", "FIN-ACK") caused comparison failures.
    /// 
    /// TCP Flag Reference:
    /// - SYN: Connection initiation
    /// - ACK: Acknowledgment
    /// - PSH: Push data immediately
    /// - FIN: Connection termination
    /// - RST: Connection reset
    /// </summary>
    private string ParseTcpFlags(TcpPacket tcp)
    {
        // CRITICAL FIX: Build flags list in standard order and use comma-separated format
        // This matches the testkit Detail.xlsx format (e.g., "SYN, ACK", "FIN, ACK")
        var flags = new List<string>();
        
        // Add flags in priority order (same order as MiddlewareSniffPort reference implementation)
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Urgent) flags.Add("URG");
        
        // Return comma-separated format to match testkit expectations
        // Examples: "SYN", "SYN, ACK", "PSH, ACK", "FIN, ACK", "ACK"
        return flags.Count > 0 ? string.Join(", ", flags) : "UNKNOWN";
    }
    
    /// <summary>
    /// Determine TCP state based on flags.
    /// 
    /// CRITICAL FIX: Updated to handle comma-separated flag format
    /// (e.g., "SYN, ACK" instead of "SYN-ACK") to match ParseTcpFlags output.
    /// </summary>
    private string DetermineState(string flags)
    {
        // Normalize flags for comparison - handle both comma and hyphen separators
        var normalizedFlags = flags.Replace("-", ", ").Replace("  ", " ");
        
        // Check for specific flag combinations using Contains for flexibility
        if (normalizedFlags.Contains("RST"))
            return "RESET";
        if (normalizedFlags.Contains("SYN") && normalizedFlags.Contains("ACK"))
            return "SYN_RECEIVED";
        if (normalizedFlags.Contains("SYN"))
            return "SYN_SENT";
        if (normalizedFlags.Contains("FIN"))
            return "FIN_WAIT";
        if (normalizedFlags.Contains("PSH") && normalizedFlags.Contains("ACK"))
            return "ESTABLISHED";
        if (normalizedFlags.Contains("ACK"))
            return "ESTABLISHED";
        
        return "";
    }
    
    // HTTP method detection set for O(1) lookup performance
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET ", "POST ", "PUT ", "DELETE ", "PATCH ", "HEAD ", "OPTIONS ", "CONNECT ", "TRACE "
    };
    
    /// <summary>
    /// Check if payload data contains HTTP protocol signatures
    /// </summary>
    private bool IsHttpPacket(string payloadData)
    {
        if (string.IsNullOrWhiteSpace(payloadData))
            return false;
        
        // Check for HTTP request methods at the start
        // Extract first word (up to space) for efficient method matching
        var firstWordEnd = payloadData.IndexOf(' ');
        if (firstWordEnd > 0 && firstWordEnd < 10) // Method names are short
        {
            var firstWord = payloadData.Substring(0, firstWordEnd + 1); // Include space
            if (HttpMethods.Contains(firstWord))
                return true;
        }
        
        // Check for HTTP response signature
        if (payloadData.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            return true;
            
        return false;
    }
    
    /// <summary>
    /// Parse HTTP-specific fields from payload data.
    /// This method extracts HTTP method, URI, version, status, headers, and body.
    /// </summary>
    /// <param name="packet">The packet to populate with HTTP data</param>
    /// <param name="payloadData">Raw HTTP payload as string</param>
    private void ParseHttpFields(CapturedNetworkPacket packet, string payloadData)
    {
        try
        {
            // Split into lines
            var lines = payloadData.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return;
            
            var firstLine = lines[0].Trim();
            
            // Check if this is an HTTP Request or Response
            if (firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                // This is an HTTP Response
                packet.IsHttpRequest = false;
                
                // Parse: HTTP/1.1 200 OK
                var parts = firstLine.Split(new[] { ' ' }, 3);
                if (parts.Length >= 2)
                {
                    packet.HttpVersion = parts[0]; // HTTP/1.1
                    packet.Status = parts.Length >= 3 ? $"{parts[1]} {parts[2]}" : parts[1]; // "200 OK" or "200"
                }
            }
            else
            {
                // This is an HTTP Request
                packet.IsHttpRequest = true;
                
                // Parse: GET /api/students HTTP/1.1
                var parts = firstLine.Split(' ');
                if (parts.Length >= 3)
                {
                    packet.Method = parts[0]; // GET, POST, etc.
                    packet.URI = parts[1]; // /api/students
                    packet.HttpVersion = parts[2]; // HTTP/1.1
                }
                else if (parts.Length >= 2)
                {
                    packet.Method = parts[0];
                    packet.URI = parts[1];
                }
            }
            
            // Parse headers and body
            var headerLines = new List<string>();
            int bodyStartIndex = -1;
            
            // Find where headers end (empty line)
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    bodyStartIndex = i + 1;
                    break;
                }
                headerLines.Add(lines[i]);
            }
            
            // Store headers as concatenated string
            if (headerLines.Count > 0)
            {
                packet.HttpHeaders = string.Join("; ", headerLines);
            }
            
            // Extract body if present
            if (bodyStartIndex > 0 && bodyStartIndex < lines.Length)
            {
                var bodyLines = lines.Skip(bodyStartIndex).Where(l => !string.IsNullOrWhiteSpace(l));
                var body = string.Join("\n", bodyLines).Trim();
                
                // Clean body - remove non-printable characters but keep JSON structure
                var cleanedBody = CleanPayloadData(body);
                if (!string.IsNullOrWhiteSpace(cleanedBody))
                {
                    packet.HttpBody = cleanedBody;
                    packet.Data = cleanedBody; // Also store in Data field for compatibility
                }
            }
        }
        catch (Exception)
        {
            // If HTTP parsing fails, fall back to storing raw data
            // Error is silently handled - packet will have cleaned payload data
            packet.Data = CleanPayloadData(payloadData);
        }
    }
    
    /// <summary>
    /// Clean payload data by keeping only printable characters and common symbols.
    /// This helps filter out binary data while preserving text content.
    /// </summary>
    private string CleanPayloadData(string payloadData)
    {
        if (string.IsNullOrWhiteSpace(payloadData))
            return "";
            
        var sb = new StringBuilder();
        foreach (var c in payloadData)
        {
            // Keep letters, digits, spaces, newlines, and common printable symbols
            if (char.IsLetterOrDigit(c) || 
                " \t\r\n{}[]\"':,;.!?@#$%^&*()_+-=<>/\\|~`".Contains(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
