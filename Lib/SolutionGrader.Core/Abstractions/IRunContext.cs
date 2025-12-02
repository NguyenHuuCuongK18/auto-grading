namespace SolutionGrader.Core.Abstractions
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
        /// <summary>Payload data (for PSH packets)</summary>
        public string? Data { get; set; }
        /// <summary>Source port</summary>
        public int SourcePort { get; set; }
        /// <summary>Destination port</summary>
        public int DestinationPort { get; set; }
    }
    
    public interface IRunContext
    {
        string ResultRoot { get; set; }
        string? CurrentQuestionCode { get; set; }
        int? CurrentStage { get; set; }
        string? CurrentStageLabel { get; set; }
        string? DateTimeFormat { get; set; }
        string? ResolveServerExecutable();

        string GetClientCaptureKey(string questionCode, string stage);
        string GetServerCaptureKey(string questionCode, string stage);
        string GetServerRequestCaptureKey(string questionCode, string stage);
        string GetServerResponseCaptureKey(string questionCode, string stage);

        void AppendClientOutput(string questionCode, string stage, string content);
        void AppendServerOutput(string questionCode, string stage, string content);
        void SetClientOutput(string questionCode, string stage, string content);
        void SetServerOutput(string questionCode, string stage, string content);
        void SetServerRequest(string questionCode, string stage, string content);
        void SetServerResponse(string questionCode, string stage, string content);
        
        /// <summary>
        /// Sets captured output for a custom key (e.g., network.{stage}.req.body).
        /// Used for storing network packet data for comparison.
        /// </summary>
        void SetCapturedOutput(string captureKey, string content);
        
        // HTTP metadata capture
        void SetHttpMetadata(string questionCode, string stage, string httpMethod, int statusCode, int byteSize);
        bool TryGetHttpMetadata(string questionCode, string stage, out string? httpMethod, out int? statusCode, out int? byteSize);

        bool TryGetCapturedOutput(string captureKey, out string? content);
        
        /// <summary>
        /// Clears all captured network data and HTTP metadata.
        /// Used to flush health check traffic before executing actual test steps.
        /// </summary>
        void ClearNetworkCaptures();
        
        /// <summary>
        /// Adds a captured network packet to the list for the current stage.
        /// Used for grading TCP handshake and connection lifecycle.
        /// </summary>
        void AddCapturedNetworkPacket(string questionCode, string stage, CapturedNetworkPacket packet);
        
        /// <summary>
        /// Gets all captured network packets for a specific stage.
        /// Returns an empty list if no packets were captured.
        /// </summary>
        IReadOnlyList<CapturedNetworkPacket> GetCapturedNetworkPackets(string questionCode, string stage);
        
        /// <summary>
        /// Gets ALL captured network packets across all stages.
        /// Used when you need to retrieve all packets regardless of context.
        /// </summary>
        IReadOnlyList<CapturedNetworkPacket> GetAllCapturedNetworkPackets();
    }
}
