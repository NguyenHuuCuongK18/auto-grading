using SolutionGrader.Core.Abstractions;

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

        public string ResolveActualClientText(string questionCode, string stage)
        {
            var folder = Path.Combine(ResultRoot, "actual", "clients", questionCode);
            return Path.Combine(folder, $"{stage}.txt");
        }
    }
}
