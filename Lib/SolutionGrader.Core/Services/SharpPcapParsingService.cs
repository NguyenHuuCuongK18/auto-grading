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
                    string srcRole, dstRole;
                    if (srcPort == expectedPort)
                    {
                        srcRole = "Server";
                        dstRole = "Client";
                    }
                    else if (dstPort == expectedPort)
                    {
                        srcRole = "Client";
                        dstRole = "Server";
                    }
                    else
                    {
                        continue; // Skip packets not related to our port
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
    /// Parse TCP flags into human-readable format
    /// </summary>
    private string ParseTcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment && flags.Count == 0) flags.Add("ACK");
        if (tcp.Push) flags.Add("PSH");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        
        // Handle combined flags
        if (tcp.Synchronize && tcp.Acknowledgment)
            return "SYN-ACK";
        if (tcp.Push && tcp.Acknowledgment)
            return "PSH-ACK";
        if (tcp.Finished && tcp.Acknowledgment)
            return "FIN-ACK";
        if (tcp.Reset && tcp.Acknowledgment)
            return "RST-ACK";
        
        return flags.Count > 0 ? string.Join(", ", flags) : "UNKNOWN";
    }
    
    /// <summary>
    /// Determine TCP state based on flags
    /// </summary>
    private string DetermineState(string flags)
    {
        return flags switch
        {
            "SYN" => "SYN_SENT",
            "SYN-ACK" => "SYN_RECEIVED",
            "ACK" => "ESTABLISHED",
            "PSH-ACK" => "ESTABLISHED",
            "FIN-ACK" => "FIN_WAIT",
            "RST" or "RST-ACK" => "RESET",
            _ => ""
        };
    }
    
    /// <summary>
    /// Check if payload data contains HTTP protocol signatures
    /// </summary>
    private bool IsHttpPacket(string payloadData)
    {
        if (string.IsNullOrWhiteSpace(payloadData))
            return false;
            
        // Check for HTTP request methods at the start
        var httpMethods = new[] { "GET ", "POST ", "PUT ", "DELETE ", "PATCH ", "HEAD ", "OPTIONS ", "CONNECT ", "TRACE " };
        foreach (var method in httpMethods)
        {
            if (payloadData.StartsWith(method, StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex)
        {
            // If HTTP parsing fails, fall back to storing raw data
            Console.WriteLine($"[SharpPcap] HTTP parsing error: {ex.Message}");
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
