using System;
using System.IO;
using System.Text.Json;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Helpers;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service for generating appsettings.json files for client and server applications.
/// NOTE: Client connects directly to server - NO proxy or middleware involved.
/// </summary>
public sealed class AppsettingsCreationService : IAppsettingsCreationService
{
    private int _clientPort;
    private int _serverPort;
    private readonly GradingConfig _gradingConfig;

    public AppsettingsCreationService() : this(GradingConfig.Default)
    {
    }

    public AppsettingsCreationService(GradingConfig gradingConfig)
    {
        _gradingConfig = gradingConfig ?? GradingConfig.Default;
    }

    public (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath)
    {
        return GenerateAppsettings(dbConfig, clientExePath, serverExePath, null, null);
    }

    public (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig)
    {
        return GenerateAppsettings(dbConfig, clientExePath, serverExePath, envConfig, null);
    }

    public (int ClientPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig, string? protocol)
    {
        _serverPort = _gradingConfig.GraderPort;
        _clientPort = _gradingConfig.GraderPort; // Client connects to same port as server
        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} Using GraderPort from config: {_serverPort}");

        var ipAddress = DetermineIpAddress(protocol ?? dbConfig?.Type ?? AppsettingKeywords.PROTOCOL_HTTP);

        if (!string.IsNullOrEmpty(serverExePath) && File.Exists(serverExePath))
        {
            var serverDir = Path.GetDirectoryName(serverExePath);
            if (!string.IsNullOrEmpty(serverDir))
            {
                var serverAppsettingsPath = Path.Combine(serverDir, FileKeywords.FileName_AppSettings);
                var serverConfig = CreateServerAppsettings(ipAddress, _serverPort, dbConfig, envConfig);
                File.WriteAllText(serverAppsettingsPath, JsonSerializer.Serialize(serverConfig, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_GENERATED_SERVER_APPSETTINGS, serverAppsettingsPath)}");
            }
        }

        if (!string.IsNullOrEmpty(clientExePath) && File.Exists(clientExePath))
        {
            var clientDir = Path.GetDirectoryName(clientExePath);
            if (!string.IsNullOrEmpty(clientDir))
            {
                var clientAppsettingsPath = Path.Combine(clientDir, FileKeywords.FileName_AppSettings);
                var clientConfig = CreateClientAppsettings(ipAddress, _clientPort);
                File.WriteAllText(clientAppsettingsPath, JsonSerializer.Serialize(clientConfig, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_GENERATED_CLIENT_APPSETTINGS, clientAppsettingsPath)}");
            }
        }

        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_ALLOCATED_PORTS, _clientPort, _serverPort)}");
        return (_clientPort, _serverPort);
    }

    public (int ClientPort, int ServerPort) GetPorts()
    {
        return (_clientPort, _serverPort);
    }

    private static string DetermineIpAddress(string type)
    {
        return type.Equals(AppsettingKeywords.PROTOCOL_TCP, StringComparison.OrdinalIgnoreCase) 
            ? AppsettingKeywords.TCP_LOCALHOST 
            : AppsettingKeywords.HTTP_LOCALHOST;
    }

    private static object CreateServerAppsettings(string ipAddress, int port, DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        var connectionString = ConnectionStringHelper.Build(dbConfig, envConfig);
        
        return new
        {
            ConnectionStrings = new { MyCnn = connectionString },
            IpAddress = ipAddress,
            Port = port.ToString()
        };
    }

    private static object CreateClientAppsettings(string ipAddress, int port)
    {
        return new
        {
            IpAddress = ipAddress,
            Port = port.ToString()
        };
    }
}
