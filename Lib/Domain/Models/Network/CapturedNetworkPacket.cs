#nullable enable

using System;

namespace Domain.Models.Network
{
    /// <summary>
    /// Represents a captured network packet with TCP/HTTP flow information.
    /// Used for grading TCP handshake, connection lifecycle, and HTTP request/response validation.
    /// Supports both TCP and HTTP protocols based on the testkit configuration.
    /// </summary>
    public class CapturedNetworkPacket
    {
        /// <summary>The stage number when this packet was captured</summary>
        public int Stage { get; set; }
        /// <summary>Packet capture timestamp</summary>
        public DateTime Timestamp { get; set; }
        /// <summary>TCP flags (e.g., "SYN", "SYN, ACK", "ACK", "PSH, ACK", "FIN, ACK")</summary>
        public string Flags { get; set; } = "";
        /// <summary>Connection state description</summary>
        public string State { get; set; } = "";
        /// <summary>Source role (Client or Server)</summary>
        public string SourceRole { get; set; } = "";
        /// <summary>Destination role (Client or Server)</summary>
        public string DestinationRole { get; set; } = "";
        /// <summary>Source address (IP:Port)</summary>
        public string Source { get; set; } = "";
        /// <summary>Destination address (IP:Port)</summary>
        public string Destination { get; set; } = "";
        /// <summary>Protocol (TCP or HTTP)</summary>
        public string Protocol { get; set; } = "";
        /// <summary>Packet length in bytes</summary>
        public int Length { get; set; }
        /// <summary>Additional information</summary>
        public string Info { get; set; } = "";
        /// <summary>Payload data (for PSH packets in TCP, or raw data)</summary>
        public string? Data { get; set; }
        /// <summary>Source port</summary>
        public int SourcePort { get; set; }
        /// <summary>Destination port</summary>
        public int DestinationPort { get; set; }
        
        // ====== HTTP-specific fields ======
        // These fields are only populated when Protocol = "HTTP"
        
        /// <summary>HTTP Request URI (e.g., "/api/students" or "/students/S001")</summary>
        public string? URI { get; set; }
        
        /// <summary>HTTP method (GET, POST, PUT, DELETE, etc.)</summary>
        public string? Method { get; set; }
        
        /// <summary>HTTP status code (e.g., "200 OK", "404 Not Found")</summary>
        public string? Status { get; set; }
        
        /// <summary>HTTP version (e.g., "HTTP/1.1")</summary>
        public string? HttpVersion { get; set; }
        
        /// <summary>HTTP request/response body content</summary>
        public string? HttpBody { get; set; }
        
        /// <summary>HTTP headers as a concatenated string</summary>
        public string? HttpHeaders { get; set; }
        
        /// <summary>Indicates if this is an HTTP request (true) or response (false)</summary>
        public bool IsHttpRequest { get; set; }
    }
}
