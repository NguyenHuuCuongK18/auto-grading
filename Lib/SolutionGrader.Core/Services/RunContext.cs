using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed class RunContext : IRunContext
    {
        public string ResultRoot { get; set; } = "";
        public string? CurrentQuestionCode { get; set; }
        public int? CurrentStage { get; set; }

        private string? _serverExecutablePath;

        public void SetServerExecutable(string? path) => _serverExecutablePath = path;

        public string? ResolveServerExecutable() => _serverExecutablePath;
        public string? CurrentStageLabel { get; set; }

        private const string MemoryScheme = "memory://";

        private readonly ConcurrentDictionary<string, StringBuilder> _captures = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (string HttpMethod, int StatusCode, int ByteSize)> _httpMetadata = new(StringComparer.OrdinalIgnoreCase);

        public string GetClientCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_Clients, questionCode, stage);

        public string GetServerCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_Servers, questionCode, stage);

        public string GetServerRequestCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_ServersRequest, questionCode, stage);

        public string GetServerResponseCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_ServersResponse, questionCode, stage);

        public void AppendClientOutput(string questionCode, string stage, string content)
            => AppendCapture(FileKeywords.Folder_Clients, questionCode, stage, content);

        public void AppendServerOutput(string questionCode, string stage, string content)
            => AppendCapture(FileKeywords.Folder_Servers, questionCode, stage, content);

        public void SetClientOutput(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_Clients, questionCode, stage, content);

        public void SetServerOutput(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_Servers, questionCode, stage, content);

        public void SetServerRequest(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_ServersRequest, questionCode, stage, content);

        public void SetServerResponse(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_ServersResponse, questionCode, stage, content);

        public bool TryGetCapturedOutput(string captureKey, out string? content)
        {
            if (_captures.TryGetValue(captureKey, out var builder))
            {
                content = builder.ToString();
                return true;
            }

            content = null;
            return false;
        }

        public void SetHttpMetadata(string questionCode, string stage, string httpMethod, int statusCode, int byteSize)
        {
            var key = $"{questionCode}-{stage}";
            _httpMetadata[key] = (httpMethod, statusCode, byteSize);
        }

        public bool TryGetHttpMetadata(string questionCode, string stage, out string? httpMethod, out int? statusCode, out int? byteSize)
        {
            var key = $"{questionCode}-{stage}";
            if (_httpMetadata.TryGetValue(key, out var metadata))
            {
                httpMethod = metadata.HttpMethod;
                statusCode = metadata.StatusCode;
                byteSize = metadata.ByteSize;
                return true;
            }

            httpMethod = null;
            statusCode = null;
            byteSize = null;
            return false;
        }

        private void AppendCapture(string scope, string questionCode, string stage, string content)
        {
            var key = BuildKey(scope, questionCode, stage);
            var builder = _captures.GetOrAdd(key, _ => new StringBuilder());
            builder.Append(content);
        }

        private void SetCapture(string scope, string questionCode, string stage, string content)
        {
            var key = BuildKey(scope, questionCode, stage);
            var builder = new StringBuilder();
            builder.Append(content);
            _captures[key] = builder;
        }

        // Removed: ResolveActualServerText - no longer using txt files for actual outputs
        // Actual outputs are now stored in memory only and included in Excel reports

        private string BuildKey(string scope, string questionCode, string stage)
        {
            var normalizedQuestion = string.IsNullOrWhiteSpace(questionCode)
                ? FileKeywords.Value_UnknownQuestion
                : questionCode;

            var normalizedStage = string.IsNullOrWhiteSpace(stage)
                ? "0"
                : stage;

            return $"{MemoryScheme}{scope}/{normalizedQuestion}/{normalizedStage}";
        }
    }
}
