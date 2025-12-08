using System;
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
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Helpers;
using SolutionGrader.Core.Keywords;

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
    /// </summary>
    public sealed class DockerGradingService
    {
        // Timing constants - optimized for faster execution
        // These are fallback values; prefer using config.TestCaseTimeoutSeconds
        private const int StartupDelayMs = 1500;  // Reduced from 3000 - wait for process to start
        private const int InputProcessingDelayMs = 2000;  // Reduced from 5000 - wait for input to be processed
        private const int OutputRetryMaxAttempts = 5;
        private const int OutputRetryDelayMs = 500;  // Reduced from 1000 - faster polling
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        private readonly DockerConsoleManager _consoleManager;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IRunContext _runContext;
        private string? _currentStudentCode; // Track current student for logging
        
        // Network monitoring for sidecar pattern
        private string? _currentMonitorContainer; // Name of network monitor container (e.g., ag-monitor-StudentCode)
        private string? _currentPcapFilePath; // Path to pcap file being written by network monitor
        private int _lastParsedPacketCount = 0; // Track how many packets we've already processed
        
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
        /// </summary>
        /// <param name="config">Docker grading configuration</param>
        public void DisposeAllContainers(DockerGradingConfig config)
        {
            OnProgress("[Docker] Disposing all containers...");
            
            var databaseContainer = config.DatabaseContainerName;
            var serverContainer = $"server-{databaseContainer}";
            var clientContainer = $"client-{databaseContainer}";
            
            // Remove code containers
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }
            
            // Remove database container
            try { _dockerExecutor.RemoveContainer(databaseContainer); } catch { }
            
            OnProgress("[Docker] All containers disposed");
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
            
            // Unified container name
            var unifiedContainer = $"ag-unified-{studentCode}";
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
                var monitorContainer = $"ag-monitor-{studentCode}";
                pcapFilePath = Path.Combine(studentResultPath, "network_capture.pcap");
                _currentPcapFilePath = pcapFilePath; // Store for per-stage parsing
                _lastParsedPacketCount = 0; // Reset packet counter
                Directory.CreateDirectory(studentResultPath);
                
                OnProgress($"Setting up network monitor container for {studentCode}...");
                await SetupNetworkMonitorContainerAsync(
                    monitorContainer,
                    unifiedContainer,
                    config.CodeContainerHostPort,
                    pcapFilePath,
                    testKitConfig.Protocol);
                
                // Notify that containers are ready (for staggered startup optimization)
                OnProgress($"Docker containers ready for {studentCode}");
                ContainersReady?.Invoke(this, EventArgs.Empty);
                
                // Execute test cases
                bool isFirstTestCase = true;
                foreach (var testCase in testKitConfig.TestCases)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    // CRITICAL FIX: For EACH test case, determine which files to copy based on Grade_Content
                    // This ensures we use golden server when grading client, and golden client when grading server
                    string? serverPath = actualServerDllPath;
                    string? clientPath = actualClientDllPath;
                    
                    if (!string.IsNullOrEmpty(testCase.GradeContent))
                    {
                        if (testCase.GradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                        {
                            // Grading client implementation -> use golden (given) server
                            serverPath = testKitConfig.GivenServerPath;
                            clientPath = actualClientDllPath;
                            OnProgress($"[TestCase {testCase.Name}] Grade_Content='Client' -> Using golden server + student client");
                        }
                        else if (testCase.GradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                        {
                            // Grading server implementation -> use golden (given) client
                            serverPath = actualServerDllPath;
                            clientPath = testKitConfig.GivenClientPath;
                            OnProgress($"[TestCase {testCase.Name}] Grade_Content='Server' -> Using student server + golden client");
                            OnProgress($"[TestCase {testCase.Name}] Student server path: {serverPath ?? "(NULL)"}");
                            OnProgress($"[TestCase {testCase.Name}] Golden client path: {clientPath ?? "(NULL)"}");
                            if (!string.IsNullOrEmpty(clientPath))
                            {
                                OnProgress($"[TestCase {testCase.Name}] Golden client filename: {Path.GetFileName(clientPath)}");
                            }
                        }
                    }
                    
                    // CRITICAL: Clear old stage log files before executing new test case
                    // Same container is reused across test cases, so logs must be cleaned up
                    ClearStageLogsInContainer(unifiedContainer);
                    
                    // Copy files to unified container (will overwrite existing files)
                    OnProgress($"Copying files for test case {testCase.Name}...");
                    await CopyFilesToUnifiedContainerAsync(serverPath, clientPath, config, testKitConfig, unifiedContainer);
                    
                    // Generate appsettings.json in unified container
                    GenerateAppsettingsInUnifiedContainer(config, testKitConfig, unifiedContainer);
                    
                    isFirstTestCase = false;
                    
                    // Use per-test-case timeout from Header.xlsx (with fallback to config or default)
                    var testCaseTimeout = testCase.TimeoutSeconds;
                    OnProgress($"Executing test case: {testCase.Name} (timeout: {testCaseTimeout}s)...");
                    
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
                        OnProgress($"Student timed out during {testCase.Name} (timeout: {testCaseTimeout}s)");
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
                    
                    OnProgress($"Test case {testCase.Name}: {(tcResult.Passed ? "PASS" : "FAIL")} ({tcResult.EarnedMark:F2}/{tcResult.MaxMark:F2})");
                }
                
                // Calculate totals
                result.TotalMark = result.TestCaseResults.Sum(tc => tc.EarnedMark);
                // CRITICAL FIX: Student passes ONLY if ALL test cases pass (not ANY)
                result.Passed = result.TestCaseResults.All(tc => tc.Passed);
                
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
                // UNIFIED CONTAINER: Export per-stage log files from container to student directory
                try
                {
                    OnProgress($"[Unified] Exporting per-stage log files from container...");
                    await ExportLogsFromUnifiedContainerAsync(unifiedContainer, studentResultPath);
                    OnProgress($"[Unified] Per-stage log files exported successfully");
                }
                catch (Exception ex)
                {
                    OnProgress($"[Unified] WARNING: Failed to export log files: {ex.Message}");
                }
                
                // Cleanup network monitor container (sidecar pattern)
                var monitorContainer = $"ag-monitor-{studentCode}";
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
        
        #region Container Setup
        
        /// <summary>
        /// Sets up all required Docker containers for grading:
        /// 1. MSSQL Database Container - provides database backend for student applications
        /// 2. Server Container - runs the student's server application
        /// 3. Client Container - runs the student's client application
        /// 
        /// All containers are connected to the same Docker network for inter-container communication.
        /// The server container port is EXPOSED to the host for NetworkMonitor packet capture.
        /// </summary>
        
        /// <summary>
        /// Setup unified container that runs both client and server processes.
        /// Processes are managed by supervisord and started/stopped by test case actions.
        /// CLIENT AND SERVER ARE NOT STARTED AUTOMATICALLY - they start only when test case Detail.xlsx says so.
        /// Logs are written per stage to /apps/server/server-stage-N.log and /apps/client/client-stage-N.log.
        /// </summary>
        private async Task SetupUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            OnProgress($"[Unified] Creating unified container: {unifiedContainer}");
            
            // Remove existing unified container if any
            _commandExecutor.RunCommand($"docker rm -f {unifiedContainer} 2>/dev/null || true", null, null, 10000);
            
            // Create the unified container with supervisord
            // CRITICAL: Processes are NOT started automatically
            // They are controlled by test case actions (StartClient, StartServer, CloseClient, CloseServer)
            // Logs are written per stage to /apps/server/server-stage-N.log and /apps/client/client-stage-N.log
            var dockerCmd = $"docker run -d --name {unifiedContainer} " +
                           $"--network {config.DockerNetwork} " +
                           $"-t " +  // TTY for unbuffered logs
                           $"{config.CodeImageName}";
            
            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);
            OnProgress($"[Unified] Container created - supervisord running, processes idle");
            
            // Wait for supervisord to be ready
            await Task.Delay(1000);
            
            // Copy DLLs and appsettings to container (in separate /apps/server and /apps/client folders)
            await CopyFilesToUnifiedContainerAsync(
                serverDllPath, 
                clientDllPath, 
                config, 
                testKitConfig,
                unifiedContainer);
            
            OnProgress($"[Unified] Container ready - processes will start when test cases execute StartClient/StartServer actions");
            OnProgress($"[Unified] Logs will be written per stage: /apps/server/server-stage-N.log and /apps/client/client-stage-N.log");
        }
        
        /// <summary>
        /// Sets up the MSSQL database container if not already running.
        /// The database container is shared between students for efficiency.
        /// </summary>
        private async Task SetupDatabaseContainerAsync(DockerGradingConfig config)
        {
            var databaseContainer = config.DatabaseContainerName;
            
            // Check if database container is already running
            if (_dockerExecutor.IsContainerRunning(databaseContainer))
            {
                OnProgress($"[Docker] Database container {databaseContainer} is already running");
                return;
            }
            
            // Check if container exists but stopped
            if (_dockerExecutor.IsContainerExist(databaseContainer))
            {
                OnProgress($"[Docker] Starting existing database container {databaseContainer}...");
                _dockerExecutor.StartExistedContainer(databaseContainer);
                // Wait for container to be running with quick health checks (no logging spam)
                await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 10);
                return;
            }
            
            // Create new MSSQL database container
            OnProgress($"[Docker] Creating new MSSQL database container {databaseContainer}...");
            
            var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
            var databaseBase = new DockerBase
            {
                ImageName = config.DatabaseImageName,
                ContainerName = databaseContainer,
                DockerNetwork = config.DockerNetwork,
                ContainerPort = config.DatabaseContainerInternalPort,
                HostPort = config.DatabaseContainerHostPort,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    { "ACCEPT_EULA", "Y" },
                    { "MSSQL_SA_PASSWORD", databasePassword }
                }
            };
            
            _dockerExecutor.RunContainer(databaseBase, 3000);
            OnProgress($"[Docker] Database container {databaseContainer} created with port {config.DatabaseContainerHostPort}:{config.DatabaseContainerInternalPort} exposed");
            
            // Wait for MSSQL to fully start with polling instead of fixed delay
            OnProgress("[Docker] Waiting for MSSQL to start...");
            await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 20);
        }
        
        /// <summary>
        /// Waits for a container to be in running state with efficient polling.
        /// Uses short intervals without logging to avoid spam while ensuring container is ready.
        /// </summary>
        private async Task WaitForContainerRunningAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 2; // Check every 500ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (_dockerExecutor.IsContainerRunning(containerName))
                {
                    // Container is running, give it a moment to fully initialize
                    await Task.Delay(500);
                    return;
                }
                await Task.Delay(500); // Check every 500ms without logging
            }
            // If we get here, container didn't start in time but proceed anyway
            OnProgress($"[Docker] Warning: Container {containerName} may not be fully ready after {maxWaitSeconds}s");
        }
        
        /// <summary>
        /// OPTIMIZATION: Dynamically waits for a container to be removed instead of fixed delays.
        /// Checks every 100ms up to maxWaitSeconds. Returns immediately when container is gone.
        /// Much faster than fixed waits - typically returns in 0-200ms vs 500ms+ fixed delay.
        /// </summary>
        private async Task WaitForContainerRemovedAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 10; // Check every 100ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    // Container is gone - return immediately
                    OnProgress($"[Docker Cleanup] Container {containerName} successfully removed (waited {i * 100}ms)");
                    return;
                }
                await Task.Delay(100); // Check every 100ms without logging
            }
            
            // CRITICAL: Container still exists after max wait - this is a zombie container
            OnProgress($"[Docker Cleanup] WARNING: Container {containerName} still exists after {maxWaitSeconds}s - attempting force removal");
            
            // Try force removal with -f flag
            try
            {
                var forceCommand = $"rm -f {containerName}";
                _dockerExecutor.ExecDockerCommand(forceCommand, 5000);
                OnProgress($"[Docker Cleanup] Force removal attempted for {containerName}");
                
                // Wait a bit more to see if force removal worked
                await Task.Delay(1000);
                
                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    OnProgress($"[Docker Cleanup] Force removal successful for {containerName}");
                }
                else
                {
                    OnProgress($"[Docker Cleanup] CRITICAL: Container {containerName} is a zombie - cannot be removed. This may cause resource exhaustion!");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] ERROR: Force removal failed for {containerName}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// OPTIMIZATION: Dynamically waits for processes to be killed in a container.
        /// Checks every 50ms up to maxWaitMs. Returns immediately when no target processes remain.
        /// Much faster than fixed waits - typically returns in 0-100ms vs 100ms+ fixed delay.
        /// </summary>
        /// <summary>
        /// Checks Docker container count and warns if approaching limits.
        /// CRITICAL for batch grading 200+ students to prevent resource exhaustion.
        /// </summary>
        private void CheckDockerContainerLimit()
        {
            try
            {
                // Count total containers (running + stopped)
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput("ps -a -q", 5000);
                if (success)
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var totalContainers = containerIds.Length;
                    
                    OnProgress($"[Docker Resource Monitor] Total containers: {totalContainers}");
                    
                    // Docker default limit is typically 256-512 containers per daemon
                    // Warn at 50% and 75% thresholds
                    if (totalContainers > 380) // 75% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] CRITICAL WARNING: {totalContainers} containers exist! Approaching Docker daemon limit. Container creation may fail soon!");
                    }
                    else if (totalContainers > 256) // 50% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] WARNING: {totalContainers} containers exist. Consider aggressive cleanup to prevent exhaustion.");
                    }
                    else if (totalContainers > 128) // 25% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] Info: {totalContainers} containers exist. Monitoring for potential exhaustion.");
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Resource Monitor] Warning: Could not check container count: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Aggressively cleans up old auto-grading containers that may have been left behind.
        /// CRITICAL for batch grading 200+ students to prevent Docker exhaustion.
        /// </summary>
        private void AggressiveCleanupOldContainers()
        {
            OnProgress("[Docker Aggressive Cleanup] Starting cleanup of old auto-grading containers...");
            
            try
            {
                // Find all auto-grading containers (ag-server-*, ag-client-*)
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(
                    "ps -a --filter 'name=ag-server-' --filter 'name=ag-client-' -q", 5000);
                
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    OnProgress($"[Docker Aggressive Cleanup] Found {containerIds.Length} old auto-grading containers to remove");
                    
                    foreach (var containerId in containerIds)
                    {
                        try
                        {
                            _dockerExecutor.ExecDockerCommand($"rm -f {containerId}", 5000);
                        }
                        catch
                        {
                            // Ignore individual failures, continue with cleanup
                        }
                    }
                    
                    OnProgress($"[Docker Aggressive Cleanup] Cleanup complete. Removed {containerIds.Length} containers.");
                }
                else
                {
                    OnProgress("[Docker Aggressive Cleanup] No old containers found.");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Aggressive Cleanup] Warning: Cleanup encountered errors: {ex.Message}");
            }
        }
        
        private async Task WaitForProcessesKilledAsync(string containerName, string processPattern, int maxWaitMs = 500)
        {
            var maxAttempts = maxWaitMs / 50; // Check every 50ms
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    // Check if any target processes still exist
                    var command = $"{containerName} sh -c \"ps aux | grep '{processPattern}' | grep -v grep | wc -l\"";
                    var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(command, 1000);
                    
                    if (success && int.TryParse(output.Trim(), out int count) && count == 0)
                    {
                        // All processes killed - return immediately
                        return;
                    }
                }
                catch
                {
                    // Error checking processes - assume they're gone
                    return;
                }
                
                await Task.Delay(50); // Check every 50ms without logging
            }
            // If we get here, some processes may still exist but proceed anyway
        }
        
        
        /// <summary>
        /// Copy DLLs and appsettings to unified container in SEPARATE folders.
        /// Server goes to /apps/server, Client goes to /apps/client.
        /// This ensures appsettings.json and DLL mod fallback work correctly.
        /// </summary>
        private async Task CopyFilesToUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            var dllModService = new DllModificationService();
            var tempDirectories = new List<string>();
            
            try
            {
                // Create /apps/server and /apps/client directories in container
                _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/server");
                _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/client");
                
                // Copy SERVER files
                if (!string.IsNullOrEmpty(serverDllPath))
                {
                    var serverDir = Path.GetDirectoryName(serverDllPath);
                    if (serverDir != null)
                    {
                        string dirToCopy = serverDir;
                        
                        try
                        {
                            // Apply DLL modification fallback if enabled
                            if (config.UseDllModificationFallback)
                            {
                                var tempStagingDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_UnifiedServer_{_currentStudentCode}_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(tempStagingDir);
                                tempDirectories.Add(tempStagingDir);
                                
                                CopyDirectory(serverDir, tempStagingDir);
                                OnProgress($"[Unified] Copied server files to temp for modification");
                                
                                // Server binds to 0.0.0.0 (all interfaces) or 127.0.0.1 (localhost only)
                                // We DON'T force IP replacement - student code should already use correct IP
                                // We ONLY modify PORT for parallel grading
                                // Passing "0.0.0.0" here won't hurt - it will try to replace any hardcoded "0.0.0.0" with "0.0.0.0" (no-op)
                                var result = dllModService.CheckAndPatchIfNeeded(
                                    tempStagingDir,
                                    config.ServerProjectName,
                                    isServer: true,
                                    targetPort: config.CodeContainerInternalPort,
                                    targetIp: "0.0.0.0"  // No-op: replaces 0.0.0.0 with 0.0.0.0, only ports change
                                );
                                
                                OnProgress($"[Unified] Server DLL mod: {result.GetSummary()}");
                                dirToCopy = tempStagingDir;
                            }
                            
                            // Copy to /apps/server
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/server/AutoGrading_UnifiedServer_*/
                            // With "/.": creates /apps/server/*.dll, /apps/server/appsettings.json, etc.
                            var serverSource = dirToCopy.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(serverSource, $"{unifiedContainer}:/apps/server/");
                            OnProgress($"[Unified] Copied server files to /apps/server");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Server copy failed: {ex.Message}");
                        }
                    }
                }
                
                // Copy CLIENT files
                if (!string.IsNullOrEmpty(clientDllPath))
                {
                    var clientDir = Path.GetDirectoryName(clientDllPath);
                    if (clientDir != null)
                    {
                        string dirToCopy = clientDir;
                        
                        try
                        {
                            // Apply DLL modification fallback if enabled
                            if (config.UseDllModificationFallback)
                            {
                                var tempStagingDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_UnifiedClient_{_currentStudentCode}_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(tempStagingDir);
                                tempDirectories.Add(tempStagingDir);
                                
                                CopyDirectory(clientDir, tempStagingDir);
                                OnProgress($"[Unified] Copied client files to temp for modification");
                                
                                // Client connects to localhost/127.0.0.1 in unified container
                                // We DON'T force IP replacement - student code should already use localhost
                                // We ONLY modify PORT for parallel grading
                                // Passing "localhost" here: replaces "localhost" → "localhost" (no-op), only ports change
                                var result = dllModService.CheckAndPatchIfNeeded(
                                    tempStagingDir,
                                    config.ClientProjectName,
                                    isServer: false,
                                    targetPort: config.CodeContainerInternalPort,
                                    targetIp: "localhost"  // No-op: replaces localhost with localhost, only ports change
                                );
                                
                                OnProgress($"[Unified] Client DLL mod: {result.GetSummary()}");
                                dirToCopy = tempStagingDir;
                            }
                            
                            // Copy to /apps/client
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/client/AutoGrading_UnifiedClient_*/
                            // With "/.": creates /apps/client/*.dll, /apps/client/appsettings.json, etc.
                            var clientSource = dirToCopy.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(clientSource, $"{unifiedContainer}:/apps/client/");
                            OnProgress($"[Unified] Copied client files to /apps/client");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Client copy failed: {ex.Message}");
                        }
                    }
                }
                
                // Generate appsettings.json in both folders
                GenerateAppsettingsInUnifiedContainer(config, testKitConfig, unifiedContainer);
            }
            finally
            {
                // Cleanup temp directories
                foreach (var tempDir in tempDirectories)
                {
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch (Exception cleanEx)
                    {
                        OnProgress($"[Unified] WARNING: Failed to cleanup temp directory {tempDir}: {cleanEx.Message}");
                    }
                }
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Recursively copies a directory and all its contents to a new location.
        /// Used for creating temporary staging areas for DLL modification.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
            }
            
            // Create destination directory
            Directory.CreateDirectory(destDir);
            
            // Copy all files
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destDir, file.Name);
                file.CopyTo(targetFilePath, overwrite: true);
            }
            
            // Recursively copy subdirectories
            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestDir = Path.Combine(destDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestDir);
            }
        }
        
        /// <summary>
        /// Generate appsettings.json for UNIFIED container.
        /// Server goes to /apps/server/appsettings.json, Client goes to /apps/client/appsettings.json.
        /// Both use localhost (127.0.0.1) for communication within the same container.
        /// </summary>
        private void GenerateAppsettingsInUnifiedContainer(
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            var connectionString = ConnectionStringHelper.BuildForDocker(
                config.DatabaseContainerHostPort,
                testKitConfig.DatabaseName,
                config.DatabaseUsername,
                config.DatabasePassword ?? DefaultDatabasePassword);
            
            // UNIFIED CONTAINER: Both client and server use localhost (127.0.0.1)
            // Server binds to 127.0.0.1, Client connects to 127.0.0.1
            var serverIpAddress = "127.0.0.1";  // Bind to localhost
            var clientIpAddress = "127.0.0.1";  // Connect to localhost
            
            var port = config.CodeContainerInternalPort;
            var serverPort = port.ToString();
            var clientPort = port.ToString();
            
            OnProgress($"[Unified] Generating appsettings for localhost communication (127.0.0.1:{port})");
            
            // Generate SERVER appsettings
            var serverConfig = $@"{{
  ""ConnectionStrings"": {{ ""MyCnn"": ""{connectionString}"" }},
  ""IpAddress"": ""{serverIpAddress}"",
  ""Port"": ""{serverPort}""
}}";
            
            string? tempServerFile = null;
            try
            {
                // Remove any existing appsettings from student files
                OnProgress($"[Unified] Removing old server appsettings");
                _dockerExecutor.ExecDockerCommand($"{unifiedContainer} rm -f /apps/server/appsettings.json", 3000);
                
                tempServerFile = Path.Combine(Path.GetTempPath(), $"appsettings_unified_server_{Guid.NewGuid()}.json");
                File.WriteAllText(tempServerFile, serverConfig);
                _dockerExecutor.CopyFileToContainer(tempServerFile, $"{unifiedContainer}:/apps/server/appsettings.json");
                OnProgress($"[Unified] Server appsettings: 127.0.0.1:{serverPort} -> /apps/server/appsettings.json");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to generate server appsettings: {ex.Message}");
            }
            finally
            {
                if (tempServerFile != null && File.Exists(tempServerFile))
                    try { File.Delete(tempServerFile); } catch { }
            }
            
            // Generate CLIENT appsettings
            var clientConfig = $@"{{
  ""IpAddress"": ""{clientIpAddress}"",
  ""Port"": ""{clientPort}""
}}";
            
            string? tempClientFile = null;
            try
            {
                // Remove any existing appsettings from student files
                OnProgress($"[Unified] Removing old client appsettings");
                _dockerExecutor.ExecDockerCommand($"{unifiedContainer} rm -f /apps/client/appsettings.json", 3000);
                
                tempClientFile = Path.Combine(Path.GetTempPath(), $"appsettings_unified_client_{Guid.NewGuid()}.json");
                File.WriteAllText(tempClientFile, clientConfig);
                _dockerExecutor.CopyFileToContainer(tempClientFile, $"{unifiedContainer}:/apps/client/appsettings.json");
                OnProgress($"[Unified] Client appsettings: 127.0.0.1:{clientPort} -> /apps/client/appsettings.json");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to generate client appsettings: {ex.Message}");
            }
            finally
            {
                if (tempClientFile != null && File.Exists(tempClientFile))
                    try { File.Delete(tempClientFile); } catch { }
            }
        }
        
        #endregion
        
        #region Test Case Execution
        
        private async Task<TestCaseResult> ExecuteTestCaseAsync(
            TestCaseInfo testCase,
            TestKitConfig testKitConfig,
            DockerGradingConfig config,
            string? serverDllPath,
            string? clientDllPath,
            string unifiedContainer,
            string _unused_clientContainer, // Legacy parameter, not used in unified approach
            CancellationToken ct)
        {
            var result = new TestCaseResult
            {
                TestCaseName = testCase.Name,
                MaxMark = testCase.MaxMark
            };
            
            try
            {
                // IMPORTANT: Resolve actual DLLs to use based on Grade_Content
                // This determines whether to use student's code or golden code for each component
                string? actualServerDll = null;
                string? actualClientDll = null;
                
                var gradeContent = (testCase.GradeContent ?? "Client/Server").Trim();
                OnProgress($"[TestCase] {testCase.Name}: Grade_Content = '{gradeContent}'");
                
                // Validate Grade_Content value
                var validValues = new[] { "Client", "Server", "Client/Server" };
                if (!validValues.Contains(gradeContent, StringComparer.OrdinalIgnoreCase))
                {
                    OnProgress($"[TestCase] WARNING: Invalid Grade_Content value '{gradeContent}', defaulting to 'Client/Server'");
                    gradeContent = "Client/Server";
                }
                
                if (gradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                {
                    // Grade student's CLIENT only - use golden SERVER
                    actualClientDll = clientDllPath;
                    actualServerDll = testKitConfig.GivenServerPath;
                    OnProgress($"[TestCase] Using student CLIENT + golden SERVER");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    
                    // Validate required DLLs exist
                    if (string.IsNullOrEmpty(actualClientDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires student CLIENT but none was found. Grade_Content='Client'");
                    }
                    if (string.IsNullOrEmpty(actualServerDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires golden SERVER but none was found in Meta/Given/Server. Grade_Content='Client'");
                    }
                }
                else if (gradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                {
                    // Grade student's SERVER only - use golden CLIENT
                    actualServerDll = serverDllPath;
                    actualClientDll = testKitConfig.GivenClientPath;
                    OnProgress($"[TestCase] Using student SERVER + golden CLIENT");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    
                    // Validate required DLLs exist
                    if (string.IsNullOrEmpty(actualServerDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires student SERVER but none was found. Grade_Content='Server'");
                    }
                    if (string.IsNullOrEmpty(actualClientDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires golden CLIENT but none was found in Meta/Given/Client. Grade_Content='Server'");
                    }
                }
                else // "Client/Server" or default
                {
                    // Grade BOTH student's CLIENT and SERVER - no golden used
                    actualClientDll = clientDllPath;
                    actualServerDll = serverDllPath;
                    OnProgress($"[TestCase] Using student CLIENT + student SERVER (no golden)");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    
                    // Note: For Client/Server mode, we allow one to be missing if the test only uses one
                    // The test will fail naturally if it tries to use a missing component
                }
                
                // Clear network captures for this test case
                // CRITICAL: Must clear BOTH NetworkMonitor AND RunContext to ensure
                // only traffic from this test case is captured
                // 
                // CRITICAL FIX: Clear captures multiple times with delays to ensure
                // any in-flight packets from previous tests are flushed
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Clearing captures before test case starts...");
                
                // VERIFICATION: Check packet count BEFORE clear
                var packetCountBefore = _runContext.GetAllCapturedNetworkPackets().Count;
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Packet count BEFORE clear: {packetCountBefore}");
                
                _networkMonitor?.ClearCaptures();
                _runContext.ClearNetworkCaptures();
                
                // VERIFICATION: Check packet count AFTER clear
                var packetCountAfterFirstClear = _runContext.GetAllCapturedNetworkPackets().Count;
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Packet count AFTER first clear: {packetCountAfterFirstClear}");
                
                // CRITICAL FIX: Add small delay to allow any in-flight packets to be processed and cleared
                // This prevents cross-contamination from previous test cases
                await Task.Delay(100, ct);
                
                // Clear again to catch any packets that arrived during the delay
                _networkMonitor?.ClearCaptures();
                _runContext.ClearNetworkCaptures();
                
                // VERIFICATION: Check packet count AFTER second clear
                var packetCountAfterSecondClear = _runContext.GetAllCapturedNetworkPackets().Count;
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Packet count AFTER second clear: {packetCountAfterSecondClear}");
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Captures cleared, setting context...");
                
                _networkMonitor?.SetCurrentContext(testCase.Name, "0");
                
                // Read Detail.xlsx
                var detailPath = Path.Combine(testCase.Path, "Detail.xlsx");
                var actions = ReadActions(detailPath);
                var expectedOutputs = ReadExpectedOutputs(detailPath);
                var expectedNetwork = ReadExpectedNetwork(detailPath);
                
                // Populate Actions for User sheet
                result.Actions = actions.Select(a => new ActionRecord
                {
                    Stage = a.Stage,
                    Input = a.Input,
                    ActionType = a.Action
                }).ToList();
                
                // Execute actions and capture outputs - UNIFIED CONTAINER (default)
                var outputs = await ExecuteActionsForUnifiedContainerAsync(
                    actions, config, unifiedContainer, ct);
                var clientOutputs = outputs.Item1;
                var serverOutputs = outputs.Item2;
                
                // Compare outputs
                var (earnedMark, passed, comparisons) = CompareOutputs(
                    expectedOutputs, clientOutputs, serverOutputs, testCase.MaxMark);
                
                // Compare network (if expected)
                var networkComparisons = CompareNetwork(expectedNetwork);
                
                // Get captured network packets for Network sheet
                var capturedPackets = GetCapturedNetworkPackets();
                
                // CRITICAL DEBUGGING: Log detailed packet information
                OnProgress($"[NetworkMonitor] Captured {capturedPackets.Count} packets for test case {testCase.Name}");
                OnProgress($"[NetworkMonitor] Student: {_currentStudentCode}, Port: {config.CodeContainerHostPort}");
                
                if (capturedPackets.Count > 0)
                {
                    OnProgress($"[NetworkMonitor] First packet details: Stage={capturedPackets[0].Stage}, Flags={capturedPackets[0].Flags}, SrcRole={capturedPackets[0].SourceRole}, DstRole={capturedPackets[0].DestinationRole}");
                    OnProgress($"[NetworkMonitor] Packet timestamps range: {capturedPackets.Min(p => p.Timestamp):HH:mm:ss.fff} to {capturedPackets.Max(p => p.Timestamp):HH:mm:ss.fff}");
                }
                
                result.NetworkCaptures = capturedPackets;
                
                // CRITICAL: Validate network monitoring is working
                // If we expected network data but got none, this indicates a problem with network monitoring
                // OR the student's server exited immediately without accepting connections
                //
                // SIDECAR PATTERN: Network data is captured to pcap file and analyzed post-grading
                // During test execution, capturedPackets will be empty. Skip validation for now.
                bool networkCheckPassed = true;
                
                if (_networkMonitor != null)  // Only validate if using legacy HOST-based monitoring
                {
                    if (expectedNetwork.Count > 0 && capturedPackets.Count == 0)
                    {
                        OnProgress("[NetworkMonitor] CRITICAL: Expected network traffic but captured NONE!");
                        OnProgress($"[NetworkMonitor] Expected {expectedNetwork.Count} network flows, but captured 0 packets");
                        OnProgress("[NetworkMonitor] This usually means:");
                        OnProgress("  1. Student's server exited immediately without accepting connections (check server process logs)");
                        OnProgress("  2. Network monitor was not running with proper permissions (run with: sudo on Linux)");
                        OnProgress("  3. libpcap/NPcap not installed (Linux: sudo apt-get install libpcap-dev, Windows: install NPcap)");
                        OnProgress("  4. Loopback interface not found (check: ip addr show lo on Linux, ipconfig on Windows)");
                        OnProgress("[NetworkMonitor] Marking test case as FAILED");
                        networkCheckPassed = false;
                    }
                    
                    // CRITICAL FIX: Validate NO unexpected packets when expecting NONE
                    // BUG FIX: When expectedNetwork.Count == 0 (no network flows expected),
                    // but capturedPackets.Count > 0 (some packets were captured),
                    // this indicates cross-contamination from other students or stale packets.
                    // The test MUST FAIL in this case.
                    if (expectedNetwork.Count == 0 && capturedPackets.Count > 0)
                    {
                        OnProgress($"[NetworkMonitor] CRITICAL: Expected NO network traffic but captured {capturedPackets.Count} packets!");
                        OnProgress("[NetworkMonitor] This usually means:");
                        OnProgress("  1. Student's code is creating network connections when it shouldn't (check student code)");
                        OnProgress("  2. Packets from previous test or another student (cross-contamination bug)");
                        OnProgress("  3. Stale packets not properly cleared between tests");
                        OnProgress("[NetworkMonitor] Captured packets details:");
                        foreach (var pkt in capturedPackets.Take(10))
                        {
                            OnProgress($"  Stage {pkt.Stage}: {pkt.SourceRole}->{pkt.DestinationRole} [{pkt.Flags}] {pkt.State}");
                        }
                        OnProgress("[NetworkMonitor] Marking test case as FAILED due to unexpected network traffic");
                        networkCheckPassed = false;
                    }
                }
                else
                {
                    // SIDECAR PATTERN: Network monitoring via Docker container
                    // Packets are being captured to pcap file and will be analyzed after grading completes
                    OnProgress("[NetworkMonitor] Using sidecar pattern - network traffic being captured to pcap file");
                    OnProgress($"[NetworkMonitor] Expected {expectedNetwork.Count} network flows - will be validated from pcap after grading");
                    networkCheckPassed = true;  // Don't fail during execution, validate from pcap later
                }
                
                // ALL-OR-NOTHING GRADING STRATEGY FOR NETWORK FLOWS
                // - If ANY flow has FAIL status, entire test FAILS
                // - CRITICAL FIX: PARTIAL is treated as FAIL (flags match but roles don't - wrong packet source/dest)
                // - Only EXACT matches (flags + roles + correct source/dest) count as PASS
                // - Only flows recorded in Detail.xlsx are validated
                // - Flows NOT in Detail.xlsx are ignored (even if captured)
                
                int totalNetworkFlows = networkComparisons.Count;
                int passCount = networkComparisons.Count(c => c.Passed);
                int partialCount = networkComparisons.Count(c => c.IsPartial);
                // CRITICAL FIX: Count ALL !Passed items as failures (including PARTIAL)
                // This prevents NGINX from passing tests with correct flags but wrong roles
                int failCount = networkComparisons.Count(c => !c.Passed);
                
                // DIRECT FILE LOGGING FOR DEBUGGING
                var debugPath = Path.Combine(Path.GetTempPath(), "DEBUG_CompareNetwork.txt");
                try {
                    File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] Network Scoring: Total={totalNetworkFlows}, PASS={passCount}, PARTIAL={partialCount}, FAIL={failCount}\n");
                } catch { }
                
                OnProgress($"[Network Scoring] networkComparisons.Count={totalNetworkFlows}, PASS={passCount}, PARTIAL={partialCount}, FAIL={failCount}");
                
                // ALL-OR-NOTHING: Test passes ONLY if ALL flows passed (failCount == 0)
                // PARTIAL matches count as FAIL - only EXACT matches (flags + roles) pass
                bool networkFlowsPassed = failCount == 0 || totalNetworkFlows == 0;
                
                try {
                    File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] networkFlowsPassed={networkFlowsPassed} (failCount={failCount})\n");
                    File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] passed={passed}, networkCheckPassed={networkCheckPassed}\n");
                } catch { }
                
                OnProgress($"[Network Scoring] networkFlowsPassed={networkFlowsPassed} (failCount={failCount}, totalNetworkFlows={totalNetworkFlows})");
                OnProgress($"[Network Scoring] Output comparison passed={passed}, networkCheckPassed={networkCheckPassed}");
                
                // Final result: must pass both output comparison AND network check
                // No partial credit - ALL or NOTHING
                result.EarnedMark = (passed && networkCheckPassed && networkFlowsPassed) ? earnedMark : 0;
                result.Passed = passed && networkCheckPassed && networkFlowsPassed;
                
                try {
                    File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] FINAL: Passed={result.Passed}, EarnedMark={result.EarnedMark}/{earnedMark}\n");
                    if (!result.Passed) {
                        File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] Test FAILED: output={passed}, netCheck={networkCheckPassed}, netFlows={networkFlowsPassed}\n");
                    }
                } catch { }
                
                OnProgress($"[Network Scoring] FINAL TEST RESULT: Passed={result.Passed}, EarnedMark={result.EarnedMark}/{earnedMark}");
                if (!result.Passed)
                {
                    OnProgress($"[Network Scoring] Test FAILED because: outputPassed={passed}, networkCheckPassed={networkCheckPassed}, networkFlowsPassed={networkFlowsPassed}");
                }
                result.ClientComparisons = comparisons.Where(c => c.Source == "Client").ToList();
                result.ServerComparisons = comparisons.Where(c => c.Source == "Server").ToList();
                result.NetworkComparisons = networkComparisons;
                
                // Build detailed error message for OverallSummary.xlsx
                var errorMessages = new List<string>();
                
                if (!networkCheckPassed)
                {
                    errorMessages.Add("Network monitoring failed: No packets captured. Run with sudo and ensure libpcap/NPcap is installed.");
                }
                
                if (!passed)
                {
                    int failedOutputs = comparisons.Count(c => !c.Passed);
                    if (failedOutputs > 0)
                        errorMessages.Add($"Console output: {failedOutputs} check(s) failed");
                }
                
                if (totalNetworkFlows > 0 && failCount > 0)
                {
                    // Show detailed breakdown: which flows failed
                    errorMessages.Add($"Network flows: {failCount} FAIL (ALL-OR-NOTHING: test FAILED), {partialCount} PARTIAL, {passCount} PASS");
                }
                
                if (errorMessages.Any())
                {
                    result.ErrorMessage = string.Join("; ", errorMessages);
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                OnProgress($"[Error] Test case {testCase.Name} failed: {ex.Message}");
            }
            
            return result;
        }
        
        /// <summary>
        /// Gets all captured network packets from the NetworkMonitor.
        /// 
        /// SIDECAR PATTERN (NEW - LIVE GRADING):
        /// Packets are parsed from pcap file PER-STAGE and added to RunContext during execution.
        /// This enables live per-stage validation for all-or-nothing grading strategy.
        /// 
        /// LEGACY PATTERN (OLD):
        /// Uses SharedNetworkMonitorService on HOST to capture packets in real-time.
        /// </summary>
        private List<CapturedNetworkPacket> GetCapturedNetworkPackets()
        {
            // Get ALL captured packets from the RunContext (across all stages)
            // This works for both:
            // - Sidecar pattern: Packets parsed per-stage from pcap and added to RunContext
            // - Legacy pattern: Packets captured real-time by SharedNetworkMonitorService
            return _runContext.GetAllCapturedNetworkPackets().ToList();
        }
        
        
        /// <summary>
        /// Execute test case actions for UNIFIED container (client and server in same container).
        /// Uses unified-control.sh script to start/stop processes via supervisord.
        /// Logs are written per stage to /apps/server/server-stage-N.log and /apps/client/client-stage-N.log.
        /// </summary>
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsForUnifiedContainerAsync(
            List<(int Stage, string Input, string Action)> actions,
            DockerGradingConfig config,
            string unifiedContainer,
            CancellationToken ct)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();
            
            foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
            {
                ct.ThrowIfCancellationRequested();
                
                // Update network monitor stage context
                _networkMonitor?.SetCurrentContext("", stage.ToString());
                
                OnProgress($"  [Stage {stage}] {action}" + (string.IsNullOrEmpty(input) ? "" : $" input='{input}'"));
                
                switch (action.ToUpperInvariant())
                {
                    case "STARTSERVER":
                        // Use unified-control.sh to start server via supervisord
                        var startServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartServer {stage}";
                        _commandExecutor.RunCommand(startServerCmd, null, null, 30000);
                        
                        await Task.Delay(StartupDelayMs);
                        
                        OnProgress($"    Server started for stage {stage} (logging to /apps/server/server-stage-{stage}.log)");
                        break;
                        
                    case "STARTCLIENT":
                        // Use unified-control.sh to start client via supervisord
                        var startClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartClient {stage}";
                        _commandExecutor.RunCommand(startClientCmd, null, null, 30000);
                        
                        await Task.Delay(StartupDelayMs);
                        
                        OnProgress($"    Client started for stage {stage} (logging to /apps/client/client-stage-{stage}.log)");
                        break;
                        
                    case "INPUT":
                        // INPUT action: Send the input value to the client
                        // Only send if there's actual content
                        if (!string.IsNullOrWhiteSpace(input))
                        {
                            var sendInputCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh SendInput {stage} \"{input}\"";
                            try
                            {
                                _commandExecutor.RunCommand(sendInputCmd, null, null, 5000);
                                await Task.Delay(InputProcessingDelayMs);
                                OnProgress($"    Input sent: '{input}'");
                            }
                            catch (Exception ex)
                            {
                                OnProgress($"    WARNING: Failed to send input: {ex.Message}");
                            }
                        }
                        else
                        {
                            OnProgress($"    Skipping empty input for stage {stage}");
                        }
                        break;
                        
                    case "SENDINPUT":
                        // Legacy support - treat same as INPUT
                        goto case "INPUT";
                        
                    case "CLOSECLIENT":
                        // Use unified-control.sh to stop client via supervisord
                        var stopClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseClient {stage}";
                        try
                        {
                            _commandExecutor.RunCommand(stopClientCmd, null, null, 5000);
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to stop client: {ex.Message}");
                        }
                        
                        OnProgress($"    Client stopped for stage {stage}");
                        break;
                        
                    case "CLOSESERVER":
                        // Use unified-control.sh to stop server via supervisord
                        var stopServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseServer {stage}";
                        try
                        {
                            _commandExecutor.RunCommand(stopServerCmd, null, null, 10000);
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to stop server: {ex.Message}");
                        }
                        
                        OnProgress($"    Server stopped for stage {stage}");
                        break;
                }
                
                // LIVE GRADING: Parse network packets for current stage
                // This enables per-stage validation (all-or-nothing grading strategy)
                OnProgress($"[NetworkMonitor] Stage {stage}: _networkMonitor={(_networkMonitor == null ? "null" : "not-null")}, _currentPcapFilePath={_currentPcapFilePath ?? "null"}");
                if (_networkMonitor == null && !string.IsNullOrEmpty(_currentPcapFilePath))
                {
                    // Using sidecar pattern - parse pcap file for this stage
                    await ParsePcapForCurrentStageAsync(stage, config.CodeContainerHostPort);
                }
                else
                {
                    OnProgress($"[NetworkMonitor] Stage {stage}: Skipping parse - using legacy network monitor or no pcap path");
                }
                
                await Task.Delay(10);  // Brief delay between actions
            }
            
            // After all actions complete, read log files from container for each stage
            OnProgress($"[Unified] Reading per-stage log files from container...");
            
            // Collect all stages that had actions
            var allStages = actions.Select(a => a.Stage).Distinct().OrderBy(s => s).ToList();
            
            foreach (var stage in allStages)
            {
                // Read server log for this stage
                try
                {
                    var serverLogPath = $"/apps/server/server-stage-{stage}.log";
                    var serverLog = ReadFileFromContainer(unifiedContainer, serverLogPath);
                    if (!string.IsNullOrEmpty(serverLog))
                    {
                        serverOutputs[stage] = serverLog;
                        OnProgress($"    Server stage {stage}: {serverLog.Length} chars");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read server log for stage {stage}: {ex.Message}");
                    serverOutputs[stage] = "";
                }
                
                // Read client log for this stage
                try
                {
                    var clientLogPath = $"/apps/client/client-stage-{stage}.log";
                    var clientLog = ReadFileFromContainer(unifiedContainer, clientLogPath);
                    if (!string.IsNullOrEmpty(clientLog))
                    {
                        clientOutputs[stage] = clientLog;
                        OnProgress($"    Client stage {stage}: {clientLog.Length} chars");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read client log for stage {stage}: {ex.Message}");
                    clientOutputs[stage] = "";
                }
            }
            
            return (clientOutputs, serverOutputs);
        }
        
        #endregion
        
        #region Output Comparison
        
        /// <summary>
        /// Compares actual outputs against expected outputs using ALL-OR-NOTHING grading policy.
        /// 
        /// GRADING POLICY: ALL-OR-NOTHING
        /// - If ALL comparisons pass, student earns FULL marks for the test case
        /// - If ANY comparison fails, student earns ZERO marks for the test case
        /// - This policy ensures students implement complete functionality, not partial solutions
        /// </summary>
        /// <param name="expected">Expected outputs by stage</param>
        /// <param name="clientOutputs">Actual client outputs by stage</param>
        /// <param name="serverOutputs">Actual server outputs by stage</param>
        /// <param name="maxMark">Maximum marks for this test case</param>
        /// <returns>Earned mark, pass status, and comparison details</returns>
        private (double earnedMark, bool passed, List<ComparisonResult> comparisons) CompareOutputs(
            Dictionary<int, (string? ClientConsole, string? ServerConsole)> expected,
            Dictionary<int, string> clientOutputs,
            Dictionary<int, string> serverOutputs,
            double maxMark)
        {
            var comparisons = new List<ComparisonResult>();
            int total = 0;
            int passed = 0;
            
            foreach (var (stage, exp) in expected)
            {
                if (!string.IsNullOrEmpty(exp.ClientConsole))
                {
                    total++;
                    var actual = clientOutputs.TryGetValue(stage, out var c) ? c : "";
                    var match = NormalizeAndContains(actual, exp.ClientConsole);
                    if (match) passed++;
                    
                    // Log detailed comparison for debugging (NO TRUNCATION - full output for debugging)
                    OnProgress($"  [Stage {stage}] Client comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        OnProgress($"    Expected (contains): '{exp.ClientConsole}'");
                        OnProgress($"    Actual output: '{actual}'");
                    }
                    
                    comparisons.Add(new ComparisonResult
                    {
                        Source = "Client",
                        Stage = stage,
                        Expected = exp.ClientConsole,
                        Actual = actual,
                        Passed = match
                    });
                }
                
                if (!string.IsNullOrEmpty(exp.ServerConsole))
                {
                    total++;
                    var actual = serverOutputs.TryGetValue(stage, out var s) ? s : "";
                    var match = NormalizeAndContains(actual, exp.ServerConsole);
                    if (match) passed++;
                    
                    // Log detailed comparison for debugging (NO TRUNCATION - full output for debugging)
                    OnProgress($"  [Stage {stage}] Server comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        OnProgress($"    Expected (contains): '{exp.ServerConsole}'");
                        OnProgress($"    Actual output: '{actual}'");
                    }
                    
                    comparisons.Add(new ComparisonResult
                    {
                        Source = "Server",
                        Stage = stage,
                        Expected = exp.ServerConsole,
                        Actual = actual,
                        Passed = match
                    });
                }
            }
            
            // ALL-OR-NOTHING policy for console output comparison
            // CRITICAL FIX: If total == 0 (no console output expectations), treat as PASS
            // Only enforce ALL-OR-NOTHING when there ARE expectations to check
            bool allPassed = total == 0 || (passed == total && total > 0);
            double earnedMark = allPassed ? maxMark : 0;
            
            if (total == 0)
            {
                OnProgress($"  Comparison summary: No console output expectations - PASS by default");
            }
            else
            {
                OnProgress($"  Comparison summary: {passed}/{total} checks passed, earned {earnedMark:F2}/{maxMark:F2} marks");
            }
            
            return (earnedMark, allPassed, comparisons);
        }
        
        private List<ComparisonResult> CompareNetwork(List<ExpectedNetworkFlow> expected)
        {
            var results = new List<ComparisonResult>();
            
            // CRITICAL FIX: Get ALL captured packets for this stage (regardless of questionCode)
            // because packets may be stored with various questionCode values or empty string
            var allCapturedPackets = _runContext.GetAllCapturedNetworkPackets();
            
            // DIRECT FILE LOGGING - Bypass OnProgress to ensure messages are written
            var debugPath = Path.Combine(Path.GetTempPath(), "DEBUG_CompareNetwork.txt");
            try {
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] CompareNetwork called\n");
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] Expected flows: {expected.Count}\n");
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] Captured packets: {allCapturedPackets.Count}\n");
            } catch { }
            
            // DIAGNOSTIC LOGGING - Written to GradingLogs for debugging
            OnProgress($"[CompareNetwork] Expected network flows from Detail.xlsx: {expected.Count}");
            OnProgress($"[CompareNetwork] Total captured packets in RunContext: {allCapturedPackets.Count}");
            if (expected.Count > 0)
            {
                OnProgress($"[CompareNetwork] Expected stages: {string.Join(", ", expected.Select(e => e.Stage).Distinct().OrderBy(s => s))}");
            }
            if (allCapturedPackets.Count > 0)
            {
                OnProgress($"[CompareNetwork] Captured stages: {string.Join(", ", allCapturedPackets.Select(p => p.Stage).Distinct().OrderBy(s => s))}");
            }
            
            foreach (var exp in expected)
            {
                // Filter packets by stage from the complete set
                var capturedPackets = allCapturedPackets.Where(p => p.Stage == exp.Stage).ToList();
                
                OnProgress($"[CompareNetwork] Stage {exp.Stage}: Expected flags='{exp.Flags}' from '{exp.SourceRole}' to '{exp.DestinationRole}', Found {capturedPackets.Count} packets for this stage");
                
                // Find matching packet by flags
                var matchingPacket = capturedPackets.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(exp.Flags) && 
                    NormalizeFlags(exp.Flags) == NormalizeFlags(p.Flags));
                
                if (matchingPacket != null)
                {
                    // Check if it's an exact match (PASS) or partial match (PARTIAL)
                    bool exactMatch = true;
                    
                    // Compare flags - exact match required (but order-normalized)
                    if (!string.IsNullOrEmpty(exp.Flags) && NormalizeFlags(exp.Flags) != NormalizeFlags(matchingPacket.Flags))
                        exactMatch = false;
                    
                    // Compare roles exactly
                    if (!string.IsNullOrEmpty(exp.SourceRole) && matchingPacket.SourceRole != exp.SourceRole)
                        exactMatch = false;
                    if (!string.IsNullOrEmpty(exp.DestinationRole) && matchingPacket.DestinationRole != exp.DestinationRole)
                        exactMatch = false;
                    
                    // CRITICAL FIX: Track PASS vs PARTIAL in ComparisonResult
                    // PASS = exact match (flags + roles match)
                    // PARTIAL = flags match but roles don't match
                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}",
                        Actual = $"Flags={matchingPacket.Flags}, From={matchingPacket.SourceRole}, To={matchingPacket.DestinationRole}",
                        Passed = exactMatch,  // true for PASS, false for PARTIAL
                        IsPartial = !exactMatch && true  // PARTIAL if matched flags but not exact
                    });
                }
                else
                {
                    // No matching packet found - FAIL
                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}",
                        Actual = capturedPackets.Any() ? string.Join("; ", capturedPackets.Select(p => p.Flags)) : "(no captures)",
                        Passed = false,
                        IsPartial = false  // Complete FAIL - missing packet
                    });
                }
            }
            
            // DIAGNOSTIC LOGGING - Summary of comparison results
            int passCount = results.Count(r => r.Passed);
            int partialCount = results.Count(r => r.IsPartial);
            int failCount = results.Count(r => !r.Passed && !r.IsPartial);
            
            // DIRECT FILE LOGGING
            debugPath = Path.Combine(Path.GetTempPath(), "DEBUG_CompareNetwork.txt");
            try {
                File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] RESULTS: Total={results.Count}, PASS={passCount}, PARTIAL={partialCount}, FAIL={failCount}\n");
                if (failCount > 0) {
                    File.AppendAllText(debugPath, $"[{DateTime.Now:HH:mm:ss}] WARNING: {failCount} FAIL flows - test should FAIL!\n");
                }
            } catch { }
            
            OnProgress($"[CompareNetwork] RESULTS: {results.Count} total comparisons - PASS={passCount}, PARTIAL={partialCount}, FAIL={failCount}");
            if (failCount > 0)
            {
                OnProgress($"[CompareNetwork] WARNING: {failCount} FAIL network flows detected - test should FAIL!");
            }
            
            return results;
        }
        
        /// <summary>
        /// Simple string contains check with basic normalization for line endings.
        /// For more robust comparison, use DataComparisonService.CompareText().
        /// </summary>
        private bool NormalizeAndContains(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            var normExpected = expected.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
            var normActual = (actual ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
            return normActual.Contains(normExpected);
        }
        
        /// <summary>
        /// Normalize TCP flags for comparison - sorts flags alphabetically and removes whitespace.
        /// REUSES logic from Executor.NormalizeFlags() to avoid code duplication.
        /// 
        /// Examples:
        /// - "PSH, ACK" -> "ACK, PSH"
        /// - "SYN" -> "SYN"
        /// - "ACK, RST" -> "ACK, RST"
        /// </summary>
        private static string NormalizeFlags(string flags)
        {
            if (string.IsNullOrWhiteSpace(flags)) return "";
            
            var flagList = flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .OrderBy(f => f)
                .ToList();
            
            return string.Join(", ", flagList);
        }
        
        #endregion
        
        #region Test Kit Loading
        
        private TestKitConfig LoadTestKitConfig(string testKitPath, DockerGradingConfig config)
        {
            var tkConfig = new TestKitConfig();
            
            // Load Environment.xlsx
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (File.Exists(envPath))
            {
                using var wb = new XLWorkbook(envPath);
                if (wb.TryGetWorksheet("Config", out var ws))
                {
                    foreach (var row in ws.RowsUsed().Skip(1))
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim()?.ToLowerInvariant()?.Replace("_", "");
                        var value = row.Cell(2).GetValue<string>()?.Trim();
                        
                        switch (key)
                        {
                            case "codecontainerinternalport":
                                if (int.TryParse(value, out var ip)) tkConfig.CodeContainerInternalPort = ip;
                                break;
                            case "codecontainerhostport":
                                if (int.TryParse(value, out var hp)) tkConfig.CodeContainerHostPort = hp;
                                break;
                            case "codeimagename":
                                tkConfig.CodeImageName = value ?? tkConfig.CodeImageName;
                                break;
                            case "dockernetwork":
                                tkConfig.DockerNetwork = value ?? tkConfig.DockerNetwork;
                                break;
                            case "defaultdatabasename":
                                tkConfig.DatabaseName = value ?? tkConfig.DatabaseName;
                                break;
                            case "databasepassword":
                                tkConfig.DatabasePassword = value ?? "";
                                break;
                        }
                    }
                }
            }
            
            // Load Header.xlsx
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                using var wb = new XLWorkbook(headerPath);
                if (wb.TryGetWorksheet("QuestionMark", out var markSheet))
                {
                    foreach (var row in markSheet.RowsUsed().Skip(1))
                    {
                        var tcName = row.Cell(1).GetString()?.Trim();
                        
                        // Safely parse mark value - handle both numeric and text cells
                        double mark = 0.0;
                        if (row.Cell(2).TryGetValue<double>(out var directValue))
                        {
                            mark = directValue;
                        }
                        else
                        {
                            var markStr = row.Cell(2).GetString().Trim();
                            if (!double.TryParse(markStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out mark))
                            {
                                OnProgress($"[Warning] Cannot parse mark value '{markStr}' for test case '{tcName}' - defaulting to 0");
                                mark = 0.0;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(tcName))
                            tkConfig.TestCaseMarks[tcName] = mark;
                    }
                }
                
                if (wb.TryGetWorksheet("Config", out var configSheet))
                {
                    foreach (var row in configSheet.RowsUsed().Skip(1))
                    {
                        var key = row.Cell(1).GetString()?.Trim();
                        var value = row.Cell(2).GetString()?.Trim();
                        if (key?.Equals("Protocol", StringComparison.OrdinalIgnoreCase) == true)
                            tkConfig.Protocol = value ?? "TCP";
                    }
                }
            }
            
            // Discover test cases and read per-test-case configuration from Header.xlsx
            tkConfig.TestCases = Directory.GetDirectories(testKitPath)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "Detail.xlsx")))
                .Select(d =>
                {
                    var (timeout, gradeContent) = ReadTestCaseConfig(d, config.TestCaseTimeoutSeconds);
                    return new TestCaseInfo
                    {
                        Name = Path.GetFileName(d),
                        Path = d,
                        MaxMark = tkConfig.TestCaseMarks.TryGetValue(Path.GetFileName(d), out var m) ? m : 0,
                        TimeoutSeconds = timeout,
                        GradeContent = gradeContent
                    };
                })
                .OrderBy(tc => tc.Name)
                .ToList();
            
            // Discover given/golden server and client from Meta folder
            // These are used when student only provides one component (e.g., client only)
            var metaPath = Path.Combine(testKitPath, "Meta");
            if (Directory.Exists(metaPath))
            {
                // Look for given server in Meta/Given/Server
                var givenServerPath = Path.Combine(metaPath, "Given", "Server");
                if (Directory.Exists(givenServerPath))
                {
                    // Find Project11.dll or any main DLL in the server folder
                    var serverDll = Directory.GetFiles(givenServerPath, "Project11.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.GetFiles(givenServerPath, "*.dll", SearchOption.TopDirectoryOnly)
                            .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                            .FirstOrDefault();
                    
                    if (serverDll != null)
                    {
                        tkConfig.GivenServerPath = serverDll;
                        OnProgress($"[TestKit] Found given server: {Path.GetFileName(serverDll)}");
                    }
                }
                
                // Look for given client in Meta/Given/Client
                var givenClientPath = Path.Combine(metaPath, "Given", "Client");
                if (Directory.Exists(givenClientPath))
                {
                    // Find Project12.dll or any main DLL in the client folder
                    var clientDll = Directory.GetFiles(givenClientPath, "Project12.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.GetFiles(givenClientPath, "*.dll", SearchOption.TopDirectoryOnly)
                            .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                            .FirstOrDefault();
                    
                    if (clientDll != null)
                    {
                        tkConfig.GivenClientPath = clientDll;
                        OnProgress($"[TestKit] Found given client: {Path.GetFileName(clientDll)}");
                    }
                }
            }
            
            // Apply config overrides
            OnProgress($"[Port Config] LoadTestKitConfig - Before override: tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}, tkConfig.CodeContainerHostPort={tkConfig.CodeContainerHostPort}");
            OnProgress($"[Port Config] LoadTestKitConfig - Config values: config.CodeContainerInternalPort={config.CodeContainerInternalPort}, config.CodeContainerHostPort={config.CodeContainerHostPort}");
            
            if (config.CodeContainerInternalPort > 0)
                tkConfig.CodeContainerInternalPort = config.CodeContainerInternalPort;
            if (config.CodeContainerHostPort > 0)
                tkConfig.CodeContainerHostPort = config.CodeContainerHostPort;
            
            OnProgress($"[Port Config] LoadTestKitConfig - After override: tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}, tkConfig.CodeContainerHostPort={tkConfig.CodeContainerHostPort}");
            
            return tkConfig;
        }
        
        /// <summary>
        /// Reads the per-test-case configuration from the test case's Header.xlsx file.
        /// Looks for the Testcase_Property sheet and reads:
        /// - Timeout(Seconds): timeout in seconds
        /// - Grade_Content: what to grade ("Client", "Server", or "Client/Server")
        /// Falls back to defaults if not found or on error.
        /// </summary>
        /// <param name="testCasePath">Path to the test case folder</param>
        /// <param name="defaultTimeout">Default timeout to use if not specified in Header.xlsx</param>
        /// <returns>Tuple of (timeout, gradeContent)</returns>
        private static (int timeout, string gradeContent) ReadTestCaseConfig(string testCasePath, int defaultTimeout)
        {
            var headerPath = Path.Combine(testCasePath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return (defaultTimeout, "Client/Server");
            
            try
            {
                using var wb = new XLWorkbook(headerPath);
                if (wb.TryGetWorksheet("Testcase_Property", out var ws))
                {
                    int timeout = defaultTimeout;
                    string gradeContent = "Client/Server";
                    
                    foreach (var row in ws.RowsUsed())
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim() ?? "";
                        var value = row.Cell(2).GetValue<string>()?.Trim() ?? "";
                        
                        // Read Timeout
                        if ((key.Equals("Timeout(Seconds)", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Timeout", StringComparison.OrdinalIgnoreCase)) &&
                            int.TryParse(value, out var parsedTimeout) && parsedTimeout > 0)
                        {
                            timeout = parsedTimeout;
                            // NOTE: Cannot use OnProgress here - this is a static context in CopyFilesToContainersAsync
                            // Logging moved to instance method context where OnProgress is available
                        }
                        
                        // Read Grade_Content
                        if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(value))
                        {
                            gradeContent = value;
                            // NOTE: Cannot use OnProgress here - this is a static context in CopyFilesToContainersAsync
                            // Logging moved to instance method context where OnProgress is available
                        }
                    }
                    
                    return (timeout, gradeContent);
                }
            }
            catch (Exception ex)
            {
                // NOTE: Cannot use OnProgress here - this is a static context in CopyFilesToContainersAsync
                // Silently use defaults if header cannot be read
            }
            
            return (defaultTimeout, "Client/Server");
        }
        
        private List<(int Stage, string Input, string Action)> ReadActions(string detailPath)
        {
            var actions = new List<(int Stage, string Input, string Action)>();
            using var wb = new XLWorkbook(detailPath);
            if (wb.TryGetWorksheet("User", out var ws))
            {
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var input = row.Cell(2).GetValue<string>() ?? "";
                    var action = row.Cell(3).GetValue<string>() ?? "";
                    
                    if (int.TryParse(stageStr, out var stage) && !string.IsNullOrEmpty(action))
                        actions.Add((stage, input, action));
                }
            }
            return actions;
        }
        
        private Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
        {
            var outputs = new Dictionary<int, (string? ClientConsole, string? ServerConsole)>();
            using var wb = new XLWorkbook(detailPath);
            
            if (wb.TryGetWorksheet("Client", out var clientWs))
            {
                foreach (var row in clientWs.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var console = row.Cell(2).GetValue<string>();
                    if (int.TryParse(stageStr, out var stage))
                    {
                        if (!outputs.ContainsKey(stage))
                            outputs[stage] = (null, null);
                        var current = outputs[stage];
                        outputs[stage] = (console, current.ServerConsole);
                    }
                }
            }
            
            if (wb.TryGetWorksheet("Server", out var serverWs))
            {
                foreach (var row in serverWs.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var console = row.Cell(2).GetValue<string>();
                    if (int.TryParse(stageStr, out var stage))
                    {
                        if (!outputs.ContainsKey(stage))
                            outputs[stage] = (null, null);
                        var current = outputs[stage];
                        outputs[stage] = (current.ClientConsole, console);
                    }
                }
            }
            
            return outputs;
        }
        
        private List<ExpectedNetworkFlow> ReadExpectedNetwork(string detailPath)
        {
            var flows = new List<ExpectedNetworkFlow>();
            
            // DIAGNOSTIC LOGGING - Written to GradingLogs files via OnProgress
            OnProgress($"[ReadExpectedNetwork] Called with Detail.xlsx path: {detailPath}");
            OnProgress($"[ReadExpectedNetwork] File exists: {File.Exists(detailPath)}");
            
            using var wb = new XLWorkbook(detailPath);
            
            OnProgress($"[ReadExpectedNetwork] Workbook loaded, checking for 'Network' worksheet");
            
            if (wb.TryGetWorksheet("Network", out var ws))
            {
                var rowCount = ws.RowsUsed().Count();
                OnProgress($"[ReadExpectedNetwork] 'Network' worksheet found with {rowCount} rows");
                
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var timeCell = row.Cell(2).GetValue<string>();
                    
                    // CRITICAL FIX: Skip rows marked as "(Not validated by this test case)"
                    // These rows appear in Detail.xlsx but should NOT be used for network validation
                    if (timeCell != null && timeCell.Contains("Not validated"))
                    {
                        continue; // Skip this row
                    }
                    
                    var flags = row.Cell(6).GetValue<string>();
                    var state = row.Cell(7).GetValue<string>();
                    var sourceRole = row.Cell(9).GetValue<string>();
                    var destRole = row.Cell(10).GetValue<string>();
                    
                    if (int.TryParse(stageStr, out var stage))
                    {
                        flows.Add(new ExpectedNetworkFlow
                        {
                            Stage = stage,
                            Flags = flags,
                            State = state,
                            SourceRole = sourceRole,
                            DestinationRole = destRole
                        });
                    }
                }
            }
            else
            {
                OnProgress($"[ReadExpectedNetwork] WARNING: 'Network' worksheet NOT FOUND in Detail.xlsx!");
                OnProgress($"[ReadExpectedNetwork] Available worksheets: {string.Join(", ", wb.Worksheets.Select(w => w.Name))}");
            }
            
            OnProgress($"[ReadExpectedNetwork] Returning {flows.Count} flows");
            
            return flows;
        }
        
        #endregion
        
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
        /// Cleans up code containers (server, client) after each student.
        /// CRITICAL: Database container is SHARED and NOT removed - only server/client containers are removed.
        /// Database instance cleanup is handled separately via CleanupDatabaseInstanceAsync.
        /// </summary>
        /// <summary>
        /// Export per-stage log files from unified container to student result directory.
        /// Logs are in /apps/server/server-stage-N.log and /apps/client/client-stage-N.log.
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
        /// Clear old stage log files in the unified container before executing a new test case.
        /// This prevents log accumulation across test cases (same container is reused).
        /// </summary>
        private void ClearStageLogsInContainer(string unifiedContainer)
        {
            try
            {
                // Remove all server stage log files
                var clearServerCmd = $"docker exec {unifiedContainer} /bin/bash -c \"rm -f /apps/server/server-stage-*.log\"";
                _commandExecutor.RunCommand(clearServerCmd, null, null, 5000);
                
                // Remove all client stage log files
                var clearClientCmd = $"docker exec {unifiedContainer} /bin/bash -c \"rm -f /apps/client/client-stage-*.log\"";
                _commandExecutor.RunCommand(clearClientCmd, null, null, 5000);
                
                OnProgress($"[Unified] Cleared old stage log files for new test case");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to clear old logs: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Cleanup unified container after grading.
        /// Removes the container and unregisters student from shared monitor.
        /// </summary>
        private async Task CleanupUnifiedContainerAsync(string unifiedContainer, string studentCode)
        {
            OnProgress($"[Unified] Starting cleanup for {unifiedContainer}");
            
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
                OnProgress($"[Unified] Removed container {unifiedContainer}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] WARNING: Failed to remove container: {ex.Message}");
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Sets up network monitor container using sidecar pattern.
        /// 
        /// SIDECAR PATTERN:
        /// The monitor container attaches to the student's unified container network namespace
        /// using --net=container:{unifiedContainer}. This allows tcpdump to capture all traffic
        /// on the student container's loopback (lo) interface.
        /// 
        /// CRITICAL DESIGN DECISIONS (per new requirement):
        /// 1. Monitor loopback interface (lo) - NOT eth0
        /// 2. Capture ALL traffic - NO port filtering (catches student mistakes like wrong ports)
        /// 3. Use Alpine Linux + tcpdump (lightweight, dedicated monitoring)
        /// 4. Sidecar survives if student container crashes
        /// 5. Clean separation of concerns (student code vs monitoring)
        /// 
        /// REQUIREMENTS:
        /// - NET_ADMIN and NET_RAW capabilities for packet capture
        /// - Attached to unified container's network namespace
        /// - Output written to bind-mounted volume for extraction
        /// </summary>
        /// <param name="monitorContainer">Name of the monitor container</param>
        /// <param name="unifiedContainer">Name of the unified student container to attach to</param>
        /// <param name="port">Port number (for logging/reference only, NOT for filtering)</param>
        /// <param name="pcapOutputPath">Host path where pcap file will be saved</param>
        /// <param name="protocol">Protocol type (TCP/HTTP) for logging</param>
        private async Task SetupNetworkMonitorContainerAsync(
            string monitorContainer,
            string unifiedContainer,
            int port,
            string pcapOutputPath,
            string protocol)
        {
            OnProgress($"[Monitor] Creating sidecar network monitor for {unifiedContainer}");
            
            // === CRITICAL FIX: Save monitor container name to class field ===
            _currentMonitorContainer = monitorContainer;
            _currentPcapFilePath = pcapOutputPath;
            // ================================================================
            
            // Remove existing monitor container if any
            try
            {
                _dockerExecutor.RemoveContainer(monitorContainer);
            }
            catch
            {
                // Container doesn't exist or already removed - this is fine
            }
            
            // Create directory for pcap output on host
            var outputDir = Path.GetDirectoryName(pcapOutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                // CRITICAL: Convert to absolute path for Docker volume mount
                // Docker requires absolute paths for volume mounts, relative paths will fail
                outputDir = Path.GetFullPath(outputDir);
            }
            
            // Extract the filename from the full path
            var pcapFileName = Path.GetFileName(pcapOutputPath);
            
            // Build the docker run command for network monitor sidecar
            // CRITICAL: 
            // - Use --net=container:{unifiedContainer} to attach to student's network namespace
            // - Use --cap-add=NET_ADMIN and --cap-add=NET_RAW for tcpdump permissions
            // - Capture on loopback interface (-i lo) to catch localhost traffic
            // - NO port filtering - capture everything to detect student mistakes
            // - Write to /data/{pcapFileName} inside container (bind-mounted to host)
            //
            // The network-monitor image uses ENTRYPOINT ["tcpdump"] with CMD ["-i", "lo", "-U", "-w", "capture.pcap"]
            // We override the -w argument to use our custom filename
            
            // CRITICAL: Capture on loopback interface specifically
            // Client and server communicate via localhost (127.0.0.1) in unified container
            // Using -i lo captures this traffic; -i any was capturing external eth0 instead
            var dockerCmd = $"docker run -d --name {monitorContainer} " +
                           $"--net=container:{unifiedContainer} " +  // SIDECAR: Attach to student container
                           $"--cap-add=NET_ADMIN " +                 // Required for tcpdump
                           $"--cap-add=NET_RAW " +                   // Required for raw packet capture
                           $"-v \"{outputDir}:/data\" " +            // Mount host directory for pcap output
                           $"fptuxaes/network-monitor:latest " +     // Debian + tcpdump image with ENTRYPOINT
                           $"-i lo -n -U -v -w /data/{pcapFileName}";  // CRITICAL: Write to /data/ (mounted volume)
            
            OnProgress($"[Monitor] Command: {dockerCmd}");
            OnProgress($"[Monitor] Capturing on loopback (lo) - ALL traffic (no port filter)");
            OnProgress($"[Monitor] Output will be saved to: {pcapOutputPath}");
            
            try
            {
                _commandExecutor.RunCommand(dockerCmd, null, null, 10000);
                OnProgress($"[Monitor] Sidecar monitor {monitorContainer} started successfully");
                
                // Brief delay to ensure container is up
                await Task.Delay(500);
                
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
        /// Cleans up network monitor container and extracts pcap file.
        /// 
        /// CLEANUP WORKFLOW:
        /// 1. Stop the monitor container (this flushes tcpdump's buffer)
        /// 2. Extract the pcap file from the container (already on host via volume mount)
        /// 3. Remove the monitor container
        /// 4. Parse pcap file and integrate with grading system (if needed)
        /// 
        /// The pcap file is already on the host due to volume mounting, so we just need
        /// to ensure the container is stopped to flush any buffered packets.
        /// </summary>
        /// <param name="monitorContainer">Name of the monitor container</param>
        /// <param name="studentResultPath">Student result directory path</param>
        private async Task CleanupNetworkMonitorContainerAsync(string monitorContainer, string studentResultPath)
        {
            OnProgress($"[Monitor] Cleaning up {monitorContainer}");
            
            try
            {
                // Stop the container (this flushes tcpdump buffer)
                if (_dockerExecutor.IsContainerRunning(monitorContainer))
                {
                    OnProgress($"[Monitor] Stopping container to flush tcpdump buffer...");
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
                OnProgress($"[Monitor] Removed container {monitorContainer}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Monitor] WARNING during cleanup: {ex.Message}");
            }
            
            await Task.CompletedTask;
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
        
        #region Result Writing
        
        /// <summary>
        /// Writes test case result to GradeDetail.xlsx in the EXACT SampleLogging format:
        /// - User sheet: Stage, Input, Action, DataType, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt
        /// - Client sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ClientStdout
        /// - Server sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ServerStdout
        /// - Network sheet: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// - Database sheet: (empty)
        /// </summary>
        private async Task WriteTestCaseResultAsync(string tcResultPath, string tcName, string testCasePath, TestCaseResult result)
        {
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();
            
            // === User Sheet ===
            // Contains the action steps (StartClient, StartServer, Input, etc.)
            var userWs = wb.Worksheets.Add("User");
            SetUserSheetHeaders(userWs);
            int userRow = 2;
            foreach (var action in result.Actions)
            {
                userWs.Cell(userRow, 1).Value = action.Stage;
                userWs.Cell(userRow, 2).Value = action.Input ?? "";
                userWs.Cell(userRow, 3).Value = action.ActionType ?? "";
                // DataType, Result, etc. are optional for action rows
                userRow++;
            }
            userWs.Columns().AdjustToContents();
            
            // === Client Sheet ===
            // Contains client console output comparisons
            var clientWs = wb.Worksheets.Add("Client");
            SetClientSheetHeaders(clientWs);
            int clientRow = 2;
            foreach (var comp in result.ClientComparisons)
            {
                clientWs.Cell(clientRow, 1).Value = comp.Stage;  // Stage
                clientWs.Cell(clientRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                // Skip Input, DataType, Action
                clientWs.Cell(clientRow, 6).Value = comp.Passed ? "PASS" : "FAIL";  // Result
                clientWs.Cell(clientRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";  // ErrorCode
                clientWs.Cell(clientRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";  // ErrorCategory
                clientWs.Cell(clientRow, 9).Value = comp.PointsAwarded;  // PointsAwarded
                clientWs.Cell(clientRow, 10).Value = comp.PointsPossible;  // PointsPossible
                clientWs.Cell(clientRow, 11).Value = comp.DurationMs;  // DurationMs
                clientWs.Cell(clientRow, 13).Value = comp.Passed ? "Text comparison passed: client output matches exactly" : "Text comparison failed: client output mismatch";  // Message
                clientWs.Cell(clientRow, 19).Value = comp.Actual ?? "";  // ClientStdout
                clientRow++;
            }
            clientWs.Columns().AdjustToContents();
            
            // === Server Sheet ===
            // Contains server console output comparisons
            var serverWs = wb.Worksheets.Add("Server");
            SetServerSheetHeaders(serverWs);
            int serverRow = 2;
            foreach (var comp in result.ServerComparisons)
            {
                serverWs.Cell(serverRow, 1).Value = comp.Stage;  // Stage
                serverWs.Cell(serverRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                // Skip Input, DataType, Action
                serverWs.Cell(serverRow, 6).Value = comp.Passed ? "PASS" : "FAIL";  // Result
                serverWs.Cell(serverRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";  // ErrorCode
                serverWs.Cell(serverRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";  // ErrorCategory
                serverWs.Cell(serverRow, 9).Value = comp.PointsAwarded;  // PointsAwarded
                serverWs.Cell(serverRow, 10).Value = comp.PointsPossible;  // PointsPossible
                serverWs.Cell(serverRow, 11).Value = comp.DurationMs;  // DurationMs
                serverWs.Cell(serverRow, 13).Value = comp.Passed ? "Text comparison passed: server output matches exactly" : "Text comparison failed: server output mismatch";  // Message
                serverWs.Cell(serverRow, 19).Value = comp.Actual ?? "";  // ServerStdout
                serverRow++;
            }
            serverWs.Columns().AdjustToContents();
            
            // === Database Sheet ===
            // Empty placeholder for database operations
            wb.Worksheets.Add("Database");
            
            // === Network Sheet ===
            // IMPROVED FORMAT: Show ALL expected network flows and ALL actual captured packets
            // This provides a comprehensive comparison that makes it easy to identify:
            // - Missing packets (expected but not captured)
            // - Extra packets (captured but not expected)
            // - Mismatched packets (flags, roles differ from expected)
            //
            // The format shows expected flows on the left (columns 1-10) and actual captures 
            // on the right (columns 11-15), with a Match column (16) showing comparison result.
            // This allows reviewers to quickly scan for red (FAIL/MISSING) rows.
            var netWs = wb.Worksheets.Add("Network");
            SetNetworkSheetHeaders(netWs);
            int netRow = 2;
            
            // Read expected network flows from testkit Detail.xlsx to get COMPLETE data
            // This ensures we show ALL expected flows, not just the ones used in comparison
            var detailPath_forNetwork = Path.Combine(testCasePath, "Detail.xlsx");
            var expectedNetworkFlows = ReadExpectedNetwork(detailPath_forNetwork);
            
            // Group actual captures by stage for easier lookup
            var capturesByStage = result.NetworkCaptures
                .GroupBy(p => p.Stage)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).ToList());
            
            // === SECTION 1: EXPECTED Network Flows (from TestKit) ===
            // Show ALL expected flows with their matching actual captures
            if (expectedNetworkFlows.Count > 0)
            {
                OnProgress($"[Network Sheet] Writing {expectedNetworkFlows.Count} expected network flows...");
                
                foreach (var expectedFlow in expectedNetworkFlows.OrderBy(f => f.Stage))
                {
                    // Write expected network flow (columns 1-10)
                    netWs.Cell(netRow, 1).Value = expectedFlow.Stage;  // Stage
                    netWs.Cell(netRow, 2).Value = "";  // Time (from testkit - not always available)
                    netWs.Cell(netRow, 3).Value = "TCP";  // Info
                    netWs.Cell(netRow, 4).Value = "";  // Source (IP from testkit if available)
                    netWs.Cell(netRow, 5).Value = "";  // Destination (IP from testkit if available)
                    netWs.Cell(netRow, 6).Value = expectedFlow.Flags ?? "";  // Flags
                    netWs.Cell(netRow, 7).Value = expectedFlow.State ?? "";  // State
                    netWs.Cell(netRow, 8).Value = "";  // Data
                    netWs.Cell(netRow, 9).Value = expectedFlow.SourceRole ?? "";  // SourceRole
                    netWs.Cell(netRow, 10).Value = expectedFlow.DestinationRole ?? "";  // DestinationRole
                    
                    // Find matching actual packet(s) for this expected flow
                    var actualPacketsForStage = capturesByStage.TryGetValue(expectedFlow.Stage, out var packets) 
                        ? packets 
                        : new List<CapturedNetworkPacket>();
                    
                    // Try to find a packet that matches the expected flags
                    // Find matching packet by comparing flags
                    // Flags must match exactly (case-insensitive, order-normalized)
                    var matchingPacket = actualPacketsForStage.FirstOrDefault(p => 
                        !string.IsNullOrEmpty(expectedFlow.Flags) && 
                        NormalizeFlags(expectedFlow.Flags) == NormalizeFlags(p.Flags));
                    
                    if (matchingPacket != null)
                    {
                        // Found matching packet - write actual data (columns 11-17)
                        netWs.Cell(netRow, 11).Value = matchingPacket.Flags;  // ActualFlags
                        netWs.Cell(netRow, 12).Value = matchingPacket.State;  // ActualState
                        netWs.Cell(netRow, 13).Value = matchingPacket.SourceRole;  // ActualSourceRole
                        netWs.Cell(netRow, 14).Value = matchingPacket.DestinationRole;  // ActualDestRole
                        netWs.Cell(netRow, 15).Value = matchingPacket.Data ?? "";  // ActualData
                        
                        // CRITICAL FIX: Write source and destination ports for debugging
                        netWs.Cell(netRow, 16).Value = matchingPacket.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, 17).Value = matchingPacket.DestinationPort;  // ActualDestPort
                        
                        // Check if it's an exact match or just partial
                        // Flags must match exactly (but order doesn't matter - normalize both)
                        bool exactMatch = true;
                        
                        // Compare flags - exact match required (but order-normalized)
                        if (!string.IsNullOrEmpty(expectedFlow.Flags) && NormalizeFlags(expectedFlow.Flags) != NormalizeFlags(matchingPacket.Flags))
                            exactMatch = false;
                        
                        // Compare roles exactly
                        if (!string.IsNullOrEmpty(expectedFlow.SourceRole) && matchingPacket.SourceRole != expectedFlow.SourceRole)
                            exactMatch = false;
                        if (!string.IsNullOrEmpty(expectedFlow.DestinationRole) && matchingPacket.DestinationRole != expectedFlow.DestinationRole)
                            exactMatch = false;
                        
                        netWs.Cell(netRow, 18).Value = exactMatch ? "PASS" : "PARTIAL";
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = exactMatch ? XLColor.LightGreen : XLColor.Yellow;
                        
                        // Remove from list so we can identify extra packets later
                        actualPacketsForStage.Remove(matchingPacket);
                    }
                    else
                    {
                        // No matching packet found - expected flow is MISSING
                        netWs.Cell(netRow, 11).Value = "(MISSING - not captured)";  // ActualFlags
                        netWs.Cell(netRow, 12).Value = "";  // ActualState
                        netWs.Cell(netRow, 13).Value = "";  // ActualSourceRole
                        netWs.Cell(netRow, 14).Value = "";  // ActualDestRole
                        netWs.Cell(netRow, 15).Value = "";  // ActualData
                        netWs.Cell(netRow, 16).Value = "";  // ActualSourcePort
                        netWs.Cell(netRow, 17).Value = "";  // ActualDestPort
                        netWs.Cell(netRow, 18).Value = "FAIL";
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = XLColor.LightPink;
                        
                        OnProgress($"[Network Sheet] Expected flow MISSING at stage {expectedFlow.Stage}: Flags={expectedFlow.Flags}, SourceRole={expectedFlow.SourceRole}, DestRole={expectedFlow.DestinationRole}");
                    }
                    
                    netRow++;
                }
            }
            
            // === SECTION 2: Additional Captured Packets (not validated by this test case) ===
            // These packets were captured but not validated by the test case.
            // This is NORMAL - test cases intentionally validate only specific aspects:
            //   - TC1 may only validate sending
            //   - TC2 may validate send + server confirm
            //   - TC3 may validate all communication + console output
            //   - TC4 may validate disconnect behavior
            // Extra packets are shown for information but DO NOT cause test failure.
            foreach (var stage in capturesByStage.Keys.OrderBy(k => k))
            {
                var remainingPackets = capturesByStage[stage];
                if (remainingPackets.Count > 0)
                {
                    OnProgress($"[Network Sheet] Found {remainingPackets.Count} additional (not validated) packets at stage {stage}");
                    
                    foreach (var packet in remainingPackets)
                    {
                        // No expected flow for this packet - shown for information only
                        netWs.Cell(netRow, 1).Value = packet.Stage;  // Stage
                        netWs.Cell(netRow, 2).Value = "(Not validated by this test case)";  // Time
                        // Leave expected columns 3-10 empty
                        for (int i = 3; i <= 10; i++) 
                            netWs.Cell(netRow, i).Value = "";
                        
                        // Write actual packet data (columns 11-17)
                        netWs.Cell(netRow, 11).Value = packet.Flags;  // ActualFlags
                        netWs.Cell(netRow, 12).Value = packet.State;  // ActualState
                        netWs.Cell(netRow, 13).Value = packet.SourceRole;  // ActualSourceRole
                        netWs.Cell(netRow, 14).Value = packet.DestinationRole;  // ActualDestRole
                        netWs.Cell(netRow, 15).Value = packet.Data ?? "";  // ActualData
                        
                        // CRITICAL FIX: Write source and destination ports for debugging
                        netWs.Cell(netRow, 16).Value = packet.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, 17).Value = packet.DestinationPort;  // ActualDestPort
                        
                        netWs.Cell(netRow, 18).Value = "INFO";  // Informational - not validated
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = XLColor.LightGray;
                        
                        netRow++;
                    }
                }
            }
            
            // === SECTION 3: No network data case ===
            if (expectedNetworkFlows.Count == 0 && result.NetworkCaptures.Count == 0)
            {
                // No expected flows and no captures - add a note
                netWs.Cell(netRow, 1).Value = "N/A";
                netWs.Cell(netRow, 2).Value = "No network flows expected or captured for this test case";
            }
            
            netWs.Columns().AdjustToContents();
            
            wb.SaveAs(detailPath);
            
            // NO LONGER NEEDED: {TestCase}_Result.xlsx files are not logging anything useful
            // They were redundant with GradeDetail.xlsx and OverallSummary.xlsx
            // Removed per user requirement: "remove the excessive sheet {testcasename}_Result under each student folder"
            /*
            // Also write TC_Result.xlsx (summary file per test case)
            var resultFilePath = Path.Combine(tcResultPath, $"{tcName}_Result.xlsx");
            using var resultWb = new XLWorkbook();
            var resultWs = resultWb.Worksheets.Add("Result");
            resultWs.Cell(1, 1).Value = "TestCase";
            resultWs.Cell(1, 2).Value = "Passed";
            resultWs.Cell(1, 3).Value = "EarnedMark";
            resultWs.Cell(1, 4).Value = "MaxMark";
            resultWs.Row(1).Style.Font.Bold = true;
            resultWs.Cell(2, 1).Value = tcName;
            resultWs.Cell(2, 2).Value = result.Passed ? "PASS" : "FAIL";
            resultWs.Cell(2, 3).Value = result.EarnedMark;
            resultWs.Cell(2, 4).Value = result.MaxMark;
            resultWs.Columns().AdjustToContents();
            resultWb.SaveAs(resultFilePath);
            */
            
            await Task.CompletedTask;
        }
        
        private static void SetUserSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Input", "Action", "DataType", "Result", "ErrorCode", "ErrorCategory", 
                "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message", "DiffIndex", 
                "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        private static void SetClientSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", "ErrorCode", 
                "ErrorCategory", "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message", 
                "DiffIndex", "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ClientStdout" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        private static void SetServerSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", "ErrorCode", 
                "ErrorCategory", "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message", 
                "DiffIndex", "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ServerStdout" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        private static void SetNetworkSheetHeaders(IXLWorksheet ws)
        {
            // Network sheet format matching ExcelDetailLogService naming convention (NO underscores).
            // Format: Stage, expected columns (Time, Flags, etc.), then Actual* columns, then NetworkResult.
            // CRITICAL FIX: Added SourcePort and DestPort columns for debugging network traffic
            // This ensures consistency across both Docker and regular grading flows.
            var headers = new[] { 
                "Stage",  // Test stage number
                "Time", "Info", "Source", "Destination", 
                "Flags", "State", "Data", "SourceRole", "DestinationRole",
                "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData",
                "ActualSourcePort", "ActualDestPort",  // CRITICAL FIX: Added port columns for debugging
                "NetworkResult"  // PASS or FAIL for network flow matching
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        private async Task WriteOverallSummaryAsync(string studentResultPath, List<TestCaseResult> results)
        {
            var summaryPath = Path.Combine(studentResultPath, "OverallSummary.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Summary");
            
            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "EarnedMark";
            ws.Cell(1, 4).Value = "MaxMark";
            ws.Cell(1, 5).Value = "Error";
            ws.Row(1).Style.Font.Bold = true;
            
            int row = 2;
            foreach (var result in results)
            {
                ws.Cell(row, 1).Value = result.TestCaseName;
                ws.Cell(row, 2).Value = result.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 3).Value = result.EarnedMark;
                ws.Cell(row, 4).Value = result.MaxMark;
                ws.Cell(row, 5).Value = result.ErrorMessage ?? "";
                row++;
            }
            
            ws.Columns().AdjustToContents();
            wb.SaveAs(summaryPath);
            await Task.CompletedTask;
        }
        
        #endregion
        
        private void OnProgress(string message)
        {
            // Add [StudentCode] prefix to help with debugging
            var formattedMessage = !string.IsNullOrEmpty(_currentStudentCode) 
                ? $"[{_currentStudentCode}] {message}" 
                : message;
            // Invoke the event to send progress update
            ProgressUpdated?.Invoke(this, new GradingProgressEventArgs(formattedMessage));
        }
        
        /// <summary>
        /// Start network monitor container to capture traffic for Docker internal networking mode.
        /// 
        /// SIDECAR APPROACH (Option A):
        /// The monitor container shares the network namespace of the server container using
        /// --net=container:{serverContainer}. This "sidecar" approach provides:
        /// 1. Full visibility into server's network traffic (sees everything server sends/receives)
        /// 2. Direct access to server's network interface (eth0)
        /// 3. Platform-independent (works on Linux, Windows, Mac)
        /// 4. No bridge network complexity or switching isolation issues
        /// 
        /// CRITICAL REQUIREMENTS FOR PACKET CAPTURE:
        /// 1. NET_ADMIN capability: Required for tcpdump to access network interfaces
        /// 2. NET_RAW capability: Required for tcpdump to capture raw packets
        /// 3. Attached to server container's network namespace via --net=container
        /// 4. Filter expression must match actual traffic (tcp port {port})
        /// 
        /// Without these capabilities, tcpdump will fail silently or produce empty pcap files.
        /// </summary>
        
        /// <summary>
        /// Stop network monitor container and analyze captured traffic.
        /// Returns network flow data parsed from the pcap file.
        /// 
        /// SIDECAR CLEANUP:
        /// When using --net=container:{serverContainer}, the monitor shares the server's network namespace.
        /// When the server container is removed, the monitor container automatically stops.
        /// This method ensures the monitor is properly stopped and removed, and the pcap file is analyzed.
        /// </summary>
        
        /// <summary>
        /// Parse pcap file for current stage using SNAPSHOT strategy.
        /// Creates an internal snapshot copy inside the container to bypass file locking issues,
        /// then copies the snapshot to host for parsing.
        /// This enables per-stage network validation without stopping the monitor.
        /// </summary>
        private async Task ParsePcapForCurrentStageAsync(int currentStage, int port)
        {
            if (string.IsNullOrEmpty(_currentPcapFilePath) || string.IsNullOrEmpty(_currentMonitorContainer))
            {
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Skipping - monitor container not set (_currentMonitorContainer={_currentMonitorContainer ?? "null"})");
                return;
            }
            
            var monitorContainer = _currentMonitorContainer; // Use the saved container name
            var pcapFileName = Path.GetFileName(_currentPcapFilePath);
            var snapshotPath = Path.Combine(Path.GetDirectoryName(_currentPcapFilePath) ?? "", $"snapshot_stage{currentStage}.pcap");
            
            try
            {
                // Skip the IsContainerRunning check - it has reliability issues with docker ps filters
                // If the container doesn't exist or has crashed, the snapshot copy will fail which we handle below
                
                // SNAPSHOT STRATEGY Step 0: Force tcpdump to flush its buffer by sending SIGUSR1 signal
                // This ensures all captured packets are written to the pcap file before we copy it
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Flushing tcpdump buffer in {monitorContainer}...");
                var flushCmd = $"{monitorContainer} pkill -USR1 tcpdump";
                _dockerExecutor.ExecDockerCommandWithOutput(flushCmd, 2000);
                
                // Give tcpdump a moment to finish flushing
                await Task.Delay(500);
                
                // SNAPSHOT STRATEGY Step 1: Create a snapshot copy INSIDE the container
                // This bypasses Windows/WSL2 file locking issues with the live capture file
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Creating snapshot from /data/{pcapFileName}");
                // Note: ExecDockerCommandWithOutput adds "docker exec" prefix, so we only provide container name and command
                var snapshotCmd = $"{monitorContainer} cp /data/{pcapFileName} /data/snapshot.pcap";
                var (snapshotSuccess, snapshotOutput) = _dockerExecutor.ExecDockerCommandWithOutput(snapshotCmd, 5000);
                
                if (!snapshotSuccess)
                {
                    // File doesn't exist yet - normal for early stages before traffic
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot copy failed (file may not exist yet): {snapshotOutput}");
                    return;
                }
                
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot created, downloading to host");
                
                // SNAPSHOT STRATEGY Step 2: Download the snapshot to host
                // Note: docker cp is a top-level command, not docker exec, so we run it directly
                var copyCmd = $"docker cp {monitorContainer}:/data/snapshot.pcap \"{snapshotPath}\"";
                var copyResult = _commandExecutor.RunCommandAndCaptureOutput(copyCmd, null, null, 5000);
                var copySuccess = copyResult.ExitCode == 0;
                var copyOutput = string.Join("\n", copyResult.Output);
                
                if (!copySuccess)
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: Failed to download snapshot: {copyOutput}");
                    return;
                }
                
                if (!File.Exists(snapshotPath))
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot file not found at {snapshotPath}");
                    return;
                }
                
                var fileSize = new FileInfo(snapshotPath).Length;
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot downloaded ({fileSize} bytes), parsing with tcpdump");
                
                // SNAPSHOT STRATEGY Step 3: Parse the snapshot (cumulative - contains all packets so far)
                // Use tcpdump to read pcap with detailed output
                var psi = new ProcessStartInfo
                {
                    FileName = "tcpdump",
                    Arguments = $"-r \"{snapshotPath}\" -nn -tttt -v tcp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null)
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: Failed to start tcpdump process");
                    return;
                }
                
                string output = await process.StandardOutput.ReadToEndAsync();
                string errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: tcpdump stderr: {errorOutput}");
                }
                
                // Parse tcpdump output
                // Example: "2024-12-08 11:08:03.543348 IP 127.0.0.1.47044 > 127.0.0.1.4000: Flags [S], seq 3911487358, ..."
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                OnProgress($"[NetworkMonitor] Stage {currentStage}: tcpdump output has {lines.Length} lines");
                
                // Skip packets we've already processed
                var newPackets = lines.Skip(_lastParsedPacketCount).ToList();
                
                foreach (var line in newPackets)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains(" > ")) continue;
                    
                    try
                    {
                        // Parse packet details
                        var packet = ParseTcpdumpLine(line, currentStage, port);
                        if (packet != null)
                        {
                            // Add to RunContext for this stage
                            _runContext.AddCapturedNetworkPacket(_currentStudentCode ?? "", currentStage.ToString(), packet);
                        }
                    }
                    catch
                    {
                        // Skip malformed lines
                        continue;
                    }
                }
                
                // Update counter to skip these packets next time
                _lastParsedPacketCount = lines.Length;
                
                OnProgress($"[NetworkMonitor] Parsed {newPackets.Count} new packets for stage {currentStage}, total packets: {lines.Length}");
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] Error parsing pcap for stage {currentStage}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Parse a single tcpdump output line into CapturedNetworkPacket.
        /// Example line: "2024-12-08 11:08:03.543348 IP 127.0.0.1.47044 > 127.0.0.1.4000: Flags [S], seq 3911487358, ..."
        /// </summary>
        private CapturedNetworkPacket? ParseTcpdumpLine(string line, int stage, int expectedPort)
        {
            // Extract timestamp
            var timestampMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            DateTime timestamp = timestampMatch.Success && DateTime.TryParse(timestampMatch.Groups[1].Value, out var dt) 
                ? dt 
                : DateTime.Now;
            
            // Extract source and destination: "IP 127.0.0.1.47044 > 127.0.0.1.4000:"
            var addressMatch = System.Text.RegularExpressions.Regex.Match(line, @"IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+)");
            if (!addressMatch.Success) return null;
            
            var srcIp = addressMatch.Groups[1].Value;
            var srcPort = int.Parse(addressMatch.Groups[2].Value);
            var dstIp = addressMatch.Groups[3].Value;
            var dstPort = int.Parse(addressMatch.Groups[4].Value);
            
            // Determine roles based on port
            string srcRole, dstRole;
            if (srcPort == expectedPort)
            {
                srcRole = "Server";
                dstRole = "Client";
            }
            else if (dstPort == expectedPort)
            {
                srcRole = "Client";
                dstRole = "Server";
            }
            else
            {
                // Not related to our expected port
                return null;
            }
            
            // Extract flags: [S] = SYN, [S.] = SYN-ACK, [.] = ACK, [P.] = PSH-ACK, [F.] = FIN-ACK, [R] = RST
            string flags = "UNKNOWN";
            string state = "";
            
            if (line.Contains("Flags [S]") && !line.Contains("Flags [S.]"))
            {
                flags = "SYN";
                state = "SYN_SENT";
            }
            else if (line.Contains("Flags [S.]"))
            {
                flags = "SYN-ACK";
                state = "SYN_RECEIVED";
            }
            else if (line.Contains("Flags [.]") && !line.Contains("Flags [P.]") && !line.Contains("Flags [F.]"))
            {
                flags = "ACK";
                state = "ESTABLISHED";
            }
            else if (line.Contains("Flags [P.]"))
            {
                flags = "PSH-ACK";
                state = "ESTABLISHED";
            }
            else if (line.Contains("Flags [F.]"))
            {
                flags = "FIN-ACK";
                state = "FIN_WAIT";
            }
            else if (line.Contains("Flags [R]"))
            {
                flags = "RST";
                state = "RESET";
            }
            
            // Extract payload (if any)
            // Look for "length N" at the end
            var lengthMatch = System.Text.RegularExpressions.Regex.Match(line, @"length (\d+)");
            string data = lengthMatch.Success && int.Parse(lengthMatch.Groups[1].Value) > 0 
                ? $"{lengthMatch.Groups[1].Value} bytes"
                : "";
            
            return new CapturedNetworkPacket
            {
                Stage = stage,
                Timestamp = timestamp,
                Flags = flags,
                State = state,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Data = data,
                SourcePort = srcPort,
                DestinationPort = dstPort
            };
        }
        
        /// <summary>
        /// Parse pcap file using tcpdump to extract network flows.
        /// Returns list of packets with SYN/ACK/PSH/RST flags.
        /// </summary>
        private async Task<List<Dictionary<string, string>>> ParsePcapFileAsync(string pcapFile)
        {
            var flows = new List<Dictionary<string, string>>();
            
            try
            {
                // Use tcpdump to read the pcap file
                // Format: timestamp src > dst: flags [...]
                var psi = new ProcessStartInfo
                {
                    FileName = "tcpdump",
                    Arguments = $"-r \"{pcapFile}\" -nn -tttt",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        OnProgress("[NetworkMonitor] Failed to start tcpdump for parsing");
                        return flows;
                    }
                    
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    
                    // Parse tcpdump output
                    // Example: "2024-12-08 05:00:00.123456 IP 172.18.0.2.54321 > 172.18.0.3.4000: Flags [S], ..."
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        
                        var packet = new Dictionary<string, string>();
                        
                        // Extract flags: [S] = SYN, [.] = ACK, [P] = PSH, [R] = RST, [F] = FIN
                        if (line.Contains("Flags [S]")) packet["Flags"] = "SYN";
                        else if (line.Contains("Flags [S.]")) packet["Flags"] = "SYN-ACK";
                        else if (line.Contains("Flags [.]")) packet["Flags"] = "ACK";
                        else if (line.Contains("Flags [P.]")) packet["Flags"] = "PSH-ACK";
                        else if (line.Contains("Flags [R]")) packet["Flags"] = "RST";
                        else if (line.Contains("Flags [F.]")) packet["Flags"] = "FIN-ACK";
                        else packet["Flags"] = "OTHER";
                        
                        packet["RawLine"] = line;
                        flows.Add(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] Error parsing pcap: {ex.Message}");
            }
            
            return flows;
        }
    }
    
    #region Model Classes
    
    /// <summary>
    /// Configuration for Docker-based grading.
    /// </summary>
    public class DockerGradingConfig
    {
        /// <summary>
        /// Whether the examiner expects the student to provide a CLIENT component.
        /// If true, the grader will search for the client DLL in student's solution.
        /// If false and a client is needed, use the golden client from Meta/Given/Client.
        /// </summary>
        public bool HasClient { get; set; } = true;
        
        /// <summary>
        /// Whether the examiner expects the student to provide a SERVER component.
        /// If true, the grader will search for the server DLL in student's solution.
        /// If false and a server is needed, use the golden server from Meta/Given/Server.
        /// </summary>
        public bool HasServer { get; set; } = true;
        
        /// <summary>
        /// Project name for the client DLL (e.g., "Project12" searches for "Project12.dll")
        /// </summary>
        public string ClientProjectName { get; set; } = "Project12";
        
        /// <summary>
        /// Project name for the server DLL (e.g., "Project11" searches for "Project11.dll")
        /// </summary>
        public string ServerProjectName { get; set; } = "Project11";
        
        public int CodeContainerInternalPort { get; set; } = 8000;
        public int CodeContainerHostPort { get; set; } = 8000;
        public string DockerNetwork { get; set; } = "auto-grading-network";
        
        // Database container settings
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2019-latest";
        public string DatabaseContainerName { get; set; } = "auto-grading-sqlserver";
        public int DatabaseContainerInternalPort { get; set; } = 1433;
        public int DatabaseContainerHostPort { get; set; } = 1434;
        public string? DatabaseUsername { get; set; } = "sa";
        public string? DatabasePassword { get; set; }
        
        /// <summary>
        /// Total grading timeout in seconds (overall timeout for all test cases).
        /// Default: 60 seconds.
        /// </summary>
        public int GradingTimeoutSeconds { get; set; } = 60;
        
        /// <summary>
        /// Per-test-case timeout in seconds. If a test case takes longer than this,
        /// it is stopped and marked as failed. Default: 15 seconds.
        /// </summary>
        public int TestCaseTimeoutSeconds { get; set; } = 15;
        
        /// <summary>
        /// Enables DLL modification fallback when appsettings.json is not found.
        /// When enabled and appsettings.json doesn't exist, the system will attempt to 
        /// directly patch the compiled DLL files to set correct IP addresses and ports.
        /// Default: false (disabled).
        /// </summary>
        public bool UseDllModificationFallback { get; set; } = false;
        
        /// <summary>
        /// Image name for unified containers that run both client and server processes.
        /// This image has supervisord installed for process management.
        /// Default: "fptuxaes/aes-dotnet8-console:latest"
        /// </summary>
        public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8-console:latest";
    }
    
    /// <summary>
    /// Test kit configuration loaded from Environment.xlsx and Header.xlsx.
    /// </summary>
    internal class TestKitConfig
    {
        public int CodeContainerInternalPort { get; set; } = 8000;
        public int CodeContainerHostPort { get; set; } = 8000;
        public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8-console:latest";
        public string DockerNetwork { get; set; } = "auto-grading-network";
        public string DatabaseName { get; set; } = "Library";
        public string DatabasePassword { get; set; } = "";
        public string Protocol { get; set; } = "TCP";
        
        /// <summary>
        /// Path to the given/golden server DLL from Meta/Given/Server folder.
        /// This is used when the student only provides a client (Project12).
        /// </summary>
        public string? GivenServerPath { get; set; }
        
        /// <summary>
        /// Path to the given/golden client DLL from Meta/Given/Client folder.
        /// This is used when the student only provides a server (Project11).
        /// </summary>
        public string? GivenClientPath { get; set; }
        
        public Dictionary<string, double> TestCaseMarks { get; set; } = new();
        public List<TestCaseInfo> TestCases { get; set; } = new();
        public double TotalMaxMark => TestCases.Sum(tc => tc.MaxMark);
    }
    
    internal class TestCaseInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public double MaxMark { get; set; }
        /// <summary>
        /// Per-test-case timeout in seconds, read from Header.xlsx Testcase_Property sheet.
        /// Defaults to 15 seconds if not specified in the test kit.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;
        
        /// <summary>
        /// Specifies what should be graded for this test case.
        /// Values: "Client", "Server", or "Client/Server"
        /// - "Client": Grade student's client with golden server
        /// - "Server": Grade student's server with golden client
        /// - "Client/Server": Grade both student's client and server (no golden used)
        /// Read from Header.xlsx Testcase_Property sheet.
        /// Defaults to "Client/Server" if not specified.
        /// </summary>
        public string GradeContent { get; set; } = "Client/Server";
    }
    
    internal class ExpectedNetworkFlow
    {
        public int Stage { get; set; }
        public string? Flags { get; set; }
        public string? State { get; set; }
        public string? SourceRole { get; set; }
        public string? DestinationRole { get; set; }
    }
    
    /// <summary>
    /// Result of Docker-based grading for a single student.
    /// </summary>
    public class DockerGradingResult
    {
        public string StudentCode { get; set; } = "";
        public double TotalMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new();
    }
    
    /// <summary>
    /// Result of a single test case - matches SampleLogging format.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = "";
        public double EarnedMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
        
        /// <summary>Actions executed (StartClient, StartServer, Input, etc.) - for User sheet</summary>
        public List<ActionRecord> Actions { get; set; } = new();
        
        /// <summary>Client console output comparisons - for Client sheet</summary>
        public List<ComparisonResult> ClientComparisons { get; set; } = new();
        
        /// <summary>Server console output comparisons - for Server sheet</summary>
        public List<ComparisonResult> ServerComparisons { get; set; } = new();
        
        /// <summary>Network flow comparisons - for Network sheet (expected vs actual)</summary>
        public List<ComparisonResult> NetworkComparisons { get; set; } = new();
        
        /// <summary>Captured network packets - for Network sheet (raw captures)</summary>
        public List<CapturedNetworkPacket> NetworkCaptures { get; set; } = new();
    }
    
    /// <summary>
    /// Action record for User sheet (StartClient, StartServer, Input, etc.)
    /// </summary>
    public class ActionRecord
    {
        public int Stage { get; set; }
        public string? Input { get; set; }
        public string? ActionType { get; set; }
    }
    
    /// <summary>
    /// Comparison result for console output or network - extended with SampleLogging fields.
    /// </summary>
    public class ComparisonResult
    {
        public string Source { get; set; } = "";
        public int Stage { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public bool Passed { get; set; }
        
        /// <summary>
        /// Indicates if this is a PARTIAL match (flags match but roles don't).
        /// PARTIAL matches should count as passing with partial credit.
        /// </summary>
        public bool IsPartial { get; set; }
        
        // Additional fields for SampleLogging format
        public double PointsAwarded { get; set; }
        public double PointsPossible { get; set; }
        public double DurationMs { get; set; }
        public string? Message { get; set; }
    }
    
    
    /// <summary>
    /// Event arguments for grading progress updates.
    /// </summary>
    public class GradingProgressEventArgs : EventArgs
    {
        public string Message { get; }
        public GradingProgressEventArgs(string message) => Message = message;
    }
    
    #endregion
}
