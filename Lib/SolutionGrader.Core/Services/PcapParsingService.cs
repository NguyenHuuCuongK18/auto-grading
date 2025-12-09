using System.Text;
using System.Text.RegularExpressions;
using Domain.Models; // CapturedNetworkPacket is in Domain project

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service responsible for parsing PCAP/tcpdump output into structured network packets.
/// Extracts from DockerGradingService to follow single responsibility principle.
/// </summary>
public class PcapParsingService
{
    private CapturedNetworkPacket? _currentParsingPacket = null;
    private StringBuilder _currentPayloadBuffer = new StringBuilder();

    /// <summary>
    /// Parse a single tcpdump output line into CapturedNetworkPacket.
    /// With -A flag, tcpdump outputs:
    /// Line 1: "2024-12-08 11:08:03.543348 IP 127.0.0.1.47044 > 127.0.0.1.4000: Flags [P.], seq 1:5, ack 1, win 512, length 4"
    /// Line 2+: ASCII payload data (hex offset + printable chars)
    /// Example payload lines:
    ///   0x0000:  4500 0038 ...   E..8...
    ///   0x0010:  ... S123       (actual data)
    /// </summary>
    /// <param name="line">Single line from tcpdump output</param>
    /// <param name="stage">Current test stage number</param>
    /// <param name="expectedPort">Port number to identify client/server roles</param>
    /// <returns>Completed packet if header line triggers finalization, null if still collecting payload</returns>
    public CapturedNetworkPacket? ParseTcpdumpLine(string line, int stage, int expectedPort)
    {
        // Check if this is a payload line (hex dump format from -A flag)
        // Payload lines start with spaces/tabs followed by 0x or just hex data
        // Example: "	0x0000:  4500 0038 ..." or data continuation lines
        if (line.TrimStart().StartsWith("0x") || (line.StartsWith("\t") || line.StartsWith(" ")) && !line.Contains(" IP "))
        {
            // This is a payload line for the current packet
            if (_currentParsingPacket != null)
            {
                // Extract ASCII data from the hex dump line
                // Format: "	0x0000:  4500 0038 ...  E..8...S123" 
                // We want the part after the hex bytes (the ASCII representation)
                var parts = line.Split(new[] { "  " }, StringSplitOptions.None);
                if (parts.Length >= 2)
                {
                    // Last part typically contains ASCII representation
                    var asciiPart = parts[parts.Length - 1].Trim();
                    // Filter out non-printable characters but keep readable text
                    var readable = new string(asciiPart.Where(c => c >= 32 && c < 127).ToArray());
                    if (!string.IsNullOrWhiteSpace(readable))
                    {
                        _currentPayloadBuffer.Append(readable);
                    }
                }
            }
            return null; // Don't return yet, still collecting payload
        }
        
        // If we were parsing a packet and hit a new header line, finalize the previous packet
        CapturedNetworkPacket? completedPacket = null;
        if (_currentParsingPacket != null)
        {
            // Finalize the previous packet with collected payload
            var collectedPayload = _currentPayloadBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(collectedPayload))
            {
                _currentParsingPacket.Data = collectedPayload;
            }
            completedPacket = _currentParsingPacket;
            _currentParsingPacket = null;
            _currentPayloadBuffer.Clear();
        }
        
        // Now parse the new header line
        // Extract timestamp
        var timestampMatch = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
        if (!timestampMatch.Success)
        {
            // Not a header line, return the completed packet if any
            return completedPacket;
        }
        
        DateTime timestamp = DateTime.TryParse(timestampMatch.Groups[1].Value, out var dt) 
            ? dt 
            : DateTime.Now;
        
        // Extract source and destination: "IP 127.0.0.1.47044 > 127.0.0.1.4000:"
        var addressMatch = Regex.Match(line, @"IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+)");
        if (!addressMatch.Success)
        {
            return completedPacket;
        }
        
        var srcIp = addressMatch.Groups[1].Value;
        var srcPort = int.Parse(addressMatch.Groups[2].Value);
        var dstIp = addressMatch.Groups[3].Value;
        var dstPort = int.Parse(addressMatch.Groups[4].Value);
        
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
            // Not related to our expected port, return completed packet
            return completedPacket;
        }
        
        // Extract flags: [S] = SYN, [S.] = SYN-ACK, [.] = ACK, [P.] = PSH-ACK, [F.] = FIN-ACK, [R] = RST, [R.] = RST-ACK
        string flags = "UNKNOWN";
        string state = "";
        
        if (line.Contains("Flags [S]") && !line.Contains("Flags [S.]"))
        {
            flags = "SYN";
            state = "SYN_SENT";
        }
        else if (line.Contains("Flags [S.]"))
        {
            flags = "SYN-ACK";
            state = "SYN_RECEIVED";
        }
        else if (line.Contains("Flags [.]") && !line.Contains("Flags [P.]") && !line.Contains("Flags [F.]") && !line.Contains("Flags [R.]"))
        {
            flags = "ACK";
            state = "ESTABLISHED";
        }
        else if (line.Contains("Flags [P.]"))
        {
            flags = "PSH-ACK";
            state = "ESTABLISHED";
        }
        else if (line.Contains("Flags [F.]"))
        {
            flags = "FIN-ACK";
            state = "FIN_WAIT";
        }
        else if (line.Contains("Flags [R.]"))
        {
            // RST+ACK - server rejecting connection
            flags = "RST-ACK";
            state = "RESET";
        }
        else if (line.Contains("Flags [R]"))
        {
            // RST only
            flags = "RST";
            state = "RESET";
        }
        
        // Extract payload length (for logging/debugging)
        var lengthMatch = Regex.Match(line, @"length (\d+)");
        int payloadLength = lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value) : 0;
        
        // Create new packet for this header line
        var newPacket = new CapturedNetworkPacket
        {
            Stage = stage,
            Timestamp = timestamp,
            Flags = flags,
            State = state,
            SourceRole = srcRole,
            DestinationRole = dstRole,
            Data = "", // Will be filled by subsequent payload lines or left empty
            SourcePort = srcPort,
            DestinationPort = dstPort
        };
        
        // If this packet has payload, start collecting it
        if (payloadLength > 0)
        {
            _currentParsingPacket = newPacket;
            _currentPayloadBuffer.Clear();
            // Return the completed previous packet if any
            return completedPacket;
        }
        else
        {
            // No payload, return this packet immediately (and the completed one if exists)
            // If there was a previous packet, we need to handle it
            if (completedPacket != null)
            {
                // We can only return one packet at a time, so store the new one for next call
                _currentParsingPacket = newPacket;
                return completedPacket;
            }
            return newPacket;
        }
    }

    /// <summary>
    /// Finalize any pending packet being parsed.
    /// Call this after processing all tcpdump lines to ensure the last packet is captured.
    /// </summary>
    /// <returns>The final packet if one was being parsed, otherwise null</returns>
    public CapturedNetworkPacket? FinalizeCurrentPacket()
    {
        if (_currentParsingPacket != null)
        {
            var collectedPayload = _currentPayloadBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(collectedPayload))
            {
                _currentParsingPacket.Data = collectedPayload;
            }
            
            var finalPacket = _currentParsingPacket;
            _currentParsingPacket = null;
            _currentPayloadBuffer.Clear();
            return finalPacket;
        }
        return null;
    }

    /// <summary>
    /// Reset the parser state for a new tcpdump parsing session.
    /// </summary>
    public void Reset()
    {
        _currentParsingPacket = null;
        _currentPayloadBuffer.Clear();
    }
}
