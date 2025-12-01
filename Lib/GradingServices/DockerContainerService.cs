using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using Domain.Entities.Constants;

namespace GradingServices
{
    /// <summary>
    /// Docker container service for grading.
    /// Manages client, server, and database containers with stage-based console capture.
    /// 
    /// Key features:
    /// - Uses docker run with -t flag for TTY allocation
    /// - Uses docker attach --sig-proxy=false for console reading
    /// - Uses named pipes for stdin input to containers
    /// - Supports stage-based output capture (1-2 sec wait after input)
    /// </summary>
    public class DockerContainerService : IDockerContainerService, IDisposable
    {
        private readonly DockerCommandExecutor _executor;
        private readonly string _networkName;
        private readonly int _serverPort;
        
        private string? _clientContainerName;
        private string? _serverContainerName;
        private string? _databaseContainerName;
        
        private Process? _clientAttachProcess;
        private Process? _serverAttachProcess;
        private StringBuilder _clientOutputBuffer = new();
        private StringBuilder _serverOutputBuffer = new();
        private readonly object _clientLock = new();
        private readonly object _serverLock = new();

        // Console output per stage
        private readonly Dictionary<int, string> _clientStageOutput = new();
        private readonly Dictionary<int, string> _serverStageOutput = new();
        private int _currentStage = 0;
        private string _lastClientOutput = string.Empty;
        private string _lastServerOutput = string.Empty;

        // Container configuration
        private const string DefaultImageName = "fptuxaes/aes-dotnet8:latest";
        private const string DatabaseImageName = "mcr.microsoft.com/mssql/server:2022-latest";
        private const string ContainerPrefix = "ag-";

        public DockerContainerService(string networkName = "ag-network", int serverPort = 5000)
        {
            _executor = new DockerCommandExecutor();
            _networkName = networkName;
            _serverPort = serverPort;
        }

