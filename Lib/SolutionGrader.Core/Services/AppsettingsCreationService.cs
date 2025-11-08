using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

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
        var ipAddress = DetermineIpAddress(dbConfig?.Type ?? AppsettingKeywords.PROTOCOL_HTTP);

        // Generate server appsettings.json if server path provided
        if (!string.IsNullOrEmpty(serverExePath) && File.Exists(serverExePath))
        {
            var serverDir = Path.GetDirectoryName(serverExePath);
            if (!string.IsNullOrEmpty(serverDir))
            {
                var serverAppsettingsPath = Path.Combine(serverDir, FileKeywords.FileName_AppSettings);
                var serverConfig = CreateServerAppsettings(ipAddress, _serverPort, dbConfig);
                File.WriteAllText(serverAppsettingsPath, JsonSerializer.Serialize(serverConfig, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                }));
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_GENERATED_SERVER_APPSETTINGS, serverAppsettingsPath)}");
            }
        }

        // Generate client appsettings.json if client path provided
        if (!string.IsNullOrEmpty(clientExePath) && File.Exists(clientExePath))
        {
            var clientDir = Path.GetDirectoryName(clientExePath);
            if (!string.IsNullOrEmpty(clientDir))
            {
                var clientAppsettingsPath = Path.Combine(clientDir, FileKeywords.FileName_AppSettings);
                var clientConfig = CreateClientAppsettings(ipAddress, _proxyPort);
                File.WriteAllText(clientAppsettingsPath, JsonSerializer.Serialize(clientConfig, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                }));
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_GENERATED_CLIENT_APPSETTINGS, clientAppsettingsPath)}");
            }
        }

        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} {string.Format(AppsettingKeywords.MSG_ALLOCATED_PORTS, _proxyPort, _serverPort)}");
        return (_proxyPort, _serverPort);
    }

    public (int ProxyPort, int ServerPort) GetPorts()
    {
        return (_proxyPort, _serverPort);
    }

    private static string DetermineIpAddress(string type)
    {
        // HTTP uses localhost, TCP uses 127.0.0.1
        return type.Equals(AppsettingKeywords.PROTOCOL_HTTP, StringComparison.OrdinalIgnoreCase) 
            ? AppsettingKeywords.HTTP_LOCALHOST 
            : AppsettingKeywords.TCP_LOCALHOST;
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

    private static string BuildConnectionString(DatabaseConfiguration? dbConfig)
    {
        if (dbConfig == null)
        {
            // Default connection string using template
            return string.Format(
                AppsettingKeywords.DEFAULT_CONNECTION_STRING_TEMPLATE,
                AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE,
                AppsettingKeywords.DEFAULT_DATABASE_NAME,
                AppsettingKeywords.DEFAULT_USERNAME,
                AppsettingKeywords.DEFAULT_PASSWORD
            );
        }

        var server = dbConfig.SqlServer ?? AppsettingKeywords.SQL_EXPRESS;
        // Format SQL Server instance name properly
        // Only add .\ prefix for named instances (e.g., SQLEXPRESS), not for:
        // - localhost, 127.0.0.1, or hostnames/IPs
        // - Already formatted instances (.\SQLEXPRESS)
        // - (local) keyword
        // - Server with port specification (server:port or server,port)
        if (!server.StartsWith(AppsettingKeywords.SERVER_LOCAL_PREFIX) && 
            !server.Contains("\\") && 
            !server.Equals(AppsettingKeywords.SERVER_LOCAL_KEYWORD, StringComparison.OrdinalIgnoreCase) &&
            !server.Equals(AppsettingKeywords.SERVER_LOCALHOST, StringComparison.OrdinalIgnoreCase) &&
            !server.Contains(":") &&   // Avoid prefixing server with port (server:port)
            !server.Contains(",") &&   // Avoid prefixing server with port (server,port)
            !IPAddress.TryParse(server, out _)) // Don't prefix valid IP addresses
        {
            // Check if it looks like a hostname (contains dots for FQDN or computer.instance format)
            // Only add prefix if it's a simple instance name without dots
            if (!server.Contains("."))
            {
                server = $"{AppsettingKeywords.SERVER_LOCAL_PREFIX}{server}";
            }
        }

        var database = dbConfig.Database ?? AppsettingKeywords.DEFAULT_DATABASE_NAME;
        var username = dbConfig.Username ?? AppsettingKeywords.DEFAULT_USERNAME;
        var password = dbConfig.Password ?? AppsettingKeywords.DEFAULT_PASSWORD;

        // Build connection string using simple template for consistent lowercase formatting
        return string.Format(
            AppsettingKeywords.DEFAULT_CONNECTION_STRING_TEMPLATE,
            server,
            database,
            username,
            password
        );
    }
}
