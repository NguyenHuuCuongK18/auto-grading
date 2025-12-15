using System.Text.Json;
using Domain.Models;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service responsible for parsing JSON lines output from the SharpPcap-based network monitor sidecar.
/// 
/// This replaces the PCAP parsing approach with direct JSON parsing, which is more reliable
/// because the sidecar has already parsed the packets using the same SharpPcap/PacketDotNet
/// logic as MiddlewareSniffPort.
/// 
/// OUTPUT FORMAT (JSON Lines - one JSON object per line):
/// {"timestamp":"2024-12-11T10:30:00Z","sourceIp":"127.0.0.1","sourcePort":4000,...}
/// {"timestamp":"2024-12-11T10:30:01Z","sourceIp":"127.0.0.1","sourcePort":54321,...}
/// 
/// ADVANTAGES:
/// - No PCAP parsing needed - packets already parsed by sidecar
/// - Exact flag format match (comma-separated: "FIN, ACK")
/// - Real-time capture - no buffering/timing issues
/// - Easy to read and debug
/// </summary>
public class JsonPacketParsingService
{
    /// <summary>
    /// Parse JSON lines file from network monitor sidecar.
    /// Each line is a complete JSON object representing a captured packet.
    /// </summary>
    /// <param name="jsonlFilePath">Path to the .jsonl file</param>
    /// <param name="stage">Current test stage number to assign to packets</param>
    /// <returns>List of captured network packets</returns>
    public List<CapturedNetworkPacket> ParseJsonlFile(string jsonlFilePath, int stage)
    {
        var packets = new List<CapturedNetworkPacket>();
        
        if (!File.Exists(jsonlFilePath))
        {
            Console.WriteLine($"[JsonPacketParser] File not found: {jsonlFilePath}");
            return packets;
        }
        
        try
        {
            var lines = File.ReadAllLines(jsonlFilePath);
            Console.WriteLine($"[JsonPacketParser] Parsing {lines.Length} lines from {jsonlFilePath}");
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    var jsonPacket = JsonSerializer.Deserialize<JsonCapturedPacket>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    
                    if (jsonPacket == null) continue;
                    
                    // Convert to CapturedNetworkPacket (domain model)
                    var packet = new CapturedNetworkPacket
                    {
                        Timestamp = DateTime.TryParse(jsonPacket.Timestamp, out var ts) ? ts : DateTime.UtcNow,
                        Stage = stage,
                        Source = $"{jsonPacket.SourceIp}:{jsonPacket.SourcePort}",
                        Destination = $"{jsonPacket.DestinationIp}:{jsonPacket.DestinationPort}",
                        SourcePort = jsonPacket.SourcePort,
                        DestinationPort = jsonPacket.DestinationPort,
                        SourceRole = jsonPacket.SourceRole,
                        DestinationRole = jsonPacket.DestinationRole,
                        Flags = jsonPacket.Flags,
                        State = jsonPacket.State,
                        Data = jsonPacket.Data,
                        Length = jsonPacket.PayloadLength,
                        Protocol = "TCP"  // Sidecar only captures TCP
                    };
                    
                    packets.Add(packet);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[JsonPacketParser] Warning: Failed to parse line: {ex.Message}");
                    continue;
                }
            }
            
            Console.WriteLine($"[JsonPacketParser] Successfully parsed {packets.Count} packets");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonPacketParser] Error reading file: {ex.Message}");
        }
        
        return packets;
    }
    
    /// <summary>
    /// Parse JSON lines file and return only new packets (packets after the given count).
    /// This supports cumulative parsing where we skip already-processed packets.
    /// </summary>
    /// <param name="jsonlFilePath">Path to the .jsonl file</param>
    /// <param name="stage">Current test stage number</param>
    /// <param name="skipCount">Number of packets to skip (already processed)</param>
    /// <returns>Tuple of (new packets, total packet count)</returns>
    public (List<CapturedNetworkPacket> NewPackets, int TotalCount) ParseNewPackets(
        string jsonlFilePath, 
        int stage, 
        int skipCount)
    {
        var allPackets = ParseJsonlFile(jsonlFilePath, stage);
        var newPackets = allPackets.Skip(skipCount).ToList();
        
        return (newPackets, allPackets.Count);
    }
}
// JsonCapturedPacket moved to SolutionGrader.Core/Domain/Models/JsonCapturedPacket.cs
