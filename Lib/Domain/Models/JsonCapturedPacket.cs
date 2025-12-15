namespace Domain.Models
{
    /// <summary>
    /// JSON structure matching the sidecar's CapturedPacket output.
    /// Property names use camelCase to match JSON serialization.
    /// Used by JsonPacketParsingService to deserialize network capture data.
    /// </summary>
    public class JsonCapturedPacket
    {
        public string Timestamp { get; set; } = "";
        public string SourceIp { get; set; } = "";
        public int SourcePort { get; set; }
        public string DestinationIp { get; set; } = "";
        public int DestinationPort { get; set; }
        public string SourceRole { get; set; } = "";
        public string DestinationRole { get; set; } = "";
        public string Flags { get; set; } = "";
        public string State { get; set; } = "";
        public string? Data { get; set; }
        public int PayloadLength { get; set; }
    }
}
