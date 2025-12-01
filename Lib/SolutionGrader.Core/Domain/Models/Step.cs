namespace SolutionGrader.Core.Domain.Models;

public sealed class Step
{
    public required string Id { get; init; }
    public required string QuestionCode { get; init; }
    public required string Stage { get; init; }
    public required string Action { get; init; }
    public string? Target { get; init; }
    public string? Value { get; init; }
    
    // Extended properties for comprehensive validation
    public string? HttpMethod { get; init; }
    public string? StatusCode { get; init; }
    public int? ByteSize { get; init; }
    public string? DataType { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public sealed class StepResult
{
    public required Step Step { get; init; }
    public required bool Passed { get; init; }
    public required string Message { get; init; }
    public required double DurationMs { get; init; }
}