        /// <summary>
        /// Starts the MSSQL database container.
        /// </summary>
        public async Task<bool> StartDatabaseContainerAsync(CancellationToken ct = default)
        {
            _databaseContainerName = $"{ContainerPrefix}database";
            
            try
            {
                // Check if already running
                if (_executor.IsContainerRunning(_databaseContainerName))
                {
                    Console.WriteLine($"[Docker] Database container {_databaseContainerName} already running");
                    return true;
                }

                // Create network if not exists
                try
                {
                    _executor.CreateNetwork(_networkName);
                }
                catch { /* Network might already exist */ }

                // Run database container
                var envVars = new Dictionary<string, string>
                {
                    { "ACCEPT_EULA", "Y" },
                    { "MSSQL_SA_PASSWORD", "YourStrong@Passw0rd" }
                };

                var dockerBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                {
                    ImageName = DatabaseImageName,
                    ContainerName = _databaseContainerName,
                    DockerNetwork = _networkName,
                    ContainerPort = 1433,
                    HostPort = 1433,
                    EnvironmentVariables = envVars
                };

                _executor.RunContainer(dockerBase);
                
                // Wait for SQL Server to be ready
                await Task.Delay(5000, ct);
                Console.WriteLine($"[Docker] Database container {_databaseContainerName} started");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to start database container: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates the client container with TTY flag (-t) without starting the application.
        /// </summary>
        public async Task<bool> CreateClientContainerAsync(string studentCode, string clientPath, CancellationToken ct = default)
        {
            _clientContainerName = $"{ContainerPrefix}client";
            
            try
            {
                // Remove existing container if present
                if (_executor.IsContainerExist(_clientContainerName))
                {
                    _executor.RemoveContainer(_clientContainerName);
                }

                // Create network if not exists
                try
                {
                    _executor.CreateNetwork(_networkName);
                }
                catch { /* Network might already exist */ }

                // Run container with -t flag for TTY allocation using raw command
                var command = $"docker run -d -t " +
                             $"--name {_clientContainerName} " +
                             $"--network {_networkName} " +
                             $"-e DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1 " +
                             $"{DefaultImageName}";

                RunCommand(command);
                await Task.Delay(1000, ct);

                // Copy client files to container
                if (!string.IsNullOrEmpty(clientPath) && Directory.Exists(clientPath))
                {
                    _executor.CopyFileToContainer(clientPath, $"{_clientContainerName}:/apps");
                    Console.WriteLine($"[Docker] Copied client files from {clientPath}");
                }

                Console.WriteLine($"[Docker] Client container {_clientContainerName} created");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to create client container: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates the server container with TTY flag (-t) without starting the application.
        /// </summary>
        public async Task<bool> CreateServerContainerAsync(string studentCode, string serverPath, CancellationToken ct = default)
        {
            _serverContainerName = $"{ContainerPrefix}server";
            
            try
            {
                // Remove existing container if present
                if (_executor.IsContainerExist(_serverContainerName))
                {
                    _executor.RemoveContainer(_serverContainerName);
                }

                // Run container with -t flag for TTY allocation using raw command
                var command = $"docker run -d -t " +
                             $"--name {_serverContainerName} " +
                             $"--network {_networkName} " +
                             $"-p {_serverPort}:{_serverPort} " +
                             $"-e DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1 " +
                             $"{DefaultImageName}";

                RunCommand(command);
                await Task.Delay(1000, ct);

                // Copy server files to container
                if (!string.IsNullOrEmpty(serverPath) && Directory.Exists(serverPath))
                {
                    _executor.CopyFileToContainer(serverPath, $"{_serverContainerName}:/apps");
                    Console.WriteLine($"[Docker] Copied server files from {serverPath}");
                }

                Console.WriteLine($"[Docker] Server container {_serverContainerName} created");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to create server container: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the client application inside the container and attaches for console reading.
        /// </summary>
        public async Task<bool> StartClientApplicationAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_clientContainerName))
                return false;

            try
            {
                // Create named pipe for input
                var inputPipe = $"/tmp/{_clientContainerName}_input_pipe";
                RunDockerExec(_clientContainerName, $"mkfifo {inputPipe}", waitForExit: true);

                // Start a background process to keep the pipe open
                RunDockerExec(_clientContainerName, $"sh -c 'sleep 100000 > {inputPipe}'", waitForExit: false);
                await Task.Delay(500, ct);

                // Find the DLL to run
                var dllPath = await FindDllInContainerAsync(_clientContainerName, ct);
                if (string.IsNullOrEmpty(dllPath))
                {
                    Console.WriteLine("[Docker] No DLL found in client container");
                    return false;
                }

                // Start the application with input from pipe
                var startCmd = $"sh -c 'stdbuf -o0 -e0 dotnet {dllPath} < {inputPipe}'";
                RunDockerExec(_clientContainerName, startCmd, waitForExit: false);
                await Task.Delay(500, ct);

                // Start attach process for reading output
                StartAttachProcess(_clientContainerName, ref _clientAttachProcess, _clientOutputBuffer, _clientLock);

                Console.WriteLine($"[Docker] Client application started with DLL: {dllPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to start client application: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the server application inside the container and attaches for console reading.
        /// </summary>
        public async Task<bool> StartServerApplicationAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_serverContainerName))
                return false;

            try
            {
                // Find the DLL to run
                var dllPath = await FindDllInContainerAsync(_serverContainerName, ct);
                if (string.IsNullOrEmpty(dllPath))
                {
                    Console.WriteLine("[Docker] No DLL found in server container");
                    return false;
                }

                // Start the server application (no input pipe needed for server typically)
                var startCmd = $"sh -c 'stdbuf -o0 -e0 dotnet {dllPath}'";
                RunDockerExec(_serverContainerName, startCmd, waitForExit: false);
                await Task.Delay(500, ct);

                // Start attach process for reading output
                StartAttachProcess(_serverContainerName, ref _serverAttachProcess, _serverOutputBuffer, _serverLock);

                Console.WriteLine($"[Docker] Server application started with DLL: {dllPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to start server application: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends input to the client container via named pipe.
        /// </summary>
        public async Task<bool> SendClientInputAsync(string input, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_clientContainerName))
                return false;

            try
            {
                var inputPipe = $"/tmp/{_clientContainerName}_input_pipe";
                var safeInput = input.Replace("'", "'\\''");
                
                // Send input via echo to named pipe
                var command = $"sh -c \"echo '{safeInput}' | tee /proc/1/fd/1 > {inputPipe}\"";
                RunDockerExec(_clientContainerName, command, waitForExit: true);
                
                Console.WriteLine($"[Docker] Sent input to client: {input}");
                
                // Wait for output to stabilize (1-2 seconds as specified)
                await Task.Delay(Common.GradingConstants.PostInputDelayMs, ct);
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to send client input: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the current client console output from the attached process.
        /// </summary>
        public Task<string> GetClientConsoleOutputAsync(CancellationToken ct = default)
        {
            lock (_clientLock)
            {
                return Task.FromResult(_clientOutputBuffer.ToString());
            }
        }

        /// <summary>
        /// Gets the current server console output from the attached process.
        /// </summary>
        public Task<string> GetServerConsoleOutputAsync(CancellationToken ct = default)
        {
            lock (_serverLock)
            {
                return Task.FromResult(_serverOutputBuffer.ToString());
            }
        }

        /// <summary>
        /// Captures the current stage output by comparing with previous output.
        /// </summary>
        public async Task CaptureStageOutputAsync(int stage, CancellationToken ct = default)
        {
            // Wait for output to stabilize
            await Task.Delay(Common.GradingConstants.PostStageChangeDelayMs, ct);

            var currentClientOutput = await GetClientConsoleOutputAsync(ct);
            var currentServerOutput = await GetServerConsoleOutputAsync(ct);

            // Get new output since last capture
            var newClientOutput = GetNewOutput(currentClientOutput, _lastClientOutput);
            var newServerOutput = GetNewOutput(currentServerOutput, _lastServerOutput);

            // Store stage output
            if (!string.IsNullOrEmpty(newClientOutput))
            {
                _clientStageOutput[stage] = newClientOutput;
            }
            if (!string.IsNullOrEmpty(newServerOutput))
            {
                _serverStageOutput[stage] = newServerOutput;
            }

            // Update last output
            _lastClientOutput = currentClientOutput;
            _lastServerOutput = currentServerOutput;

            Console.WriteLine($"[Docker] Captured stage {stage} output - Client: {newClientOutput.Length} chars, Server: {newServerOutput.Length} chars");
        }

        /// <summary>
        /// Gets the client output for a specific stage.
        /// </summary>
        public string? GetClientOutputForStage(int stage)
        {
            return _clientStageOutput.TryGetValue(stage, out var output) ? output : null;
        }

        /// <summary>
        /// Gets the server output for a specific stage.
        /// </summary>
        public string? GetServerOutputForStage(int stage)
        {
            return _serverStageOutput.TryGetValue(stage, out var output) ? output : null;
        }

        /// <summary>
        /// Stops and removes the client container.
        /// </summary>
        public async Task StopClientContainerAsync(CancellationToken ct = default)
        {
            try
            {
                _clientAttachProcess?.Kill();
                _clientAttachProcess?.Dispose();
                _clientAttachProcess = null;

                if (!string.IsNullOrEmpty(_clientContainerName) && _executor.IsContainerExist(_clientContainerName))
                {
                    _executor.RemoveContainer(_clientContainerName);
                    Console.WriteLine($"[Docker] Removed client container {_clientContainerName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Error stopping client container: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// Stops and removes the server container.
        /// </summary>
        public async Task StopServerContainerAsync(CancellationToken ct = default)
        {
            try
            {
                _serverAttachProcess?.Kill();
                _serverAttachProcess?.Dispose();
                _serverAttachProcess = null;

                if (!string.IsNullOrEmpty(_serverContainerName) && _executor.IsContainerExist(_serverContainerName))
                {
                    _executor.RemoveContainer(_serverContainerName);
                    Console.WriteLine($"[Docker] Removed server container {_serverContainerName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Error stopping server container: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// Stops and removes the database container.
        /// </summary>
        public async Task StopDatabaseContainerAsync(CancellationToken ct = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(_databaseContainerName) && _executor.IsContainerExist(_databaseContainerName))
                {
                    _executor.RemoveContainer(_databaseContainerName);
                    Console.WriteLine($"[Docker] Removed database container {_databaseContainerName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Error stopping database container: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        /// <summary>
        /// Disposes all containers.
        /// </summary>
        public async Task DisposeAllContainersAsync(CancellationToken ct = default)
        {
            await StopClientContainerAsync(ct);
            await StopServerContainerAsync(ct);
            await StopDatabaseContainerAsync(ct);
            
            // Clear stage data
            _clientStageOutput.Clear();
            _serverStageOutput.Clear();
            _clientOutputBuffer.Clear();
            _serverOutputBuffer.Clear();
            _lastClientOutput = string.Empty;
            _lastServerOutput = string.Empty;
            _currentStage = 0;
        }

        public void Dispose()
        {
            DisposeAllContainersAsync().Wait();
        }

        #region Helper Methods

        /// <summary>
        /// Starts an attach process to read container console output.
        /// Uses --sig-proxy=false so Ctrl+C doesn't propagate.
        /// </summary>
        private void StartAttachProcess(string containerName, ref Process? attachProcess, StringBuilder outputBuffer, object lockObj)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"attach --sig-proxy=false {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            attachProcess = new Process { StartInfo = psi };
            attachProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    lock (lockObj)
                    {
                        outputBuffer.AppendLine(e.Data);
                    }
                }
            };
            attachProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    lock (lockObj)
                    {
                        outputBuffer.AppendLine(e.Data);
                    }
                }
            };

            attachProcess.Start();
            attachProcess.BeginOutputReadLine();
            attachProcess.BeginErrorReadLine();

            Console.WriteLine($"[Docker] Attached to container {containerName} for console reading");
        }

        /// <summary>
        /// Finds the main application .dll file in the container's /apps directory.
        /// Looks for DLLs named Project*, Q11*, Q12*, Client*, Server* etc.
        /// </summary>
        private async Task<string?> FindDllInContainerAsync(string containerName, CancellationToken ct)
        {
            try
            {
                // Find all DLLs, excluding runtimes and known framework DLLs
                var output = RunDockerExecAndCapture(containerName, "find /apps -name '*.dll' -type f 2>/dev/null | grep -v 'runtimes/' | head -50");
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                // Priority patterns for finding the main application DLL
                var priorityPatterns = new[]
                {
                    "Project1", "Project2", "Project3", "Project4", "Project5",
                    "Q11", "Q12", "Q13", "Q14", "Q15",
                    "Client", "Server",
                    "Calculator"
                };
                
                // Framework DLLs to exclude
                var excludePatterns = new[]
                {
                    "Microsoft.", "System.", "Newtonsoft.", "Dapper.",
                    "ClosedXML", "EPPlus", "DocumentFormat", "SkiaSharp",
                    "runtimes/", "ref/", "analyzers/"
                };
                
                // First pass: look for priority pattern DLLs
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                        
                    var fileName = Path.GetFileName(trimmed);
                    
                    // Check if it's a priority pattern
                    if (priorityPatterns.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Verify it's not an excluded pattern
                        if (!excludePatterns.Any(e => fileName.Contains(e, StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine($"[Docker] Found application DLL: {trimmed}");
                            return trimmed;
                        }
                    }
                }
                
                // Second pass: look for any non-framework DLL
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                        
                    var fileName = Path.GetFileName(trimmed);
                    
                    // Verify it's not an excluded pattern
                    if (!excludePatterns.Any(e => fileName.Contains(e, StringComparison.OrdinalIgnoreCase) || trimmed.Contains(e)))
                    {
                        Console.WriteLine($"[Docker] Found potential application DLL: {trimmed}");
                        return trimmed;
                    }
                }

                Console.WriteLine($"[Docker] No application DLL found in container {containerName}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Error finding DLL: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Runs a docker exec command.
        /// </summary>
        private void RunDockerExec(string containerName, string command, bool waitForExit = true)
        {
            var fullCommand = waitForExit 
                ? $"docker exec {containerName} {command}"
                : $"docker exec -d {containerName} {command}";
            
            RunCommand(fullCommand, waitForExit);
        }

        /// <summary>
        /// Runs a docker exec command and captures output.
        /// </summary>
        private string RunDockerExecAndCapture(string containerName, string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                    ? $"/c docker exec {containerName} {command}"
                    : $"-c \"docker exec {containerName} {command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return output;
        }

        /// <summary>
        /// Runs a shell command.
        /// </summary>
        private void RunCommand(string command, bool waitForExit = true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                    ? $"/c {command}"
                    : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            if (waitForExit)
            {
                process.WaitForExit(30000);
            }
        }

        /// <summary>
        /// Gets new output by comparing current with previous.
        /// </summary>
        private static string GetNewOutput(string current, string previous)
        {
            if (string.IsNullOrEmpty(previous))
                return current;

            if (current.StartsWith(previous))
                return current.Substring(previous.Length);

            return current;
        }

        #endregion
    }
}
