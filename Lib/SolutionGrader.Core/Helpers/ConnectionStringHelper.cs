using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
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

    /// <summary>
    /// Builds a SqlConnectionStringBuilder for database reset operations with additional connection options.
    /// Used by EnvironmentResetService.
    /// </summary>
    public static SqlConnectionStringBuilder BuildSqlConnectionStringBuilder(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        var builder = new SqlConnectionStringBuilder();
        
        if (dbConfig == null && envConfig == null)
        {
            if (IsWindowsPlatform)
            {
                builder.DataSource = AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE;
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.DataSource = AppsettingKeywords.DEFAULT_SQL_SERVER_DOCKER;
                builder.UserID = AppsettingKeywords.DEFAULT_USERNAME;
                builder.Password = AppsettingKeywords.DOCKER_SA_PASSWORD;
                builder.TrustServerCertificate = true;
            }
            builder.InitialCatalog = AppsettingKeywords.DEFAULT_DATABASE_NAME;
        }
        else
        {
            builder.DataSource = ResolveServerAddress(dbConfig?.SqlServer, envConfig?.DatabaseHostPort);
            builder.InitialCatalog = ResolveDatabaseName(dbConfig, envConfig);
            builder.UserID = ResolveUsername(dbConfig, envConfig);
            builder.Password = ResolvePassword(dbConfig, envConfig);
            builder.TrustServerCertificate = true;
        }
        
        // Standard options for database reset operations
        builder.ConnectTimeout = 30;
        builder.Pooling = false;
        builder.PersistSecurityInfo = true;
        
        return builder;
    }

    /// <summary>
    /// Builds a master database connection string from an existing connection builder.
    /// Used for database management operations (CREATE/DROP DATABASE).
    /// </summary>
    public static string BuildMasterConnectionString(SqlConnectionStringBuilder builder)
    {
        return new SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = AppsettingKeywords.MASTER_DATABASE
        }.ConnectionString;
    }

    /// <summary>
    /// Determines if the current platform is Windows.
    /// </summary>
    public static bool IsWindowsPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string GetDefaultConnectionString()
    {
        return FormatConnectionString(
            IsWindowsPlatform ? AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE : AppsettingKeywords.DEFAULT_SQL_SERVER_DOCKER,
            AppsettingKeywords.DEFAULT_DATABASE_NAME,
            AppsettingKeywords.DEFAULT_USERNAME,
            IsWindowsPlatform ? AppsettingKeywords.DEFAULT_PASSWORD : AppsettingKeywords.DOCKER_SA_PASSWORD);
    }

    /// <summary>
    /// Resolves the SQL Server address from configuration.
    /// </summary>
    /// <param name="configuredServer">The configured server name, or null to use defaults.</param>
    /// <param name="hostPort">The database host port for non-Windows platforms.</param>
    public static string ResolveServerAddress(string? configuredServer, int? hostPort)
    {
        var server = configuredServer;
        
        if (string.IsNullOrWhiteSpace(server))
        {
            server = IsWindowsPlatform
                ? AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE
                : $"localhost,{hostPort ?? 1433}";
        }

        return RequiresLocalPrefix(server) 
            ? $"{AppsettingKeywords.SERVER_LOCAL_PREFIX}{server}" 
            : server;
    }

    /// <summary>
    /// Determines if server name requires .\ prefix for SQL Server named instances.
    /// </summary>
    public static bool RequiresLocalPrefix(string server)
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

    /// <summary>
    /// Resolves database name from configuration with priority: envConfig > dbConfig > default.
    /// </summary>
    public static string ResolveDatabaseName(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        return envConfig?.DatabaseName ?? dbConfig?.Database ?? AppsettingKeywords.DEFAULT_DATABASE_NAME;
    }

    /// <summary>
    /// Resolves username from configuration with priority: envConfig > dbConfig > default.
    /// </summary>
    public static string ResolveUsername(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        return envConfig?.DatabaseUsername ?? dbConfig?.Username ?? AppsettingKeywords.DEFAULT_USERNAME;
    }

    /// <summary>
    /// Resolves password from configuration with platform-appropriate defaults.
    /// </summary>
    public static string ResolvePassword(DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        var password = envConfig?.DatabasePassword ?? dbConfig?.Password;
        
        if (!string.IsNullOrWhiteSpace(password))
        {
            return password;
        }

        return IsWindowsPlatform
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
