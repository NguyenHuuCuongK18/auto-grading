namespace SolutionGrader.Core.Domain.Models;

public sealed class TestCaseDefinition
{
    public required string Name { get; init; }
    public required double Mark { get; init; }
    public required string DirectoryPath { get; init; }
    public required string DetailPath { get; init; }
    public string? InnerHeaderPath { get; init; }
    
    /// <summary>
    /// Grade content indicator: "Client", "Server", or null (grade both)
    /// </summary>
    public string? GradeContent { get; init; }
    
    /// <summary>
    /// Environment configuration for this test case
    /// </summary>
    public EnvironmentConfiguration? Environment { get; init; }
}
