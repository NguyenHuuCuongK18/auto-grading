using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Abstractions;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for Docker-based network monitoring using sidecar pattern.
    /// Handles:
    /// - Network monitor container setup (SharpPcap-based sidecar)
    /// - PCAP file management
    /// - Packet parsing from JSON lines
    /// - Stage-based snapshot management
    /// </summary>
    public sealed class DockerNetworkMonitorService
    {
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        private readonly JsonPacketParsingService _jsonPacketParser;
        private readonly IRunContext _runContext;
        
        private string? _currentMonitorContainer;
        private string? _currentJsonlFilePath;
        private int _lastParsedPacketCount = 0;
        
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        public DockerNetworkMonitorService(IRunContext runContext)
        {
            _dockerExecutor = new DockerCommandExecutor();
            _commandExecutor = _dockerExecutor.GetCommandExecutor();
            _jsonPacketParser = new JsonPacketParsingService();
            _runContext = runContext;
        }
        
        /// <summary>
        /// Gets the current monitor container name.
        /// </summary>
        public string? CurrentMonitorContainer => _currentMonitorContainer;
        
        /// <summary>
        /// Gets the current JSONL file path.
        /// </summary>
        public string? CurrentJsonlFilePath => _currentJsonlFilePath;
        
        /// <summary>
        /// Sets up the network monitor container attached to the unified container.
        /// Uses SharpPcap-based sidecar that outputs JSON lines.
        /// </summary>
        public async Task SetupNetworkMonitorContainerAsync(
            string monitorContainer,
            string unifiedContainer,
            int port,
            string pcapFilePath,
            string? protocol)
        {
            _currentMonitorContainer = monitorContainer;
            _currentJsonlFilePath = Path.ChangeExtension(pcapFilePath, ".jsonl");
            _lastParsedPacketCount = 0;
            
            OnProgress($"[NetworkMonitor] Setting up sidecar {monitorContainer} attached to {unifiedContainer}");
            
            // Remove any existing monitor container
            _commandExecutor.RunCommand($"docker rm -f {monitorContainer} 2>/dev/null || true", null, null, 10000);
            
            // Create the network monitor sidecar container
            // Key points:
            // - Uses --net=container:{unifiedContainer} to share network namespace
            // - This allows capturing localhost (127.0.0.1) traffic from unified container
            // - Outputs JSON lines to /data/packets.jsonl
            var protocolArg = !string.IsNullOrEmpty(protocol) ? $"--protocol {protocol}" : "";
            
            var dockerCmd = $"docker run -d --name {monitorContainer} " +
                           $"--net=container:{unifiedContainer} " +
                           $"--cap-add=NET_RAW --cap-add=NET_ADMIN " +
                           $"-v \"{Path.GetDirectoryName(pcapFilePath)}:/data\" " +
                           $"fptuxaes/network-monitor:latest " +
                           $"--interface lo --port {port} --output /data/packets.jsonl {protocolArg}";
            
            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);
            
            // Wait for monitor to start
            await Task.Delay(1000);
            
            OnProgress($"[NetworkMonitor] Sidecar {monitorContainer} started, capturing on lo:{port}");
        }
        
        /// <summary>
        /// Resets the network monitor for a new test case.
        /// This ensures packets from previous test cases don't contaminate the new one.
        /// </summary>
        public async Task ResetNetworkMonitorForNewTestCaseAsync(
            string monitorContainer,
            string jsonlFilePath)
        {
            OnProgress($"[NetworkMonitor] Resetting for new test case...");
            
            try
            {
                // Stop the current monitor gracefully
                _dockerExecutor.TryExecDockerCommand($"stop -t 2 {monitorContainer}", 5000);
                
                // Delete the old JSONL file
                var hostJsonlPath = Path.ChangeExtension(jsonlFilePath, ".jsonl");
                if (File.Exists(hostJsonlPath))
                {
                    File.Delete(hostJsonlPath);
                    OnProgress($"[NetworkMonitor] Deleted old JSONL file");
                }
                
                // Restart the monitor container
                _dockerExecutor.TryExecDockerCommand($"start {monitorContainer}", 5000);
                
                // Reset the packet counter
                _lastParsedPacketCount = 0;
                
                // Wait for monitor to restart
                await Task.Delay(500);
                
                OnProgress($"[NetworkMonitor] Reset complete, ready for new test case");
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] WARNING: Reset failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Parses the current JSONL file and extracts packets for the specified stage.
        /// </summary>
        public async Task ParsePacketsForStageAsync(int currentStage, int port)
        {
            if (string.IsNullOrEmpty(_currentJsonlFilePath))
            {
                OnProgress($"[NetworkMonitor] No JSONL file path set");
                return;
            }
            
            var hostJsonlPath = Path.ChangeExtension(_currentJsonlFilePath, ".jsonl");
            
            if (!File.Exists(hostJsonlPath))
            {
                OnProgress($"[NetworkMonitor] JSONL file not found: {hostJsonlPath}");
                return;
            }
            
            try
            {
                // Read and parse JSONL file
                var allPackets = _jsonPacketParser.ParseJsonlFile(hostJsonlPath, currentStage);
                
                // Get only new packets (since last parse)
                var newPackets = allPackets.Skip(_lastParsedPacketCount).ToList();
                
                if (newPackets.Count == 0)
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: No new packets captured");
                    return;
                }
                
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Captured {newPackets.Count} new packets");
                
                // Add packets to RunContext with current stage
                foreach (var packet in newPackets)
                {
                    packet.Stage = currentStage;
                    _runContext.AddCapturedNetworkPacket("", currentStage.ToString(), packet);
                }
                
                // Update counter
                _lastParsedPacketCount = allPackets.Count;
                
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Total packets so far: {_lastParsedPacketCount}");
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] ERROR parsing packets: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Creates a snapshot of the current JSONL file for a specific stage.
        /// </summary>
        public async Task CreateStageSnapshotAsync(string studentResultPath, string testCaseName, int stage)
        {
            if (string.IsNullOrEmpty(_currentJsonlFilePath))
            {
                return;
            }
            
            var sourceFile = Path.ChangeExtension(_currentJsonlFilePath, ".jsonl");
            if (!File.Exists(sourceFile))
            {
                return;
            }
            
            try
            {
                var snapshotFile = Path.Combine(studentResultPath, $"snapshot_{testCaseName}_stage{stage}.jsonl");
                File.Copy(sourceFile, snapshotFile, overwrite: true);
                OnProgress($"[NetworkMonitor] Created snapshot for {testCaseName} stage {stage}");
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] WARNING: Failed to create snapshot: {ex.Message}");
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Cleans up the network monitor container and extracts final capture data.
        /// </summary>
        public async Task CleanupNetworkMonitorContainerAsync(string monitorContainer, string studentResultPath)
        {
            OnProgress($"[Monitor] Stopping and extracting data from {monitorContainer}...");
            
            try
            {
                // Stop the monitor gracefully to flush any buffered data
                _dockerExecutor.TryExecDockerCommand($"stop -t 5 {monitorContainer}", 10000);
                
                // Wait for data to be flushed
                await Task.Delay(1000);
                
                // Remove the container
                _dockerExecutor.TryExecDockerCommand($"rm {monitorContainer}", 5000);
                
                OnProgress($"[Monitor] Container {monitorContainer} removed");
            }
            catch (Exception ex)
            {
                OnProgress($"[Monitor] WARNING: Cleanup failed: {ex.Message}");
            }
            
            _currentMonitorContainer = null;
            _currentJsonlFilePath = null;
            _lastParsedPacketCount = 0;
        }
        
        /// <summary>
        /// Moves stage snapshot files to the test case folder.
        /// </summary>
        public void MoveSnapshotsToTCFolder(string studentResultPath, string tcResultPath, string testCaseName)
        {
            try
            {
                var snapshotPattern = $"snapshot_{testCaseName}_*.jsonl";
                var snapshotFiles = Directory.GetFiles(studentResultPath, snapshotPattern);
                
                foreach (var snapshotFile in snapshotFiles)
                {
                    var destFile = Path.Combine(tcResultPath, Path.GetFileName(snapshotFile));
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(snapshotFile, destFile);
                }
                
                if (snapshotFiles.Length > 0)
                {
                    OnProgress($"[NetworkMonitor] Moved {snapshotFiles.Length} snapshots to {testCaseName} folder");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] WARNING: Failed to move snapshots: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clears captured network packets from the run context for a specific student.
        /// </summary>
        public void ClearCapturedPackets(string studentCode)
        {
            _runContext.ClearCapturedNetworkPackets(studentCode);
            _lastParsedPacketCount = 0;
        }
        
        /// <summary>
        /// Gets all captured network packets from the run context.
        /// </summary>
        public List<CapturedNetworkPacket> GetCapturedPackets()
        {
            return _runContext.GetAllCapturedNetworkPackets().ToList();
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
