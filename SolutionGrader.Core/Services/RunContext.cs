using SolutionGrader.Core.Abstractions;

namespace SolutionGrader.Core.Services
{
    public sealed class RunContext : IRunContext
    {
        public string ResultRoot { get; set; } = "";
        public string? CurrentQuestionCode { get; set; }
        public int? CurrentStage { get; set; }
    }
}
