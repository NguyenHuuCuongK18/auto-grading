namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Service for modifying compiled DLL files to replace hardcoded IP addresses and ports.
/// This is used as a fallback when appsettings.json files are not found in student submissions.
/// Supports both local and Docker container scenarios with appropriate IP address handling.
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
    /// Attempts to modify DLL files with Docker-aware IP address handling.
    /// For server DLLs: replaces localhost with 0.0.0.0 (bind to all interfaces)
    /// For client DLLs: replaces localhost with host.docker.internal (connect to host)
    /// </summary>
    /// <param name="dllDirectory">Directory containing DLL files to modify</param>
    /// <param name="targetIp">Target IP address for the specific role (server: 0.0.0.0, client: host.docker.internal)</param>
    /// <param name="targetPort">Target port number for the grading environment</param>
    /// <param name="isServer">True if modifying server DLLs (bind address), false for client DLLs (connect address)</param>
    /// <returns>True if any modifications were made successfully, false otherwise</returns>
    bool TryModifyDllsForDocker(string dllDirectory, string targetIp, int targetPort, bool isServer);
    
    /// <summary>
    /// Gets a list of common localhost patterns that should be replaced during DLL modification
    /// </summary>
    string[] GetCommonLocalhostPatterns();
    
    /// <summary>
    /// Gets a list of common ports that should be replaced during DLL modification
    /// </summary>
    int[] GetCommonPorts();
}
