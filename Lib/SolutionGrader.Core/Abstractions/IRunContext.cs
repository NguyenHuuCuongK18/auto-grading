namespace SolutionGrader.Core.Abstractions
{
    public interface IRunContext
    {
        string ResultRoot { get; set; }
        string? CurrentQuestionCode { get; set; }
        int? CurrentStage { get; set; }
        string? CurrentStageLabel { get; set; }
        string? ResolveServerExecutable();

        string GetClientCaptureKey(string questionCode, string stage);
        string GetServerCaptureKey(string questionCode, string stage);

        void AppendClientOutput(string questionCode, string stage, string content);
        void AppendServerOutput(string questionCode, string stage, string content);
        void SetClientOutput(string questionCode, string stage, string content);
        void SetServerOutput(string questionCode, string stage, string content);

        bool TryGetCapturedOutput(string captureKey, out string? content);
    }
}
