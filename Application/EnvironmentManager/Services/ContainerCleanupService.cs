using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;

namespace EnvironmentManager.Services
{
    /// <summary>
    /// Service responsible for Docker container cleanup operations.
    /// Handles killing processes, removing containers, and cleaning up orphaned resources.
    /// </summary>
    public class ContainerCleanupService
    {
        private const int RetryCleanupDelaySeconds = 30;
        private const int BatchRemovalTimeoutMs = 15000;
        private const int BatchRemovalSize = 20;

        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        private readonly Action<string>? _progressCallback;

        /// <summary>
        /// Containers pending retry cleanup (Key: container name, Value: first cleanup attempt time)
        /// </summary>
        public ConcurrentDictionary<string, DateTime> PendingCleanupContainers { get; } = new();

        /// <summary>
        /// Creates a new instance of the cleanup service.
        /// </summary>
        public ContainerCleanupService(Action<string>? progressCallback = null)
        {
            _dockerExecutor = new DockerCommandExecutor();
            _commandExecutor = new CommandExecutor();
            _progressCallback = progressCallback;
        }

        /// <summary>
        /// Reports progress to the callback if available.
        /// </summary>
        protected void OnProgress(string message)
        {
            _progressCallback?.Invoke(message);
        }

        /// <summary>
        /// Kills dotnet processes in a container using PID-based approach.
        /// </summary>
        public async Task KillDotnetProcessesAsync(string container, string containerType)
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
        public async Task ForceKillDotnetProcessesAsync(string container, string containerType)
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

            OnProgress($"Cleanup: {containerType} - Force killing {pids.Count} remaining process(es): PIDs [{string.Join(", ", pids)}]");

            foreach (var pid in pids)
            {
                _dockerExecutor.TryExecDockerCommand($"{container} kill -9 {pid}", 5000);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Parses the output of 'ps aux' to find PIDs of dotnet processes.
        /// </summary>
        public List<int> ParseDotnetPidsFromPsOutput(string psOutput)
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
                    if (pid != 1) // Skip PID 1 (main container process)
                    {
                        pids.Add(pid);
                    }
                }
            }

            return pids;
        }

        /// <summary>
        /// Builds a safe shell command to kill dotnet processes in a container.
        /// </summary>
        public static string BuildSafeDotnetKillCommand(string containerName)
        {
            return $"{containerName} sh -c \"ps aux | grep dotnet | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"";
        }

        /// <summary>
        /// Cleans up containers by prefix (e.g., "ag-unified-", "ag-monitor-").
        /// </summary>
        public void CleanupContainersByPrefix(
            string prefix, 
            string typeName,
            ConcurrentDictionary<string, byte>? activeContainers = null)
        {
            try
            {
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput($"ps -a --filter \"name={prefix}\" --format \"{{{{.Names}}}}\"", 5000);
                if (!success || string.IsNullOrEmpty(output))
                    return;

                var containers = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (containers.Count == 0)
                    return;

                // Filter out active containers if provided
                var containersToRemove = activeContainers != null 
                    ? containers.Where(c => !activeContainers.ContainsKey(c)).ToList()
                    : containers;

                if (containersToRemove.Count == 0)
                {
                    OnProgress($"[Docker Cleanup] All {containers.Count} {typeName} containers are still in use");
                    return;
                }

                OnProgress($"[Docker Cleanup] Found {containersToRemove.Count} orphaned {typeName} containers to remove");

                // Batch remove for efficiency
                for (int i = 0; i < containersToRemove.Count; i += BatchRemovalSize)
                {
                    var batch = containersToRemove.Skip(i).Take(BatchRemovalSize).ToList();
                    var containerNames = string.Join(" ", batch);
                    
                    try
                    {
                        _commandExecutor.RunCommand($"docker rm -f {containerNames}", null, null, BatchRemovalTimeoutMs);
                        OnProgress($"[Docker Cleanup] Batch removed {batch.Count} {typeName} containers");
                    }
                    catch
                    {
                        // Fall back to individual removal
                        foreach (var container in batch)
                        {
                            try
                            {
                                _dockerExecutor.RemoveContainer(container);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] WARNING: Error cleaning up {typeName} containers: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes containers that are pending cleanup retry.
        /// </summary>
        public void ProcessPendingCleanupRetries()
        {
            if (PendingCleanupContainers.IsEmpty) return;

            var now = DateTime.UtcNow;
            var containersToRetry = PendingCleanupContainers
                .Where(kvp => (now - kvp.Value).TotalSeconds >= RetryCleanupDelaySeconds)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var containerName in containersToRetry)
            {
                try
                {
                    if (_dockerExecutor.IsContainerExist(containerName))
                    {
                        OnProgress($"[Docker Cleanup] Retrying cleanup for container: {containerName}");
                        _dockerExecutor.RemoveContainer(containerName);
                    }
                    
                    PendingCleanupContainers.TryRemove(containerName, out _);
                }
                catch (Exception ex)
                {
                    OnProgress($"[Docker Cleanup] Retry failed for {containerName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Cleans up files from /apps folder in container to prepare for next test case.
        /// </summary>
        public void CleanupContainerAppsFolder(string container)
        {
            try
            {
                _dockerExecutor.TryExecDockerCommand($"{container} rm -rf /apps/server/* 2>/dev/null || true", 5000);
                _dockerExecutor.TryExecDockerCommand($"{container} rm -rf /apps/client/* 2>/dev/null || true", 5000);
            }
            catch (Exception ex)
            {
                OnProgress($"Cleanup: Error cleaning apps folder in {container}: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up all orphaned auto-grading containers.
        /// </summary>
        public void CleanupOrphanedContainers(ConcurrentDictionary<string, byte>? activeContainers = null)
        {
            try
            {
                ProcessPendingCleanupRetries();
                CleanupContainersByPrefix("ag-unified-", "unified", activeContainers);
                CleanupContainersByPrefix("ag-monitor-", "monitor", activeContainers);
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] WARNING: Error cleaning up orphaned containers: {ex.Message}");
            }
        }
    }
}
