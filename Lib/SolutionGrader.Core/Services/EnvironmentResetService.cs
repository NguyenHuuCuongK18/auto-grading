namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Helpers;
using SolutionGrader.Core.Keywords;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Text.RegularExpressions;
using System.Data;

/// <summary>
/// Service for resetting environment between test cases (appsettings and database reset).
/// </summary>
public sealed class EnvironmentResetService : IEnvironmentResetService
{
    private readonly IFileService _files;
    private static readonly Regex GoRegex = new("^\\s*GO\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    
    public EnvironmentResetService(IFileService files) => _files = files;

    public void ReplaceAppsettings(string? clientTemplate, string? serverTemplate, string? clientExe, string? serverExe)
    {
        try
        {
            CopyAppsettingsIfNeeded(serverTemplate, serverExe);
            CopyAppsettingsIfNeeded(clientTemplate, clientExe);
        }
        catch (System.Exception ex) 
        {
            // Log warning but don't throw - appsettings replacement is now non-fatal
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} Warning: {AppsettingKeywords.MSG_APPSETTINGS_REPLACE_FAILED} {ex.Message}");
        }
    }

    private void CopyAppsettingsIfNeeded(string? templatePath, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || string.IsNullOrWhiteSpace(exePath)) return;
        
        var dest = Path.Combine(Path.GetDirectoryName(exePath)!, FileKeywords.FileName_AppSettings);
        using var src = _files.OpenRead(templatePath);
        using var dst = _files.OpenWrite(dest, overwrite: true);
        src.CopyTo(dst);
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, DatabaseConfiguration? dbConfig, bool useDocker, System.Threading.CancellationToken ct)
    {
        await RunDatabaseResetAsync(dbScriptPath, dbConfig, useDocker, null, ct);
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, DatabaseConfiguration? dbConfig, bool useDocker, EnvironmentConfiguration? envConfig, System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dbScriptPath) || !File.Exists(dbScriptPath))
        {
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
                success = await ExecuteSqlViaLocalConnectionAsync(dbScriptPath, dbConfig, envConfig, ct);
            }
            
            if (!success)
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_DATABASE_RESET_FAILED}");
                
                // Check if we should stop grading on database reset failure
                bool stopOnFailure = envConfig?.StopGradingIfResetFails ?? true;
                if (stopOnFailure)
                {
                    throw new System.InvalidOperationException("Database reset failed and StopGradingIfResetFails is enabled");
                }
            }
            else
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_DATABASE_RESET_SUCCESS}");
            }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {string.Format(AppsettingKeywords.MSG_DATABASE_RESET_ERROR, ex.Message)}");
            
            // Check if we should stop grading on database reset error
            bool stopOnFailure = envConfig?.StopGradingIfResetFails ?? true;
            if (stopOnFailure)
            {
                throw; // Re-throw to stop grading process
            }
            // Otherwise, continue with tests even if DB reset fails
        }
    }

    private async System.Threading.Tasks.Task<bool> ExecuteSqlViaLocalConnectionAsync(string dbScriptPath, DatabaseConfiguration? dbConfig, System.Threading.CancellationToken ct)
    {
        return await ExecuteSqlViaLocalConnectionAsync(dbScriptPath, dbConfig, null, ct);
    }

    private async System.Threading.Tasks.Task<bool> ExecuteSqlViaLocalConnectionAsync(string dbScriptPath, DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig, System.Threading.CancellationToken ct)
    {
        try
        {
            var builder = ConnectionStringHelper.BuildSqlConnectionStringBuilder(dbConfig, envConfig);
            
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_NO_INITIAL_CATALOG}");
                return false;
            }

            var databaseName = builder.InitialCatalog;
            var script = await File.ReadAllTextAsync(dbScriptPath, ct);
            var scriptManagesDatabase = ScriptContainsDatabaseManagement(script, databaseName);
            
            if (scriptManagesDatabase)
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_SCRIPT_SELF_MANAGING}");
                await ExecuteScriptFromMasterAsync(builder, dbScriptPath, ct);
            }
            else
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_MANUAL_DB_MANAGEMENT}");
                await DropDatabaseAsync(builder, databaseName, ct);
                await CreateDatabaseAsync(builder, databaseName, ct);
                await ApplyScriptAsync(builder, databaseName, dbScriptPath, ct);
            }
            
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
        var masterConnectionString = ConnectionStringHelper.BuildMasterConnectionString(builder);
        SqlConnection.ClearPool(new SqlConnection(masterConnectionString));
        
        using var connection = new SqlConnection(masterConnectionString);
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
        // Clear any connection pool for this connection string to avoid stale connections
        var masterConnectionString = ConnectionStringHelper.BuildMasterConnectionString(builder);
        SqlConnection.ClearPool(new SqlConnection(masterConnectionString));
        
        using var connection = new SqlConnection(masterConnectionString);
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
        var connectionString = builder.ConnectionString;
        
        // Clear any connection pool for this connection string to avoid stale connections
        SqlConnection.ClearPool(new SqlConnection(connectionString));
        
        using var connection = new SqlConnection(connectionString);
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

    private static bool ScriptContainsDatabaseManagement(string script, string databaseName)
    {
        var scriptUpper = script.ToUpperInvariant();
        var dbNameUpper = databaseName.ToUpperInvariant();
        var dbNameBracketed = $"[{dbNameUpper}]";
        
        if (scriptUpper.Contains($"DROP DATABASE {dbNameBracketed}") || 
            scriptUpper.Contains($"DROP DATABASE [{dbNameUpper}]") ||
            Regex.IsMatch(scriptUpper, $@"\bDROP\s+DATABASE\s+{Regex.Escape(dbNameUpper)}\b"))
        {
            return true;
        }
        
        // Check for CREATE DATABASE
        if (scriptUpper.Contains($"CREATE DATABASE {dbNameBracketed}") || 
            scriptUpper.Contains($"CREATE DATABASE [{dbNameUpper}]") ||
            Regex.IsMatch(scriptUpper, $@"\bCREATE\s+DATABASE\s+{Regex.Escape(dbNameUpper)}\b"))
        {
            return true;
        }
        
        // Check for USE
        if (scriptUpper.Contains($"USE {dbNameBracketed}") || 
            scriptUpper.Contains($"USE [{dbNameUpper}]") ||
            Regex.IsMatch(scriptUpper, $@"\bUSE\s+{Regex.Escape(dbNameUpper)}\b"))
        {
            return true;
        }
        
        return false;
    }

    private async System.Threading.Tasks.Task ExecuteScriptFromMasterAsync(SqlConnectionStringBuilder builder, string scriptPath, System.Threading.CancellationToken ct)
    {
        // Execute the entire script from the master database context
        // This allows the script to manage database drop/create/use operations itself
        
        var script = await File.ReadAllTextAsync(scriptPath, ct);
        var batches = SplitSqlBatches(script);

        // Connect to master database
        var masterConnectionString = ConnectionStringHelper.BuildMasterConnectionString(builder);
        
        // Clear any connection pool for this connection string to avoid stale connections
        SqlConnection.ClearPool(new SqlConnection(masterConnectionString));
        
        using var connection = new SqlConnection(masterConnectionString);
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
                Arguments = $"exec {AppsettingKeywords.DOCKER_CONTAINER_NAME} {AppsettingKeywords.DOCKER_SQLCMD_PATH} {AppsettingKeywords.SQL_FLAG_SERVER} {AppsettingKeywords.SERVER_LOCALHOST} {AppsettingKeywords.SQL_FLAG_USER} {AppsettingKeywords.DEFAULT_USERNAME} {AppsettingKeywords.SQL_FLAG_PASSWORD} \"{saPassword}\" {AppsettingKeywords.SQL_FLAG_TRUST_CERT} {AppsettingKeywords.SQL_FLAG_INPUT_FILE} {AppsettingKeywords.DOCKER_TMP_SCRIPT_PATH}",
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
