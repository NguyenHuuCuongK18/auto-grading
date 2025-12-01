namespace Common.Models;

/// <summary>
/// Represents a TCP network flow with packet capture data.
/// Used for grading TCP protocol test kits.
/// </summary>
public class TcpNetworkFlow
{
    /// <summary>Timestamp when the packet was captured</summary>
    public string? Time { get; set; }
    
    /// <summary>Protocol info (e.g., "TCP")</summary>
    public string? Info { get; set; }
    
    /// <summary>Source IP:Port</summary>
    public string? Source { get; set; }
    
    /// <summary>Destination IP:Port</summary>
    public string? Destination { get; set; }
    
    /// <summary>TCP flags (SYN, ACK, PSH, FIN, etc.)</summary>
    public string? Flags { get; set; }
    
    /// <summary>Connection state description</summary>
    public string? State { get; set; }
    
    /// <summary>Raw TCP payload data</summary>
    public string? Data { get; set; }
    
    /// <summary>Role of source (Client/Server)</summary>
    public string? SourceRole { get; set; }
    
    /// <summary>Role of destination (Client/Server)</summary>
    public string? DestinationRole { get; set; }
}
