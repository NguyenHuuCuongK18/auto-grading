using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Service for generating appsettings.json files for client and server applications.
/// Client connects DIRECTLY to server - NO proxy or middleware involved.
/// </summary>
public interface IAppsettingsCreationService
{
    /// <summary>
    /// Generates client and server appsettings.json files based on database configuration
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json will be generated in same directory)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json will be generated in same directory)</param>
    /// <returns>Tuple containing client port and server port (both are the same - client connects directly to server)</returns>
    (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath);

    /// <summary>
    /// Generates client and server appsettings.json files based on database and environment configuration
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json will be generated in same directory)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json will be generated in same directory)</param>
    /// <param name="envConfig">Environment configuration from environment.xlsx (optional)</param>
    /// <returns>Tuple containing client port and server port (both are the same - client connects directly to server)</returns>
    (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig);

    /// <summary>
    /// Generates client and server appsettings.json files based on database, environment, and protocol configuration
    /// </summary>
    /// <param name="dbConfig">Database configuration from Header.xlsx</param>
    /// <param name="clientExePath">Path to client executable (appsettings.json will be generated in same directory)</param>
    /// <param name="serverExePath">Path to server executable (appsettings.json will be generated in same directory)</param>
    /// <param name="envConfig">Environment configuration from environment.xlsx (optional)</param>
    /// <param name="protocol">Network protocol (TCP, HTTP, Console)</param>
    /// <returns>Tuple containing client port and server port (both are the same - client connects directly to server)</returns>
    (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig, string? protocol);

    /// <summary>
    /// Gets the currently allocated ports
    /// </summary>
    (int ClientPort, int ServerPort) GetPorts();
}
