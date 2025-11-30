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
        /// Uses WaitForPublishConsoleFileDeployment which properly sets up named pipes for stdin/stdout.
        /// </summary>
        public async Task<bool> StartServerAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Starting server application in container...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerServerPath, "");
                // App name should match container name for pipe to work: /tmp/{appName}_input_pipe
                var appName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.StudentQuestionName, containerName);
                var port = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerInternalPort, "8000");

                _logger.LogDebug($"Container: {containerName}");
                _logger.LogDebug($"App name (for pipe /tmp/{appName}_input_pipe): {appName}");
                _logger.LogDebug($"DLL path (from environment): {(string.IsNullOrEmpty(dllPath) ? "(not set)" : dllPath)}");
                _logger.LogDebug($"Internal port: {port}");

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

                // Start the dotnet application in the container using proper pipe setup
                _logger.LogInfo($"Starting dotnet {dllPath} in container {containerName} (app: {appName}, port: {port})...");
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, appName, dllPath, port, ct);
                
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
        /// Uses WaitForPublishConsoleFileDeployment which properly sets up named pipes for stdin/stdout.
        /// </summary>
        public async Task<bool> StartClientAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Starting client application in container...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerClientPath, "");
                // App name should match container name for pipe to work: /tmp/{appName}_input_pipe
                var appName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleAppName, containerName);
                // Client doesn't listen on a port - it connects to server
                var port = "-1";

                _logger.LogDebug($"Container: {containerName}");
                _logger.LogDebug($"App name (for pipe /tmp/{appName}_input_pipe): {appName}");
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

                // Start the dotnet application in the container using proper pipe setup
                _logger.LogInfo($"Starting dotnet {dllPath} in container {containerName} (app: {appName})...");
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, appName, dllPath, port, ct);
                
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
        /// Stops all dotnet processes inside a container.
        /// This is used between test cases to ensure the port is released.
        /// </summary>
        public async Task<bool> StopApplicationsInContainerAsync(string containerName, CancellationToken ct = default)
        {
            _logger.LogInfo($"Stopping all dotnet processes in container {containerName}...");

            try
            {
                // Kill all dotnet processes in the container using pkill
                // This releases the ports before starting a new test case
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerName} pkill -9 dotnet",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    // pkill returns 0 if at least one process matched, 1 if no process matched
                    // Both are acceptable outcomes
                }

                // Also clean up named pipes to ensure clean restart
                var cleanupPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerName} sh -c \"rm -f /tmp/*_input_pipe 2>/dev/null || true\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var cleanupProcess = System.Diagnostics.Process.Start(cleanupPsi);
                if (cleanupProcess != null)
                {
                    await cleanupProcess.WaitForExitAsync(ct);
                }

                _logger.LogInfo($"Stopped dotnet processes in container {containerName}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error stopping processes in {containerName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops all applications in all containers (server and client).
        /// Called between test cases to ensure clean state and release ports.
        /// </summary>
        public async Task StopAllApplicationsAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Stopping all applications in containers...");
            
            var serverContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName);
            var clientContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName);

            if (!string.IsNullOrEmpty(serverContainer))
            {
                await StopApplicationsInContainerAsync(serverContainer, ct);
            }

            if (!string.IsNullOrEmpty(clientContainer))
            {
                await StopApplicationsInContainerAsync(clientContainer, ct);
            }

            // Wait for ports to be fully released
            await Task.Delay(1000, ct);
            _logger.LogInfo("All applications stopped");
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
    /// 
    /// IMPORTANT: For starting console applications properly with input/output pipes,
    /// use WaitForPublishConsoleFileDeployment() instead of StartApplicationInContainerAsync().
    /// The former sets up named pipes for stdin/stdout redirection.
    /// </summary>
    internal static class DockerCommandExecutorExtensions
    {
        /// <summary>
        /// Starts a .NET application in a container with proper stdin/stdout setup via named pipes.
        /// This method creates a named pipe for input, then starts the dotnet application
        /// with stdin redirected from the pipe and stdout redirected to container logs.
        /// 
        /// The application name is used to create the named pipe at /tmp/{appName}_input_pipe.
        /// Input can later be sent using SendInputToContainer().
        /// </summary>
        /// <param name="executor">Docker command executor</param>
        /// <param name="containerName">Name of the container</param>
        /// <param name="appName">Application name for pipe naming</param>
        /// <param name="dllPath">Path to DLL inside container</param>
        /// <param name="expectedPort">Port to check for readiness (-1 to skip port check)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if application started successfully</returns>
        public static async Task<bool> StartApplicationInContainerAsync(
            this DockerCommandExecutor executor, 
            string containerName, 
            string appName,
            string dllPath, 
            string expectedPort = "-1",
            CancellationToken ct = default)
        {
            try
            {
                // Use the proper method that sets up pipes for stdin/stdout
                // This calls WaitForPublishConsoleFileDeployment internally which:
                // 1. Creates named pipe /tmp/{appName}_input_pipe
                // 2. Starts sleep process to keep pipe open
                // 3. Starts dotnet with stdin from pipe and stdout to container logs
                // 4. Waits for process to be running and port to be listening
                bool success = executor.WaitForPublishConsoleFileDeployment(
                    containerName,
                    appName,
                    dllPath,
                    expectedPort,
                    maxWaitTimeMs: 30000);
                
                return success;
            }
            catch (Exception ex)
            {
                // Log the exception for debugging - helps identify Docker setup failures
                Console.WriteLine($"[ERROR] StartApplicationInContainerAsync failed for {containerName}/{appName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the logs from a container.
        /// </summary>
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
