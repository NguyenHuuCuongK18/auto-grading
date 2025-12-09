using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace EnvironmentBuilder.DockerCommand
{
    /// <summary>
    /// Manages Docker container lifecycle and environment setup.
    /// Handles container health checking, cleanup, and resource management.
    /// </summary>
    public class ContainerEnvironmentService
    {
        private readonly DockerClient _dockerClient;
        private readonly Action<string>? _onProgress;

        public ContainerEnvironmentService(DockerClient dockerClient, Action<string>? onProgress = null)
        {
            _dockerClient = dockerClient ?? throw new ArgumentNullException(nameof(dockerClient));
            _onProgress = onProgress;
        }

        /// <summary>
        /// Waits for a container to enter running state.
        /// </summary>
        public async Task WaitForContainerRunningAsync(string containerName, int maxWaitSeconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < maxWaitSeconds)
            {
                try
                {
                    var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["name"] = new Dictionary<string, bool> { [containerName] = true }
                        }
                    });

                    var container = containers.FirstOrDefault();
                    if (container != null && container.State == "running")
                    {
                        _onProgress?.Invoke($"[ENVIRONMENT] Container {containerName} is running");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _onProgress?.Invoke($"[ENVIRONMENT] Error checking container state: {ex.Message}");
                }

                await Task.Delay(500);
            }

            throw new TimeoutException($"Container {containerName} did not start within {maxWaitSeconds} seconds");
        }

        /// <summary>
        /// Waits for a container to be fully removed.
        /// </summary>
        public async Task WaitForContainerRemovedAsync(string containerName, int maxWaitSeconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < maxWaitSeconds)
            {
                try
                {
                    var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["name"] = new Dictionary<string, bool> { [containerName] = true }
                        }
                    });

                    if (!containers.Any())
                    {
                        _onProgress?.Invoke($"[ENVIRONMENT] Container {containerName} successfully removed");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _onProgress?.Invoke($"[ENVIRONMENT] Error checking container removal: {ex.Message}");
                }

                await Task.Delay(500);
            }

            _onProgress?.Invoke($"[ENVIRONMENT] Warning: Container {containerName} was not removed within {maxWaitSeconds} seconds");
        }

        /// <summary>
        /// Checks Docker container limit and logs warning if approaching limit.
        /// </summary>
        public void CheckDockerContainerLimit()
        {
            try
            {
                var result = ExecuteDockerCommand("docker ps -a -q | wc -l");
                if (int.TryParse(result.Trim(), out int containerCount))
                {
                    _onProgress?.Invoke($"[ENVIRONMENT] Current Docker container count: {containerCount}");
                    
                    const int warningThreshold = 50;
                    if (containerCount > warningThreshold)
                    {
                        _onProgress?.Invoke($"[ENVIRONMENT] WARNING: High container count ({containerCount}). Consider cleanup.");
                    }
                }
            }
            catch (Exception ex)
            {
                _onProgress?.Invoke($"[ENVIRONMENT] Could not check container count: {ex.Message}");
            }
        }

        /// <summary>
        /// Aggressively cleans up old containers from previous grading sessions.
        /// </summary>
        public void AggressiveCleanupOldContainers()
        {
            _onProgress?.Invoke("[ENVIRONMENT] Starting aggressive container cleanup...");

            try
            {
                // Remove all stopped auto-grading containers
                var stoppedContainers = ExecuteDockerCommand("docker ps -a --filter 'status=exited' --filter 'name=ag-' --format '{{.Names}}'");
                var containerNames = stoppedContainers.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var name in containerNames)
                {
                    try
                    {
                        ExecuteDockerCommand($"docker rm {name}");
                        _onProgress?.Invoke($"[ENVIRONMENT] Removed stopped container: {name}");
                    }
                    catch (Exception ex)
                    {
                        _onProgress?.Invoke($"[ENVIRONMENT] Could not remove {name}: {ex.Message}");
                    }
                }

                // Also remove dead containers
                var deadContainers = ExecuteDockerCommand("docker ps -a --filter 'status=dead' --format '{{.Names}}'");
                var deadNames = deadContainers.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var name in deadNames)
                {
                    try
                    {
                        ExecuteDockerCommand($"docker rm -f {name}");
                        _onProgress?.Invoke($"[ENVIRONMENT] Removed dead container: {name}");
                    }
                    catch (Exception ex)
                    {
                        _onProgress?.Invoke($"[ENVIRONMENT] Could not remove {name}: {ex.Message}");
                    }
                }

                _onProgress?.Invoke("[ENVIRONMENT] Aggressive cleanup completed");
            }
            catch (Exception ex)
            {
                _onProgress?.Invoke($"[ENVIRONMENT] Error during aggressive cleanup: {ex.Message}");
            }
        }

        private string ExecuteDockerCommand(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start process");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException($"Command failed: {error}");

            return output;
        }
    }
}
