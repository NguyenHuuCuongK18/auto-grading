namespace SolutionGrader.Core.Domain.Models;

public sealed class TestCaseDefinition
{
    public required string Name { get; init; }
    public required double Mark { get; init; }
    public required string DirectoryPath { get; init; }
    public required string DetailPath { get; init; }
    public string? InnerHeaderPath { get; init; }
}
