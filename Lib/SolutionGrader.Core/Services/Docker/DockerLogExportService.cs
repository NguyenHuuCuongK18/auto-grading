using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for exporting logs from Docker containers.
    /// Handles:
    /// - Stage-based log reading
    /// - Log file export to student result folders
    /// - Container file reading
    /// - Log clearing between test cases
    /// </summary>
    public sealed class DockerLogExportService
    {
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        public DockerLogExportService()
        {
            _dockerExecutor = new DockerCommandExecutor();
            _commandExecutor = _dockerExecutor.GetCommandExecutor();
        }
        
        /// <summary>
        /// Exports stage logs for a specific test case from the unified container.
        /// Creates ProcessLogs/{TC#}/ subdirectory with stage-specific log files.
        /// </summary>
        public async Task ExportStageLogsForTestCaseAsync(
            string unifiedContainer, 
            string studentResultPath, 
            string testCaseName,
            Dictionary<int, string>? lastTestCaseClientOutputs,
            Dictionary<int, string>? lastTestCaseServerOutputs)
        {
            if (lastTestCaseClientOutputs == null || lastTestCaseServerOutputs == null)
            {
                OnProgress($"[Logs] No stage outputs captured for {testCaseName}");
                return;
            }
            
            var logsDir = Path.Combine(studentResultPath, "ProcessLogs", testCaseName);
            Directory.CreateDirectory(logsDir);
            
            OnProgress($"[Logs] Exporting stage logs for {testCaseName} to {logsDir}");
            
            // Export client outputs by stage
            foreach (var (stage, output) in lastTestCaseClientOutputs)
            {
                if (!string.IsNullOrEmpty(output))
                {
                    var logPath = Path.Combine(logsDir, $"client-{testCaseName}-stage-{stage}.log");
                    await File.WriteAllTextAsync(logPath, output);
                }
            }
            
            // Export server outputs by stage
            foreach (var (stage, output) in lastTestCaseServerOutputs)
            {
                if (!string.IsNullOrEmpty(output))
                {
                    var logPath = Path.Combine(logsDir, $"server-{testCaseName}-stage-{stage}.log");
                    await File.WriteAllTextAsync(logPath, output);
                }
            }
            
            OnProgress($"[Logs] Exported {lastTestCaseClientOutputs.Count} client logs and {lastTestCaseServerOutputs.Count} server logs for {testCaseName}");
        }
        
        /// <summary>
        /// Clears stage log files in the unified container between test cases.
        /// </summary>
        public void ClearStageLogsInContainer(string unifiedContainer)
        {
            try
            {
                // Clear server logs
                _dockerExecutor.TryExecDockerCommand(
                    $"{unifiedContainer} sh -c \"rm -f /apps/server/server*.log 2>/dev/null || true\"", 5000);
                
                // Clear client logs
                _dockerExecutor.TryExecDockerCommand(
                    $"{unifiedContainer} sh -c \"rm -f /apps/client/client*.log 2>/dev/null || true\"", 5000);
                
                OnProgress($"[Logs] Cleared stage logs in container {unifiedContainer}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Logs] WARNING: Failed to clear logs: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reads a file from inside a Docker container.
        /// </summary>
        public string ReadFileFromContainer(string containerName, string filePath)
        {
            try
            {
                var command = $"{containerName} cat {filePath}";
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(command, 5000);
                
                return success ? output : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Saves Docker container logs to persistent files.
        /// </summary>
        public async Task SaveDockerLogsAsync(
            string serverContainer, 
            string clientContainer, 
            string studentResultPath)
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
                    OnProgress($"[Docker Logs] Server logs saved ({serverLogs.Length} bytes)");
                }
                else
                {
                    await File.WriteAllTextAsync(serverLogPath, "[No server logs captured]");
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
                    OnProgress($"[Docker Logs] Client logs saved ({clientLogs.Length} bytes)");
                }
                else
                {
                    await File.WriteAllTextAsync(clientLogPath, "[No client logs captured]");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Logs] Warning: Failed to save client logs: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reads the incremental log content from a file in the container.
        /// Returns only the new content since the last read position.
        /// </summary>
        public (string content, long newPosition) ReadIncrementalLogFromContainer(
            string containerName,
            string filePath,
            long lastPosition)
        {
            try
            {
                // Read the entire file
                var fullContent = ReadFileFromContainer(containerName, filePath);
                
                if (string.IsNullOrEmpty(fullContent))
                {
                    return (string.Empty, lastPosition);
                }
                
                // Calculate new content based on position
                if (lastPosition >= fullContent.Length)
                {
                    return (string.Empty, fullContent.Length);
                }
                
                var newContent = fullContent.Substring((int)lastPosition);
                return (newContent, fullContent.Length);
            }
            catch
            {
                return (string.Empty, lastPosition);
            }
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
