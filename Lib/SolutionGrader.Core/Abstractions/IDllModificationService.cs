namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Service for modifying compiled DLL files to replace hardcoded IP addresses and ports.
/// This is used as a fallback when appsettings.json files are not found in student submissions.
/// </summary>
public interface IDllModificationService
{
    /// <summary>
    /// Attempts to modify DLL files in the specified directory to replace hardcoded
    /// localhost references and common ports with grading environment values.
    /// </summary>
    /// <param name="dllDirectory">Directory containing DLL files to modify</param>
    /// <param name="targetIp">Target IP address to use (e.g., "http://localhost" or "127.0.0.1")</param>
    /// <param name="targetPort">Target port number for the grading environment</param>
    /// <returns>True if any modifications were made successfully, false otherwise</returns>
    bool TryModifyDlls(string dllDirectory, string targetIp, int targetPort);
    
    /// <summary>
    /// Gets a list of common localhost patterns that should be replaced during DLL modification
    /// </summary>
    string[] GetCommonLocalhostPatterns();
    
    /// <summary>
    /// Gets a list of common ports that should be replaced during DLL modification
    /// </summary>
    int[] GetCommonPorts();
}
