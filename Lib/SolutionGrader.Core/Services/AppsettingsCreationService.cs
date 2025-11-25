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
    private readonly GradingConfig _gradingConfig;

    public AppsettingsCreationService() : this(GradingConfig.Default)
    {
    }

    public AppsettingsCreationService(GradingConfig gradingConfig)
    {
        _gradingConfig = gradingConfig ?? GradingConfig.Default;
    }

    public (int ProxyPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath)
    {
        return GenerateAppsettings(dbConfig, clientExePath, serverExePath, null, null);
    }

    public (int ProxyPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig)
    {
        return GenerateAppsettings(dbConfig, clientExePath, serverExePath, envConfig, null);
    }

    public (int ProxyPort, int ServerPort) GenerateAppsettings(DatabaseConfiguration? dbConfig, string? clientExePath, string? serverExePath, EnvironmentConfiguration? envConfig, string? protocol)
    {
        // Use GraderPort from GradingConfig - this is THE port for all grading communication
        // Server listens on this port, client connects to this port, network monitor captures on this port
        _serverPort = _gradingConfig.GraderPort;
        _proxyPort = _gradingConfig.GraderPort;
        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_CREATION} Using GraderPort from config: {_serverPort}");

        // Determine IP address based on protocol (TCP vs HTTP/Console), not database Type
        var ipAddress = DetermineIpAddress(protocol ?? dbConfig?.Type ?? AppsettingKeywords.PROTOCOL_HTTP);

        // Generate server appsettings.json if server path provided
        if (!string.IsNullOrEmpty(serverExePath) && File.Exists(serverExePath))
        {
            var serverDir = Path.GetDirectoryName(serverExePath);
            if (!string.IsNullOrEmpty(serverDir))
            {
                var serverAppsettingsPath = Path.Combine(serverDir, FileKeywords.FileName_AppSettings);
                var serverConfig = CreateServerAppsettings(ipAddress, _serverPort, dbConfig, envConfig);
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
        // TCP uses 127.0.0.1, everything else (HTTP, CONSOLE, etc.) uses http://localhost
        // CONSOLE protocol uses HTTP for network communication
        return type.Equals(AppsettingKeywords.PROTOCOL_TCP, StringComparison.OrdinalIgnoreCase) 
            ? AppsettingKeywords.TCP_LOCALHOST 
            : AppsettingKeywords.HTTP_LOCALHOST;
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

    private static object CreateServerAppsettings(string ipAddress, int port, DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        var connectionString = BuildConnectionString(dbConfig, envConfig);
        
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

    private static string BuildConnectionString(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        if (dbConfig == null && envConfig == null)
        {
            // Default connection string based on platform
            // On Windows: use local SQL Server Express
            // On Linux/Mac: use Docker SQL Server
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return $"{AppsettingKeywords.CONN_STR_SERVER}={AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE};{AppsettingKeywords.CONN_STR_DATABASE}={AppsettingKeywords.DEFAULT_DATABASE_NAME};{AppsettingKeywords.CONN_STR_UID}={AppsettingKeywords.DEFAULT_USERNAME};{AppsettingKeywords.CONN_STR_PWD}={AppsettingKeywords.DEFAULT_USERNAME};{AppsettingKeywords.CONN_STR_TRUST_CERT}=true";
            }
            else
            {
                // Docker SQL Server for Linux/Mac
                return $"{AppsettingKeywords.CONN_STR_SERVER}={AppsettingKeywords.DEFAULT_SQL_SERVER_DOCKER};{AppsettingKeywords.CONN_STR_DATABASE}={AppsettingKeywords.DEFAULT_DATABASE_NAME};{AppsettingKeywords.CONN_STR_UID}={AppsettingKeywords.DEFAULT_USERNAME};{AppsettingKeywords.CONN_STR_PWD}={AppsettingKeywords.DOCKER_SA_PASSWORD};{AppsettingKeywords.CONN_STR_TRUST_CERT}=true";
            }
        }

        // Priority order for SQL Server:
        // 1. DatabaseConfiguration.SqlServer (from header.xlsx)
        // 2. Platform default (.\SQLEXPRESS on Windows, localhost,1433 on Linux)
        var server = dbConfig?.SqlServer;
        if (string.IsNullOrWhiteSpace(server))
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                server = AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE;
            }
            else
            {
                // On Linux, use Docker SQL Server with the configured port
                var dbPort = envConfig?.DatabaseHostPort ?? 1433;
                server = $"localhost,{dbPort}";
            }
        }
        
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

        // Priority order for Database:
        // 1. EnvironmentConfiguration.DatabaseName (from environment.xlsx)
        // 2. DatabaseConfiguration.Database (from header.xlsx)
        // 3. Default "Library"
        var database = envConfig?.DatabaseName ?? dbConfig?.Database ?? AppsettingKeywords.DEFAULT_DATABASE_NAME;
        
        // Priority order for Username:
        // 1. EnvironmentConfiguration.DatabaseUsername (from environment.xlsx)
        // 2. DatabaseConfiguration.Username (from header.xlsx)
        // 3. Default "sa"
        var username = envConfig?.DatabaseUsername ?? dbConfig?.Username ?? AppsettingKeywords.DEFAULT_USERNAME;
        
        // Priority order for Password:
        // 1. EnvironmentConfiguration.DatabasePassword (from environment.xlsx)
        // 2. DatabaseConfiguration.Password (from header.xlsx)
        // 3. Platform-specific default:
        //    - Windows: "sa" (local SQL Server often has simpler password requirements)
        //    - Linux/Docker: Strong password (SQL Server 2019+ requires complex password)
        var password = envConfig?.DatabasePassword ?? dbConfig?.Password;
        
        // If no password is specified, use platform-appropriate default
        if (string.IsNullOrWhiteSpace(password))
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                // On Windows local machines, use simple default password
                // as local SQL Server installations may have different password policies
                password = AppsettingKeywords.DEFAULT_PASSWORD;
            }
            else
            {
                // On Linux/Docker, use strong password as SQL Server 2019+ requires complex password
                password = AppsettingKeywords.DOCKER_SA_PASSWORD;
            }
        }

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
