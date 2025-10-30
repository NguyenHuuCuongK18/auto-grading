namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.IO;

public sealed class EnvironmentResetService : IEnvironmentResetService
{
    private readonly IFileService _files;
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

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dbScriptPath) || !File.Exists(dbScriptPath))
        {
            // No database script provided or file doesn't exist - skip reset
            return;
        }

        try
        {
            Console.WriteLine("[Database] Resetting database from script...");
            
            // Execute the SQL script using Docker (copy file to container and execute)
            var dockerResult = await ExecuteSqlViaDockerAsync(dbScriptPath, ct);
            
            if (!dockerResult)
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
