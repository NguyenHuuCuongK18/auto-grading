namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
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
                var dest = Path.Combine(Path.GetDirectoryName(serverExe)!, "appsettings.json");
                using var src = _files.OpenRead(serverTemplate);
                using var dst = _files.OpenWrite(dest, overwrite:true);
                src.CopyTo(dst);
            }
            if (!string.IsNullOrWhiteSpace(clientTemplate) && !string.IsNullOrWhiteSpace(clientExe))
            {
                var dest = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
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
            Console.WriteLine("[Database] Resetting database from script...");
            
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
                Console.WriteLine("[Database] Warning: Could not execute database reset script");
            }
            else
            {
                Console.WriteLine("[Database] Database reset completed successfully");
            }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[Database] Warning: Database reset failed: {ex.Message}");
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
                Console.WriteLine("[Database] Warning: Connection string does not specify a database name (Initial Catalog).");
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
            Console.WriteLine($"[Database] Local database reset error: {ex.Message}");
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
        if (dbConfig == null)
        {
            // Default connection string for local SQL Server
            return "server=.\\SQLEXPRESS;database=Library;uid=sa;pwd=sa;TrustServerCertificate=True;";
        }

        var server = dbConfig.SqlServer ?? "SQLEXPRESS";
        // Format SQL Server instance name properly
        if (!server.StartsWith(".\\") && !server.Contains("\\") && !server.Equals("(local)", System.StringComparison.OrdinalIgnoreCase))
        {
            server = $".\\{server}";
        }

        var database = dbConfig.Database ?? "Library";
        var username = dbConfig.Username ?? "sa";
        var password = dbConfig.Password ?? "sa";

        return $"server={server};database={database};uid={username};pwd={password};TrustServerCertificate=True;";
    }

    private static string BuildMasterConnectionString(SqlConnectionStringBuilder builder)
    {
        var masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = "master"
        };

        return masterBuilder.ConnectionString;
    }

    private async System.Threading.Tasks.Task<bool> ExecuteSqlViaDockerAsync(string dbScriptPath, System.Threading.CancellationToken ct)
    {
        try
        {
            // Copy SQL script to container
            var copyPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"cp \"{dbScriptPath}\" sqlserver-test:/tmp/db_reset.sql",
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
                FileName = "docker",
                Arguments = "exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"YourStrong@Passw0rd\" -C -i /tmp/db_reset.sql",
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
                
                if (!string.IsNullOrWhiteSpace(error) && error.Contains("Level 16"))
                {
                    // Level 16 errors are warnings we can ignore
                    Console.WriteLine($"[Database] SQL execution had warnings (non-fatal)");
                }
                
                return execProcess.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    public void ClearFolder(string path) => _files.ClearDirectory(path);
}
