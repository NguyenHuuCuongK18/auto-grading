using System.Text;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using Domain.Models;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service responsible for parsing PCAP files using SharpPcap library.
/// This is much more robust than parsing tcpdump text output.
/// Works cross-platform (Windows with Npcap, Linux with libpcap).
/// </summary>
public class SharpPcapParsingService
{
    /// <summary>
    /// Parse a PCAP file and extract TCP packets with their payload data.
    /// </summary>
    /// <param name="pcapFilePath">Path to the .pcap file</param>
    /// <param name="stage">Current test stage number</param>
    /// <param name="expectedPort">Port number to identify client/server roles</param>
    /// <returns>List of captured network packets</returns>
    public List<CapturedNetworkPacket> ParsePcapFile(string pcapFilePath, int stage, int expectedPort)
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
            RawCapture rawCapture;
            while ((rawCapture = device.GetNextPacket()) != null)
            {
                try
                {
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
                        // Convert bytes to ASCII, filtering only printable characters
                        var sb = new StringBuilder();
                        foreach (var b in tcpPacket.PayloadData)
                        {
                            // Keep letters, digits, spaces, and common printable symbols
                            if (char.IsLetterOrDigit((char)b) || " {}\",:.[]()-_=+".Contains((char)b))
                            {
                                sb.Append((char)b);
                            }
                        }
                        payloadData = sb.ToString().Trim();
                    }
                    
                    // Create captured packet
                    var capturedPacket = new CapturedNetworkPacket
                    {
                        Timestamp = rawCapture.Timeval.Date,
                        Stage = stage,
                        SourceIp = ipPacket.SourceAddress.ToString(),
                        DestinationIp = ipPacket.DestinationAddress.ToString(),
                        SourcePort = srcPort,
                        DestinationPort = dstPort,
                        Flags = flags,
                        State = state,
                        SourceRole = srcRole,
                        DestinationRole = dstRole,
                        Data = string.IsNullOrWhiteSpace(payloadData) ? null : payloadData
                    };
                    
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
}
