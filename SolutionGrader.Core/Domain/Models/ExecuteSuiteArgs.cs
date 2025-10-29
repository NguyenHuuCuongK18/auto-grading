namespace SolutionGrader.Core.Domain.Models;

public sealed class ExecuteSuiteArgs
{
    public required string SuitePath { get; init; }   // folder or Header.xlsx
    public required string ResultRoot { get; init; }  // output root

    public string Protocol { get; set; } = "HTTP";    // set from header

    public string? ClientExePath { get; init; }
    public string? ServerExePath { get; init; }
    public string? ClientAppSettingsTemplate { get; init; }
    public string? ServerAppSettingsTemplate { get; init; }
    public string? DatabaseScriptPath { get; init; }

    public int StageTimeoutSeconds { get; init; } = 10;
}
