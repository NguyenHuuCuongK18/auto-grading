namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Configuration from environment.xlsx files
/// </summary>
public sealed class EnvironmentConfiguration
{
    /// <summary>
    /// Port for middleware/proxy (Code_Container_Internal_Port)
    /// </summary>
    public int? MiddlewarePort { get; set; }

    /// <summary>
    /// Port for server (Code_Container_Host_Port)
    /// </summary>
    public int? ServerPort { get; set; }

    /// <summary>
    /// Path to reference/given server executable
    /// </summary>
    public string? GivenServerPath { get; set; }

    /// <summary>
    /// Path to reference/given client executable
    /// </summary>
    public string? GivenClientPath { get; set; }

    /// <summary>
    /// Database file path from environment
    /// </summary>
    public string? DatabaseFilePath { get; set; }

    /// <summary>
    /// Database name from environment
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Database username from environment
    /// </summary>
    public string? DatabaseUsername { get; set; }

    /// <summary>
    /// Database password from environment
    /// </summary>
    public string? DatabasePassword { get; set; }

    /// <summary>
    /// Stop grading if database reset fails (default: true)
    /// </summary>
    public bool StopGradingIfResetFails { get; set; } = true;
}
