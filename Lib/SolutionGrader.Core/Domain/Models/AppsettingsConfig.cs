namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Configuration for generating appsettings.json files
/// </summary>
public sealed class AppsettingsConfig
{
    /// <summary>
    /// IP address for the server (http://localhost or 127.0.0.1)
    /// </summary>
    public required string IPAddress { get; init; }

    /// <summary>
    /// Port number for the service
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Database connection string (server only)
    /// </summary>
    public string? ConnectionString { get; init; }
}
