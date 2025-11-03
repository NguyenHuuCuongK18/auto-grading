using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Service for generating appsettings.json files for client and server applications
/// </summary>
public interface IAppsettingsCreationService
{
    /// <summary>
    /// Generates client and server appsettings.json files based on database configuration
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json will be generated in same directory)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json will be generated in same directory)</param>
    /// <returns>Tuple containing proxy port and server port</returns>
    (int ProxyPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath);

    /// <summary>
    /// Gets the currently allocated ports
    /// </summary>
    (int ProxyPort, int ServerPort) GetPorts();
}
