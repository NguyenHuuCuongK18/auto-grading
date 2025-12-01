using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service for managing Docker containers for grading.
    /// 
    /// This service provides:
    /// - Container lifecycle management (create, start, stop, remove)
    /// - File deployment to containers (separate from starting applications)
    /// - On-demand application startup (separated from CopyPublishFolder)
    /// - Console output reading using docker attach (for unbuffered output)
    /// - Input sending using named pipes
    /// 
    /// Key Changes from EnvironmentSetupService:
    /// 1. Container startup is separated from file deployment
    /// 2. Uses docker attach for reading console output instead of docker logs
    /// 3. Provides async methods for non-blocking operations
    /// </summary>
    public class DockerContainerManager : IDisposable
    {
        private readonly DockerCommandExecutor _executor;
        private Process? _clientAttachProcess;
        private Process? _serverAttachProcess;
        private StreamReader? _clientOutput;
        private StreamReader? _serverOutput;

        /// <summary>
        /// Event raised when client console output is received.
        /// </summary>
        public event EventHandler<string>? ClientOutputReceived;

        /// <summary>
        /// Event raised when server console output is received.
        /// </summary>
        public event EventHandler<string>? ServerOutputReceived;

        /// <summary>
        /// Creates a new Docker container manager.
        /// </summary>
        public DockerContainerManager()
        {
            _executor = new DockerCommandExecutor();
        }

        #region Container Lifecycle

        /// <summary>
        /// Checks if a container is running.
        /// </summary>
        public bool IsContainerRunning(string containerName)
        {
            return _executor.IsContainerRunning(containerName);
        }

        /// <summary>
        /// Checks if a container exists (running or stopped).
        /// </summary>
        public bool ContainerExists(string containerName)
        {
            return _executor.IsContainerExist(containerName);
        }

        /// <summary>
        /// Starts an existing container.
        /// </summary>
        public void StartContainer(string containerName)
        {
            _executor.StartExistedContainer(containerName);
        }

        /// <summary>
        /// Stops a running container.
        /// </summary>
        public void StopContainer(string containerName)
        {
            _executor.StopContainer(containerName);
        }

        /// <summary>
        /// Removes a container (stops if running).
        /// </summary>
        public void RemoveContainer(string containerName)
        {
            _executor.RemoveContainer(containerName);
        }

        #endregion

        #region File Deployment

        /// <summary>
        /// Deploys files to a container without starting the application.
        /// This is the first part of CopyPublishFolder separated out.
        /// </summary>
        /// <param name="containerName">Name of the container to deploy to.</param>
        /// <param name="localPath">Local path to the publish folder.</param>
        /// <param name="containerPath">Destination path in container (default: /apps).</param>
        public void DeployFilesToContainer(string containerName, string localPath, string containerPath = "/apps")
        {
            Console.WriteLine($"[Docker] Deploying files from {localPath} to {containerName}:{containerPath}");
            _executor.CopyFileToContainer(localPath, $"{containerName}:{containerPath}");
        }

        /// <summary>
        /// Creates the input pipe for a container application.
        /// Must be called before starting the application.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="appName">Application name for pipe naming.</param>
        public void CreateInputPipe(string containerName, string appName)
        {
            string inputPipe = $"/tmp/{appName}_input_pipe";
            
            // Create named pipe
            string createPipeCommand = $"{containerName} mkfifo \"{inputPipe}\"";
            _executor.ExecDockerCommand(createPipeCommand, 60000);
            
            // Start doorstop process to keep the pipe open
            string startDoorstopCommand = $"-d {containerName} sh -c \"sleep 10000 > {inputPipe}\"";
            _executor.ExecDockerCommand(startDoorstopCommand, 60000);
            
            Console.WriteLine($"[Docker] Created input pipe for {appName}: {inputPipe}");
        }

        #endregion

        #region Application Startup (On-Demand)

        /// <summary>
        /// Starts a .NET console application inside the container.
        /// This is the second part of WaitForPublishConsoleFileDeployment separated out.
        /// 
        /// Key differences from original:
        /// - Does not wait for deployment - returns immediately after starting
        /// - Uses attach process for output reading instead of docker logs
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="appName">Application name.</param>
        /// <param name="appPath">Path to the DLL inside the container (e.g., /apps/Q11/Q11.dll).</param>
        /// <returns>True if the application started successfully.</returns>
        public bool StartApplication(string containerName, string appName, string appPath)
        {
            try
            {
                string inputPipe = $"/tmp/{appName}_input_pipe";
                
                // Start the application with unbuffered output, reading from the input pipe
                // Using stdbuf to prevent buffering, output goes to container's stdout
                string command = $"-d -i -e DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1 {containerName} sh -c \"stdbuf -o0 -e0 dotnet {appPath} > /proc/1/fd/1 2>&1 < {inputPipe}\"";
                
                Console.WriteLine($"[Docker] Starting application {appName}...");
                _executor.ExecDockerCommand(command, 60000);
                
                Console.WriteLine($"[Docker] Application {appName} started. Monitor with: docker logs -f {containerName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Failed to start {appName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Waits for an application to be ready (process running and optional port check).
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="appName">Application name to check for.</param>
        /// <param name="expectedPort">Port to check (-1 to skip port check).</param>
        /// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
        /// <returns>True if the application is ready.</returns>
        public bool WaitForApplicationReady(string containerName, string appName, int expectedPort = -1, int timeoutMs = 70000)
        {
            return _executor.WaitForPublishConsoleFileDeployment(
                containerName, 
                appName, 
                $"/apps/{appName}/{appName}.dll", // Default path format
                expectedPort.ToString(), 
                timeoutMs);
        }

        #endregion

        #region Console Attach (for unbuffered output)

        /// <summary>
        /// Attaches to a container's console output using docker attach.
        /// This provides unbuffered output as the technician suggested.
        /// 
        /// Uses --sig-proxy=false so Ctrl+C only stops the attach, not the container.
        /// </summary>
        /// <param name="containerName">Container to attach to.</param>
        /// <param name="isClient">Whether this is the client (true) or server (false).</param>
        public void AttachToContainer(string containerName, bool isClient)
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

            var process = Process.Start(psi);
            if (process == null) return;

            if (isClient)
            {
                _clientAttachProcess = process;
                _clientOutput = process.StandardOutput;
                
                // Start background task to read client output
                Task.Run(async () =>
                {
                    while (!process.HasExited)
                    {
                        var line = await _clientOutput.ReadLineAsync();
                        if (line != null)
                        {
                            ClientOutputReceived?.Invoke(this, line);
                        }
                    }
                });
            }
            else
            {
                _serverAttachProcess = process;
                _serverOutput = process.StandardOutput;
                
                // Start background task to read server output
                Task.Run(async () =>
                {
                    while (!process.HasExited)
                    {
                        var line = await _serverOutput.ReadLineAsync();
                        if (line != null)
                        {
                            ServerOutputReceived?.Invoke(this, line);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Detaches from a container's console.
        /// </summary>
        /// <param name="isClient">Whether this is the client (true) or server (false).</param>
        public void DetachFromContainer(bool isClient)
        {
            if (isClient && _clientAttachProcess != null)
            {
                if (!_clientAttachProcess.HasExited)
                {
                    _clientAttachProcess.Kill();
                }
                _clientAttachProcess.Dispose();
                _clientAttachProcess = null;
                _clientOutput = null;
            }
            else if (!isClient && _serverAttachProcess != null)
            {
                if (!_serverAttachProcess.HasExited)
                {
                    _serverAttachProcess.Kill();
                }
                _serverAttachProcess.Dispose();
                _serverAttachProcess = null;
                _serverOutput = null;
            }
        }

        #endregion

        #region Input/Output Operations

        /// <summary>
        /// Sends input to a container application.
        /// Uses named pipes as configured by CreateInputPipe.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="appName">Application name.</param>
        /// <param name="input">Input text to send.</param>
        public void SendInput(string containerName, string appName, string input)
        {
            _executor.SendInputToContainer(containerName, appName, input);
        }

        /// <summary>
        /// Gets the current docker logs for a container.
        /// Note: This may be buffered. Use AttachToContainer for real-time output.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <returns>Log contents.</returns>
        public string GetContainerLogs(string containerName)
        {
            return _executor.GetContainerLogs(containerName) ?? string.Empty;
        }

        /// <summary>
        /// Gets container logs with tail limit and follow option.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="tailLines">Number of lines from the end (0 for all).</param>
        /// <param name="follow">Whether to follow/stream logs.</param>
        /// <returns>Process for reading logs.</returns>
        public Process? GetContainerLogsStream(string containerName, int tailLines = 0, bool follow = true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = follow 
                    ? $"logs -f --tail {tailLines} {containerName}"
                    : $"logs --tail {tailLines} {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return Process.Start(psi);
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleans up resources between test cases.
        /// - Kills and restarts the application
        /// - Clears output buffers
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="appName">Application name.</param>
        public void CleanupForNextTestCase(string containerName, string appName)
        {
            try
            {
                // Kill the running application
                string killCommand = $"{containerName} sh -c \"APP_NAME={appName} && if [ -f /tmp/$APP_NAME.pid ]; then kill `cat /tmp/$APP_NAME.pid` 2>/dev/null; rm -f /tmp/$APP_NAME.pid /tmp/$APP_NAME.port; fi\"";
                _executor.ExecDockerCommand(killCommand, 30000);
                
                Console.WriteLine($"[Docker] Cleaned up {appName} for next test case");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Cleanup warning for {appName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes all resources.
        /// </summary>
        public void Dispose()
        {
            DetachFromContainer(true);
            DetachFromContainer(false);
        }

        #endregion
    }
}
