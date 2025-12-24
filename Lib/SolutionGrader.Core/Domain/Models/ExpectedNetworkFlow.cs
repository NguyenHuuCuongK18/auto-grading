namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Represents expected network flow data read from Detail.xlsx.
/// Supports both TCP and HTTP protocols.
/// For TCP: Validates Flags, State, Data, SourceRole, DestinationRole
/// For HTTP: Validates Flags, State, URI, Method, Status, HttpVersion, HttpBody, SourceRole, DestinationRole
/// </summary>
public class ExpectedNetworkFlow
{
    public int Stage { get; set; }

    // Common fields (TCP and HTTP)
    public string? Flags { get; set; }
    public string? State { get; set; }
    public string? SourceRole { get; set; }
    public string? DestinationRole { get; set; }
    public string? Data { get; set; }  // For TCP payload data

    // HTTP-specific fields
    public string? URI { get; set; }
    public string? Method { get; set; }
    public string? Status { get; set; }
    public string? HttpVersion { get; set; }
    public string? HttpBody { get; set; }
}
