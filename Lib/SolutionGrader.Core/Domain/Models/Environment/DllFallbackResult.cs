namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Result of a comprehensive DLL fallback operation.
/// Contains information about appsettings existence, DLL modification, and results.
/// </summary>
public class DllFallbackResult
{
    /// <summary>
    /// Path to the directory that was checked.
    /// </summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Name of the project being processed.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// True if this is a server component, false if client.
    /// </summary>
    public bool IsServer { get; set; }

    /// <summary>
    /// The target port that was configured.
    /// </summary>
    public int TargetPort { get; set; }
    
    /// <summary>
    /// The target IP address/hostname that was configured.
    /// For servers: typically "0.0.0.0" (bind all interfaces)
    /// For clients: "host.docker.internal" (legacy) or server container name (internal networking)
    /// </summary>
    public string? TargetIp { get; set; }

    /// <summary>
    /// True if appsettings.json exists in the directory.
    /// </summary>
    public bool AppsettingsExists { get; set; }

    /// <summary>
    /// True if DLL modification was required (appsettings.json not found).
    /// </summary>
    public bool RequiresDllModification { get; set; }

    /// <summary>
    /// Path to the DLL file that was modified (if applicable).
    /// </summary>
    public string? DllPath { get; set; }

    /// <summary>
    /// True if the operation succeeded (either appsettings found or DLL successfully modified).
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Number of IP address replacements made in the DLL.
    /// </summary>
    public int IpReplacements { get; set; }

    /// <summary>
    /// Number of port replacements made in the DLL.
    /// </summary>
    public int PortReplacements { get; set; }

    /// <summary>
    /// Human-readable message describing the result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Formatted summary of the operation for logging.
    /// </summary>
    public string GetSummary()
    {
        if (!RequiresDllModification)
        {
            return $"[{ProjectName}] appsettings.json found - no modification needed";
        }

        if (!Success)
        {
            return $"[{ProjectName}] DLL modification failed: {Message}";
        }

        var componentType = IsServer ? "Server" : "Client";
        return $"[{ProjectName}] {componentType} DLL modified successfully - " +
               $"{IpReplacements} IP replacements, {PortReplacements} port replacements - " +
               $"Target port: {TargetPort}";
    }
}
