namespace SolutionGrader.Core.Domain.Models;

public sealed class ExecuteSuiteArgs
{
    public required string SuitePath { get; init; }   // folder or Header.xlsx
    public required string ResultRoot { get; init; }  // output root

    public string Protocol { get; set; } = "HTTP";    // set from header

    // Optional: Override executables from environment.xlsx Meta/Given folder
    public string? ClientExePath { get; init; }
    public string? ServerExePath { get; init; }
    
    /// <summary>
    /// Use Docker for database operations instead of local SQL Server
    /// When true, database reset operations will be executed via Docker container
    /// </summary>
    public bool UseDocker { get; init; } = false;
}
