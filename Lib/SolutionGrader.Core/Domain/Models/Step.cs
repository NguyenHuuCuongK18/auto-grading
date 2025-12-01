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
    
    // Network flow validation properties for TCP handshake grading
    /// <summary>TCP flags expected for this network step (e.g., "SYN", "SYN, ACK", "ACK", "PSH, ACK", "FIN, ACK")</summary>
    public string? TcpFlags { get; init; }
    /// <summary>Connection state description expected for this step (e.g., "Client connecting to server (SYN)")</summary>
    public string? ConnectionState { get; init; }
    /// <summary>Source role expected for this step (Client or Server)</summary>
    public string? SourceRole { get; init; }
    /// <summary>Destination role expected for this step (Client or Server)</summary>
    public string? DestinationRole { get; init; }
    /// <summary>Unique row index within the network sheet for this step (used to match captured packets)</summary>
    public int? NetworkRowIndex { get; init; }
}

public sealed class StepResult
{
    public required Step Step { get; init; }
    public required bool Passed { get; init; }
    public required string Message { get; init; }
    public required double DurationMs { get; init; }
}
