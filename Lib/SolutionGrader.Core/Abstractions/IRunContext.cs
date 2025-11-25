namespace SolutionGrader.Core.Abstractions
{
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
    }
}
