namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Represents a discovered student with all metadata.
/// Shared data structure for CLI and UI.
/// </summary>
public class DiscoveredStudent
{
    public string StudentCode { get; set; } = "";
    public string PaperNo { get; set; } = "";
    public string SolutionPath { get; set; } = "";
    public string? ServerDllPath { get; set; }
    public string? ClientDllPath { get; set; }
    
    /// <summary>
    /// Warning message for issues detected during discovery.
    /// This is displayed in the UI Message column to notify users about problems
    /// like missing question folder (/1) or missing DLL files.
    /// </summary>
    public string? WarningMessage { get; set; }
}
