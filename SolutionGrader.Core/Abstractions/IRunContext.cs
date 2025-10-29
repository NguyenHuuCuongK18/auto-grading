namespace SolutionGrader.Core.Abstractions
{
    public interface IRunContext
    {
        string ResultRoot { get; set; }
        string? CurrentQuestionCode { get; set; }
        int? CurrentStage { get; set; }
    }
}
