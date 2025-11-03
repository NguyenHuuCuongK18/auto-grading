using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services;

public sealed class AppsettingsCreationService : IAppsettingsCreationService
{
    private int _proxyPort;
    private int _serverPort;

    public (int ProxyPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath)
    {
        // Allocate random available ports, ensuring they're different
        _proxyPort = FindAvailablePort();
        do
        {
            _serverPort = FindAvailablePort();
        } while (_serverPort == _proxyPort);

        // Determine IP address based on Type
        var ipAddress = DetermineIpAddress(dbConfig?.Type ?? "HTTP");

        // Generate server appsettings.json if server path provided
        if (!string.IsNullOrEmpty(serverExePath) && File.Exists(serverExePath))
        {
            var serverDir = Path.GetDirectoryName(serverExePath);
            if (!string.IsNullOrEmpty(serverDir))
            {
                var serverAppsettingsPath = Path.Combine(serverDir, "appsettings.json");
                var serverConfig = CreateServerAppsettings(ipAddress, _serverPort, dbConfig);
                File.WriteAllText(serverAppsettingsPath, JsonSerializer.Serialize(serverConfig, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                }));
                Console.WriteLine($"[AppsettingsCreation] Generated server appsettings.json at: {serverAppsettingsPath}");
            }
        }

        // Generate client appsettings.json if client path provided
        if (!string.IsNullOrEmpty(clientExePath) && File.Exists(clientExePath))
        {
            var clientDir = Path.GetDirectoryName(clientExePath);
            if (!string.IsNullOrEmpty(clientDir))
            {
                var clientAppsettingsPath = Path.Combine(clientDir, "appsettings.json");
                var clientConfig = CreateClientAppsettings(ipAddress, _proxyPort);
                File.WriteAllText(clientAppsettingsPath, JsonSerializer.Serialize(clientConfig, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                }));
                Console.WriteLine($"[AppsettingsCreation] Generated client appsettings.json at: {clientAppsettingsPath}");
            }
        }

        Console.WriteLine($"[AppsettingsCreation] Allocated ports - Proxy: {_proxyPort}, Server: {_serverPort}");
        return (_proxyPort, _serverPort);
    }

    public (int ProxyPort, int ServerPort) GetPorts()
    {
        return (_proxyPort, _serverPort);
    }

    private static string DetermineIpAddress(string type)
    {
        // HTTP uses localhost, TCP uses 127.0.0.1
        return type.Equals("HTTP", StringComparison.OrdinalIgnoreCase) 
            ? "http://localhost" 
            : "127.0.0.1";
    }

    private static int FindAvailablePort()
    {
        // Use ephemeral port range (49152-65535) which is safer for dynamic allocation
        const int maxAttempts = 100;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Generate random port in ephemeral range
            // Use Random.Shared for better randomization across rapid calls
            int port = Random.Shared.Next(49152, 65536);

            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        // Fallback: let the OS assign a port
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int assignedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return assignedPort;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static object CreateServerAppsettings(string ipAddress, int port, DatabaseConfiguration? dbConfig)
    {
        var connectionString = BuildConnectionString(dbConfig);
        
        return new
        {
            ConnectionStrings = new
            {
                MyCnn = connectionString
            },
            IPAddress = ipAddress,
            Port = port.ToString()
        };
    }

    private static object CreateClientAppsettings(string ipAddress, int port)
    {
        return new
        {
            IPAddress = ipAddress,
            Port = port.ToString()
        };
    }

    private static string BuildConnectionString(DatabaseConfiguration? dbConfig)
    {
        if (dbConfig == null)
        {
            // Default connection string
            return "server=.\\SQLEXPRESS;database=Library;uid=sa;pwd=sa;TrustServerCertificate=True;";
        }

        var server = dbConfig.SqlServer ?? "SQLEXPRESS";
        // Format SQL Server instance name properly
        if (!server.StartsWith(".\\") && !server.Contains("\\") && !server.Equals("(local)", StringComparison.OrdinalIgnoreCase))
        {
            server = $".\\{server}";
        }

        var database = dbConfig.Database ?? "Library";
        var username = dbConfig.Username ?? "sa";
        var password = dbConfig.Password ?? "sa";

        return $"server={server};database={database};uid={username};pwd={password};TrustServerCertificate=True;";
    }
}
