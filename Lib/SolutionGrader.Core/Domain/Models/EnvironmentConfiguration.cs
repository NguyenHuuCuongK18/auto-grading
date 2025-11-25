namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Configuration from environment.xlsx files.
/// 
/// NOTE: The new test kit format no longer uses separate middleware and server ports.
/// Instead, a single MonitorPort is used where:
/// - Server listens on this port
/// - Client connects to this port  
/// - Network monitor sniffs traffic on this port
/// 
/// The old MiddlewarePort and ServerPort properties are kept for backward compatibility
/// but MonitorPort should be preferred for new test kits.
/// </summary>
public sealed class EnvironmentConfiguration
{
    /// <summary>
    /// Single port used by both client and server for direct communication.
    /// The network monitor sniffs traffic on this port.
    /// This replaces the old middleware proxy approach.
    /// </summary>
    public int? MonitorPort { get; set; }
    
    /// <summary>
    /// [DEPRECATED] Port for middleware/proxy (Code_Container_Internal_Port).
    /// Kept for backward compatibility with old test kits.
    /// Use MonitorPort instead for new test kits.
    /// </summary>
    [Obsolete("Use MonitorPort instead. Middleware proxy has been replaced by network monitoring.")]
    public int? MiddlewarePort { get; set; }

    /// <summary>
    /// [DEPRECATED] Port for server (Code_Container_Host_Port).
    /// Kept for backward compatibility with old test kits.
    /// Use MonitorPort instead for new test kits.
    /// </summary>
    [Obsolete("Use MonitorPort instead. Server port is now the same as MonitorPort.")]
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
    public bool StopGradingIfResetFails { get; set; } = false;
}
