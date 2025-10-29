namespace SolutionGrader.Core.Abstractions;

using SolutionGrader.Core.Domain.Models;

public interface IExecutor
{
    System.Threading.Tasks.Task<(bool ok, string message)> ExecuteAsync(Step step, ExecuteSuiteArgs args, System.Threading.CancellationToken ct);
}
