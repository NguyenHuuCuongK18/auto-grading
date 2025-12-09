using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Service for modifying existing appsettings.json files for client and server applications.
/// Unlike IAppsettingsCreationService which generates new files, this service:
/// 1. Reads existing appsettings.json
/// 2. Modifies only specific values (Port, IpAddress, ConnectionString)
/// 3. Preserves all other settings (logging config, custom settings, etc.)
/// 
/// Returns Success flag indicating whether appsettings were found and modified.
/// If false, caller should use DLL modification fallback (if enabled).
/// </summary>
public interface IAppsettingsModificationService
{
    /// <summary>
    /// Modifies existing client and server appsettings.json files based on database configuration.
    /// Returns (Success, ClientPort, ServerPort) where Success indicates if modification succeeded.
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json in same directory will be modified)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json in same directory will be modified)</param>
    /// <returns>Tuple containing success flag, client port, and server port</returns>
    (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath);

    /// <summary>
    /// Modifies existing client and server appsettings.json files based on database and environment configuration.
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json in same directory will be modified)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json in same directory will be modified)</param>
    /// <param name="envConfig">Environment configuration from environment.xlsx (optional)</param>
    /// <returns>Tuple containing success flag, client port, and server port</returns>
    (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath, 
        EnvironmentConfiguration? envConfig);

    /// <summary>
    /// Modifies existing client and server appsettings.json files based on database, environment, and protocol configuration.
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json in same directory will be modified)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json in same directory will be modified)</param>
    /// <param name="envConfig">Environment configuration from environment.xlsx (optional)</param>
    /// <param name="protocol">Network protocol (TCP, HTTP, Console)</param>
    /// <returns>Tuple containing success flag, client port, and server port</returns>
    (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath, 
        EnvironmentConfiguration? envConfig, 
        string? protocol);

    /// <summary>
    /// Gets the currently allocated ports.
    /// </summary>
    (int ClientPort, int ServerPort) GetPorts();
}
