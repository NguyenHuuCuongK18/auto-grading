namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Text.RegularExpressions;
using System.Data;

public sealed class EnvironmentResetService : IEnvironmentResetService
{
    private readonly IFileService _files;
    private static readonly Regex GoRegex = new("^\\s*GO\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    
    public EnvironmentResetService(IFileService files) => _files = files;

    public void ReplaceAppsettings(string? clientTemplate, string? serverTemplate, string? clientExe, string? serverExe)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(serverTemplate) && !string.IsNullOrWhiteSpace(serverExe))
            {
                var dest = Path.Combine(Path.GetDirectoryName(serverExe)!, FileKeywords.FileName_AppSettings);
                using var src = _files.OpenRead(serverTemplate);
                using var dst = _files.OpenWrite(dest, overwrite:true);
                src.CopyTo(dst);
            }
            if (!string.IsNullOrWhiteSpace(clientTemplate) && !string.IsNullOrWhiteSpace(clientExe))
            {
                var dest = Path.Combine(Path.GetDirectoryName(clientExe)!, FileKeywords.FileName_AppSettings);
                using var src = _files.OpenRead(clientTemplate);
                using var dst = _files.OpenWrite(dest, overwrite:true);
                src.CopyTo(dst);
            }
        }
        catch (System.Exception ex) { throw new System.InvalidOperationException("Failed to replace appsettings.", ex); }
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, DatabaseConfiguration? dbConfig, bool useDocker, System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dbScriptPath) || !File.Exists(dbScriptPath))
        {
            // No database script provided or file doesn't exist - skip reset
            return;
        }

        try
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_RESETTING_DATABASE}");
            
            bool success;
            if (useDocker)
            {
                success = await ExecuteSqlViaDockerAsync(dbScriptPath, ct);
            }
            else
            {
                success = await ExecuteSqlViaLocalConnectionAsync(dbScriptPath, dbConfig, ct);
            }
            
            if (!success)
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_DATABASE_RESET_FAILED}");
            }
            else
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_DATABASE_RESET_SUCCESS}");
            }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {string.Format(AppsettingKeywords.MSG_DATABASE_RESET_ERROR, ex.Message)}");
            // Don't throw - allow tests to continue even if DB reset fails
        }
    }

    private async System.Threading.Tasks.Task<bool> ExecuteSqlViaLocalConnectionAsync(string dbScriptPath, DatabaseConfiguration? dbConfig, System.Threading.CancellationToken ct)
    {
        try
        {
            // Build connection string from DatabaseConfiguration
            var connectionString = BuildConnectionString(dbConfig);
            
            // Extract database name from connection string
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_NO_INITIAL_CATALOG}");
                return false;
            }

            var databaseName = builder.InitialCatalog;
            
            // Drop the database if it exists
            await DropDatabaseAsync(builder, databaseName, ct);
            
            // Create a new database
            await CreateDatabaseAsync(builder, databaseName, ct);
            
            // Apply the SQL script to the new database
            await ApplyScriptAsync(builder, databaseName, dbScriptPath, ct);
            
            return true;
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {string.Format(AppsettingKeywords.MSG_LOCAL_DB_RESET_ERROR, ex.Message)}");
            return false;
        }
    }

    private static async System.Threading.Tasks.Task DropDatabaseAsync(SqlConnectionStringBuilder builder, string databaseName, System.Threading.CancellationToken ct)
    {
        using var connection = new SqlConnection(BuildMasterConnectionString(builder));
        await connection.OpenAsync(ct);

        var commandText = $@"
IF EXISTS (SELECT name FROM sys.databases WHERE name = @name)
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END";

        using var command = new SqlCommand(commandText, connection)
        {
            CommandType = CommandType.Text
        };
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async System.Threading.Tasks.Task CreateDatabaseAsync(SqlConnectionStringBuilder builder, string databaseName, System.Threading.CancellationToken ct)
    {
        using var connection = new SqlConnection(BuildMasterConnectionString(builder));
        await connection.OpenAsync(ct);

        using var command = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection)
        {
            CommandType = CommandType.Text
        };

        await command.ExecuteNonQueryAsync(ct);
    }

    private async System.Threading.Tasks.Task ApplyScriptAsync(SqlConnectionStringBuilder builder, string databaseName, string scriptPath, System.Threading.CancellationToken ct)
    {
        var script = await File.ReadAllTextAsync(scriptPath, ct);
        var batches = SplitSqlBatches(script);

        builder.InitialCatalog = databaseName;
        using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var batch in batches)
        {
            using var command = new SqlCommand(batch, connection)
            {
                CommandType = CommandType.Text
            };

            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static System.Collections.Generic.IEnumerable<string> SplitSqlBatches(string script)
    {
        return GoRegex.Split(script)
            .Select(batch => batch.Trim())
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
    }

    private static string BuildConnectionString(DatabaseConfiguration? dbConfig)
    {
        var builder = new SqlConnectionStringBuilder();
        
        if (dbConfig == null)
        {
            // Default connection string for local SQL Server
            // Note: These defaults are for testing only. For production, configure properly in Header.xlsx
            builder.DataSource = AppsettingKeywords.DEFAULT_SQL_SERVER_INSTANCE;
            builder.InitialCatalog = AppsettingKeywords.DEFAULT_DATABASE_NAME;
            builder.UserID = AppsettingKeywords.DEFAULT_USERNAME;
            builder.Password = AppsettingKeywords.DEFAULT_PASSWORD;
            builder.TrustServerCertificate = true;
        }
        else
        {
            var server = dbConfig.SqlServer ?? AppsettingKeywords.SQL_EXPRESS;
            // Format SQL Server instance name properly
            if (!server.StartsWith(".\\") && !server.Contains("\\") && !server.Equals("(local)", System.StringComparison.OrdinalIgnoreCase))
            {
                server = $".\\{server}";
            }

            builder.DataSource = server;
            builder.InitialCatalog = dbConfig.Database ?? AppsettingKeywords.DEFAULT_DATABASE_NAME;
            builder.UserID = dbConfig.Username ?? AppsettingKeywords.DEFAULT_USERNAME;
            builder.Password = dbConfig.Password ?? AppsettingKeywords.DEFAULT_PASSWORD;
            builder.TrustServerCertificate = true;
        }

        return builder.ConnectionString;
    }

    private static string BuildMasterConnectionString(SqlConnectionStringBuilder builder)
    {
        var masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = AppsettingKeywords.MASTER_DATABASE
        };

        return masterBuilder.ConnectionString;
    }

    private async System.Threading.Tasks.Task<bool> ExecuteSqlViaDockerAsync(string dbScriptPath, System.Threading.CancellationToken ct)
    {
        try
        {
            // Note: For production use, consider using environment variables for the SA password
            // or mounting a secure configuration file instead of passing it via command line
            var saPassword = AppsettingKeywords.DOCKER_SA_PASSWORD;  // This is visible in process lists
            
            // Copy SQL script to container
            var copyPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppsettingKeywords.DOCKER_COMMAND,
                Arguments = $"cp \"{dbScriptPath}\" {AppsettingKeywords.DOCKER_CONTAINER_NAME}:{AppsettingKeywords.DOCKER_TMP_SCRIPT_PATH}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using (var copyProcess = System.Diagnostics.Process.Start(copyPsi))
            {
                if (copyProcess == null)
                    return false;
                await copyProcess.WaitForExitAsync(ct);
                if (copyProcess.ExitCode != 0)
                    return false;
            }
            
            // Execute SQL script from file
            var execPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppsettingKeywords.DOCKER_COMMAND,
                Arguments = $"exec {AppsettingKeywords.DOCKER_CONTAINER_NAME} {AppsettingKeywords.DOCKER_SQLCMD_PATH} -S {AppsettingKeywords.DOCKER_LOCALHOST} -U sa -P \"{saPassword}\" -C -i {AppsettingKeywords.DOCKER_TMP_SCRIPT_PATH}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using (var execProcess = System.Diagnostics.Process.Start(execPsi))
            {
                if (execProcess == null)
                    return false;
                    
                await execProcess.WaitForExitAsync(ct);
                
                // Read output for debugging
                var output = await execProcess.StandardOutput.ReadToEndAsync();
                var error = await execProcess.StandardError.ReadToEndAsync();
                
                if (!string.IsNullOrWhiteSpace(error) && error.Contains(AppsettingKeywords.SQL_ERROR_LEVEL_16))
                {
                    // Level 16 errors are warnings we can ignore
                    Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_SQL_WARNINGS_NONFATAL}");
                }
                
                return execProcess.ExitCode == 0;
            }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {string.Format(AppsettingKeywords.MSG_DOCKER_DB_RESET_ERROR, ex.Message)}");
            return false;
        }
    }

    public void ClearFolder(string path) => _files.ClearDirectory(path);
}
