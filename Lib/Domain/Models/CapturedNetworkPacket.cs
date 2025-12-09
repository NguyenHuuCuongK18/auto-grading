using System;

namespace Domain.Models
{
    /// <summary>
    /// Represents a captured network packet with TCP flow information.
    /// Used for grading TCP handshake and connection lifecycle.
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
        /// <summary>Protocol (typically TCP)</summary>
        public string Protocol { get; set; } = "";
        /// <summary>Packet length in bytes</summary>
        public int Length { get; set; }
        /// <summary>Additional information</summary>
        public string Info { get; set; } = "";
        /// <summary>Payload data (for PSH packets)</summary>
        public string? Data { get; set; }
        /// <summary>Source port</summary>
        public int SourcePort { get; set; }
        /// <summary>Destination port</summary>
        public int DestinationPort { get; set; }
    }
}
