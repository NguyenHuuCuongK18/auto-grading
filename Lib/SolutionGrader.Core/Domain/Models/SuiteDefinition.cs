namespace SolutionGrader.Core.Domain.Models;

public sealed class SuiteDefinition
{
    public required string HeaderPath { get; init; }
    public required string Protocol { get; init; }
    public required System.Collections.Generic.IReadOnlyList<TestCaseDefinition> Cases { get; init; }
    public DatabaseConfiguration? DatabaseConfig { get; init; }
    public EnvironmentConfiguration? Environment { get; init; }
    public string? DateTimeFormat { get; init; }
    public global::Domain.Entities.Main.Environment? DomainEnvironment { get; init; }
    public string RootDirectory => System.IO.Path.GetDirectoryName(HeaderPath)!;
}
