namespace SolutionGrader.Core.Abstractions;

using SolutionGrader.Core.Domain.Models;

public interface IReportService
{
    System.Threading.Tasks.Task<string> WriteQuestionResultAsync(string outFolder, string questionCode, System.Collections.Generic.IReadOnlyList<StepResult> steps, System.Threading.CancellationToken ct);
}
