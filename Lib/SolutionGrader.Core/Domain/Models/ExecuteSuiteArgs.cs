namespace SolutionGrader.Core.Domain.Models;

public sealed class ExecuteSuiteArgs
{
    public required string SuitePath { get; init; }   // folder or Header.xlsx
    public required string ResultRoot { get; init; }  // output root

    public string Protocol { get; set; } = "HTTP";    // set from header

    // Optional: Override executables from environment.xlsx Meta/Given folder
    public string? ClientExePath { get; init; }
    public string? ServerExePath { get; init; }
}
