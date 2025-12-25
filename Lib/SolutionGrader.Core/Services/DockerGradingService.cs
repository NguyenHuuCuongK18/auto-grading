using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;
using Domain.Entities.Docker.DockerSupporter.Entity;
using Domain.Models;
using Domain.Models.Grading;
using Domain.Models.Configuration;
using Domain.Models.Network;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Helpers;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Unified Docker-based grading service that can be used by both UI and CLI.
    /// 
    /// CRITICAL EXECUTION ORDER:
    /// 1. NetworkMonitor starts FIRST (before any containers)
    /// 2. Docker containers are created and started
    /// 3. Student files are copied to containers
    /// 4. Test cases are executed
    /// 5. NetworkMonitor stops LAST (after cleanup)
    /// 
    /// This service manages the complete Docker grading workflow:
    /// - Setup Docker containers (server/client) with TTY support for reliable output
    /// - Copy student files to containers
    /// - Generate appsettings.json with proper configuration
    /// - Execute test cases from Detail.xlsx
    /// - Capture outputs using application log files (bypasses docker logs buffering)
    /// - Run NetworkMonitor on the HOST to sniff exposed server port
    /// - Compare outputs against expected values
    /// - Write results in Excel format
    /// 
    /// NETWORK MONITORING ARCHITECTURE:
    /// For the NetworkMonitor (running on the host) to capture traffic, the client must
    /// connect to the server through the host's exposed port, NOT directly via Docker network.
    /// 
    /// Traffic Flow (CORRECT - enables network monitoring):
    ///   Client Container -> host.docker.internal:{hostPort} -> Host Loopback -> Server Container:{containerPort}
    /// 
    /// Traffic Flow (WRONG - bypasses network monitoring):
    ///   Client Container -> Docker Network (container name) -> Server Container
    /// 
    /// Key design decisions:
    /// - NetworkMonitor ALWAYS runs first to capture full network traffic
    /// - Server container port is EXPOSED to host (e.g., -p 8000:8000)
    /// - Client connects via host.docker.internal to route through exposed port
    /// - Client container uses --add-host=host.docker.internal:host-gateway for Linux support
    /// - NetworkMonitor runs on HOST and sniffs localhost:8000 (requires sudo/admin)
    /// - Application output is captured via log files, not docker logs (avoids buffering)
    /// - TTY flag (-t) ensures immediate console output flushing
    /// 
    /// This service is split into partial classes for maintainability:
    /// - DockerGradingService.cs (this file) - Core service, constructor, public API
    /// - DockerGradingService.ContainerSetup.cs - Container setup methods
    /// - DockerGradingService.TestExecution.cs - Test case execution methods
    /// - DockerGradingService.OutputComparison.cs - Output comparison methods
    /// - DockerGradingService.TestKitLoading.cs - Test kit loading methods
    /// - DockerGradingService.Cleanup.cs - Cleanup and resource management methods
    /// - DockerGradingService.ResultWriting.cs - Excel result writing methods
    /// </summary>
    public sealed partial class DockerGradingService
    {
        #region Timeout Constants
        // =======================================================================
        // TIMEOUT CONFIGURATION - Easy to find and modify
        // These are the default timeouts used during grading.
        // Modify these values to adjust grading behavior.
        // =======================================================================
        
        /// <summary>
        /// Default timeout per test case in seconds.
        /// If a single test case takes longer than this, it will be marked as timed out.
        /// Default: 120 seconds (2 minutes)
        /// </summary>
        public const int DefaultTestCaseTimeoutSeconds = 120;
        
        /// <summary>
        /// Default total timeout per student (test kit) in seconds.
        /// If grading a student takes longer than this, the entire grading will be cancelled.
        /// Default: 600 seconds (10 minutes)
        /// </summary>
        public const int DefaultStudentTimeoutSeconds = 600;
        
        /// <summary>
        /// Gets the effective test case timeout, using the longer of the default or configured value.
        /// </summary>
        public static int GetEffectiveTestCaseTimeout(int? configuredTimeout = null)
        {
            if (configuredTimeout.HasValue && configuredTimeout.Value > 0)
            {
                // Use the LONGER timeout (prioritize longer timeout as per requirement)
                return Math.Max(DefaultTestCaseTimeoutSeconds, configuredTimeout.Value);
            }
            return DefaultTestCaseTimeoutSeconds;
        }
        
        /// <summary>
        /// Gets the effective student (test kit) timeout, using the longer of the default or configured value.
        /// </summary>
        public static int GetEffectiveStudentTimeout(int? configuredTimeout = null)
        {
            if (configuredTimeout.HasValue && configuredTimeout.Value > 0)
            {
                // Use the LONGER timeout (prioritize longer timeout as per requirement)
                return Math.Max(DefaultStudentTimeoutSeconds, configuredTimeout.Value);
            }
            return DefaultStudentTimeoutSeconds;
        }
        
        #endregion

        // Timing constants - optimized for faster execution
        // These are fallback values; prefer using config.TestCaseTimeoutSeconds
        private const int StartupDelayMs = 1500;  // Reduced from 3000 - wait for process to start
        private const int InputProcessingDelayMs = 2000;  // Reduced from 5000 - wait for input to be processed
        private const int OutputRetryMaxAttempts = 5;
        private const int OutputRetryDelayMs = 500;  // Reduced from 1000 - faster polling
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        // Container cleanup constants
        private const int ContainerRemovalVerificationDelayMs = 500;  // Delay to verify container removal
        private const int BatchRemovalTimeoutMs = 15000;  // Timeout for batch container removal
        private const int BatchRemovalSize = 20;  // Number of containers to remove in a single batch
        private const int ProcessStopDelayMs = 300;  // Delay after stopping processes to ensure termination

        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        private readonly DockerConsoleManager _consoleManager;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IRunContext _runContext;
        private readonly JsonPacketParsingService _jsonPacketParser; // JSON parser for SharpPcap-based sidecar
        private string? _currentStudentCode; // Track current student for logging
        private string? _currentTestCaseName; // Track current test case for per-TC logging (e.g., "TC3")
        private string? _currentTestKitProtocol; // Track protocol type (TCP or HTTP) from testkit Header.xlsx

        // Network monitoring for sidecar pattern
        private string? _currentMonitorContainer; // Name of network monitor container (e.g., ag-monitor-StudentCode)
        private string? _currentPcapFilePath; // Path to output file (JSONL from sidecar)
        private string? _currentJsonlFilePath; // Path to JSON lines file from SharpPcap-based sidecar
        private int _lastParsedPacketCount = 0; // Track how many packets we've already processed

        // Stage output tracking for per-test-case log export
        private Dictionary<int, string>? _lastTestCaseClientOutputs; // Track last test case client outputs by stage
        private Dictionary<int, string>? _lastTestCaseServerOutputs; // Track last test case server outputs by stage

        // Retry cleanup tracking for containers that failed initial cleanup
        // Key: container name, Value: timestamp when cleanup was first attempted
        private readonly ConcurrentDictionary<string, DateTime> _pendingCleanupContainers = new();
        
        // Delay before retrying cleanup for a container (30 seconds)
        private const int RetryCleanupDelaySeconds = 30;
        
        // CRITICAL FIX: Static registry of containers currently in use by parallel grading tasks
        // This prevents periodic cleanup from killing containers that are still being used by other students.
        // Without this, when Student A finishes and triggers cleanup, the monitor/unified containers
        // for Students B, C, D would be killed, causing them to fail with "no network captured".
        // Key: container name, Value: byte (unused, just for ConcurrentDictionary - more memory efficient than bool)
        private static readonly ConcurrentDictionary<string, byte> _activeContainers = new();

        /// <summary>
        /// Event raised when grading progress is updated.
        /// </summary>
        public event EventHandler<GradingProgressEventArgs>? ProgressUpdated;

        /// <summary>
        /// Event raised when Docker containers are ready (setup complete).
        /// This allows staggered startup to release its lock early.
        /// </summary>
        public event EventHandler? ContainersReady;

        public DockerGradingService(INetworkMonitorService? networkMonitor, IRunContext runContext)
        {
            _dockerExecutor = new DockerCommandExecutor();
            _commandExecutor = _dockerExecutor.GetCommandExecutor();
            _consoleManager = new DockerConsoleManager();
            _networkMonitor = networkMonitor;
            _runContext = runContext;
            _jsonPacketParser = new JsonPacketParsingService(); // JSON parser for sidecar
        }

        /// <summary>
        /// Builds a safe shell command to kill dotnet processes in a container.
        /// This command explicitly excludes PID 1 to avoid killing the container's main process.
        /// </summary>
        /// <param name="containerName">The Docker container name</param>
        /// <returns>The safe kill command for docker exec</returns>
        private static string BuildSafeDotnetKillCommand(string containerName)
        {
            // Find dotnet PIDs (excluding PID 1) and kill them
            // - ps aux: list all processes
            // - grep dotnet: filter for dotnet processes
            // - grep -v grep: exclude the grep process itself
            // - awk '{if ($2 != 1) print $2}': extract PIDs, excluding PID 1
            // - xargs -r kill -9: kill the processes (-r means don't run if no input)
            // - 2>/dev/null || true: suppress errors and always return success
            return $"{containerName} sh -c \"ps aux | grep dotnet | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"";
        }

        /// <summary>
        /// Resets the database container for a new student.
        /// This should be called before grading each student to ensure:
        /// - Correct database is loaded (if switching papers)
        /// - Database state is identical for all students
        /// 
        /// Call this BEFORE calling GradeStudentAsync for each student.
        /// </summary>
        /// <param name="config">Docker grading configuration with database settings</param>
        public async Task ResetDatabaseForNewStudentAsync(DockerGradingConfig config)
        {
            await ResetDatabaseContainerAsync(config);
        }

        /// <summary>
        /// Disposes all Docker containers including the database container.
        /// Call this at the end of a grading session to clean up all resources.
        /// Also cleans up any orphaned auto-grading containers (ag-unified-*, ag-monitor-*).
        /// </summary>
        /// <param name="config">Docker grading configuration</param>
        /// <param name="forceCleanup">When true, attempts more aggressive cleanup for containers that may be stuck.
        /// Use forceCleanup=true at end of grading session, forceCleanup=false during periodic cleanup.</param>
        public void DisposeAllContainers(DockerGradingConfig config, bool forceCleanup = false)
        {
            OnProgress($"[Docker] Disposing all containers (forceCleanup={forceCleanup})...");

            var databaseContainer = config.DatabaseContainerName;
            var serverContainer = $"server-{databaseContainer}";
            var clientContainer = $"client-{databaseContainer}";

            // Remove code containers
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }

            // Remove database container
            try { _dockerExecutor.RemoveContainer(databaseContainer); } catch { }

            // CRITICAL: Clean up any orphaned auto-grading containers (ag-unified-*, ag-monitor-*)
            // These may remain after the final batch of students if cleanup wasn't triggered
            // This addresses the issue: "containers not being cleaned up after final batch of student grading"
            // 
            // When forceCleanup=true (end of session):
            // - Log any containers that are still registered as active (indicates a bug or timeout)
            // - Attempt to remove ALL containers, including those marked active
            // - This is safe because at end of session, no grading should be in progress
            if (forceCleanup)
            {
                var activeCount = _activeContainers.Count;
                if (activeCount > 0)
                {
                    OnProgress($"[Docker Cleanup] FORCE MODE: Found {activeCount} container(s) still in active registry at end of session");
                    OnProgress($"[Docker Cleanup] This indicates containers that didn't complete cleanup properly (timeout, crash, or bug)");
                    
                    // Log which containers are still marked active
                    foreach (var containerName in _activeContainers.Keys)
                    {
                        OnProgress($"[Docker Cleanup] Still active at session end: {containerName}");
                    }
                    
                    // Clear the registry so these containers can be removed
                    _activeContainers.Clear();
                    OnProgress($"[Docker Cleanup] Cleared active registry to allow final cleanup");
                }
            }
            
            CleanupOrphanedContainers();

            OnProgress("[Docker] All containers disposed");
        }

        /// <summary>
        /// Cleans up orphaned auto-grading containers (ag-unified-*, ag-monitor-*).
        /// This is called at the end of grading sessions to ensure no containers are left behind,
        /// especially after the final batch which may not trigger the periodic cleanup.
        /// Also processes any containers that are pending retry cleanup.
        /// </summary>
        private void CleanupOrphanedContainers()
        {
            try
            {
                // First, process any containers pending retry cleanup
                ProcessPendingCleanupRetries();
                
                CleanupContainersByPrefix("ag-unified-", "unified");
                CleanupContainersByPrefix("ag-monitor-", "monitor");
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] WARNING: Error cleaning up orphaned containers: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes containers that are pending cleanup retry.
        /// Retries cleanup for containers that were added to the pending queue at least
        /// RetryCleanupDelaySeconds ago. This ensures we don't interfere with containers
        /// that might still be in use by students currently being graded.
        /// </summary>
        private void ProcessPendingCleanupRetries()
        {
            if (_pendingCleanupContainers.IsEmpty) return;

            var now = DateTime.UtcNow;
            var containersToRetry = new List<string>();
            var containersSuccessfullyRemoved = new List<string>();

            // Find containers that are ready for retry (older than RetryCleanupDelaySeconds)
            foreach (var kvp in _pendingCleanupContainers)
            {
                if ((now - kvp.Value).TotalSeconds >= RetryCleanupDelaySeconds)
                {
                    containersToRetry.Add(kvp.Key);
                }
            }

            if (containersToRetry.Count == 0) return;

            OnProgress($"[Docker Cleanup] Processing {containersToRetry.Count} container(s) pending retry cleanup...");

            // Try to remove containers that are ready for retry
            foreach (var containerName in containersToRetry)
            {
                try
                {
                    // Check if container still exists before attempting removal
                    if (_dockerExecutor.IsContainerExist(containerName))
                    {
                        _dockerExecutor.ExecDockerCommand($"rm -f {containerName}", 10000);
                        
                        // Verify removal
                        if (!_dockerExecutor.IsContainerExist(containerName))
                        {
                            containersSuccessfullyRemoved.Add(containerName);
                            OnProgress($"[Docker Cleanup] Retry successful: removed {containerName}");
                        }
                        else
                        {
                            OnProgress($"[Docker Cleanup] Retry failed: {containerName} still exists");
                        }
                    }
                    else
                    {
                        // Container no longer exists, remove from pending queue
                        containersSuccessfullyRemoved.Add(containerName);
                        OnProgress($"[Docker Cleanup] Container {containerName} no longer exists, removing from retry queue");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"[Docker Cleanup] Retry cleanup failed for {containerName}: {ex.Message}");
                }
            }

            // Remove successfully cleaned containers from pending queue
            foreach (var containerName in containersSuccessfullyRemoved)
            {
                _pendingCleanupContainers.TryRemove(containerName, out _);
            }

            if (containersSuccessfullyRemoved.Count > 0)
            {
                OnProgress($"[Docker Cleanup] Retry cleanup completed: {containersSuccessfullyRemoved.Count}/{containersToRetry.Count} containers removed");
            }
        }

        /// <summary>
        /// Adds a container to the pending cleanup retry queue.
        /// The container will be retried for cleanup after RetryCleanupDelaySeconds.
        /// </summary>
        /// <param name="containerName">Name of the container to add to retry queue</param>
        private void AddToPendingCleanupRetry(string containerName)
        {
            // Only add if not already in the queue
            if (_pendingCleanupContainers.TryAdd(containerName, DateTime.UtcNow))
            {
                OnProgress($"[Docker Cleanup] Added {containerName} to retry cleanup queue");
            }
        }
        
        /// <summary>
        /// Registers a container as actively in use by a grading task.
        /// Active containers are excluded from periodic cleanup to prevent
        /// killing containers that are still being used by parallel students.
        /// </summary>
        /// <param name="containerName">Name of the container to register</param>
        private static void RegisterActiveContainer(string containerName)
        {
            _activeContainers.TryAdd(containerName, 0);
        }
        
        /// <summary>
        /// Unregisters a container from the active containers registry.
        /// Call this when a container is no longer needed and can be cleaned up.
        /// </summary>
        /// <param name="containerName">Name of the container to unregister</param>
        private static void UnregisterActiveContainer(string containerName)
        {
            _activeContainers.TryRemove(containerName, out _);
        }
        
        /// <summary>
        /// Checks if a container is currently registered as active.
        /// </summary>
        /// <param name="containerName">Name of the container to check</param>
        /// <returns>True if the container is active and should not be cleaned up</returns>
        private static bool IsContainerActive(string containerName)
        {
            return _activeContainers.ContainsKey(containerName);
        }

        /// <summary>
        /// Cleans up containers matching a specific name prefix.
        /// OPTIMIZATION: Uses batch removal (docker rm -f container1 container2...) instead of sequential
        /// commands for better performance when grading 200+ students in batches of 15.
        /// CRITICAL FIX: Excludes containers that are currently registered as active to prevent
        /// killing containers that are still being used by parallel grading tasks.
        /// </summary>
        /// <param name="prefix">The container name prefix to filter by (e.g., "ag-unified-")</param>
        /// <param name="containerType">Human-readable type name for logging (e.g., "unified")</param>
        private void CleanupContainersByPrefix(string prefix, string containerType)
        {
            // Get container names (not IDs) so we can check against active containers registry
            var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(
                $"ps -a --filter 'name={prefix}' --format '{{{{.Names}}}}'", 5000);

            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var containerNames = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                OnProgress($"[Docker Cleanup] Found {containerNames.Length} {containerType} container(s)");

                // CRITICAL FIX: Filter out containers that are currently in use by parallel grading tasks
                // Without this, periodic cleanup would kill containers for students still being graded
                var containersToRemove = new List<string>();
                var skippedActiveCount = 0;
                
                foreach (var containerName in containerNames)
                {
                    if (IsContainerActive(containerName))
                    {
                        // Container is actively being used - DO NOT remove it
                        skippedActiveCount++;
                        OnProgress($"[Docker Cleanup] Skipping active container: {containerName}");
                    }
                    else
                    {
                        containersToRemove.Add(containerName);
                    }
                }
                
                if (skippedActiveCount > 0)
                {
                    OnProgress($"[Docker Cleanup] Protected {skippedActiveCount} active {containerType} container(s) from cleanup");
                }
                
                if (containersToRemove.Count == 0)
                {
                    OnProgress($"[Docker Cleanup] No orphaned {containerType} containers to remove");
                    return;
                }
                
                OnProgress($"[Docker Cleanup] Removing {containersToRemove.Count} orphaned {containerType} container(s)");

                // OPTIMIZATION: Batch remove containers in chunks for efficiency
                // This reduces Docker API overhead significantly for large batch grading
                for (int i = 0; i < containersToRemove.Count; i += BatchRemovalSize)
                {
                    var batchCount = Math.Min(BatchRemovalSize, containersToRemove.Count - i);
                    // OPTIMIZATION: Use GetRange instead of LINQ Skip/Take to avoid intermediate collections
                    var batchContainers = containersToRemove.GetRange(i, batchCount);
                    var batchNames = string.Join(" ", batchContainers);
                    try
                    {
                        // Use single docker rm command for batch removal
                        _dockerExecutor.ExecDockerCommand($"rm -f {batchNames}", BatchRemovalTimeoutMs);
                        OnProgress($"[Docker Cleanup] Removed batch of {batchCount} {containerType} container(s)");
                    }
                    catch (Exception ex)
                    {
                        // Fallback: try removing individually if batch fails
                        OnProgress($"[Docker Cleanup] Batch removal failed, falling back to individual removal: {ex.Message}");
                        foreach (var containerName in batchContainers)
                        {
                            try { _dockerExecutor.ExecDockerCommand($"rm -f {containerName}", 5000); } catch { }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Grades a single student's submission in Docker containers.
        /// 
        /// EXECUTION ORDER (critical for network capture):
        /// 1. Load test kit configuration
        /// 2. START NetworkMonitor FIRST (captures traffic from the very beginning)
        /// 3. Set up Docker containers (server and client) with TTY support
        /// 4. Copy student DLLs to containers
        /// 5. Generate appsettings.json for both server and client
        /// 6. Execute test cases from Detail.xlsx
        /// 7. Capture and compare outputs
        /// 8. Stop NetworkMonitor and cleanup containers
        /// 
        /// NetworkMonitor MUST start before containers to capture:
        /// - Docker health checks (filtered out later)
        /// - Full TCP handshake (SYN, SYN-ACK, ACK)
        /// - All data transfers (PSH-ACK)
        /// - Connection teardown (FIN-ACK)
        /// </summary>
        /// <param name="config">Docker grading configuration</param>
        /// <param name="testKitPath">Path to test kit folder containing Header.xlsx, Environment.xlsx, test cases</param>
        /// <param name="studentResultPath">Path to save student's grading results</param>
        /// <param name="serverDllPath">Path to student's server DLL (optional)</param>
        /// <param name="clientDllPath">Path to student's client DLL (optional)</param>
        /// <param name="studentCode">Student code for container naming</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Grading result with test case scores</returns>
        public async Task<DockerGradingResult> GradeStudentAsync(
            DockerGradingConfig config,
            string testKitPath,
            string studentResultPath,
            string? serverDllPath,
            string? clientDllPath,
            string studentCode,
            CancellationToken ct = default)
        {
            var result = new DockerGradingResult { StudentCode = studentCode };
            Directory.CreateDirectory(studentResultPath);

            // Set current student code for logging
            _currentStudentCode = studentCode;

            // Unified container name - use sanitized student code for valid Docker container names
            // Docker container names must match [a-zA-Z0-9][a-zA-Z0-9_.-]+ (no spaces or special chars)
            var unifiedContainer = ContainerNameHelper.BuildUnifiedContainerName(studentCode);
            var databaseContainer = config.DatabaseContainerName; // Database container name from config

            // CRITICAL FIX: Check Docker container count before starting
            // This prevents hitting Docker daemon limits when grading 200+ students
            CheckDockerContainerLimit();

            // CRITICAL FIX: Each student needs a unique database instance name
            // Format: {BaseDatabaseName}_{studentCode} (e.g., Library_student1, Library_student2)
            // This allows multiple students to share the same container without data conflicts
            string studentDatabaseName = "";

            // Network monitor pcap file path (for sidecar pattern)
            string pcapFilePath = "";

            try
            {
                OnProgress($"Loading test kit configuration from {testKitPath}...");
                var testKitConfig = LoadTestKitConfig(testKitPath, config);
                result.MaxMark = testKitConfig.TotalMaxMark;

                // Store the protocol type for use in network capture parsing
                _currentTestKitProtocol = testKitConfig.Protocol ?? "TCP";
                OnProgress($"[TestKit] Protocol type: {_currentTestKitProtocol}");

                // CRITICAL: Apply DefaultGradeContent from test kit to determine HasServer/HasClient
                // This overrides the default behavior based on what the test kit expects students to submit
                if (!string.IsNullOrEmpty(testKitConfig.DefaultGradeContent))
                {
                    OnProgress($"[TestKit] Applying DefaultGradeContent from test kit: {testKitConfig.DefaultGradeContent}");
                    if (testKitConfig.DefaultGradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    {
                        // Students submit ONLY server - use golden client
                        config.HasServer = true;
                        config.HasClient = false;
                        OnProgress($"[TestKit] Students submit SERVER only → HasServer=true, HasClient=false");
                    }
                    else if (testKitConfig.DefaultGradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                    {
                        // Students submit ONLY client - use golden server
                        config.HasServer = false;
                        config.HasClient = true;
                        OnProgress($"[TestKit] Students submit CLIENT only → HasServer=false, HasClient=true");
                    }
                    else // "Client/Server" or default
                    {
                        // Students submit BOTH - no golden needed
                        config.HasServer = true;
                        config.HasClient = true;
                        OnProgress($"[TestKit] Students submit BOTH → HasServer=true, HasClient=true");
                    }
                }

                // CRITICAL FIX: Create unique database name for this student
                // Format: {BaseName}_{StudentCode} to ensure isolation
                var baseDatabaseName = testKitConfig.DatabaseName ?? "Library";
                studentDatabaseName = $"{baseDatabaseName}_{studentCode}";
                testKitConfig.DatabaseName = studentDatabaseName;

                OnProgress($"[Database] Student {studentCode} will use database instance: {studentDatabaseName}");
                OnProgress($"[Database] Database container {databaseContainer} is shared, but instance is unique per student");

                // INITIAL DLL DISCOVERY: Find what DLLs the student provided
                // This logic discovers which DLLs exist in the student's submission.
                // The actual selection of which DLL to use (student vs golden) happens
                // PER TEST CASE based on the Grade_Content field in each test case's Header.xlsx.
                //
                // - HasServer=true: Search for student's server in their solution
                // - HasServer=false: Don't search for server, will use golden if needed
                // - HasClient=true: Search for student's client in their solution
                // - HasClient=false: Don't search for client, will use golden if needed
                //
                // The serverDllPath and clientDllPath parameters contain what was found in student's solution.
                // Each test case will decide whether to use student's DLLs or golden DLLs based on its Grade_Content.

                string? actualServerDllPath = null;
                string? actualClientDllPath = null;

                // Server discovery
                OnProgress($"[SERVER DISCOVERY] config.HasServer={config.HasServer}, serverDllPath={serverDllPath ?? "(null)"}");
                if (config.HasServer)
                {
                    // Examiner expects student to provide server - use discovered path
                    actualServerDllPath = serverDllPath;
                    if (string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"WARNING: Student should provide server ({config.ServerProjectName}) but none found!");
                        OnProgress($"[SERVER DISCOVERY] actualServerDllPath will be NULL - test should FAIL");
                    }
                    else
                    {
                        OnProgress($"Discovered student's server: {Path.GetFileName(actualServerDllPath)}");
                        OnProgress($"[SERVER DISCOVERY] Using STUDENT's server: {actualServerDllPath}");
                    }
                }
                else
                {
                    // Examiner doesn't expect student to provide server, prepare golden server from test kit
                    actualServerDllPath = testKitConfig.GivenServerPath;
                    if (!string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"Prepared golden server from Meta/Given/Server: {Path.GetFileName(actualServerDllPath)}");
                        OnProgress($"[SERVER DISCOVERY] Using GOLDEN server: {actualServerDllPath}");
                    }
                    else
                    {
                        OnProgress("WARNING: No golden server found in Meta/Given/Server!");
                    }
                }

                // Client discovery
                if (config.HasClient)
                {
                    // Examiner expects student to provide client - use discovered path
                    actualClientDllPath = clientDllPath;
                    if (string.IsNullOrEmpty(actualClientDllPath))
                    {
                        OnProgress($"WARNING: Student should provide client ({config.ClientProjectName}) but none found!");
                    }
                    else
                    {
                        OnProgress($"Discovered student's client: {Path.GetFileName(actualClientDllPath)}");
                    }
                }
                else
                {
                    // Examiner doesn't expect student to provide client, prepare golden client from test kit
                    actualClientDllPath = testKitConfig.GivenClientPath;
                    if (!string.IsNullOrEmpty(actualClientDllPath))
                    {
                        OnProgress($"Prepared golden client from Meta/Given/Client: {Path.GetFileName(actualClientDllPath)}");
                    }
                    else
                    {
                        OnProgress("WARNING: No golden client found in Meta/Given/Client!");
                    }
                }

                // Log discovered/prepared paths (final selection happens per test case based on Grade_Content)
                OnProgress($"Available Server DLL: {(actualServerDllPath != null ? Path.GetFileName(actualServerDllPath) : "(NONE)")}");
                OnProgress($"Available Client DLL: {(actualClientDllPath != null ? Path.GetFileName(actualClientDllPath) : "(NONE)")}");
                OnProgress($"NOTE: Each test case will select DLLs based on its Grade_Content field");

                // NOTE: Network monitor is shared across all students (started by UI/CLI)
                // No per-student registration needed - monitor captures all traffic on loopback
                // and filters by port number (each student gets unique port)

                // CRITICAL: Setup database container FIRST before creating instance
                // The database container must be running before we can create database instances
                OnProgress($"[Database] Ensuring database container is running...");
                await SetupDatabaseContainerAsync(config);

                // CRITICAL: Create database instance for this student
                // Database container must be running before creating instance
                // Each student gets their own database instance (e.g., Library_student1)
                OnProgress($"[Database] Creating database instance for {studentCode}...");

                // Look for SQL initialization script in test kit Environment.xlsx
                // Key name: Default_Database_File_Path
                string? sqlScriptPath = null;
                var environmentExcelPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (File.Exists(environmentExcelPath))
                {
                    try
                    {
                        using var envWb = new XLWorkbook(environmentExcelPath);
                        if (envWb.TryGetWorksheet("Config", out var envWs))
                        {
                            // Look for Default_Database_File_Path in Environment.xlsx Config sheet
                            foreach (var row in envWs.RowsUsed().Skip(1))
                            {
                                var key = row.Cell(1).GetValue<string>()?.Trim();
                                var value = row.Cell(2).GetValue<string>()?.Trim();

                                // Match exact key name: Default_Database_File_Path
                                // Also support legacy key names for backward compatibility
                                if (key != null && (
                                    key.Equals("Default_Database_File_Path", StringComparison.OrdinalIgnoreCase) ||
                                    key.Replace("_", "").Equals("DefaultDatabaseFilePath", StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (!string.IsNullOrEmpty(value))
                                    {
                                        // Resolve relative path from test kit folder
                                        sqlScriptPath = Path.IsPathRooted(value) ? value : Path.Combine(testKitPath, value);
                                        OnProgress($"[Database] Found SQL script path in Environment.xlsx (key '{key}'): {sqlScriptPath}");
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[Database] Warning: Failed to read SQL script path from Environment.xlsx: {ex.Message}");
                    }
                }

                // If no SQL script in Environment.xlsx, check for default locations
                if (string.IsNullOrEmpty(sqlScriptPath))
                {
                    var defaultScriptLocations = new[]
                    {
                        Path.Combine(testKitPath, $"{baseDatabaseName}.sql"),
                        Path.Combine(testKitPath, "database.sql"),
                        Path.Combine(testKitPath, "init.sql"),
                        Path.Combine(Path.GetDirectoryName(testKitPath) ?? "", $"{baseDatabaseName}.sql")
                    };

                    foreach (var location in defaultScriptLocations)
                    {
                        if (File.Exists(location))
                        {
                            sqlScriptPath = location;
                            OnProgress($"[Database] Found SQL script at default location: {sqlScriptPath}");
                            break;
                        }
                    }
                }

                // Attempt to create database instance - failure will not stop grading
                // SQL server container will remain running even if database creation fails
                try
                {
                    if (!string.IsNullOrEmpty(sqlScriptPath) && File.Exists(sqlScriptPath))
                    {
                        OnProgress($"[Database] Creating database '{studentDatabaseName}' from SQL script: {Path.GetFileName(sqlScriptPath)}");

                        // Read SQL script and replace database name with student-specific name
                        var sqlContent = File.ReadAllText(sqlScriptPath);

                        // Replace the default database name with student-specific name
                        // Common patterns: CREATE DATABASE [DatabaseName], USE [DatabaseName]
                        if (!string.IsNullOrEmpty(baseDatabaseName))
                        {
                            sqlContent = sqlContent.Replace($"[{baseDatabaseName}]", $"[{studentDatabaseName}]");
                            sqlContent = sqlContent.Replace($"{baseDatabaseName}", studentDatabaseName);
                        }

                        // Create temporary SQL file with student-specific database name
                        var tempSqlPath = Path.Combine(Path.GetTempPath(), $"{studentDatabaseName}.sql");
                        File.WriteAllText(tempSqlPath, sqlContent);

                        await CreateDatabaseInstanceAsync(config, studentDatabaseName, tempSqlPath);

                        // Clean up temp file
                        try { File.Delete(tempSqlPath); } catch { }
                    }
                    else
                    {
                        OnProgress($"[Database] No SQL script found - skipping database instance creation");
                        OnProgress($"[Database] SQL server container will remain running for the session");
                        // Don't create empty database if no script is found - just skip
                    }
                }
                catch (Exception dbEx)
                {
                    OnProgress($"[Database] Failed to create database instance: {dbEx.Message}");
                    OnProgress($"[Database] Continuing with grading - SQL server container remains running");
                    // Don't throw - allow grading to continue
                }

                // Setup unified container
                OnProgress($"Setting up unified Docker container for {studentCode}...");
                // Logs will be exported from container after grading
                // They are written per stage inside the container to /apps/server and /apps/client
                await SetupUnifiedContainerAsync(
                    actualServerDllPath,
                    actualClientDllPath,
                    config,
                    testKitConfig,
                    unifiedContainer);

                // Setup network monitor container attached to unified container
                // Use sanitized student code for valid Docker container names
                var monitorContainer = ContainerNameHelper.BuildMonitorContainerName(studentCode);
                pcapFilePath = Path.Combine(studentResultPath, "network_capture.pcap");
                _currentPcapFilePath = pcapFilePath; // Store for per-stage parsing
                _lastParsedPacketCount = 0; // Reset packet counter
                Directory.CreateDirectory(studentResultPath);

                OnProgress($"Setting up network monitor container for {studentCode}...");
                // Use CodeContainerInternalPort for network monitoring (no port allocation needed)
                await SetupNetworkMonitorContainerAsync(
                    monitorContainer,
                    unifiedContainer,
                    config.CodeContainerInternalPort,
                    pcapFilePath,
                    testKitConfig.Protocol ?? "TCP");

                // Notify that containers are ready (for staggered startup optimization)
                OnProgress($"Docker containers ready for {studentCode}");
                ContainersReady?.Invoke(this, EventArgs.Empty);

                // Execute test cases
                foreach (var testCase in testKitConfig.TestCases)
                {
                    ct.ThrowIfCancellationRequested();

                    // CRITICAL: Set current test case name for per-TC logging
                    _currentTestCaseName = testCase.Name;

                    // CRITICAL: Clear old stage log files before executing new test case
                    // Same container is reused across test cases, so logs must be cleaned up
                    ClearStageLogsInContainer(unifiedContainer);

                    // CRITICAL FIX: Stop server and client processes before starting new test case
                    // Each test case must start fresh - processes from previous test cases MUST be killed
                    // This prevents issues like:
                    // - TC2 starts server, TC3 expects no server but client connects to TC2's still-running server
                    // - Data from previous test case leaking into current test case
                    // - Cross-contamination of test results
                    // NOTE: We stop only server/client, NOT the keeper process (which keeps named pipe open)
                    await StopAllProcessesForNewTestCaseAsync(unifiedContainer);

                    // CRITICAL: Clear RunContext at START of each test case for proper isolation
                    // This prevents packets from previous test cases from being included in comparisons
                    if (_currentMonitorContainer != null && !string.IsNullOrEmpty(_currentPcapFilePath))
                    {
                        OnProgress($"[NetworkMonitor] [{testCase.Name}] Starting test case - clearing RunContext...");

                        // Clear previous test case packets from RunContext (in-memory)
                        _runContext.ClearCapturedNetworkPackets(_currentStudentCode ?? "");

                        // CRITICAL: DO NOT reset _lastParsedPacketCount or restart the sidecar!
                        // 
                        // The correct approach is to keep the sidecar running continuously and
                        // let the packet counter keep incrementing. This way:
                        // - TC1 parses packets 0-10, counter becomes 10
                        // - TC2 parses packets 10-25, counter becomes 25
                        // - TC3 parses packets 25-40, counter becomes 40
                        // Each test case only sees NEW packets captured during its execution.
                        // 
                        // PREVIOUS BUGGY APPROACHES:
                        // 1. Reset counter to 0 -> re-parses old packets from previous TCs
                        // 2. Delete output file -> sidecar keeps writing to orphaned file handle
                        // 3. Restart container -> adds overhead, may cause issues over time
                        //
                        // The RunContext is cleared above, so even though the JSONL file contains
                        // packets from all test cases, only the NEW packets (since last parse)
                        // will be added to RunContext for comparison.

                        OnProgress($"[NetworkMonitor] [{testCase.Name}] RunContext cleared, packet counter at {_lastParsedPacketCount} (will only parse new packets)");
                    }

                    // Use per-test-case timeout from Header.xlsx (with fallback to config or default)
                    var testCaseTimeout = testCase.TimeoutSeconds;
                    OnProgress($"[TEST_CASE] Starting {testCase.Name} (timeout: {testCaseTimeout}s)");

                    using var testCaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    testCaseCts.CancelAfter(TimeSpan.FromSeconds(testCaseTimeout));

                    TestCaseResult tcResult;
                    try
                    {
                        tcResult = await ExecuteTestCaseAsync(
                            testCase, testKitConfig, config,
                            actualServerDllPath, actualClientDllPath,
                            unifiedContainer, unifiedContainer, testCaseCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Test case timed out (not overall cancellation)
                        OnProgress($"[TEST_CASE] {testCase.Name} TIMED OUT after {testCaseTimeout}s");
                        tcResult = new TestCaseResult
                        {
                            TestCaseName = testCase.Name,
                            MaxMark = testCase.MaxMark,
                            EarnedMark = 0,
                            Passed = false,
                            ErrorMessage = $"Student timed out during {testCase.Name} (timeout: {testCaseTimeout}s)"
                        };
                    }

                    result.TestCaseResults.Add(tcResult);

                    // Write test case results
                    var tcResultPath = Path.Combine(studentResultPath, testCase.Name);
                    Directory.CreateDirectory(tcResultPath);
                    await WriteTestCaseResultAsync(tcResultPath, testCase.Name, testCase.Path, tcResult);

                    // Move per-stage PCAP snapshots to TC folder for better organization
                    // Format: snapshot_TC3_stage1.pcap -> TC3/snapshot_TC3_stage1.pcap
                    MoveSnapshotsToTCFolder(studentResultPath, tcResultPath, testCase.Name);

                    // Export per-stage logs from container to ProcessLogs/TC# subdirectory
                    await ExportStageLogsForTestCaseAsync(unifiedContainer, studentResultPath, testCase.Name);

                    OnProgress($"Test case {testCase.Name}: {(tcResult.Passed ? "PASS" : "FAIL")} ({tcResult.EarnedMark:F2}/{tcResult.MaxMark:F2})");
                }

                // Calculate totals
                result.TotalMark = result.TestCaseResults.Sum(tc => tc.EarnedMark);
                // Note: No "Passed" status at grading level - individual test cases have Pass/Fail

                // Write overall summary
                await WriteOverallSummaryAsync(studentResultPath, result.TestCaseResults);
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Grading was cancelled";
                OnProgress("Grading cancelled");
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error: {ex.Message}";
                OnProgress($"Error: {ex.Message}");
            }
            finally
            {
                // UNIFIED CONTAINER: Logs are now exported per test case in ExportStageLogsForTestCaseAsync
                // No need to export here - commenting out old code
                // try
                // {
                //     OnProgress($"[Unified] Exporting per-stage log files from container...");
                //     await ExportLogsFromUnifiedContainerAsync(unifiedContainer, studentResultPath);
                //     OnProgress($"[Unified] Per-stage log files exported successfully");
                // }
                // catch (Exception ex)
                // {
                //     OnProgress($"[Unified] WARNING: Failed to export log files: {ex.Message}");
                // }

                // Cleanup network monitor container (sidecar pattern)
                // Use sanitized student code for valid Docker container names
                var monitorContainer = ContainerNameHelper.BuildMonitorContainerName(studentCode);
                try
                {
                    OnProgress($"[Monitor] Stopping and extracting pcap from {monitorContainer}...");
                    await CleanupNetworkMonitorContainerAsync(monitorContainer, studentResultPath);
                    OnProgress($"[Monitor] Network monitor cleanup completed");
                }
                catch (Exception ex)
                {
                    OnProgress($"[Monitor] WARNING: Failed to cleanup network monitor: {ex.Message}");
                }

                // Cleanup unified container
                await CleanupUnifiedContainerAsync(unifiedContainer, studentCode);

                // CRITICAL FIX: Cleanup the student's database INSTANCE within the shared container
                // This drops the database (e.g., Library_student1) but keeps the container running
                var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
                await CleanupDatabaseInstanceAsync(databaseContainer, studentDatabaseName, databasePassword);

                // Clear student context
                _currentStudentCode = null;
            }

            return result;
        }

        /// <summary>
        /// Helper method to report progress to listeners.
        /// Adds student code prefix for debugging if available.
        /// </summary>
        private void OnProgress(string message)
        {
            // Add [StudentCode] prefix to help with debugging
            var formattedMessage = !string.IsNullOrEmpty(_currentStudentCode)
                ? $"[{_currentStudentCode}] {message}"
                : message;
            // Invoke the event to send progress update
            ProgressUpdated?.Invoke(this, new GradingProgressEventArgs(formattedMessage));
        }

        // Method implementations are in partial class files:
        // - DockerGradingService.ContainerSetup.cs
        // - DockerGradingService.TestExecution.cs
        // - DockerGradingService.OutputComparison.cs
        // - DockerGradingService.TestKitLoading.cs
        // - DockerGradingService.Cleanup.cs
        // - DockerGradingService.ResultWriting.cs
    }
}
