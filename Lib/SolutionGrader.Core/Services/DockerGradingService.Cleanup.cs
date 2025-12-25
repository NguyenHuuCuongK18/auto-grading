// This file contains the Cleanup region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using Domain.Models;
using Domain.Models.Configuration;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Cleanup

        /// <summary>
        /// Cleans up between test cases (same student) by:
        /// 1. Killing any running dotnet processes in containers (SIGTERM then SIGKILL)
        /// 2. Killing sleep processes that keep input pipes open
        /// 3. REMOVING files from /apps folder (this also removes logs)
        /// 4. Clearing network captures
        /// 5. Waiting for port release (inside container AND host)
        /// 
        /// CRITICAL: This cleanup must be thorough to prevent "Address already in use" errors.
        /// The files will be re-copied before the next test case starts.
        /// This approach is much faster than disposing/rebuilding containers.
        /// 
        /// OPTIMIZATION: Reduced delays from 1.5s to 500ms for faster cleanup.
        /// </summary>

        /// <summary>
        /// Kills dotnet processes in a container using PID-based approach.
        /// This is more reliable than pkill on Windows.
        /// Steps: 1) Run 'ps aux' to list processes, 2) Parse output to find dotnet PIDs, 3) Kill by PID
        /// </summary>
        private async Task KillDotnetProcessesInContainerAsync(string container, string containerType)
        {
            OnProgress($"Cleanup: Finding dotnet processes in {containerType} container...");

            // Get list of processes using 'ps aux'
            var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput($"{container} ps aux", 5000);

            if (!success || string.IsNullOrEmpty(output))
            {
                OnProgress($"Cleanup: {containerType} - Could not list processes (ps aux failed), using safe kill fallback...");
                // Use safe kill method that excludes PID 1 to avoid killing the container
                _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(container), 5000);
                return;
            }

            // Parse output to find dotnet process PIDs (skip PID 1 which is the main container process)
            var pids = ParseDotnetPidsFromPsOutput(output);

            if (pids.Count == 0)
            {
                OnProgress($"Cleanup: {containerType} - No dotnet processes found");
                return;
            }

            OnProgress($"Cleanup: {containerType} - Found {pids.Count} dotnet process(es): PIDs [{string.Join(", ", pids)}]");

            // Kill each process by PID using SIGTERM (graceful)
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
        private async Task ForceKillDotnetProcessesInContainerAsync(string container, string containerType)
        {
            // Get list of processes using 'ps aux'
            var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput($"{container} ps aux", 5000);

            if (!success || string.IsNullOrEmpty(output))
            {
                // Use safe kill method that excludes PID 1 to avoid killing the container
                _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(container), 5000);
                return;
            }

            // Parse output to find dotnet process PIDs
            var pids = ParseDotnetPidsFromPsOutput(output);

            if (pids.Count == 0)
            {
                OnProgress($"Cleanup: {containerType} - No remaining dotnet processes");
                return;
            }

            OnProgress($"Cleanup: {containerType} - Force killing {pids.Count} remaining process(es): PIDs [{string.Join(", ", pids)}]");

            // Force kill each process by PID using SIGKILL (-9)
            foreach (var pid in pids)
            {
                _dockerExecutor.TryExecDockerCommand($"{container} kill -9 {pid}", 5000);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Parses the output of 'ps aux' to find PIDs of dotnet processes.
        /// Example ps aux output:
        /// USER       PID %CPU %MEM    VSZ   RSS TTY      STAT START   TIME COMMAND
        /// root         1  0.0  0.0   2520  1224 ?        Ss   13:27   0:00 tail -f /dev/null
        /// root        19  4.3  0.2 273635552 36016 ?     Ssl  13:27   0:00 dotnet /apps/test/Project11.dll
        /// </summary>
        private List<int> ParseDotnetPidsFromPsOutput(string psOutput)
        {
            var pids = new List<int>();
            var lines = psOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Skip header line
                if (line.Contains("PID") && line.Contains("COMMAND"))
                    continue;

                // Check if this line contains 'dotnet'
                if (!line.Contains("dotnet", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Parse PID from the line (format: USER PID %CPU %MEM ...)
                // PID is typically the second column
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int pid))
                {
                    // Skip PID 1 (main container process - killing it would stop the container)
                    if (pid != 1)
                    {
                        pids.Add(pid);
                    }
                }
            }

            return pids;
        }

        /// <summary>
        /// Disposes and rebuilds the DATABASE container between students.
        /// This ensures:
        /// - Correct DB is loaded if we switch papers
        /// - Database is absolutely the same for all students
        /// </summary>
        private async Task ResetDatabaseContainerAsync(DockerGradingConfig config)
        {
            var databaseContainer = config.DatabaseContainerName;
            OnProgress($"[Database] Resetting database container {databaseContainer} for new student...");

            // Stop and remove existing database container
            try { _dockerExecutor.StopContainer(databaseContainer, 10000); } catch { }
            try { _dockerExecutor.RemoveContainer(databaseContainer, 10000); } catch { }

            // OPTIMIZATION: Wait for container to be fully removed (dynamic check vs fixed 500ms)
            await WaitForContainerRemovedAsync(databaseContainer, maxWaitSeconds: 5);

            // Recreate the database container
            await SetupDatabaseContainerAsync(config);

            OnProgress($"[Database] Database container reset complete");
        }

        /// <summary>
        /// Saves Docker container logs to persistent files in the student's result directory.
        /// 
        /// CRITICAL: This must be called BEFORE container cleanup, as docker logs are destroyed
        /// when containers are removed. These logs are essential for debugging test failures,
        /// especially when student's server exits immediately (e.g., "Hello World" and exit).
        /// 
        /// Logs are saved to:
        /// - {studentResultPath}/DockerLogs/server.log
        /// - {studentResultPath}/DockerLogs/client.log
        /// </summary>
        /// <param name="serverContainer">Server container name</param>
        /// <param name="clientContainer">Client container name</param>
        /// <param name="studentResultPath">Path to student's result directory</param>
        private async Task SaveDockerLogsAsync(string serverContainer, string clientContainer, string studentResultPath)
        {
            var logsDir = Path.Combine(studentResultPath, "DockerLogs");
            Directory.CreateDirectory(logsDir);

            // Save server logs
            try
            {
                var serverLogPath = Path.Combine(logsDir, "server.log");
                var serverLogs = _dockerExecutor.GetContainerLogs(serverContainer);

                if (!string.IsNullOrEmpty(serverLogs))
                {
                    await File.WriteAllTextAsync(serverLogPath, serverLogs);
                    OnProgress($"[Docker Logs] Server logs saved to {serverLogPath} ({serverLogs.Length} bytes)");
                }
                else
                {
                    await File.WriteAllTextAsync(serverLogPath, "[No server logs captured]");
                    OnProgress($"[Docker Logs] No server logs to save (container may not have started)");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Logs] Warning: Failed to save server logs: {ex.Message}");
            }

            // Save client logs
            try
            {
                var clientLogPath = Path.Combine(logsDir, "client.log");
                var clientLogs = _dockerExecutor.GetContainerLogs(clientContainer);

                if (!string.IsNullOrEmpty(clientLogs))
                {
                    await File.WriteAllTextAsync(clientLogPath, clientLogs);
                    OnProgress($"[Docker Logs] Client logs saved to {clientLogPath} ({clientLogs.Length} bytes)");
                }
                else
                {
                    await File.WriteAllTextAsync(clientLogPath, "[No client logs captured]");
                    OnProgress($"[Docker Logs] No client logs to save (container may not have started)");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Logs] Warning: Failed to save client logs: {ex.Message}");
            }

            OnProgress($"[Docker Logs] All docker logs saved to {logsDir}");
        }

        /// <summary>
        /// Reads a file from a Docker container using docker exec cat.
        /// </summary>
        private string ReadFileFromContainer(string containerName, string filePath)
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerName} cat {filePath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Failed to read file {filePath} from container {containerName}");
            }

            return output;
        }

        /// <summary>
        /// Read a file from container starting from a specific byte position.
        /// Returns the new content and the updated file position.
        /// This enables incremental reading to separate output by stage.
        /// </summary>
        /// <param name="containerName">Container name</param>
        /// <param name="filePath">Path to file in container</param>
        /// <param name="startPosition">Byte position to start reading from</param>
        /// <returns>Tuple of (new content, updated position)</returns>
        private (string newContent, long newPosition) ReadFileFromContainerIncremental(
            string containerName,
            string filePath,
            long startPosition)
        {
            // Use tail with byte offset to read from specific position
            // tail -c +N reads from byte N (1-indexed, so we add 1 to 0-indexed position)
            var tailPosition = startPosition + 1;

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerName} tail -c +{tailPosition} {filePath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var newContent = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                // File doesn't exist yet or other error - return empty content and same position
                return ("", startPosition);
            }

            // Calculate new position (old position + bytes read)
            var bytesRead = Encoding.UTF8.GetByteCount(newContent);
            var newPosition = startPosition + bytesRead;

            return (newContent, newPosition);
        }

        /// <summary>
        /// Cleans up code containers (server, client) after each student.
        /// CRITICAL: Database container is SHARED and NOT removed - only server/client containers are removed.
        /// Database instance cleanup is handled separately via CleanupDatabaseInstanceAsync.
        /// </summary>
        /// <summary>
        /// Export per-stage log files from unified container to student result directory.
        /// NOTE: This method is deprecated - logs are now exported per test case in ExportStageLogsForTestCaseAsync.
        /// Keeping this method for reference but it's no longer called.
        /// </summary>
        private async Task ExportLogsFromUnifiedContainerAsync(string unifiedContainer, string studentResultPath)
        {
            var logsDir = Path.Combine(studentResultPath, "ProcessLogs");
            Directory.CreateDirectory(logsDir);

            OnProgress($"[Unified] Exporting logs to {logsDir}");

            // Export all server and client log files using docker cp
            try
            {
                // Copy all server log files
                var serverCopyCmd = $"docker cp {unifiedContainer}:/apps/server/. {logsDir}/";
                _commandExecutor.RunCommand(serverCopyCmd, null, null, 10000);
                OnProgress($"[Unified] Exported server logs");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to export server logs: {ex.Message}");
            }

            try
            {
                // Copy all client log files
                var clientCopyCmd = $"docker cp {unifiedContainer}:/apps/client/. {logsDir}/";
                _commandExecutor.RunCommand(clientCopyCmd, null, null, 10000);
                OnProgress($"[Unified] Exported client logs");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to export client logs: {ex.Message}");
            }

            // Clean up: remove DLL files from logs directory, keep only log files
            try
            {
                var dllFiles = Directory.GetFiles(logsDir, "*.dll", SearchOption.AllDirectories);
                foreach (var dllFile in dllFiles)
                {
                    try { File.Delete(dllFile); } catch { }
                }
                var exeFiles = Directory.GetFiles(logsDir, "*.exe", SearchOption.AllDirectories);
                foreach (var exeFile in exeFiles)
                {
                    try { File.Delete(exeFile); } catch { }
                }
                var jsonFiles = Directory.GetFiles(logsDir, "appsettings.json", SearchOption.AllDirectories);
                foreach (var jsonFile in jsonFiles)
                {
                    try { File.Delete(jsonFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to clean up non-log files: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Export per-stage log files for a specific test case to ProcessLogs/TC# subdirectory.
        /// Logs are organized as: ProcessLogs/TC1/client-TC1-stage-1.log, ProcessLogs/TC1/server-TC1-stage-2.log, etc.
        /// This method is called after each test case completes.
        /// </summary>
        /// <param name="unifiedContainer">Container name</param>
        /// <param name="studentResultPath">Student result directory path</param>
        /// <param name="testCaseName">Test case name (e.g., "TC1")</param>
        private async Task ExportStageLogsForTestCaseAsync(string unifiedContainer, string studentResultPath, string testCaseName)
        {
            // Create ProcessLogs/TC# subdirectory
            var tcLogsDir = Path.Combine(studentResultPath, "ProcessLogs", testCaseName);
            Directory.CreateDirectory(tcLogsDir);

            OnProgress($"[Unified] Exporting stage logs for {testCaseName} to {tcLogsDir}");

            // Export client stage logs
            if (_lastTestCaseClientOutputs != null && _lastTestCaseClientOutputs.Count > 0)
            {
                foreach (var (stage, output) in _lastTestCaseClientOutputs.OrderBy(kv => kv.Key))
                {
                    if (!string.IsNullOrEmpty(output))
                    {
                        var logFileName = $"client-{testCaseName}-stage-{stage}.log";
                        var logFilePath = Path.Combine(tcLogsDir, logFileName);

                        try
                        {
                            await File.WriteAllTextAsync(logFilePath, output);
                            OnProgress($"  Exported {logFileName} ({output.Length} chars)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"  WARNING: Failed to export {logFileName}: {ex.Message}");
                        }
                    }
                }
            }

            // Export server stage logs
            if (_lastTestCaseServerOutputs != null && _lastTestCaseServerOutputs.Count > 0)
            {
                foreach (var (stage, output) in _lastTestCaseServerOutputs.OrderBy(kv => kv.Key))
                {
                    if (!string.IsNullOrEmpty(output))
                    {
                        var logFileName = $"server-{testCaseName}-stage-{stage}.log";
                        var logFilePath = Path.Combine(tcLogsDir, logFileName);

                        try
                        {
                            await File.WriteAllTextAsync(logFilePath, output);
                            OnProgress($"  Exported {logFileName} ({output.Length} chars)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"  WARNING: Failed to export {logFileName}: {ex.Message}");
                        }
                    }
                }
            }

            OnProgress($"[Unified] Stage logs for {testCaseName} exported successfully");
        }

        /// <summary>
        /// Clear old log files in the unified container before executing a new test case.
        /// This prevents log accumulation across test cases (same container is reused).
        /// </summary>
        private void ClearStageLogsInContainer(string unifiedContainer)
        {
            try
            {
                // Remove unified server log file
                var clearServerCmd = $"docker exec {unifiedContainer} /bin/bash -c \"rm -f /apps/server/server.log\"";
                _commandExecutor.RunCommand(clearServerCmd, null, null, 5000);

                // Remove unified client log file
                var clearClientCmd = $"docker exec {unifiedContainer} /bin/bash -c \"rm -f /apps/client/client.log\"";
                _commandExecutor.RunCommand(clearClientCmd, null, null, 5000);

                OnProgress($"[Unified] Cleared old log files for new test case");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to clear old logs: {ex.Message}");
            }
        }

        /// <summary>
        /// CRITICAL FIX: Stop server and client processes before starting a new test case.
        /// 
        /// PROBLEM:
        /// Each test case reuses the same unified container across all test cases.
        /// If TC2 starts a server and TC3 doesn't explicitly start one, the server from TC2
        /// continues running. When TC3's client starts, it connects to TC2's server,
        /// causing TC3 to receive unexpected data and fail.
        /// 
        /// SOLUTION:
        /// Stop ONLY the server and client processes at the START of each new test case.
        /// This ensures each test case starts with a clean slate - no processes from previous
        /// test cases can interfere.
        /// 
        /// IMPORTANT: We use CloseServer and CloseClient separately instead of StopAll because:
        /// 1. StopAll would also stop the 'keeper' process that holds the named pipe open
        /// 2. The network monitor runs in a separate container and is NOT affected by supervisord
        /// 3. Stopping only server/client ensures proper isolation without breaking infrastructure
        /// </summary>
        /// <param name="unifiedContainer">Name of the unified container</param>
        private async Task StopAllProcessesForNewTestCaseAsync(string unifiedContainer)
        {
            OnProgress($"[TestCase Isolation] Stopping server and client from previous test case...");
            
            try
            {
                // Stop server process via supervisord (if running)
                // Using CloseServer instead of StopAll to preserve the keeper process
                var stopServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseServer 0";
                try
                {
                    _commandExecutor.RunCommand(stopServerCmd, null, null, 5000);
                    OnProgress($"[TestCase Isolation] Server process stopped");
                }
                catch (Exception ex)
                {
                    // Server might not have been running - this is OK
                    OnProgress($"[TestCase Isolation] Server was not running or already stopped: {ex.Message}");
                }
                
                // Stop client process via supervisord (if running)
                // Using CloseClient instead of StopAll to preserve the keeper process
                var stopClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseClient 0";
                try
                {
                    _commandExecutor.RunCommand(stopClientCmd, null, null, 5000);
                    OnProgress($"[TestCase Isolation] Client process stopped");
                }
                catch (Exception ex)
                {
                    // Client might not have been running - this is OK
                    OnProgress($"[TestCase Isolation] Client was not running or already stopped: {ex.Message}");
                }
                
                // Wait briefly to ensure processes have fully terminated
                await Task.Delay(ProcessStopDelayMs);
                
                OnProgress($"[TestCase Isolation] Server and client stopped - ready for new test case");
            }
            catch (Exception ex)
            {
                // Log warning but don't fail - processes might not have been running
                OnProgress($"[TestCase Isolation] WARNING: Failed to stop processes: {ex.Message}");
                OnProgress($"[TestCase Isolation] This may be expected if no processes were running");
            }
        }

        /// <summary>
        /// Cleanup unified container after grading.
        /// Removes the container and unregisters student from shared monitor.
        /// CRITICAL: Verifies container removal and adds to retry queue if failed,
        /// preventing resource exhaustion during batch grading of large numbers of students.
        /// </summary>
        private async Task CleanupUnifiedContainerAsync(string unifiedContainer, string studentCode)
        {
            OnProgress($"[Unified] Starting cleanup for {unifiedContainer}");
            bool removalSuccessful = false;

            try
            {
                // Stop all processes in the container
                try
                {
                    _commandExecutor.RunCommand($"docker exec {unifiedContainer} /scripts/unified-control.sh StopAll", null, null, 5000);
                    OnProgress($"[Unified] Stopped all processes in {unifiedContainer}");
                }
                catch (Exception ex)
                {
                    OnProgress($"[Unified] WARNING: Failed to stop processes: {ex.Message}");
                }

                // Remove the unified container
                _dockerExecutor.RemoveContainer(unifiedContainer);
                
                // Verify container was actually removed
                await Task.Delay(ContainerRemovalVerificationDelayMs);
                if (!_dockerExecutor.IsContainerExist(unifiedContainer))
                {
                    removalSuccessful = true;
                    OnProgress($"[Unified] Removed container {unifiedContainer}");
                }
                else
                {
                    OnProgress($"[Unified] WARNING: Container {unifiedContainer} still exists after removal attempt");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to remove container: {ex.Message}");
            }
            finally
            {
                // CRITICAL: Always unregister the container from active registry
                // This must happen even if removal failed, to prevent memory leaks
                // and allow future cleanup attempts to remove the container
                UnregisterActiveContainer(unifiedContainer);
                OnProgress($"[Unified] Unregistered {unifiedContainer} from active containers");
            }

            // If removal failed, add to retry queue for later cleanup
            if (!removalSuccessful && _dockerExecutor.IsContainerExist(unifiedContainer))
            {
                AddToPendingCleanupRetry(unifiedContainer);
            }
        }

        /// <summary>
        /// Sets up network monitor container using sidecar pattern.
        /// 
        /// SIDECAR PATTERN:
        /// The monitor container attaches to the student's unified container network namespace
        /// using --net=container:{unifiedContainer}. This allows SharpPcap to capture all traffic
        /// on the student container's loopback (lo) interface.
        /// 
        /// CRITICAL DESIGN DECISIONS:
        /// 1. Uses SharpPcap/PacketDotNet for real-time capture (matching MiddlewareSniffPort)
        /// 2. Monitor loopback interface (lo) - NOT eth0
        /// 3. Captures traffic on target port range (4000-4010)
        /// 4. Sidecar survives if student container crashes
        /// 5. Clean separation of concerns (student code vs monitoring)
        /// 6. Outputs JSON lines for reliable parsing (not raw PCAP)
        /// 
        /// REQUIREMENTS:
        /// - NET_ADMIN and NET_RAW capabilities for packet capture
        /// - Attached to unified container's network namespace via --net=container:
        /// - Output written to bind-mounted volume for extraction
        /// </summary>
        /// <param name="monitorContainer">Name of the monitor container</param>
        /// <param name="unifiedContainer">Name of the unified student container to attach to</param>
        /// <param name="port">Port number for role detection (server port)</param>
        /// <param name="pcapOutputPath">Host path where output file will be saved</param>
        /// <param name="protocol">Protocol type (TCP/HTTP) for logging</param>
        private async Task SetupNetworkMonitorContainerAsync(
            string monitorContainer,
            string unifiedContainer,
            int port,
            string pcapOutputPath,
            string protocol)
        {
            OnProgress($"[SETUP] Creating SharpPcap-based network monitor sidecar: {monitorContainer}");

            // === CRITICAL: Save monitor container name to class field ===
            _currentMonitorContainer = monitorContainer;
            // For new SharpPcap sidecar, output is JSON lines not PCAP
            // Change extension from .pcap to .jsonl
            var jsonlOutputPath = Path.ChangeExtension(pcapOutputPath, ".jsonl");
            _currentPcapFilePath = jsonlOutputPath; // Update to use JSONL path
            _currentJsonlFilePath = jsonlOutputPath;
            // =============================================================

            // Remove existing monitor container if any
            try
            {
                _dockerExecutor.RemoveContainer(monitorContainer);
            }
            catch
            {
                // Container doesn't exist or already removed - this is fine
            }

            // Create directory for output on host
            var outputDir = Path.GetDirectoryName(jsonlOutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                // CRITICAL: Convert to absolute path for Docker volume mount
                outputDir = Path.GetFullPath(outputDir);
            }

            // Extract the filename from the full path
            var outputFileName = Path.GetFileName(jsonlOutputPath);

            // Build the docker run command for SharpPcap-based network monitor sidecar
            // CRITICAL: 
            // - Use --net=container:{unifiedContainer} to attach to student's network namespace
            // - Use --cap-add=NET_ADMIN and --cap-add=NET_RAW for SharpPcap permissions
            // - SharpPcap captures on loopback interface (lo) to catch localhost traffic
            // - Outputs JSON lines to /data/{outputFileName} inside container (bind-mounted to host)
            //
            // The new network-monitor image uses SharpPcap/PacketDotNet for real-time capture
            // matching MiddlewareSniffPort's behavior exactly.
            // ENTRYPOINT is the NetworkMonitor app, CMD is [port, outputPath]

            // CRITICAL: --net=container:{unifiedContainer} attaches to the unified container's
            // network namespace, allowing the sidecar to see localhost (127.0.0.1) traffic
            // between client and server running in the unified container.
            var dockerCmd = $"docker run -d --name {monitorContainer} " +
                           $"--net=container:{unifiedContainer} " +  // SIDECAR: Attach to student container's network
                           $"--cap-add=NET_ADMIN " +                 // Required for SharpPcap
                           $"--cap-add=NET_RAW " +                   // Required for raw packet capture
                           $"-v \"{outputDir}:/data\" " +            // Mount host directory for output
                           $"fptuxaes/network-monitor:latest " +     // SharpPcap-based monitor
                           $"{port} /data/{outputFileName}";         // Args: port, output path

            OnProgress($"[Monitor] Command: {dockerCmd}");
            OnProgress($"[Monitor] Using SharpPcap-based sidecar (matching MiddlewareSniffPort)");
            OnProgress($"[Monitor] Attached to {unifiedContainer}'s network namespace via --net=container:");
            OnProgress($"[Monitor] Capturing on loopback (lo) interface - localhost traffic between client/server");
            OnProgress($"[Monitor] Output will be saved to: {jsonlOutputPath}");

            try
            {
                _commandExecutor.RunCommand(dockerCmd, null, null, 10000);
                
                // CRITICAL: Register container as active IMMEDIATELY after creation
                // This prevents periodic cleanup from killing this container while it's in use
                RegisterActiveContainer(monitorContainer);
                OnProgress($"[Monitor] Sidecar monitor {monitorContainer} started and registered as active");

                // Brief delay to ensure container is up and SharpPcap is initialized
                await Task.Delay(1000);

                // Verify monitor is running
                if (_dockerExecutor.IsContainerRunning(monitorContainer))
                {
                    OnProgress($"[Monitor] Verified {monitorContainer} is running and ready to capture");
                }
                else
                {
                    OnProgress($"[Monitor] WARNING: {monitorContainer} may not be running properly");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Monitor] ERROR: Failed to start network monitor: {ex.Message}");
                throw new InvalidOperationException($"Failed to start network monitor container: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cleans up network monitor container.
        /// 
        /// CLEANUP WORKFLOW:
        /// 1. Stop the monitor container (SharpPcap flushes automatically on SIGTERM)
        /// 2. Output file is already on host via volume mount
        /// 3. Remove the monitor container and wait for removal to complete
        /// 
        /// The output file (JSON lines) is already on the host due to volume mounting.
        /// The container stop ensures any buffered packets are flushed before removal.
        /// CRITICAL: Verifies container removal and adds to retry queue if failed,
        /// preventing zombie containers during batch grading.
        /// </summary>
        /// <param name="monitorContainer">Name of the monitor container</param>
        /// <param name="studentResultPath">Student result directory path</param>
        private async Task CleanupNetworkMonitorContainerAsync(string monitorContainer, string studentResultPath)
        {
            OnProgress($"[Monitor] Cleaning up {monitorContainer}");
            bool removalSuccessful = false;

            try
            {
                // Stop the container to ensure SharpPcap flushes any buffered packets
                if (_dockerExecutor.IsContainerRunning(monitorContainer))
                {
                    OnProgress($"[Monitor] Stopping container to flush SharpPcap buffer...");
                    _commandExecutor.RunCommand($"docker stop {monitorContainer}", null, null, 10000);

                    // Wait for clean shutdown
                    await Task.Delay(1000);
                }

                // Verify pcap file exists on host (should be there via volume mount)
                var pcapPath = Path.Combine(studentResultPath, "network_capture.pcap");
                if (File.Exists(pcapPath))
                {
                    var fileInfo = new FileInfo(pcapPath);
                    OnProgress($"[Monitor] Network capture saved: {pcapPath} ({fileInfo.Length} bytes)");
                }
                else
                {
                    OnProgress($"[Monitor] WARNING: Network capture file not found at {pcapPath}");
                }

                // Remove the monitor container
                _dockerExecutor.RemoveContainer(monitorContainer);
                
                // Verify container was actually removed
                await Task.Delay(ContainerRemovalVerificationDelayMs);
                if (!_dockerExecutor.IsContainerExist(monitorContainer))
                {
                    removalSuccessful = true;
                    OnProgress($"[Monitor] Removed container {monitorContainer}");
                }
                else
                {
                    OnProgress($"[Monitor] WARNING: Container {monitorContainer} still exists after removal attempt");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Monitor] WARNING during cleanup: {ex.Message}");
            }
            finally
            {
                // CRITICAL: Always unregister the container from active registry
                // This must happen even if removal failed, to prevent memory leaks
                // and allow future cleanup attempts to remove the container
                UnregisterActiveContainer(monitorContainer);
                OnProgress($"[Monitor] Unregistered {monitorContainer} from active containers");
            }

            // If removal failed, add to retry queue for later cleanup
            if (!removalSuccessful && _dockerExecutor.IsContainerExist(monitorContainer))
            {
                AddToPendingCleanupRetry(monitorContainer);
            }
        }

        /// <summary>
        /// Resets the network monitor by restarting the sidecar container.
        /// 
        /// NOTE: This method is NO LONGER called between test cases.
        /// The correct approach is to keep the sidecar running continuously and
        /// let the packet counter (_lastParsedPacketCount) keep incrementing.
        /// This way each test case only sees NEW packets captured during its execution.
        /// 
        /// This method is kept for potential future use cases where a full reset
        /// is needed (e.g., between students, or when the sidecar crashes).
        /// 
        /// HISTORY OF APPROACHES:
        /// 1. Reset counter to 0 -> BUG: re-parses old packets from previous TCs
        /// 2. Delete output file -> BUG: sidecar keeps writing to orphaned file handle (Linux inode behavior)
        /// 3. Restart container -> WORKS but adds overhead, may cause issues over time
        /// 4. Current: Don't reset at all between TCs, just clear RunContext
        /// </summary>
        /// <param name="monitorContainer">Name of the monitor container</param>
        /// <param name="outputPath">Host path where the output file is stored</param>
        private async Task ResetNetworkMonitorForNewTestCaseAsync(
            string monitorContainer,
            string outputPath)
        {
            OnProgress($"[Monitor] Resetting network monitor (full restart - used for manual reset or error recovery)...");

            try
            {
                // Step 1: Delete the output file on host if it exists
                // This must be done BEFORE restarting the container, as the container
                // will create a fresh file when it starts
                if (File.Exists(outputPath))
                {
                    try
                    {
                        File.Delete(outputPath);
                        OnProgress($"[Monitor] Deleted output file on host: {outputPath}");
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[Monitor] WARNING: Could not delete host file: {ex.Message}");
                    }
                }

                // Step 2: Restart the sidecar container to get a fresh file handle
                // CRITICAL: We must restart the container, not just delete the file inside it.
                // The sidecar opens the output file with StreamWriter at startup and keeps
                // the file handle open. On Linux/Unix, deleting a file while it's open just
                // unlinks it from the directory - the process keeps writing to the orphaned
                // file descriptor. Only by restarting do we close the old handle and open
                // a fresh file.
                OnProgress($"[Monitor] Restarting container {monitorContainer} to reset file handle...");
                _commandExecutor.RunCommand($"docker restart {monitorContainer}", null, null, 10000);
                
                // Wait for the sidecar to restart and initialize SharpPcap
                await Task.Delay(1500);

                // Step 3: Verify the sidecar is running after restart
                if (_dockerExecutor.IsContainerRunning(monitorContainer))
                {
                    OnProgress($"[Monitor] Container {monitorContainer} restarted successfully");
                    
                    // Additional verification: check if the sidecar process is running
                    var checkCmd = $"{monitorContainer} pgrep -f NetworkMonitor";
                    var (checkSuccess, _) = _dockerExecutor.ExecDockerCommandWithOutput(checkCmd, 2000);
                    
                    if (checkSuccess)
                    {
                        OnProgress($"[Monitor] Verified NetworkMonitor process is running");
                    }
                    else
                    {
                        OnProgress($"[Monitor] WARNING: NetworkMonitor process not found after restart");
                    }
                }
                else
                {
                    OnProgress($"[Monitor] WARNING: Container {monitorContainer} may not be running after restart");
                }

                // Step 4: Reset the packet counter since we have a fresh file
                _lastParsedPacketCount = 0;

                OnProgress($"[Monitor] Network monitor reset complete - ready for new captures");
            }
            catch (Exception ex)
            {
                OnProgress($"[Monitor] WARNING: Error resetting network monitor: {ex.Message}");
                // Continue even if reset fails - the test case may still work with stale data
            }
        }


        /// <summary>
        /// Cleans up a specific database INSTANCE within the shared database container.
        /// CRITICAL FIX: Each student uses a unique database instance (e.g., Library_student1).
        /// After grading, we DROP that specific database to free up resources within the container.
        /// The container itself stays running and is shared across all students.
        /// </summary>
        private async Task CleanupDatabaseInstanceAsync(string databaseContainer, string databaseName, string databasePassword)
        {
            if (string.IsNullOrEmpty(databaseName))
            {
                OnProgress("[Database Cleanup] No database name provided, skipping instance cleanup");
                return;
            }

            OnProgress($"[Database Cleanup] Dropping database instance '{databaseName}' in container {databaseContainer}");

            try
            {
                // Use sqlcmd to drop the database instance
                // First, we need to kill any active connections to the database
                var killConnectionsSql = $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";

                // Execute SQL command inside the container
                var sqlCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \"{databasePassword}\" -Q \"{killConnectionsSql}\"";

                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(sqlCommand, 10000);

                if (success)
                {
                    OnProgress($"[Database Cleanup] Successfully dropped database instance '{databaseName}'");
                    OnProgress($"[Database Cleanup] Output: {output}");
                }
                else
                {
                    OnProgress($"[Database Cleanup] Warning: Failed to drop database instance '{databaseName}': {output}");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database Cleanup] Warning: Exception dropping database instance '{databaseName}': {ex.Message}");
                // Don't throw - this is cleanup, we want to continue even if it fails
            }

            await Task.CompletedTask;
        }

        #endregion
    }
}
