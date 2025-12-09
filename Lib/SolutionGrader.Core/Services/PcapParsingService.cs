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
    private List<byte> _currentPayloadBytes = new List<byte>(); // Changed to collect bytes instead of strings
    private int _currentPacketPayloadStart = -1; // Track where TCP payload starts in the dump

    /// <summary>
    /// Parse a single tcpdump output line into CapturedNetworkPacket.
    /// With -X flag, tcpdump outputs hex dump with ASCII on the right.
    /// We parse the HEX bytes directly and convert them to ASCII ourselves to avoid garbage from TCP/IP headers.
    /// Example:
    ///   0x0000:  4500 0038 eecc 4000 4006 4df1 7f00 0001  E..8..@.@.M.....
    ///   0x0030:  f8e7 db42 5330 3031                      ...BS001
    /// The hex bytes 5330 3031 = "S001" in ASCII.
    /// </summary>
    /// <param name="line">Single line from tcpdump output</param>
    /// <param name="stage">Current test stage number</param>
    /// <param name="expectedPort">Port number to identify client/server roles</param>
    /// <returns>Completed packet if header line triggers finalization, null if still collecting payload</returns>
    public CapturedNetworkPacket? ParseTcpdumpLine(string line, int stage, int expectedPort)
    {
        // Check if this is a payload line (hex dump format from -X flag)
        // Example: "	0x0030:  f8e7 db42 5330 3031                      ...BS001"
        if (line.TrimStart().StartsWith("0x") || (line.StartsWith("\t") || line.StartsWith(" ")) && !line.Contains(" IP "))
        {
            // This is a hex dump line for the current packet
            if (_currentParsingPacket != null)
            {
                // CRITICAL FIX: Parse HEX bytes directly instead of relying on ASCII column
                // The ASCII column includes garbage from TCP/IP headers (dots, special chars)
                // By parsing hex and converting ourselves, we get clean application payload
                
                // Split by whitespace to get individual tokens
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var token in tokens)
                {
                    // Filter: Only accept 2-character or 4-character hex strings (byte pairs)
                    // Ignore offset (e.g., "0x0030:"), ignore ASCII representation
                    if ((token.Length == 2 || token.Length == 4) && IsHexString(token))
                    {
                        // Parse hex bytes
                        for (int i = 0; i < token.Length; i += 2)
                        {
                            if (i + 1 < token.Length)
                            {
                                var hexByte = token.Substring(i, 2);
                                _currentPayloadBytes.Add(Convert.ToByte(hexByte, 16));
                            }
                        }
                    }
                }
            }
            return null; // Don't return yet, still collecting payload
        }
        
        // If we were parsing a packet and hit a new header line, finalize the previous packet
        CapturedNetworkPacket? completedPacket = null;
        if (_currentParsingPacket != null)
        {
            // CRITICAL FIX: Convert collected hex bytes to ASCII string
            // This extracts ONLY the actual TCP payload data, skipping all headers
            // We determine payload start by looking at TCP header length from the packet
            
            if (_currentPayloadBytes.Count > 0)
            {
                // For TCP packets, the payload typically starts after byte 0x40 (64 bytes = IP+TCP headers)
                // But this varies with TCP options. A safer approach: extract ALL bytes, convert to ASCII,
                // and filter out non-printable chars. The header bytes will be gibberish and get filtered.
                
                // Skip the first ~54 bytes (Ethernet 14 + IP 20 + TCP 20 minimum headers)
                // But since we're getting raw packet dump, we need a smarter approach
                
                // BETTER: Convert all bytes to ASCII and only keep valid printable characters
                var sb = new StringBuilder();
                foreach (var b in _currentPayloadBytes)
                {
                    // Only include bytes that are printable ASCII (letters, digits, common symbols)
                    // This automatically filters out header bytes which are random binary values
                    if ((b >= 32 && b <= 126)) // Printable ASCII range
                    {
                        sb.Append((char)b);
                    }
                }
                
                var payload = sb.ToString().Trim();
                
                // Further cleanup: Remove sequences of dots and control characters that leak from headers
                // Keep only sequences with actual letters/digits
                if (!string.IsNullOrWhiteSpace(payload) && payload.Any(char.IsLetterOrDigit))
                {
                    _currentParsingPacket.Data = payload;
                }
            }
            
            completedPacket = _currentParsingPacket;
            _currentParsingPacket = null;
            _currentPayloadBytes.Clear();
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
