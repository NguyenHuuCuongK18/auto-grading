using System.Text.RegularExpressions;

namespace SolutionGrader.Core.Helpers;

/// <summary>
/// Helper for generating valid Docker container names from student codes.
/// Docker container names must match [a-zA-Z0-9][a-zA-Z0-9_.-]+ (cannot contain spaces or special chars).
/// </summary>
public static class ContainerNameHelper
{
    /// <summary>
    /// Pattern for valid Docker container name characters.
    /// Docker allows alphanumeric, underscore, period, and hyphen (but not at the start).
    /// </summary>
    private static readonly Regex InvalidCharsRegex = new(@"[^a-zA-Z0-9_.-]", RegexOptions.Compiled);
    
    /// <summary>
    /// Pattern for valid starting character (must be alphanumeric).
    /// </summary>
    private static readonly Regex InvalidStartCharRegex = new(@"^[^a-zA-Z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a student code to be used as part of a Docker container name.
    /// Replaces invalid characters (like spaces) with underscores.
    /// </summary>
    /// <param name="studentCode">The original student code (may contain spaces, special chars, etc.)</param>
    /// <returns>A sanitized string safe for use in Docker container names</returns>
    public static string SanitizeForContainerName(string studentCode)
    {
        if (string.IsNullOrWhiteSpace(studentCode))
        {
            return "unknown";
        }

        // Replace invalid characters with underscores
        var sanitized = InvalidCharsRegex.Replace(studentCode, "_");
        
        // Ensure the name starts with an alphanumeric character
        sanitized = InvalidStartCharRegex.Replace(sanitized, "");
        
        // If after sanitization the string is empty, use a default
        if (string.IsNullOrEmpty(sanitized))
        {
            return "unknown";
        }

        // Docker container names have a maximum length of 128 characters
        // Since we prepend "ag-unified-" (11 chars) or "ag-monitor-" (11 chars), 
        // limit the sanitized code to 100 chars to be safe
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }

        return sanitized;
    }

    /// <summary>
    /// Builds a unified container name for a student.
    /// </summary>
    /// <param name="studentCode">The student code (will be sanitized)</param>
    /// <returns>Container name in format "ag-unified-{sanitizedCode}"</returns>
    public static string BuildUnifiedContainerName(string studentCode)
    {
        return $"ag-unified-{SanitizeForContainerName(studentCode)}";
    }

    /// <summary>
    /// Builds a monitor container name for a student.
    /// </summary>
    /// <param name="studentCode">The student code (will be sanitized)</param>
    /// <returns>Container name in format "ag-monitor-{sanitizedCode}"</returns>
    public static string BuildMonitorContainerName(string studentCode)
    {
        return $"ag-monitor-{SanitizeForContainerName(studentCode)}";
    }
}
