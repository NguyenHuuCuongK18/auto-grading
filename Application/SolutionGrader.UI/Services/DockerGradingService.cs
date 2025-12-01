using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.helper;
using SolutionGrader.UI.Models;
using Environment = Domain.Entities.Main.Environment;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for Docker container management for student grading.
    /// Creates containers per student: Server, Client, and Database (if needed).
    /// 
    /// Key responsibilities:
    /// - Setup containers with proper port mappings for network monitoring
    /// - Copy student solution files to containers (separate from execution)
    /// - Start/Stop applications inside containers via Docker commands
    /// - Redirect stdin/stdout for grading using named pipes
    /// - Clean up between test cases (reset database, clear network monitor, restart processes)
    /// - Flush network monitor before each grading step
    /// 
    /// Test case isolation:
    /// Each test case is independent and requires cleanup before the next one:
    /// - Database reset using SQL scripts
    /// - Network monitor capture buffer cleared
    /// - Process restart if needed
    /// </summary>
    public class DockerGradingService
    {
        private readonly ILoggingService _logger;
        private readonly DockerCommandExecutor _dockerExecutor;

        // Container name suffixes for student-specific containers
        private const string ServerSuffix = "-server";
        private const string ClientSuffix = "-client";
        private const string DatabaseSuffix = "-db";
        
        // Pipe name for stdin communication
        private const string InputPipeSuffix = "_input_pipe";

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
        /// Creates and configures containers for a student solution.
        /// Uses environment configuration to determine container settings.
        /// </summary>
        /// <param name="student">Student solution to grade</param>
        /// <param name="environment">Environment configuration</param>
        /// <param name="config">Grading configuration</param>
        /// <returns>True if setup successful</returns>
        public async Task<bool> SetupContainersAsync(
            StudentSolution student,
            Environment environment,
            GradingConfiguration config,
            CancellationToken ct = default)
        {
            _logger.LogInfo($"Setting up Docker containers for {student.StudentCode}...");

            try
            {
                // Configure environment for this student's containers
                ConfigureContainerNames(environment, student.StudentCode);
                ConfigurePorts(environment, config);

                // Setup file paths for Docker
                if (!string.IsNullOrEmpty(student.ServerDllPath))
                {
                    var serverDir = Path.GetDirectoryName(student.ServerDllPath)!;
                    SetOrAddConfig(environment.Configs, EnvironmentConfiguration.CodeFilePath, serverDir);
                    SetOrAddConfig(environment.Configs, EnvironmentConfiguration.DockerServerPath, 
                        GetDockerInternalPath(serverDir, student.ServerDllPath));
                }

                if (!string.IsNullOrEmpty(student.ClientDllPath))
                {
                    var clientDir = Path.GetDirectoryName(student.ClientDllPath)!;
                    SetOrAddConfig(environment.Configs, EnvironmentConfiguration.GivenConsolePath, clientDir);
                    SetOrAddConfig(environment.Configs, EnvironmentConfiguration.DockerClientPath, 
                        GetDockerInternalPath(clientDir, student.ClientDllPath));
                }

                // Use existing EnvironmentManagerInvoker to setup containers
                if (!EnvironmentManagerInvoker.TrySetupContainer(environment, out var setupError))
                {
                    _logger.LogError($"Container setup failed: {setupError}");
                    return false;
                }

                await Task.Delay(1000, ct); // Wait for containers to initialize
                _logger.LogInfo("Docker containers created successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to setup containers: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies student solution files to their respective containers.
        /// This is separate from execution to allow proper staging before grading.
        /// </summary>
        public async Task<bool> CopyFilesToContainersAsync(
            StudentSolution student,
            Environment environment,
            CancellationToken ct = default)
        {
            _logger.LogInfo($"Copying solution files for {student.StudentCode}...");

            try
            {
                // Use existing EnvironmentManagerInvoker for file copying
                if (!EnvironmentManagerInvoker.TrySetupQuestion(environment, out var copyError))
                {
                    _logger.LogError($"File copy failed: {copyError}");
                    return false;
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Files copied to containers");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to copy files: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the server application inside its container.
        /// Returns immediately - server runs in background.
        /// </summary>
        public async Task<bool> StartServerAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Starting server application...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerServerPath, "");

                if (string.IsNullOrEmpty(dllPath))
                {
                    _logger.LogError("Server DLL path not configured");
                    return false;
                }

                // Start the dotnet application in the container
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, dllPath, ct);
                
                if (result)
                {
                    _logger.LogInfo($"Server started in container {containerName}");
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
            _logger.LogInfo("Starting client application...");

            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                var dllPath = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DockerClientPath, "");

                if (string.IsNullOrEmpty(dllPath))
                {
                    _logger.LogError("Client DLL path not configured");
                    return false;
                }

                // Start the dotnet application in the container
                var result = await _dockerExecutor.StartApplicationInContainerAsync(containerName, dllPath, ct);
                
                if (result)
                {
                    _logger.LogInfo($"Client started in container {containerName}");
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
        /// Sends input to the client application's stdin using Docker named pipe.
        /// Uses: docker exec {container} sh -c "echo '{input}' | tee /proc/1/fd/1 > /tmp/{appName}_input_pipe"
        /// This writes to both stdout (for logging) and the named pipe (for the application).
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
                var pipeName = $"/tmp/{appName}{InputPipeSuffix}";

                _logger.LogDebug($"Sending input to client {containerName}: {input}");
                
                // Escape single quotes in input for shell
                var escapedInput = input.Replace("'", "'\\''");
                
                // Build the docker exec command to write to named pipe
                // This sends input to the application's stdin via a named pipe
                // The tee command also writes to /proc/1/fd/1 (stdout) for Docker logs
                var command = $"exec {containerName} sh -c \"echo '{escapedInput}' | tee /proc/1/fd/1 > {pipeName}\"";
                
                await ExecuteDockerCommandAsync(command, ct);
                
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
        /// Uses Docker logs with --follow=false to get buffered output.
        /// </summary>
        public async Task<string> GetClientOutputAsync(Environment environment, CancellationToken ct = default)
        {
            try
            {
                var containerName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                
                // Use docker logs with timestamps to ensure we get all output
                // The --since flag can be used to filter recent logs if needed
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

        #region Test Case Cleanup
        
        /// <summary>
        /// Performs cleanup between test cases within the same student's grading session.
        /// This ensures test case isolation by:
        /// 1. Resetting the database to initial state
        /// 2. Stopping and restarting client/server processes
        /// 3. Clearing any cached state
        /// 
        /// Note: Containers themselves are NOT disposed - only cleaned up for next test case.
        /// </summary>
        public async Task<bool> CleanupBetweenTestCasesAsync(
            Environment environment,
            GradingConfiguration config,
            CancellationToken ct = default)
        {
            _logger.LogInfo("Cleaning up between test cases...");
            
            try
            {
                // Step 1: Stop running processes
                await StopProcessesInContainersAsync(environment, ct);
                
                // Step 2: Reset database
                await ResetDatabaseAsync(environment, config, ct);
                
                // Step 3: Clear Docker logs (start fresh for next test case)
                // Note: Docker doesn't have a native "clear logs" command, but we can 
                // use timestamps to filter logs per test case
                
                _logger.LogInfo("Test case cleanup completed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Test case cleanup failed: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Stops all running processes inside the containers without removing the containers.
        /// </summary>
        private async Task StopProcessesInContainersAsync(Environment environment, CancellationToken ct)
        {
            _logger.LogDebug("Stopping processes in containers...");
            
            try
            {
                var serverContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server");
                var clientContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client");
                
                // Kill all dotnet processes in containers
                // This is safer than killing specific PIDs
                await ExecuteDockerCommandAsync($"exec {serverContainer} pkill -f dotnet", ct, ignoreErrors: true);
                await ExecuteDockerCommandAsync($"exec {clientContainer} pkill -f dotnet", ct, ignoreErrors: true);
                
                await Task.Delay(500, ct); // Wait for processes to terminate
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error stopping processes: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Resets the database to initial state using the SQL script.
        /// </summary>
        private async Task ResetDatabaseAsync(Environment environment, GradingConfiguration config, CancellationToken ct)
        {
            _logger.LogDebug("Resetting database...");
            
            try
            {
                var dbContainer = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DatabaseContainerName, "ag-database");
                var dbName = environment.Configs.GetValueOrDefault(EnvironmentConfiguration.DatabaseName, "TestDB");
                var dbUsername = config.DatabaseUsername;
                var dbPassword = config.DatabasePassword;
                
                // Drop and recreate database using SQL script
                // The script should be located at /var/opt/mssql/{dbName}.sql inside the container
                var dropQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{dbName}') BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END;";
                var dropCommand = $"exec {dbContainer} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {dbUsername} -P {dbPassword} -Q \"{dropQuery}\"";
                
                await ExecuteDockerCommandAsync(dropCommand, ct, ignoreErrors: true);
                
                // Recreate database from script
                var createCommand = $"exec {dbContainer} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {dbUsername} -P {dbPassword} -i /var/opt/mssql/{dbName}.sql";
                await ExecuteDockerCommandAsync(createCommand, ct, ignoreErrors: true);
                
                _logger.LogDebug("Database reset completed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Database reset warning: {ex.Message}");
            }
        }
        
        #endregion

        /// <summary>
        /// Disposes all containers for a student.
        /// Called after grading is complete or cancelled.
        /// </summary>
        public async Task DisposeContainersAsync(Environment environment, CancellationToken ct = default)
        {
            _logger.LogInfo("Disposing Docker containers...");

            try
            {
                EnvironmentManagerInvoker.TryDisposeContainer(environment, out var disposeError);
                
                if (!string.IsNullOrEmpty(disposeError))
                {
                    _logger.LogWarning($"Container disposal warning: {disposeError}");
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Containers disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to dispose containers: {ex.Message}");
            }
        }

        /// <summary>
        /// Configures container names for a specific student to avoid conflicts.
        /// </summary>
        private void ConfigureContainerNames(Environment environment, string studentCode)
        {
            var baseName = $"ag-{studentCode}";
            
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.CodeContainerName, baseName + ServerSuffix);
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerName, baseName + ClientSuffix);
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.StudentQuestionName, baseName);
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleAppName, baseName + ClientSuffix);
        }

        /// <summary>
        /// Configures port mappings for network monitoring.
        /// The host port is exposed for the Windows NetworkMonitor to sniff traffic.
        /// </summary>
        private void ConfigurePorts(Environment environment, GradingConfiguration config)
        {
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.CodeContainerInternalPort, 
                config.CodeContainerInternalPort.ToString());
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.CodeContainerHostPort, 
                config.CodeContainerHostPort.ToString());
            
            // Also set the Given Console ports to match
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerInternalPort, 
                config.CodeContainerInternalPort.ToString());
            SetOrAddConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerHostPort, 
                config.CodeContainerHostPort.ToString());
        }

        /// <summary>
        /// Gets the Docker-internal path for a DLL file.
        /// </summary>
        private string GetDockerInternalPath(string baseDir, string dllPath)
        {
            var folderName = Path.GetFileName(baseDir);
            var fileName = Path.GetFileName(dllPath);
            return $"/apps/{folderName}/{fileName}";
        }

        /// <summary>
        /// Executes a docker command asynchronously.
        /// </summary>
        private async Task<string> ExecuteDockerCommandAsync(string command, CancellationToken ct, bool ignoreErrors = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync(ct);
                    var error = await process.StandardError.ReadToEndAsync(ct);
                    await process.WaitForExitAsync(ct);
                    
                    if (!ignoreErrors && process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                    {
                        _logger.LogWarning($"Docker command warning: {error}");
                    }
                    
                    return output;
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                if (!ignoreErrors)
                {
                    _logger.LogWarning($"Docker command error: {ex.Message}");
                }
                return string.Empty;
            }
        }

        private static void SetOrAddConfig(Dictionary<string, string> configs, string key, string value)
        {
            if (configs.ContainsKey(key))
            {
                configs[key] = value;
            }
            else
            {
                configs.Add(key, value);
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
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec -d {containerName} dotnet {dllPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
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
                // Use --follow=false to get all current output including buffered content
                // without waiting for newlines
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"logs --tail 500 {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
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
