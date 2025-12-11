using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for cleaning up Docker containers.
    /// Handles:
    /// - Unified container cleanup
    /// - Database instance cleanup
    /// - Process killing in containers
    /// - Container removal
    /// - Resource monitoring
    /// </summary>
    public sealed class DockerContainerCleanupService
    {
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        private readonly DockerCommandExecutor _dockerExecutor;
        
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        public DockerContainerCleanupService()
        {
            _dockerExecutor = new DockerCommandExecutor();
        }
        
        /// <summary>
        /// Cleans up the unified container after grading.
        /// </summary>
        public async Task CleanupUnifiedContainerAsync(string unifiedContainer, string studentCode)
        {
            OnProgress($"[Unified] Cleaning up container {unifiedContainer}...");
            
            try
            {
                // Kill any running dotnet processes
                await KillDotnetProcessesInContainerAsync(unifiedContainer, "unified");
                
                // Remove the container
                _dockerExecutor.RemoveContainer(unifiedContainer, 10000);
                
                // Wait for container to be removed
                await WaitForContainerRemovedAsync(unifiedContainer, maxWaitSeconds: 5);
                
                OnProgress($"[Unified] Container {unifiedContainer} cleaned up");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to cleanup container: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Cleans up a database instance within the shared MSSQL container.
        /// </summary>
        public async Task CleanupDatabaseInstanceAsync(
            string databaseContainer, 
            string databaseName, 
            string databasePassword)
        {
            if (string.IsNullOrEmpty(databaseName))
            {
                OnProgress($"[Database] No database instance to cleanup");
                return;
            }
            
            OnProgress($"[Database] Dropping database instance '{databaseName}'...");
            
            try
            {
                var dropSql = $"USE master; IF EXISTS (SELECT name FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
                var dropCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \"{databasePassword}\" -Q \"{dropSql}\"";
                
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(dropCommand, 10000);
                
                if (success)
                {
                    OnProgress($"[Database] Database instance '{databaseName}' dropped successfully");
                }
                else
                {
                    OnProgress($"[Database] Warning: Failed to drop database '{databaseName}': {output}");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database] Warning: Failed to cleanup database instance: {ex.Message}");
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Disposes all Docker containers including the database container.
        /// </summary>
        public void DisposeAllContainers(DockerGradingConfig config)
        {
            OnProgress("[Docker] Disposing all containers...");
            
            var databaseContainer = config.DatabaseContainerName;
            var serverContainer = $"server-{databaseContainer}";
            var clientContainer = $"client-{databaseContainer}";
            
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(databaseContainer); } catch { }
            
            OnProgress("[Docker] All containers disposed");
        }
        
        /// <summary>
        /// Checks Docker container count and warns if approaching limits.
        /// CRITICAL for batch grading 200+ students to prevent resource exhaustion.
        /// </summary>
        public void CheckDockerContainerLimit()
        {
            try
            {
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput("ps -a -q", 5000);
                if (success)
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var totalContainers = containerIds.Length;
                    
                    OnProgress($"[Docker Resource Monitor] Total containers: {totalContainers}");
                    
                    if (totalContainers > 380)
                    {
                        OnProgress($"[Docker Resource Monitor] CRITICAL WARNING: {totalContainers} containers exist!");
                    }
                    else if (totalContainers > 256)
                    {
                        OnProgress($"[Docker Resource Monitor] WARNING: {totalContainers} containers exist.");
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Resource Monitor] Warning: Could not check container count: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Aggressively cleans up old auto-grading containers.
        /// </summary>
        public void AggressiveCleanupOldContainers()
        {
            OnProgress("[Docker Aggressive Cleanup] Starting cleanup of old auto-grading containers...");
            
            try
            {
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(
                    "ps -a --filter 'name=ag-server-' --filter 'name=ag-client-' -q", 5000);
                
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    OnProgress($"[Docker Aggressive Cleanup] Found {containerIds.Length} old containers to remove");
                    
                    foreach (var containerId in containerIds)
                    {
                        try
                        {
                            _dockerExecutor.ExecDockerCommand($"rm -f {containerId}", 5000);
                        }
                        catch { }
                    }
                    
                    OnProgress($"[Docker Aggressive Cleanup] Cleanup complete.");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Aggressive Cleanup] Warning: Cleanup encountered errors: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Kills dotnet processes in a container using PID-based approach.
        /// </summary>
        public async Task KillDotnetProcessesInContainerAsync(string container, string containerType)
        {
            OnProgress($"Cleanup: Finding dotnet processes in {containerType} container...");
            
            var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput($"{container} ps aux", 5000);
            
            if (!success || string.IsNullOrEmpty(output))
            {
                OnProgress($"Cleanup: {containerType} - Could not list processes, using safe kill fallback...");
                _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(container), 5000);
                return;
            }
            
            var pids = ParseDotnetPidsFromPsOutput(output);
            
            if (pids.Count == 0)
            {
                OnProgress($"Cleanup: {containerType} - No dotnet processes found");
                return;
            }
            
            OnProgress($"Cleanup: {containerType} - Found {pids.Count} dotnet process(es): PIDs [{string.Join(", ", pids)}]");
            
            foreach (var pid in pids)
            {
                var killResult = _dockerExecutor.TryExecDockerCommand($"{container} kill {pid}", 5000);
                OnProgress($"Cleanup: {containerType} - kill {pid}: {(killResult ? "sent" : "failed")}");
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Force kills any remaining dotnet processes in a container using SIGKILL (-9).
        /// </summary>
        public async Task ForceKillDotnetProcessesInContainerAsync(string container, string containerType)
        {
            var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput($"{container} ps aux", 5000);
            
            if (!success || string.IsNullOrEmpty(output))
            {
                _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(container), 5000);
                return;
            }
            
            var pids = ParseDotnetPidsFromPsOutput(output);
            
            if (pids.Count == 0)
            {
                OnProgress($"Cleanup: {containerType} - No remaining dotnet processes");
                return;
            }
            
            OnProgress($"Cleanup: {containerType} - Force killing {pids.Count} remaining process(es)");
            
            foreach (var pid in pids)
            {
                _dockerExecutor.TryExecDockerCommand($"{container} kill -9 {pid}", 5000);
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Resets the database container for a new student.
        /// </summary>
        public async Task ResetDatabaseContainerAsync(DockerGradingConfig config, Func<DockerGradingConfig, Task> setupDatabaseAsync)
        {
            var databaseContainer = config.DatabaseContainerName;
            OnProgress($"[Database] Resetting database container {databaseContainer} for new student...");
            
            try { _dockerExecutor.StopContainer(databaseContainer, 10000); } catch { }
            try { _dockerExecutor.RemoveContainer(databaseContainer, 10000); } catch { }
            
            await WaitForContainerRemovedAsync(databaseContainer, maxWaitSeconds: 5);
            
            await setupDatabaseAsync(config);
            
            OnProgress($"[Database] Database container reset complete");
        }
        
        /// <summary>
        /// Waits for a container to be fully removed.
        /// </summary>
        public async Task WaitForContainerRemovedAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    OnProgress($"[Docker Cleanup] Container {containerName} successfully removed");
                    return;
                }
                await Task.Delay(100);
            }
            
            OnProgress($"[Docker Cleanup] WARNING: Container {containerName} still exists after {maxWaitSeconds}s");
            
            try
            {
                _dockerExecutor.ExecDockerCommand($"rm -f {containerName}", 5000);
                await Task.Delay(1000);
                
                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    OnProgress($"[Docker Cleanup] Force removal successful for {containerName}");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] ERROR: Force removal failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Waits for processes to be killed in a container.
        /// </summary>
        public async Task WaitForProcessesKilledAsync(string containerName, string processPattern, int maxWaitMs = 500)
        {
            var maxAttempts = maxWaitMs / 50;
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    var command = $"{containerName} sh -c \"ps aux | grep '{processPattern}' | grep -v grep | wc -l\"";
                    var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(command, 1000);
                    
                    if (success && int.TryParse(output.Trim(), out int count) && count == 0)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
                
                await Task.Delay(50);
            }
        }
        
        /// <summary>
        /// Builds a safe shell command to kill dotnet processes in a container.
        /// This command explicitly excludes PID 1 to avoid killing the container's main process.
        /// </summary>
        private static string BuildSafeDotnetKillCommand(string containerName)
        {
            return $"{containerName} sh -c \"ps aux | grep dotnet | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"";
        }
        
        /// <summary>
        /// Parses the output of 'ps aux' to find PIDs of dotnet processes.
        /// </summary>
        private List<int> ParseDotnetPidsFromPsOutput(string psOutput)
        {
            var pids = new List<int>();
            var lines = psOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                if (line.Contains("PID") && line.Contains("COMMAND"))
                    continue;
                
                if (!line.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
                    continue;
                
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int pid))
                {
                    if (pid != 1)
                    {
                        pids.Add(pid);
                    }
                }
            }
            
            return pids;
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
