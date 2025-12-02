using System.Net;
using System.Runtime.InteropServices;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Helpers;

/// <summary>
/// Centralized helper for building SQL Server connection strings.
/// Eliminates duplication across AppsettingsCreationService, DockerGradingService, and EnvironmentResetService.
/// </summary>
public static class ConnectionStringHelper
{
    /// <summary>
    /// Builds a connection string for SQL Server using database and environment configuration.
    /// </summary>
    public static string Build(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        if (dbConfig == null && envConfig == null)
        {
            return GetDefaultConnectionString();
        }

        var server = ResolveServerAddress(dbConfig?.SqlServer, envConfig?.DatabaseHostPort);
        var database = ResolveDatabaseName(dbConfig, envConfig);
        var username = ResolveUsername(dbConfig, envConfig);
        var password = ResolvePassword(dbConfig, envConfig);

        return FormatConnectionString(server, database, username, password);
    }

    /// <summary>
    /// Builds a connection string for Docker-based grading.
    /// </summary>
    public static string BuildForDocker(int databaseHostPort, string databaseName, string? username = null, string? password = null)
    {
        var server = $"localhost,{databaseHostPort}";
        var user = username ?? AppsettingKeywords.DEFAULT_USERNAME;
        var pwd = password ?? AppsettingKeywords.DOCKER_SA_PASSWORD;

        return FormatConnectionString(server, databaseName, user, pwd);
    }

    private static string GetDefaultConnectionString()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        return FormatConnectionString(
            isWindows ? AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE : AppsettingKeywords.DEFAULT_SQL_SERVER_DOCKER,
            AppsettingKeywords.DEFAULT_DATABASE_NAME,
            AppsettingKeywords.DEFAULT_USERNAME,
            isWindows ? AppsettingKeywords.DEFAULT_PASSWORD : AppsettingKeywords.DOCKER_SA_PASSWORD);
    }

    private static string ResolveServerAddress(string? configuredServer, int? dbPort)
    {
        var server = configuredServer;
        
        if (string.IsNullOrWhiteSpace(server))
        {
            server = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE
                : $"localhost,{dbPort ?? 1433}";
        }

        return RequiresLocalPrefix(server) 
            ? $"{AppsettingKeywords.SERVER_LOCAL_PREFIX}{server}" 
            : server;
    }

    /// <summary>
    /// Determines if server name requires .\ prefix for SQL Server named instances.
    /// Returns false for: localhost, IP addresses, already-prefixed instances,
    /// (local) keyword, port specifications, and hostnames with dots.
    /// </summary>
    private static bool RequiresLocalPrefix(string server)
    {
        if (server.StartsWith(AppsettingKeywords.SERVER_LOCAL_PREFIX)) return false;
        if (server.Contains("\\")) return false;
        if (server.Equals(AppsettingKeywords.SERVER_LOCAL_KEYWORD, StringComparison.OrdinalIgnoreCase)) return false;
        if (server.Equals(AppsettingKeywords.SERVER_LOCALHOST, StringComparison.OrdinalIgnoreCase)) return false;
        if (server.Contains(":") || server.Contains(",")) return false;
        if (IPAddress.TryParse(server, out _)) return false;
        if (server.Contains(".")) return false;
        
        return true;
    }

    private static string ResolveDatabaseName(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        return envConfig?.DatabaseName ?? dbConfig?.Database ?? AppsettingKeywords.DEFAULT_DATABASE_NAME;
    }

    private static string ResolveUsername(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        return envConfig?.DatabaseUsername ?? dbConfig?.Username ?? AppsettingKeywords.DEFAULT_USERNAME;
    }

    private static string ResolvePassword(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        var password = envConfig?.DatabasePassword ?? dbConfig?.Password;
        
        if (!string.IsNullOrWhiteSpace(password))
        {
            return password;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? AppsettingKeywords.DEFAULT_PASSWORD
            : AppsettingKeywords.DOCKER_SA_PASSWORD;
    }

    private static string FormatConnectionString(string server, string database, string username, string password)
    {
        return string.Format(
            AppsettingKeywords.DEFAULT_CONNECTION_STRING_TEMPLATE,
            server,
            database,
            username,
            password);
    }
}
