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
        catch (System.Exception ex) { throw new System.InvalidOperationException(AppsettingKeywords.MSG_APPSETTINGS_REPLACE_FAILED, ex); }
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, DatabaseConfiguration? dbConfig, bool useDocker, System.Threading.CancellationToken ct)
    {
        await RunDatabaseResetAsync(dbScriptPath, dbConfig, useDocker, null, ct);
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, DatabaseConfiguration? dbConfig, bool useDocker, EnvironmentConfiguration? envConfig, System.Threading.CancellationToken ct)
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
            
            // Read the SQL script to check if it manages the database itself
            var script = await File.ReadAllTextAsync(dbScriptPath, ct);
            var scriptManagesDatabase = ScriptContainsDatabaseManagement(script, databaseName);
            
            if (scriptManagesDatabase)
            {
                // Script contains DROP/CREATE DATABASE commands
                // Execute the entire script from master database context
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_SCRIPT_SELF_MANAGING}");
                await ExecuteScriptFromMasterAsync(builder, dbScriptPath, ct);
            }
            else
            {
                // Script doesn't manage database, use manual drop/create/apply
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_DATABASE} {AppsettingKeywords.MSG_MANUAL_DB_MANAGEMENT}");
                
                // Drop the database if it exists
                await DropDatabaseAsync(builder, databaseName, ct);
                
                // Create a new database
                await CreateDatabaseAsync(builder, databaseName, ct);
                
                // Apply the SQL script to the new database
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
            // Default connection string based on platform
            // On Windows: use local SQL Server Express
            // On Linux/Mac: use Docker SQL Server
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                builder.DataSource = ".\\SQLEXPRESS";
                builder.IntegratedSecurity = true;
            }
            else
            {
                // Docker SQL Server for Linux/Mac (used for development/debugging)
                builder.DataSource = "localhost,1433";
                builder.UserID = "sa";
                builder.Password = "YourStrong@Passw0rd";
                builder.TrustServerCertificate = true;
            }
            builder.InitialCatalog = "Library";
            builder.ConnectTimeout = 30;
        }
        else
        {
            var server = dbConfig.SqlServer ?? (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? ".\\SQLEXPRESS" : "localhost,1433");
            // Format SQL Server instance name properly
            // Only add .\ prefix for named instances (e.g., SQLEXPRESS), not for:
            // - localhost, 127.0.0.1, or hostnames/IPs
            // - Already formatted instances (.\SQLEXPRESS)
            // - (local) keyword
            // - Server with port specification (server:port or server,port)
            if (!server.StartsWith(".\\") && 
                !server.Contains("\\") && 
                !server.Equals("(local)", System.StringComparison.OrdinalIgnoreCase) &&
                !server.Equals("localhost", System.StringComparison.OrdinalIgnoreCase) &&
                !server.Contains(":") &&   // Avoid prefixing server with port (server:port)
                !server.Contains(",") &&   // Avoid prefixing server with port (server,port)
                !System.Net.IPAddress.TryParse(server, out _)) // Don't prefix valid IP addresses
            {
                // Check if it looks like a hostname (contains dots for FQDN or computer.instance format)
                // Only add prefix if it's a simple instance name without dots
                if (!server.Contains("."))
                {
                    server = $".\\{server}";
                }
            }

            builder.DataSource = server;
            builder.InitialCatalog = dbConfig.Database ?? "Library";
            builder.UserID = dbConfig.Username ?? "sa";
            builder.Password = dbConfig.Password ?? "YourStrong@Passw0rd";
            builder.TrustServerCertificate = true;
            builder.ConnectTimeout = 30;
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

    private static bool ScriptContainsDatabaseManagement(string script, string databaseName)
    {
        // Check if the script contains DROP DATABASE, CREATE DATABASE, or USE commands
        // This indicates the script manages the database lifecycle itself
        
        // Patterns to check (case-insensitive):
        // - DROP DATABASE [DatabaseName] or DROP DATABASE DatabaseName
        // - CREATE DATABASE [DatabaseName] or CREATE DATABASE DatabaseName
        // - USE [DatabaseName] or USE DatabaseName
        
        var scriptUpper = script.ToUpperInvariant();
        var dbNameUpper = databaseName.ToUpperInvariant();
        var dbNameBracketed = $"[{dbNameUpper}]";
        
        // Check for DROP DATABASE
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
        var masterConnectionString = BuildMasterConnectionString(builder);
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
