namespace Common.Models;

/// <summary>
/// Represents an HTTP network flow with parsed HTTP request/response data.
/// Used for grading HTTP protocol test kits.
/// </summary>
public class HttpNetworkFlow
{
    /// <summary>Timestamp when the packet was captured</summary>
    public string? Time { get; set; }
    
    /// <summary>Protocol info (e.g., "HTTP Request", "HTTP Response")</summary>
    public string? Info { get; set; }
    
    /// <summary>Source IP:Port</summary>
    public string? Source { get; set; }
    
    /// <summary>Destination IP:Port</summary>
    public string? Destination { get; set; }
    
    /// <summary>TCP flags (SYN, ACK, PSH, FIN, etc.)</summary>
    public string? Flags { get; set; }
    
    /// <summary>Connection state description</summary>
    public string? State { get; set; }
    
    /// <summary>HTTP request URI path</summary>
    public string? URI { get; set; }
    
    /// <summary>HTTP Host header value</summary>
    public string? Host { get; set; }
    
    /// <summary>HTTP method (GET, POST, PUT, DELETE)</summary>
    public string? Method { get; set; }
    
    /// <summary>HTTP response status (e.g., "200 OK", "404 Not Found")</summary>
    public string? Status { get; set; }
    
    /// <summary>HTTP version (e.g., "HTTP/1.1")</summary>
    public string? HttpVersion { get; set; }
    
    /// <summary>HTTP headers (semicolon-separated)</summary>
    public string? HttpHeaders { get; set; }
    
    /// <summary>HTTP body content</summary>
    public string? HttpBody { get; set; }
    
    /// <summary>Role of source (Client/Server)</summary>
    public string? SourceRole { get; set; }
    
    /// <summary>Role of destination (Client/Server)</summary>
    public string? DestinationRole { get; set; }
}
