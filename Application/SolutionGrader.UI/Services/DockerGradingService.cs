using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using EnvironmentBuilder.DockerCommand;
using SolutionGrader.UI.Models;
using Environment = Domain.Entities.Main.Environment;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for Docker container operations during grading.
    /// Handles application execution inside containers (StartServer, StartClient, SendInput).
    /// 
    /// NOTE: Container lifecycle (setup, copy files, dispose) is handled by GradingOrchestrationService.
    /// This service focuses on runtime operations during test case execution.
    /// 
    /// Key responsibilities:
    /// - Check Docker availability
    /// - Start/Stop applications inside containers via Docker commands
    /// - Send input to containers via named pipes
    /// - Capture container logs for comparison
    /// </summary>
    public class DockerGradingService
    {
        private readonly ILoggingService _logger;
        private readonly DockerCommandExecutor _dockerExecutor;

        public DockerGradingService(ILoggingService logger)
        {
            _logger = logger;
            _dockerExecutor = new DockerCommandExecutor();
        }

        /// <summary>
        /// Checks if Docker is available and running.
        /// </summary>
        public bool IsDockerAvailable()
        {
            try
            {
                return _dockerExecutor.IsDockerRunning();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to check Docker status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the server application inside its container.
        /// Returns immediately - server runs in background.
        /// </summary>
        public async Task<bool> StartServerAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Starting server application in container...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerServerPath, "");

                _logger.LogDebug($"Container: {containerName}");
                _logger.LogDebug($"DLL path (from environment): {(string.IsNullOrEmpty(dllPath) ? "(not set)" : dllPath)}");

                if (string.IsNullOrEmpty(dllPath))
                {
                    // Log all available configs for debugging
                    _logger.LogError("Server DLL path not configured in environment!");
                    _logger.LogDebug("Available environment configs:");
                    foreach (var kvp in environment.Configs.Take(20))
                    {
                        _logger.LogDebug($"  {kvp.Key}: {kvp.Value}");
                    }
                    return false;
                }

                // Start the dotnet application in the container
                _logger.LogInfo($"Starting dotnet {dllPath} in container {containerName}...");
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, dllPath, ct);
                
                if (result)
                {
                    _logger.LogInfo($"Server started successfully in container {containerName}");
                }
                else
                {
                    _logger.LogError($"Server failed to start in container {containerName}");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start server: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the client application inside its container.
        /// Returns immediately - client runs in background.
        /// </summary>
        public async Task<bool> StartClientAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Starting client application in container...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerClientPath, "");

                _logger.LogDebug($"Container: {containerName}");
                _logger.LogDebug($"DLL path (from environment): {(string.IsNullOrEmpty(dllPath) ? "(not set)" : dllPath)}");

                if (string.IsNullOrEmpty(dllPath))
                {
                    // Log all available configs for debugging
                    _logger.LogError("Client DLL path not configured in environment!");
                    _logger.LogDebug("Available environment configs:");
                    foreach (var kvp in environment.Configs.Take(20))
                    {
                        _logger.LogDebug($"  {kvp.Key}: {kvp.Value}");
                    }
                    return false;
                }

                // Start the dotnet application in the container
                _logger.LogInfo($"Starting dotnet {dllPath} in container {containerName}...");
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, dllPath, ct);
                
                if (result)
                {
                    _logger.LogInfo($"Client started successfully in container {containerName}");
                }
                else
                {
                    _logger.LogError($"Client failed to start in container {containerName}");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start client: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends input to the client application's stdin.
        /// Used for test case execution.
        /// </summary>
        public async Task<string> SendInputToClientAsync(
            Environment environment, 
            string input, 
            CancellationToken ct = default)
        {
            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                var appName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleAppName, "ag-client");

                _logger.LogDebug($"Sending input to client {containerName}: {input}");
                
                _dockerExecutor.SendInputToContainer(containerName, appName, input);
                
                await Task.Delay(100, ct); // Small delay for processing
                
                return await GetClientOutputAsync(environment, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to send input to client: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the current output from the client application.
        /// </summary>
        public async Task<string> GetClientOutputAsync(Environment environment, CancellationToken ct = default)
        {
            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                
                // Use docker logs to get output
                return await _dockerExecutor.GetContainerLogsAsync(containerName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get client output: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the current output from the server application.
        /// </summary>
        public async Task<string> GetServerOutputAsync(Environment environment, CancellationToken ct = default)
        {
            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server");
                
                return await _dockerExecutor.GetContainerLogsAsync(containerName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get server output: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Disposes all containers for a student.
        /// Called after grading is complete or cancelled.
        /// Uses direct Docker commands for reliable container removal.
        /// </summary>
        public async Task DisposeContainersAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Disposing Docker containers (force removal)...");

            try
            {
                // Get container names from environment
                var serverContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName);
                var clientContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName);
                
                _logger.LogInfo($"Containers to remove: server={serverContainer}, client={clientContainer}");

                // Remove server container
                if (!string.IsNullOrEmpty(serverContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing server container: {serverContainer}");
                        _dockerExecutor.RemoveContainer(serverContainer);
                        _logger.LogInfo($"Server container '{serverContainer}' removed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to remove server container '{serverContainer}': {ex.Message}");
                    }
                }

                // Remove client container
                if (!string.IsNullOrEmpty(clientContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing client container: {clientContainer}");
                        _dockerExecutor.RemoveContainer(clientContainer);
                        _logger.LogInfo($"Client container '{clientContainer}' removed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to remove client container '{clientContainer}': {ex.Message}");
                    }
                }

                await Task.Delay(200, ct);
                _logger.LogInfo("Container disposal complete");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to dispose containers: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Extension methods for DockerCommandExecutor to provide async operations.
    /// </summary>
    internal static class DockerCommandExecutorExtensions
    {
        public static async Task<bool> StartApplicationInContainerAsync(
            this DockerCommandExecutor executor, 
            string containerName, 
            string dllPath, 
            CancellationToken ct = default)
        {
            // Run dotnet command in container to start the application
            var command = $"docker exec -d {containerName} dotnet {dllPath}";
            
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec -d {containerName} dotnet {dllPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    return process.ExitCode == 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<string> GetContainerLogsAsync(
            this DockerCommandExecutor executor, 
            string containerName, 
            CancellationToken ct = default)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"logs --tail 100 {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync(ct);
                    await process.WaitForExitAsync(ct);
                    return output;
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
