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
using Domain.Models;
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
        private readonly SharpPcapParsingService _sharpPcapParser; // SharpPcap parser for robust PCAP reading
        private string? _currentStudentCode; // Track current student for logging
        private string? _currentTestCaseName; // Track current test case for per-TC logging (e.g., "TC3")
        private string? _currentTestKitProtocol; // Track protocol type (TCP or HTTP) from testkit Header.xlsx
        
        // Network monitoring for sidecar pattern
        private string? _currentMonitorContainer; // Name of network monitor container (e.g., ag-monitor-StudentCode)
        private string? _currentPcapFilePath; // Path to pcap file being written by network monitor
        private int _lastParsedPacketCount = 0; // Track how many packets we've already processed
        
        // Stage output tracking for per-test-case log export
        private Dictionary<int, string>? _lastTestCaseClientOutputs; // Track last test case client outputs by stage
        private Dictionary<int, string>? _lastTestCaseServerOutputs; // Track last test case server outputs by stage
        
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
            _sharpPcapParser = new SharpPcapParsingService(); // Initialize SharpPcap parser
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
                var monitorContainer = $"ag-monitor-{studentCode}";
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
                    testKitConfig.Protocol);
                
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
                    
                    // CRITICAL: Clear RunContext at START of each test case for proper isolation
                    // This prevents packets from previous test cases from being included in comparisons
                    if (_currentMonitorContainer != null && !string.IsNullOrEmpty(_currentPcapFilePath))
                    {
                        OnProgress($"[NetworkMonitor] [{testCase.Name}] Starting test case - clearing RunContext...");
                        
                        // Clear previous test case packets from RunContext (in-memory)
                        _runContext.ClearCapturedNetworkPackets(_currentStudentCode ?? "");
                        
                        // CRITICAL FIX: Reset packet counter when clearing RunContext between test cases
                        // This ensures ParsePcapForCurrentStageAsync starts fresh for the new test case
                        // Without this, accumulated packets from previous test case would be skipped
                        _lastParsedPacketCount = 0;
                        
                        OnProgress($"[NetworkMonitor] [{testCase.Name}] RunContext cleared and packet counter reset for fresh comparison");
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
        /// Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
        /// The C# code reads these files incrementally after each action to separate output by stage.
        /// </summary>
        private async Task SetupUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            OnProgress($"[SETUP] Creating unified container: {unifiedContainer}");
            
            // Remove existing unified container if any
            _commandExecutor.RunCommand($"docker rm -f {unifiedContainer} 2>/dev/null || true", null, null, 10000);
            
            // Create the unified container with supervisord
            // Processes are controlled by test case actions (StartClient, StartServer, CloseClient, CloseServer)
            // Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
            // The C# code reads these files incrementally after each action to separate output by stage
            var dockerCmd = $"docker run -d --name {unifiedContainer} " +
                           $"--network {config.DockerNetwork} " +
                           $"-t " +  // TTY for unbuffered logs
                           $"{config.CodeImageName}";
            
            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);
            OnProgress($"[SETUP] Unified container ready - supervisord running, processes idle");
            
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
            OnProgress($"[Unified] Logs will be written to unified files: /apps/server/server.log and /apps/client/client.log");
            OnProgress($"[Unified] C# code reads these files incrementally to separate output by stage");
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
        /// Creates a database instance within the shared MSSQL container for a student.
        /// This ensures each student has their own isolated database even when sharing the container.
        /// </summary>
        /// <param name="config">Docker grading configuration</param>
        /// <param name="databaseName">Name of the database to create (e.g., Library_student1)</param>
        /// <param name="sqlScriptPath">Optional path to SQL initialization script on host machine</param>
        private async Task CreateDatabaseInstanceAsync(DockerGradingConfig config, string databaseName, string? sqlScriptPath = null)
        {
            var databaseContainer = config.DatabaseContainerName;
            var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
            var databaseUsername = config.DatabaseUsername ?? "sa";
            
            // SECURITY: Validate database name to prevent SQL injection
            // Database names should only contain alphanumeric characters, underscores, and hyphens
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_\-]+$"))
            {
                throw new ArgumentException($"Invalid database name '{databaseName}'. Database names must contain only letters, numbers, underscores, and hyphens.", nameof(databaseName));
            }
            
            OnProgress($"[Database] Creating database instance '{databaseName}' in container {databaseContainer}");
            
            try
            {
                // Step 1: Check if database already exists
                var checkDbSql = $"SELECT name FROM sys.databases WHERE name = '{databaseName}'";
                var checkCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";
                
                var (checkSuccess, checkOutput) = _dockerExecutor.ExecDockerCommandWithOutput(checkCommand, 5000);
                
                if (checkSuccess && checkOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Database '{databaseName}' already exists, dropping it first");
                    
                    // Drop existing database
                    var dropSql = $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
                    var dropCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{dropSql}\"";
                    _dockerExecutor.ExecDockerCommand(dropCommand, 10000);
                    
                    OnProgress($"[Database] Dropped existing database '{databaseName}'");
                    await Task.Delay(1000); // Wait for drop to complete
                }
                
                // Step 2: Create database
                if (!string.IsNullOrEmpty(sqlScriptPath) && File.Exists(sqlScriptPath))
                {
                    // Create database from SQL script
                    OnProgress($"[Database] Creating database '{databaseName}' from SQL script: {sqlScriptPath}");
                    
                    // Copy SQL script to container
                    var containerSqlPath = $"/tmp/{databaseName}.sql";
                    _dockerExecutor.CopyFileToContainer(databaseContainer, sqlScriptPath, containerSqlPath);
                    
                    // Execute SQL script
                    var execScriptCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -i {containerSqlPath}";
                    var (scriptSuccess, scriptOutput) = _dockerExecutor.ExecDockerCommandWithOutput(execScriptCommand, 30000);
                    
                    if (scriptSuccess)
                    {
                        OnProgress($"[Database] Successfully created database '{databaseName}' from script");
                    }
                    else
                    {
                        OnProgress($"[Database] WARNING: Failed to create database from script: {scriptOutput}");
                        OnProgress($"[Database] SQL server container will remain running, but database instance was not created");
                        throw new Exception($"Failed to create database from SQL script: {scriptOutput}");
                    }
                }
                else
                {
                    // Create empty database (no SQL script provided)
                    OnProgress($"[Database] Creating empty database '{databaseName}' (no SQL script provided)");
                    
                    var createDbSql = $"CREATE DATABASE [{databaseName}]";
                    var createCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{createDbSql}\"";
                    
                    var (createSuccess, createOutput) = _dockerExecutor.ExecDockerCommandWithOutput(createCommand, 10000);
                    
                    if (createSuccess)
                    {
                        OnProgress($"[Database] Successfully created empty database '{databaseName}'");
                    }
                    else
                    {
                        OnProgress($"[Database] WARNING: Failed to create database: {createOutput}");
                        OnProgress($"[Database] SQL server container will remain running, but database instance was not created");
                        throw new Exception($"Failed to create database: {createOutput}");
                    }
                }
                
                // Step 3: Verify database was created
                var verifyCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";
                var (verifySuccess, verifyOutput) = _dockerExecutor.ExecDockerCommandWithOutput(verifyCommand, 5000);
                
                if (verifySuccess && verifyOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Verified database '{databaseName}' exists and is ready");
                }
                else
                {
                    OnProgress($"[Database] WARNING: Could not verify database '{databaseName}' exists");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database] WARNING: Failed to create database instance '{databaseName}': {ex.Message}");
                OnProgress($"[Database] Skipping database creation but keeping SQL server container running");
                // Don't throw - allow grading to continue without database
                // The SQL server container will remain running for the session
            }
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
                
                // Copy SERVER files (without DLL modification - that's a fallback after appsettings)
                if (!string.IsNullOrEmpty(serverDllPath))
                {
                    var serverDir = Path.GetDirectoryName(serverDllPath);
                    if (serverDir != null)
                    {
                        try
                        {
                            // Copy original files to /apps/server
                            // DLL modification will be applied later as a fallback if appsettings.json is not found
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/server/AutoGrading_UnifiedServer_*/
                            // With "/.": creates /apps/server/*.dll, /apps/server/appsettings.json, etc.
                            var serverSource = serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(serverSource, $"{unifiedContainer}:/apps/server/");
                            OnProgress($"[Unified] Copied server files to /apps/server (DLL mod will be applied as fallback if needed)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Server copy failed: {ex.Message}");
                        }
                    }
                }
                
                // Copy CLIENT files (without DLL modification - that's a fallback after appsettings)
                if (!string.IsNullOrEmpty(clientDllPath))
                {
                    var clientDir = Path.GetDirectoryName(clientDllPath);
                    if (clientDir != null)
                    {
                        try
                        {
                            // Copy original files to /apps/client
                            // DLL modification will be applied later as a fallback if appsettings.json is not found
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/client/AutoGrading_UnifiedClient_*/
                            // With "/.": creates /apps/client/*.dll, /apps/client/appsettings.json, etc.
                            var clientSource = clientDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(clientSource, $"{unifiedContainer}:/apps/client/");
                            OnProgress($"[Unified] Copied client files to /apps/client (DLL mod will be applied as fallback if needed)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Client copy failed: {ex.Message}");
                        }
                    }
                }
                
                // Configure appsettings.json (modify existing or apply DLL mod as fallback)
                ConfigureAppsettingsInUnifiedContainer(config, testKitConfig, unifiedContainer, _currentStudentCode ?? "Unknown");
            }
            finally
            {
                // Cleanup temp directories (if any were created for DLL modification fallback)
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
        /// <summary>
        /// Configures appsettings.json in the unified container using modification-first approach:
        /// 1. Check if appsettings.json exists in /apps/server and /apps/client
        /// 2. If exists: Modify only Port, IpAddress, ConnectionString (preserves student settings)
        /// 3. If not exists AND UseDllModificationFallback=true: DLL mod already applied during copy
        /// 4. If not exists AND UseDllModificationFallback=false: Log warning (may fail at runtime)
        /// 
        /// This approach respects student configuration while enabling grading.
        /// 
        /// Connection String Logic:
        /// - If UseSharedDatabaseContainer=true: Connects to Student_{StudentCode} database on shared container
        /// - If UseSharedDatabaseContainer=false: Connects to database specified in testKitConfig
        /// </summary>
        private void ConfigureAppsettingsInUnifiedContainer(
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer,
            string studentCode)
        {
            // Build connection string based on database container architecture
            string connectionString;
            if (config.UseSharedDatabaseContainer)
            {
                // Shared container: Each student gets Student_{StudentCode} database
                connectionString = ConnectionStringHelper.BuildForStudentDatabase(
                    config.SharedDatabasePort,
                    studentCode,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
                OnProgress($"[Database] Using shared container with database: Student_{studentCode}");
            }
            else
            {
                // Legacy: Use database name from testKitConfig (e.g., Library_StudentCode)
                connectionString = ConnectionStringHelper.BuildForDocker(
                    config.DatabaseContainerHostPort,
                    testKitConfig.DatabaseName,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
                OnProgress($"[Database] Using per-student container with database: {testKitConfig.DatabaseName}");
            }
            
            // UNIFIED CONTAINER: Both client and server use localhost (127.0.0.1)
            var serverIpAddress = "127.0.0.1";  // Bind to localhost
            var clientIpAddress = "127.0.0.1";  // Connect to localhost
            var port = config.CodeContainerInternalPort;
            
            OnProgress($"[Unified] Configuring appsettings for localhost communication (127.0.0.1:{port})");
            
            // Try to modify SERVER appsettings if it exists
            // If appsettings.json doesn't exist and DLL mod is enabled, apply DLL modification as fallback
            TryModifyAppsettingsOrDllModInContainer(
                unifiedContainer,
                "/apps/server",
                "/apps/server/appsettings.json",
                serverIpAddress,
                port,
                connectionString,
                "Server",
                config.ServerProjectName,
                isServer: true,
                dllModFallbackEnabled: config.UseDllModificationFallback);
            
            // Try to modify CLIENT appsettings if it exists
            // If appsettings.json doesn't exist and DLL mod is enabled, apply DLL modification as fallback
            TryModifyAppsettingsOrDllModInContainer(
                unifiedContainer,
                "/apps/client",
                "/apps/client/appsettings.json",
                clientIpAddress,
                port,
                null, // Client doesn't need connection string
                "Client",
                config.ClientProjectName,
                isServer: false,
                dllModFallbackEnabled: config.UseDllModificationFallback);
        }
        
        /// <summary>
        /// Attempts to modify an existing appsettings.json file inside a container.
        /// If the file doesn't exist AND DLL mod fallback is enabled, applies DLL modification instead.
        /// 
        /// NEW BEHAVIOR (as requested by @dongnuc):
        /// 1. First, try to modify appsettings.json if it exists in the container
        /// 2. If appsettings.json doesn't exist AND dllModFallbackEnabled is true:
        ///    - Download DLLs from container
        ///    - Apply DLL modification
        ///    - Upload modified DLLs back to container
        /// 3. If appsettings.json doesn't exist AND dllModFallbackEnabled is false:
        ///    - Log warning that grading may fail
        /// </summary>
        private void TryModifyAppsettingsOrDllModInContainer(
            string container,
            string containerDir,
            string appsettingsPath,
            string ipAddress,
            int port,
            string? connectionString,
            string componentName,
            string projectName,
            bool isServer,
            bool dllModFallbackEnabled)
        {
            // Check if appsettings.json exists
            var checkCmd = $"{container} test -f {appsettingsPath}";
            var (exists, _) = _dockerExecutor.ExecDockerCommandWithOutput(checkCmd, 3000);
            
            if (!exists)
            {
                // Appsettings.json not found - use DLL modification as fallback if enabled
                if (dllModFallbackEnabled)
                {
                    OnProgress($"[Unified] {componentName} appsettings not found at {appsettingsPath} - applying DLL modification fallback");
                    ApplyDllModificationInContainer(container, containerDir, componentName, projectName, isServer, port, ipAddress);
                }
                else
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings not found at {appsettingsPath} and DLL mod is disabled - may fail at runtime");
                }
                return;
            }
            
            // Appsettings exists - download, modify, upload
            OnProgress($"[Unified] Found {componentName} appsettings at {appsettingsPath}, modifying...");
            
            string? tempFile = null;
            try
            {
                // Download appsettings from container
                tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_{componentName}_{Guid.NewGuid()}.json");
                var copyFromCmd = $"docker cp {container}:{appsettingsPath} \"{tempFile}\"";
                var copyResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 5000);
                
                if (copyResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] WARNING: Failed to download {componentName} appsettings for modification");
                    return;
                }
                
                // Modify the file
                var modified = ModifyAppsettingsFile(tempFile, ipAddress, port, connectionString, componentName);
                
                if (!modified)
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings modification failed or no changes needed");
                    return;
                }
                
                // Upload modified appsettings back to container
                _dockerExecutor.CopyFileToContainer(tempFile, $"{container}:{appsettingsPath}");
                OnProgress($"[Unified] {componentName} appsettings modified: IpAddress={ipAddress}, Port={port}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR modifying {componentName} appsettings: {ex.Message}");
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
        
        /// <summary>
        /// Applies DLL modification to files inside a container.
        /// Downloads DLLs, modifies them, and uploads them back.
        /// This is used as a fallback when appsettings.json is not found.
        /// </summary>
        private void ApplyDllModificationInContainer(
            string container,
            string containerDir,
            string componentName,
            string projectName,
            bool isServer,
            int targetPort,
            string targetIp)
        {
            string? tempDir = null;
            try
            {
                // Create temp directory for DLL modification
                tempDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_DllMod_{componentName}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                
                // Download files from container
                var copyFromCmd = $"docker cp {container}:{containerDir}/. \"{tempDir}\"";
                OnProgress($"[Unified] Downloading {componentName} files from container for DLL modification...");
                var downloadResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 10000);
                
                if (downloadResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] ERROR: Failed to download {componentName} files from container: {downloadResult.ErrorToString()}");
                    return;
                }
                
                // Apply DLL modification
                var dllModService = new DllModificationService();
                var result = dllModService.CheckAndPatchIfNeeded(
                    tempDir,
                    projectName,
                    isServer,
                    targetPort,
                    targetIp);
                
                OnProgress($"[Unified] {componentName} DLL mod fallback: {result.GetSummary()}");
                
                if (!result.Success && result.RequiresDllModification)
                {
                    OnProgress($"[Unified] WARNING: {componentName} DLL modification failed - student code may not work correctly");
                    return;
                }
                
                // Upload modified files back to container
                var tempSource = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                OnProgress($"[Unified] Uploading modified {componentName} DLLs back to container...");
                _dockerExecutor.CopyFileToContainer(tempSource, $"{container}:{containerDir}/");
                OnProgress($"[Unified] {componentName} DLL modification applied successfully in container");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR applying {componentName} DLL modification in container: {ex.Message}");
            }
            finally
            {
                // Cleanup temp directory
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception cleanEx)
                    {
                        OnProgress($"[Unified] WARNING: Failed to cleanup temp directory {tempDir}: {cleanEx.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Modifies an appsettings.json file, preserving all existing settings while updating specific values.
        /// Returns true if modification was successful.
        /// </summary>
        private bool ModifyAppsettingsFile(string filePath, string ipAddress, int port, string? connectionString, string componentName)
        {
            try
            {
                var jsonText = File.ReadAllText(filePath);
                var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(jsonText);
                
                if (jsonNode == null || jsonNode is not System.Text.Json.Nodes.JsonObject jsonObj)
                {
                    OnProgress($"[Unified] ERROR: Invalid JSON in {componentName} appsettings");
                    return false;
                }
                
                var modified = false;
                
                // Update ConnectionStrings.MyCnn if it exists (server only)
                if (connectionString != null && jsonObj["ConnectionStrings"] is System.Text.Json.Nodes.JsonObject connStrings)
                {
                    if (connStrings["MyCnn"] != null)
                    {
                        connStrings["MyCnn"] = connectionString;
                        modified = true;
                        OnProgress($"[Unified] Updated {componentName} ConnectionStrings.MyCnn");
                    }
                }
                
                // Update IpAddress if it exists
                if (jsonObj["IpAddress"] != null)
                {
                    jsonObj["IpAddress"] = ipAddress;
                    modified = true;
                }
                
                // Update Port if it exists (handle both string and number formats)
                if (jsonObj["Port"] != null)
                {
                    var originalPort = jsonObj["Port"];
                    if (originalPort?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    {
                        jsonObj["Port"] = port.ToString();
                    }
                    else
                    {
                        jsonObj["Port"] = port;
                    }
                    modified = true;
                }
                
                if (modified)
                {
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(filePath, jsonNode.ToJsonString(options));
                    return true;
                }
                else
                {
                    OnProgress($"[Unified] WARNING: No matching properties found to modify in {componentName} appsettings");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR modifying {componentName} appsettings file: {ex.Message}");
                return false;
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
                // CRITICAL FIX: DO NOT clear captures between test cases
                // Since we reuse the same unified container across all test cases, packets accumulate
                // The comparison happens AFTER all test cases complete and needs ALL captured packets
                // Clearing between test cases would lose previous packets
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Starting test case (captures will accumulate)...");
                
                // VERIFICATION: Check cumulative packet count
                var packetCountBefore = _runContext.GetAllCapturedNetworkPackets().Count;
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Cumulative packet count: {packetCountBefore}");
                
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
                OnProgress($"[NetworkMonitor] Student: {_currentStudentCode}, Port: {config.CodeContainerInternalPort}");
                
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
                // - If ANY flow FAILS, entire test FAILS
                // - Only EXACT matches (flags + roles + correct source/dest) count as PASS
                // - Role mismatches count as FAIL (not PARTIAL)
                // - Only flows recorded in Detail.xlsx are validated
                // - Flows NOT in Detail.xlsx are ignored (even if captured)
                
                int totalNetworkFlows = networkComparisons.Count;
                int passCount = networkComparisons.Count(c => c.Passed);
                int failCount = networkComparisons.Count(c => !c.Passed);
                
                OnProgress($"[SCORING] Network flows: Total={totalNetworkFlows}, PASS={passCount}, FAIL={failCount}");
                
                // ALL-OR-NOTHING: Test passes ONLY if ALL flows passed (failCount == 0)
                bool networkFlowsPassed = failCount == 0 || totalNetworkFlows == 0;
                
                OnProgress($"[SCORING] NetworkFlows={networkFlowsPassed} (FAIL={failCount}, Total={totalNetworkFlows})");
                OnProgress($"[SCORING] Output={passed}, NetworkCheck={networkCheckPassed}");
                
                // Final result: must pass both output comparison AND network check
                // No partial credit - ALL or NOTHING
                result.EarnedMark = (passed && networkCheckPassed && networkFlowsPassed) ? earnedMark : 0;
                result.Passed = passed && networkCheckPassed && networkFlowsPassed;
                
                OnProgress($"[SCORING] FINAL: Passed={result.Passed}, EarnedMark={result.EarnedMark}/{earnedMark}");
                if (!result.Passed)
                {
                    OnProgress($"[SCORING] Test FAILED - Output={passed}, NetworkCheck={networkCheckPassed}, NetworkFlows={networkFlowsPassed}");
                }
                
                result.ClientComparisons = comparisons.Where(c => c.Source == "Client").ToList();
                result.ServerComparisons = comparisons.Where(c => c.Source == "Server").ToList();
                result.NetworkComparisons = networkComparisons;
                
                // Build detailed error message for OverallSummary.xlsx
                var errorMessages = new List<string>();
                
                if (!networkCheckPassed)
                {
                    errorMessages.Add("Network monitoring failed: No packets captured");
                }
                
                if (!passed)
                {
                    int failedOutputs = comparisons.Count(c => !c.Passed);
                    if (failedOutputs > 0)
                        errorMessages.Add($"Console output: {failedOutputs} check(s) failed");
                }
                
                if (totalNetworkFlows > 0 && failCount > 0)
                {
                    errorMessages.Add($"Network flows: {failCount} FAIL (ALL-OR-NOTHING), {passCount} PASS");
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
        /// Packets are parsed from pcap file PER-STAGE and added to RunContext during execution.
        /// </summary>
        private List<CapturedNetworkPacket> GetCapturedNetworkPackets()
        {
            return _runContext.GetAllCapturedNetworkPackets().ToList();
        }
        
        
        /// <summary>
        /// Execute test case actions for UNIFIED container (client and server in same container).
        /// Uses unified-control.sh script to start/stop processes via supervisord.
        /// Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
        /// This method reads logs incrementally after each action to separate output by stage.
        /// </summary>
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsForUnifiedContainerAsync(
            List<(int Stage, string Input, string Action)> actions,
            DockerGradingConfig config,
            string unifiedContainer,
            CancellationToken ct)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();
            
            // Track file positions for incremental reading
            long clientLogPosition = 0;
            long serverLogPosition = 0;
            
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
                        
                        OnProgress($"    Server started for stage {stage} (logging to /apps/server/server.log)");
                        break;
                        
                    case "STARTCLIENT":
                        // Use unified-control.sh to start client via supervisord
                        var startClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartClient {stage}";
                        _commandExecutor.RunCommand(startClientCmd, null, null, 30000);
                        
                        await Task.Delay(StartupDelayMs);
                        
                        OnProgress($"    Client started for stage {stage} (logging to /apps/client/client.log)");
                        break;
                        
                    case "INPUT":
                        // INPUT action: Send the input value to the client
                        // IMPORTANT: Always send input when Input action is specified, even if empty
                        // The client application may be waiting for input (including empty lines)
                        // Not sending input causes the client to hang waiting for stdin
                        var sendInputCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh SendInput {stage} \"{input}\"";
                        try
                        {
                            _commandExecutor.RunCommand(sendInputCmd, null, null, 5000);
                            await Task.Delay(InputProcessingDelayMs);
                            if (string.IsNullOrWhiteSpace(input))
                            {
                                OnProgress($"    Empty input sent (newline) for stage {stage}");
                            }
                            else
                            {
                                OnProgress($"    Input sent: '{input}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to send input: {ex.Message}");
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
                
                // CRITICAL: Read logs incrementally AFTER each action to capture stage-specific output
                // This separates output by stage even when processes continue running
                
                // Read new server output for this stage
                try
                {
                    var (newServerOutput, newServerPosition) = ReadFileFromContainerIncremental(
                        unifiedContainer, 
                        "/apps/server/server.log", 
                        serverLogPosition);
                    
                    if (!string.IsNullOrEmpty(newServerOutput))
                    {
                        serverOutputs[stage] = newServerOutput;
                        serverLogPosition = newServerPosition;
                        OnProgress($"    Server output for stage {stage}: {newServerOutput.Length} chars (position: {serverLogPosition})");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read server log for stage {stage}: {ex.Message}");
                }
                
                // Read new client output for this stage
                try
                {
                    var (newClientOutput, newClientPosition) = ReadFileFromContainerIncremental(
                        unifiedContainer, 
                        "/apps/client/client.log", 
                        clientLogPosition);
                    
                    if (!string.IsNullOrEmpty(newClientOutput))
                    {
                        clientOutputs[stage] = newClientOutput;
                        clientLogPosition = newClientPosition;
                        OnProgress($"    Client output for stage {stage}: {newClientOutput.Length} chars (position: {clientLogPosition})");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read client log for stage {stage}: {ex.Message}");
                }
                
                // LIVE GRADING: Parse network packets for current stage
                // This enables per-stage validation (all-or-nothing grading strategy)
                OnProgress($"[NetworkMonitor] Stage {stage}: _networkMonitor={(_networkMonitor == null ? "null" : "not-null")}, _currentPcapFilePath={_currentPcapFilePath ?? "null"}");
                if (_networkMonitor == null && !string.IsNullOrEmpty(_currentPcapFilePath))
                {
                    // Using sidecar pattern - parse pcap file for this stage
                    await ParsePcapForCurrentStageAsync(stage, config.CodeContainerInternalPort);
                }
                else
                {
                    OnProgress($"[NetworkMonitor] Stage {stage}: Skipping parse - using legacy network monitor or no pcap path");
                }
                
                await Task.Delay(10);  // Brief delay between actions
            }
            
            // Store outputs for later export
            _lastTestCaseClientOutputs = new Dictionary<int, string>(clientOutputs);
            _lastTestCaseServerOutputs = new Dictionary<int, string>(serverOutputs);
            
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
            
            // CRITICAL FIX: Positional/Sequential matching within each stage
            // Network flow order matters! Must match flow-by-flow in sequence.
            // Expected flow[0] must match Captured flow[0], not just "any flow with matching flags"
            // This catches errors like "Server closes connection before Client" which violates protocol.
            //
            // Group expected flows by stage to handle per-stage sequential matching
            var expectedByStage = expected.GroupBy(e => e.Stage).ToDictionary(g => g.Key, g => g.ToList());
            var capturedByStage = allCapturedPackets.GroupBy(p => p.Stage).ToDictionary(g => g.Key, g => g.ToList());
            
            foreach (var exp in expected)
            {
                // Get all flows for this stage (both expected and captured)
                var expectedFlowsForStage = expectedByStage[exp.Stage];
                var capturedFlowsForStage = capturedByStage.ContainsKey(exp.Stage) 
                    ? capturedByStage[exp.Stage] 
                    : new List<CapturedNetworkPacket>();
                
                // Find position of this expected flow within its stage
                var positionInStage = expectedFlowsForStage.IndexOf(exp);
                
                // SEQUENTIAL MATCHING: Match by position within stage
                // If we expect the 3rd flow in stage 5, we check the 3rd captured flow in stage 5
                CapturedNetworkPacket? matchingPacket = null;
                if (positionInStage >= 0 && positionInStage < capturedFlowsForStage.Count)
                {
                    matchingPacket = capturedFlowsForStage[positionInStage];
                }
                
                if (matchingPacket != null)
                {
                    // Check if it's an exact match (PASS) or partial match (PARTIAL)
                    bool exactMatch = true;
                    var mismatchReasons = new List<string>();
                    
                    // Compare flags using set comparison (already matched in FirstOrDefault above)
                    // This is redundant but kept for clarity
                    if (!string.IsNullOrEmpty(exp.Flags) && !FlagsMatch(exp.Flags, matchingPacket.Flags))
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"flags: expected '{exp.Flags}' but got '{matchingPacket.Flags}'");
                    }
                    
                    // Compare roles exactly
                    if (!string.IsNullOrEmpty(exp.SourceRole) && matchingPacket.SourceRole != exp.SourceRole)
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"source role: expected '{exp.SourceRole}' but got '{matchingPacket.SourceRole}'");
                    }
                    if (!string.IsNullOrEmpty(exp.DestinationRole) && matchingPacket.DestinationRole != exp.DestinationRole)
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"dest role: expected '{exp.DestinationRole}' but got '{matchingPacket.DestinationRole}'");
                    }
                    
                    // Compare Data payload if expected data is provided (for TCP)
                    // Note: Expected data from Excel uses null or empty string to indicate "no data expected"
                    // We need to check if exp.Data is not null/empty AND not the string "None" (which Excel uses for null)
                    if (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        var actualData = matchingPacket.Data ?? "";
                        var expectedData = exp.Data;
                        
                        // Compare data - trim whitespace and use case-insensitive comparison for now
                        // TODO: Make this configurable per test case if needed
                        if (!actualData.Trim().Equals(expectedData.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            var expPreview = expectedData.Length > 50 ? expectedData.Substring(0, 50) + "..." : expectedData;
                            var actPreview = actualData.Length > 50 ? actualData.Substring(0, 50) + "..." : actualData;
                            mismatchReasons.Add($"data: expected '{expPreview}' but got '{actPreview}'");
                        }
                    }
                    
                    // Compare HTTP-specific fields if expected (for HTTP protocol)
                    if (!string.IsNullOrEmpty(exp.URI))
                    {
                        var actualURI = matchingPacket.URI ?? "";
                        if (!actualURI.Equals(exp.URI, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"URI: expected '{exp.URI}' but got '{actualURI}'");
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(exp.Method))
                    {
                        var actualMethod = matchingPacket.Method ?? "";
                        if (!actualMethod.Equals(exp.Method, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"Method: expected '{exp.Method}' but got '{actualMethod}'");
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(exp.Status))
                    {
                        var actualStatus = matchingPacket.Status ?? "";
                        // Use StartsWith for status code matching to avoid false positives
                        // e.g., expected "200" matches "200 OK" but not "404" or "520"
                        if (!actualStatus.StartsWith(exp.Status, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"Status: expected '{exp.Status}' but got '{actualStatus}'");
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(exp.HttpVersion))
                    {
                        var actualHttpVersion = matchingPacket.HttpVersion ?? "";
                        if (!actualHttpVersion.Equals(exp.HttpVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"HttpVersion: expected '{exp.HttpVersion}' but got '{actualHttpVersion}'");
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(exp.HttpBody))
                    {
                        var actualHttpBody = matchingPacket.HttpBody ?? "";
                        if (!actualHttpBody.Trim().Equals(exp.HttpBody.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            var expPreview = exp.HttpBody.Length > 50 ? exp.HttpBody.Substring(0, 50) + "..." : exp.HttpBody;
                            var actPreview = actualHttpBody.Length > 50 ? actualHttpBody.Substring(0, 50) + "..." : actualHttpBody;
                            mismatchReasons.Add($"HttpBody: expected '{expPreview}' but got '{actPreview}'");
                        }
                    }
                    
                    // Log detailed comparison
                    if (exactMatch)
                    {
                        var dataInfo = (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals("None", StringComparison.OrdinalIgnoreCase)) 
                            ? $" with data='{exp.Data}'" : "";
                        OnProgress($"[COMPARISON] ✓ PASS - Stage {exp.Stage}: {exp.Flags} from {exp.SourceRole} to {exp.DestinationRole}{dataInfo}");
                    }
                    else
                    {
                        OnProgress($"[COMPARISON] ✗ FAIL - Stage {exp.Stage}: {string.Join(", ", mismatchReasons)}");
                    }
                    
                    var expectedStr = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}";
                    var actualStr = $"Flags={matchingPacket.Flags}, From={matchingPacket.SourceRole}, To={matchingPacket.DestinationRole}";
                    
                    if (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        expectedStr += $", Data={exp.Data}";
                        actualStr += $", Data={matchingPacket.Data ?? "(empty)"}";
                    }
                    
                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = expectedStr,
                        Actual = actualStr,
                        Passed = exactMatch
                    });
                }
                else
                {
                    // No matching packet found - FAIL
                    OnProgress($"[COMPARISON] ✗ FAIL - Stage {exp.Stage}: MISSING {exp.Flags} from {exp.SourceRole} to {exp.DestinationRole} (position {positionInStage}) - Captured: {(capturedFlowsForStage.Any() ? string.Join(", ", capturedFlowsForStage.Select(p => $"{p.Flags}({p.SourceRole}→{p.DestinationRole})")) : "none")}");
                    
                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}",
                        Actual = capturedFlowsForStage.Any() ? string.Join("; ", capturedFlowsForStage.Select(p => p.Flags)) : "(no captures)",
                        Passed = false
                    });
                }
            }
            
            // Summary of comparison results
            int passCount = results.Count(r => r.Passed);
            int failCount = results.Count(r => !r.Passed);
            
            OnProgress($"[COMPARISON] RESULTS: {results.Count} total - PASS={passCount}, FAIL={failCount}");
            if (failCount > 0)
            {
                OnProgress($"[COMPARISON] WARNING: {failCount} network flows FAILED - test will FAIL (ALL-OR-NOTHING)");
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
            
            // CRITICAL FIX: Replace hyphens with commas so tcpdump format matches Excel format
            // tcpdump outputs: "SYN-ACK", "PSH-ACK", "FIN-ACK"
            // Excel expects: "SYN, ACK", "PSH, ACK", "FIN, ACK"
            flags = flags.Replace("-", ", ");
            
            var flagList = flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .OrderBy(f => f)
                .ToList();
            
            return string.Join(", ", flagList);
        }
        
        /// <summary>
        /// Parse flags string into a HashSet of individual flags for comparison
        /// Handles both comma-separated (Excel) and hyphen-separated (tcpdump) formats
        /// </summary>
        private static HashSet<string> ParseFlagsToSet(string flags)
        {
            if (string.IsNullOrWhiteSpace(flags))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Replace hyphens with commas to handle both formats
            flags = flags.Replace("-", ",");
            
            return flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Compare two flag strings as sets (order-independent, format-independent)
        /// Returns true if both contain the same flags, false otherwise
        /// </summary>
        private static bool FlagsMatch(string flags1, string flags2)
        {
            var set1 = ParseFlagsToSet(flags1);
            var set2 = ParseFlagsToSet(flags2);
            
            return set1.SetEquals(set2);
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
                        {
                            tkConfig.Protocol = value ?? "TCP";
                        }
                        else if (key?.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // Store Grade_Content from test kit root Header.xlsx
                            // This determines whether students submit Server, Client, or Both
                            tkConfig.DefaultGradeContent = value ?? "Client/Server";
                            OnProgress($"[TestKit] Root Header.xlsx Grade_Content: {tkConfig.DefaultGradeContent}");
                        }
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
            // CRITICAL: Do NOT override with allocated port - all students use same internal port (4000)
            // Docker containers are isolated, so there's no port conflict
            // The allocated port is no longer needed since we removed port allocation logic
            OnProgress($"[Port Config] LoadTestKitConfig - TestKit default: tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}");
            OnProgress($"[Port Config] LoadTestKitConfig - Allocated port (IGNORED): config.CodeContainerInternalPort={config.CodeContainerInternalPort}");
            
            // Do NOT override - keep the testkit default (4000)
            // if (config.CodeContainerInternalPort > 0)
            //     tkConfig.CodeContainerInternalPort = config.CodeContainerInternalPort;
            // if (config.CodeContainerHostPort > 0)
            //     tkConfig.CodeContainerHostPort = config.CodeContainerHostPort;
            
            OnProgress($"[Port Config] LoadTestKitConfig - Final (using testkit default): tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}");
            
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
            catch
            {
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
                    var data = row.Cell(8).GetValue<string>();  // Column H: Data payload (for TCP)
                    var sourceRole = row.Cell(9).GetValue<string>();
                    var destRole = row.Cell(10).GetValue<string>();
                    
                    // HTTP-specific fields (columns 11-15, if present)
                    // For HTTP protocol: URI, Method, Status, HttpVersion, HttpBody
                    var uri = row.Cell(11).GetValue<string>();
                    var method = row.Cell(12).GetValue<string>();
                    var status = row.Cell(13).GetValue<string>();
                    var httpVersion = row.Cell(14).GetValue<string>();
                    var httpBody = row.Cell(15).GetValue<string>();
                    
                    if (int.TryParse(stageStr, out var stage))
                    {
                        flows.Add(new ExpectedNetworkFlow
                        {
                            Stage = stage,
                            Flags = flags,
                            State = state,
                            Data = data,
                            SourceRole = sourceRole,
                            DestinationRole = destRole,
                            // HTTP fields
                            URI = uri,
                            Method = method,
                            Status = status,
                            HttpVersion = httpVersion,
                            HttpBody = httpBody
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
            OnProgress($"[SETUP] Creating network monitor sidecar: {monitorContainer}");
            
            // === CRITICAL: Save monitor container name to class field ===
            _currentMonitorContainer = monitorContainer;
            _currentPcapFilePath = pcapOutputPath;
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
            
            // Create directory for pcap output on host
            var outputDir = Path.GetDirectoryName(pcapOutputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                // CRITICAL: Convert to absolute path for Docker volume mount
                outputDir = Path.GetFullPath(outputDir);
            }
            
            // Extract the filename from the full path
            var pcapFileName = Path.GetFileName(pcapOutputPath);
            
            // Build the docker run command for network monitor sidecar
            // CRITICAL: 
            // - Use --net=container:{unifiedContainer} to attach to student's network namespace
            // - Use --cap-add=NET_ADMIN and --cap-add=NET_RAW for tcpdump permissions
            // - Capture on loopback interface (-i lo) to catch localhost traffic
            // - Write to /data/{pcapFileName} inside container (bind-mounted to host)
            //
            // The network-monitor image uses ENTRYPOINT ["tcpdump"] with CMD ["-i", "lo", "-U", "-w", "capture.pcap"]
            // We override the -w argument to use our custom filename
            
            // CRITICAL: Capture on loopback interface for localhost traffic
            // Client and server communicate via localhost (127.0.0.1) in unified container
            // Using -i lo is less noisy than -i any (which captures eth0 traffic too)
            var dockerCmd = $"docker run -d --name {monitorContainer} " +
                           $"--net=container:{unifiedContainer} " +  // SIDECAR: Attach to student container
                           $"--cap-add=NET_ADMIN " +                 // Required for tcpdump
                           $"--cap-add=NET_RAW " +                   // Required for raw packet capture
                           $"-v \"{outputDir}:/data\" " +            // Mount host directory for pcap output
                           $"fptuxaes/network-monitor:latest " +     // Debian + tcpdump image with ENTRYPOINT
                           $"-i lo -n -U -w /data/{pcapFileName}";  // CRITICAL: -i lo for loopback, NO -v (breaks parser with multi-line format)
            
            OnProgress($"[Monitor] Command: {dockerCmd}");
            OnProgress($"[Monitor] Capturing on loopback (lo) interface - localhost traffic only (less noisy than -i any)");
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
            
            // Determine protocol type once for the entire Network sheet
            var protocol = _currentTestKitProtocol ?? "TCP";
            bool isHttpProtocol = protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase);
            
            // === SECTION 1: EXPECTED Network Flows (from TestKit) ===
            // Show ALL expected flows with their matching actual captures
            if (expectedNetworkFlows.Count > 0)
            {
                OnProgress($"[Network Sheet] Writing {expectedNetworkFlows.Count} expected network flows...");
                
                foreach (var expectedFlow in expectedNetworkFlows.OrderBy(f => f.Stage))
                {
                    int col = 1;
                    
                    // Common columns for both TCP and HTTP
                    netWs.Cell(netRow, col++).Value = expectedFlow.Stage;  // Stage
                    netWs.Cell(netRow, col++).Value = "";  // Time (from testkit - not always available)
                    netWs.Cell(netRow, col++).Value = protocol;  // Info (TCP or HTTP)
                    netWs.Cell(netRow, col++).Value = "";  // Source (IP from testkit if available)
                    netWs.Cell(netRow, col++).Value = "";  // Destination (IP from testkit if available)
                    netWs.Cell(netRow, col++).Value = expectedFlow.Flags ?? "";  // Flags
                    netWs.Cell(netRow, col++).Value = expectedFlow.State ?? "";  // State
                    
                    if (isHttpProtocol)
                    {
                        // HTTP-specific expected columns
                        netWs.Cell(netRow, col++).Value = expectedFlow.URI ?? "";  // URI
                        netWs.Cell(netRow, col++).Value = expectedFlow.Method ?? "";  // Method
                        netWs.Cell(netRow, col++).Value = expectedFlow.Status ?? "";  // Status
                        netWs.Cell(netRow, col++).Value = expectedFlow.HttpVersion ?? "";  // HttpVersion
                        netWs.Cell(netRow, col++).Value = expectedFlow.HttpBody ?? "";  // HttpBody
                        netWs.Cell(netRow, col++).Value = expectedFlow.SourceRole ?? "";  // SourceRole
                        netWs.Cell(netRow, col++).Value = expectedFlow.DestinationRole ?? "";  // DestinationRole
                    }
                    else
                    {
                        // TCP-specific expected columns
                        netWs.Cell(netRow, col++).Value = expectedFlow.Data ?? "";  // Data
                        netWs.Cell(netRow, col++).Value = expectedFlow.SourceRole ?? "";  // SourceRole
                        netWs.Cell(netRow, col++).Value = expectedFlow.DestinationRole ?? "";  // DestinationRole
                    }
                    
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
                        // Found matching packet - write actual data
                        // Column index continues from where expected columns ended
                        netWs.Cell(netRow, col++).Value = matchingPacket.Flags;  // ActualFlags
                        netWs.Cell(netRow, col++).Value = matchingPacket.State;  // ActualState
                        
                        if (isHttpProtocol)
                        {
                            // HTTP-specific actual columns
                            netWs.Cell(netRow, col++).Value = matchingPacket.URI ?? "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = matchingPacket.Method ?? "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = matchingPacket.Status ?? "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = matchingPacket.HttpVersion ?? "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = matchingPacket.HttpBody ?? "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = matchingPacket.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.DestinationRole;  // ActualDestRole
                        }
                        else
                        {
                            // TCP-specific actual columns
                            netWs.Cell(netRow, col++).Value = matchingPacket.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.DestinationRole;  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.Data ?? "";  // ActualData
                        }
                        
                        // Port columns (common for both protocols)
                        netWs.Cell(netRow, col++).Value = matchingPacket.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = matchingPacket.DestinationPort;  // ActualDestPort
                        
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
                        
                        // Compare protocol-specific fields
                        if (isHttpProtocol)
                        {
                            if (!string.IsNullOrEmpty(expectedFlow.URI) && matchingPacket.URI != expectedFlow.URI)
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.Method) && matchingPacket.Method != expectedFlow.Method)
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.Status) && !(matchingPacket.Status ?? "").StartsWith(expectedFlow.Status, StringComparison.OrdinalIgnoreCase))
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.HttpBody) && matchingPacket.HttpBody != expectedFlow.HttpBody)
                                exactMatch = false;
                        }
                        else
                        {
                            // TCP: Compare Data field
                            if (!string.IsNullOrEmpty(expectedFlow.Data) && matchingPacket.Data != expectedFlow.Data)
                                exactMatch = false;
                        }
                        
                        netWs.Cell(netRow, col).Value = exactMatch ? "PASS" : "PARTIAL";
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = exactMatch ? XLColor.LightGreen : XLColor.Yellow;
                        
                        // Remove from list so we can identify extra packets later
                        actualPacketsForStage.Remove(matchingPacket);
                    }
                    else
                    {
                        // No matching packet found - expected flow is MISSING
                        // Fill in empty actual columns
                        netWs.Cell(netRow, col++).Value = "(MISSING - not captured)";  // ActualFlags
                        netWs.Cell(netRow, col++).Value = "";  // ActualState
                        
                        if (isHttpProtocol)
                        {
                            // HTTP: Empty actual columns
                            netWs.Cell(netRow, col++).Value = "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = "";  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualDestRole
                        }
                        else
                        {
                            // TCP: Empty actual columns
                            netWs.Cell(netRow, col++).Value = "";  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualData
                        }
                        
                        netWs.Cell(netRow, col++).Value = "";  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = "";  // ActualDestPort
                        netWs.Cell(netRow, col).Value = "FAIL";
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = XLColor.LightPink;
                        
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
                        int col = 1;
                        
                        // Common columns
                        netWs.Cell(netRow, col++).Value = packet.Stage;  // Stage
                        netWs.Cell(netRow, col++).Value = "(Not validated by this test case)";  // Time
                        
                        // Leave expected columns empty (Info, Source, Destination, Flags, State, protocol fields, roles)
                        int expectedColumnCount = isHttpProtocol ? 12 : 8;  // HTTP has more columns
                        for (int i = 0; i < expectedColumnCount; i++) 
                            netWs.Cell(netRow, col++).Value = "";
                        
                        // Write actual packet data
                        netWs.Cell(netRow, col++).Value = packet.Flags;  // ActualFlags
                        netWs.Cell(netRow, col++).Value = packet.State;  // ActualState
                        
                        if (isHttpProtocol)
                        {
                            // HTTP-specific actual columns
                            netWs.Cell(netRow, col++).Value = packet.URI ?? "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = packet.Method ?? "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = packet.Status ?? "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = packet.HttpVersion ?? "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = packet.HttpBody ?? "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = packet.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = packet.DestinationRole;  // ActualDestRole
                        }
                        else
                        {
                            // TCP-specific actual columns
                            netWs.Cell(netRow, col++).Value = packet.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = packet.DestinationRole;  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = packet.Data ?? "";  // ActualData
                        }
                        
                        // Port columns (common)
                        netWs.Cell(netRow, col++).Value = packet.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = packet.DestinationPort;  // ActualDestPort
                        
                        netWs.Cell(netRow, col).Value = "INFO";  // Informational - not validated
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = XLColor.LightGray;
                        
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
        
        /// <summary>
        /// Set Network sheet headers dynamically based on protocol (TCP or HTTP).
        /// TCP: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, Actual*, NetworkResult
        /// HTTP: Same as TCP plus URI, Method, Status, HttpVersion, HttpBody columns
        /// </summary>
        private void SetNetworkSheetHeaders(IXLWorksheet ws)
        {
            // Network sheet format matching ExcelDetailLogService naming convention (NO underscores).
            // Format: Stage, expected columns (Time, Flags, etc.), then Actual* columns, then NetworkResult.
            // CRITICAL FIX: Added SourcePort and DestPort columns for debugging network traffic
            // This ensures consistency across both Docker and regular grading flows.
            
            var protocol = _currentTestKitProtocol ?? "TCP";
            
            if (protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP protocol: Include HTTP-specific fields
                var headers = new[] { 
                    "Stage", "Time", "Info", "Source", "Destination", 
                    "Flags", "State", "URI", "Method", "Status", "HttpVersion", "HttpBody", "SourceRole", "DestinationRole",
                    "ActualFlags", "ActualState", "ActualURI", "ActualMethod", "ActualStatus", "ActualHttpVersion", "ActualHttpBody",
                    "ActualSourceRole", "ActualDestRole", "ActualSourcePort", "ActualDestPort",
                    "NetworkResult"
                };
                for (int i = 0; i < headers.Length; i++)
                    ws.Cell(1, i + 1).Value = headers[i];
            }
            else
            {
                // TCP protocol: Traditional format with Data field
                var headers = new[] { 
                    "Stage", "Time", "Info", "Source", "Destination", 
                    "Flags", "State", "Data", "SourceRole", "DestinationRole",
                    "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData",
                    "ActualSourcePort", "ActualDestPort",
                    "NetworkResult"
                };
                for (int i = 0; i < headers.Length; i++)
                    ws.Cell(1, i + 1).Value = headers[i];
            }
            
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
        
        /// <summary>
        /// Move per-stage PCAP snapshots from student root to TC-specific folder.
        /// This organizes network captures per test case for easier debugging.
        /// Format: snapshot_TC3_stage1.pcap -> TC3/snapshot_TC3_stage1.pcap
        /// </summary>
        private void MoveSnapshotsToTCFolder(string studentResultPath, string tcResultPath, string testCaseName)
        {
            try
            {
                // Find all snapshot files for this test case
                var snapshotPattern = $"snapshot_{testCaseName}_stage*.pcap";
                var snapshotFiles = Directory.GetFiles(studentResultPath, snapshotPattern, SearchOption.TopDirectoryOnly);
                
                if (snapshotFiles.Length > 0)
                {
                    OnProgress($"[TC Organization] Moving {snapshotFiles.Length} snapshot files to {testCaseName} folder...");
                    
                    foreach (var snapshotFile in snapshotFiles)
                    {
                        var fileName = Path.GetFileName(snapshotFile);
                        var destPath = Path.Combine(tcResultPath, fileName);
                        
                        try
                        {
                            // Move (not copy) to avoid duplication
                            File.Move(snapshotFile, destPath, overwrite: true);
                            OnProgress($"[TC Organization] Moved {fileName} to {testCaseName}/");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[TC Organization] WARNING: Failed to move {fileName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[TC Organization] WARNING: Failed to move snapshots for {testCaseName}: {ex.Message}");
            }
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
            
            // CRITICAL: Include test case name in snapshot path for per-TC organization
            // Format: snapshot_TC3_stage1.pcap instead of snapshot_stage1.pcap
            // This prevents overwriting between test cases and organizes artifacts per TC
            var testCasePrefix = !string.IsNullOrEmpty(_currentTestCaseName) ? $"{_currentTestCaseName}_" : "";
            var snapshotPath = Path.Combine(Path.GetDirectoryName(_currentPcapFilePath) ?? "", $"snapshot_{testCasePrefix}stage{currentStage}.pcap");
            
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
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot downloaded ({fileSize} bytes), parsing with SharpPcap");
                
                // SNAPSHOT STRATEGY Step 3: Parse the snapshot using SharpPcap
                // This is more robust than tcpdump text parsing - works cross-platform
                // Pass the protocol type from testkit configuration (TCP or HTTP)
                var protocol = _currentTestKitProtocol ?? "TCP";
                var packets = _sharpPcapParser.ParsePcapFile(snapshotPath, currentStage, port, protocol);
                
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Parsed {packets.Count} total packets");
                
                // Skip packets we've already processed (cumulative parsing)
                var newPackets = packets.Skip(_lastParsedPacketCount).ToList();
                
                foreach (var packet in newPackets)
                {
                    try
                    {
                        // Add to RunContext for this stage
                        var studentCode = _currentStudentCode ?? "";
                        OnProgress($"[NetworkMonitor] DEBUG: Adding packet to RunContext - StudentCode='{studentCode}', Stage={currentStage}, Flags={packet.Flags}, Data={packet.Data ?? "(empty)"}");
                        _runContext.AddCapturedNetworkPacket(studentCode, currentStage.ToString(), packet);
                        
                        // Verify it was added
                        var allPackets = _runContext.GetAllCapturedNetworkPackets();
                        OnProgress($"[NetworkMonitor] DEBUG: After adding, total packets in RunContext: {allPackets.Count}");
                    }
                    catch (Exception ex)
                    {
                        // Log the exception instead of silently swallowing it
                        OnProgress($"[NETWORK] ERROR adding packet: {ex.Message}");
                        continue;
                    }
                }
                
                // Update counter to skip these packets next time
                _lastParsedPacketCount = packets.Count;
                
                // CRITICAL FIX: DO NOT truncate PCAP file between stages!
                // Previous approach: Truncate PCAP after each stage to isolate flows
                // Problem: tcpdump keeps file open with buffering - truncation doesn't prevent buffered data from being written
                // Result: Next stage sees old data from previous stage (e.g., TC4 Stage 1 sees TC3 Stage 3 data)
                //
                // Current approach: Let PCAP accumulate ALL packets, use _lastParsedPacketCount to track progress
                // - PCAP grows throughout test case execution (all stages append to same file)
                // - Each ParsePcapForCurrentStageAsync call only processes NEW packets (skip _lastParsedPacketCount)
                // - RunContext is cleared between test cases (line 514) to prevent cross-contamination
                // - Snapshot files are saved per-stage for debugging and per-TC organization
                //
                // This ensures:
                // 1. No data loss from tcpdump buffering
                // 2. Correct stage-to-packet mapping (packets tagged with actual execution stage)
                // 3. Clean separation between test cases (RunContext cleared)
                // 4. Proper cumulative parsing within each test case
                
                OnProgress($"[NETWORK] Parsed {newPackets.Count} new packets for stage {currentStage}, cumulative total: {packets.Count} (cumulative parsing - RunContext cleared between test cases)");
            }
            catch (Exception ex)
            {
                OnProgress($"[NETWORK] Error parsing pcap for stage {currentStage}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Current packet being parsed (for multi-line tcpdump -A output).
        /// When tcpdump uses -A flag, payload appears on lines after the header line.
        /// </summary>
        private CapturedNetworkPacket? _currentParsingPacket = null;
        private StringBuilder _currentPayloadBuffer = new StringBuilder();
        
        /// <summary>
        /// Parse a single tcpdump output line into CapturedNetworkPacket.
        /// With -A flag, tcpdump outputs:
        /// Line 1: "2024-12-08 11:08:03.543348 IP 127.0.0.1.47044 > 127.0.0.1.4000: Flags [P.], seq 1:5, ack 1, win 512, length 4"
        /// Line 2+: ASCII payload data (hex offset + printable chars)
        /// Example payload lines:
        ///   0x0000:  4500 0038 ...   E..8...
        ///   0x0010:  ... S123       (actual data)
        /// </summary>
        private CapturedNetworkPacket? ParseTcpdumpLine(string line, int stage, int expectedPort)
        {
            // Check if this is a payload line (hex dump format from -A flag)
            // Payload lines start with spaces/tabs followed by 0x or just hex data
            // Example: "	0x0000:  4500 0038 ..." or data continuation lines
            if (line.TrimStart().StartsWith("0x") || (line.StartsWith("\t") || line.StartsWith(" ")) && !line.Contains(" IP "))
            {
                // This is a payload line for the current packet
                if (_currentParsingPacket != null)
                {
                    // Extract ASCII data from the hex dump line
                    // Format: "	0x0000:  4500 0038 ...  E..8...S123" 
                    // We want the part after the hex bytes (the ASCII representation)
                    var parts = line.Split(new[] { "  " }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        // Last part typically contains ASCII representation
                        var asciiPart = parts[parts.Length - 1].Trim();
                        // Filter out non-printable characters but keep readable text
                        var readable = new string(asciiPart.Where(c => c >= 32 && c < 127).ToArray());
                        if (!string.IsNullOrWhiteSpace(readable))
                        {
                            _currentPayloadBuffer.Append(readable);
                        }
                    }
                }
                return null; // Don't return yet, still collecting payload
            }
            
            // If we were parsing a packet and hit a new header line, finalize the previous packet
            CapturedNetworkPacket? completedPacket = null;
            if (_currentParsingPacket != null)
            {
                // Finalize the previous packet with collected payload
                var collectedPayload = _currentPayloadBuffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(collectedPayload))
                {
                    _currentParsingPacket.Data = collectedPayload;
                }
                completedPacket = _currentParsingPacket;
                _currentParsingPacket = null;
                _currentPayloadBuffer.Clear();
            }
            
            // Now parse the new header line
            // Extract timestamp
            var timestampMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            if (!timestampMatch.Success)
            {
                // Not a header line, return the completed packet if any
                return completedPacket;
            }
            
            DateTime timestamp = DateTime.TryParse(timestampMatch.Groups[1].Value, out var dt) 
                ? dt 
                : DateTime.Now;
            
            // Extract source and destination: "IP 127.0.0.1.47044 > 127.0.0.1.4000:"
            var addressMatch = System.Text.RegularExpressions.Regex.Match(line, @"IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+)");
            if (!addressMatch.Success)
            {
                return completedPacket;
            }
            
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
                // Not related to our expected port, return completed packet
                return completedPacket;
            }
            
            // Extract flags: [S] = SYN, [S.] = SYN-ACK, [.] = ACK, [P.] = PSH-ACK, [F.] = FIN-ACK, [R] = RST, [R.] = RST-ACK
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
            else if (line.Contains("Flags [.]") && !line.Contains("Flags [P.]") && !line.Contains("Flags [F.]") && !line.Contains("Flags [R.]"))
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
            else if (line.Contains("Flags [R.]"))
            {
                // RST+ACK - server rejecting connection
                flags = "RST-ACK";
                state = "RESET";
            }
            else if (line.Contains("Flags [R]"))
            {
                // RST only
                flags = "RST";
                state = "RESET";
            }
            
            // Extract payload length (for logging/debugging)
            var lengthMatch = System.Text.RegularExpressions.Regex.Match(line, @"length (\d+)");
            int payloadLength = lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value) : 0;
            
            // Create new packet for this header line
            var newPacket = new CapturedNetworkPacket
            {
                Stage = stage,
                Timestamp = timestamp,
                Flags = flags,
                State = state,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Data = "", // Will be filled by subsequent payload lines or left empty
                SourcePort = srcPort,
                DestinationPort = dstPort
            };
            
            // If this packet has payload, start collecting it
            if (payloadLength > 0)
            {
                _currentParsingPacket = newPacket;
                _currentPayloadBuffer.Clear();
                // Return the completed previous packet if any
                return completedPacket;
            }
            else
            {
                // No payload, return this packet immediately (and the completed one if exists)
                // If there was a previous packet, we need to handle it
                if (completedPacket != null)
                {
                    // We can only return one packet at a time, so store the new one for next call
                    _currentParsingPacket = newPacket;
                    return completedPacket;
                }
                return newPacket;
            }
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
    
    #region Internal Model Classes
    
    // TestKitConfig and TestCaseInfo moved to Lib/SolutionGrader.Core/Domain/Models/TestKitConfig.cs
    // to eliminate duplication between CLI and UI implementations
    
    /// <summary>
    /// Represents expected network flow data read from Detail.xlsx.
    /// Supports both TCP and HTTP protocols.
    /// For TCP: Validates Flags, State, Data, SourceRole, DestinationRole
    /// For HTTP: Validates Flags, State, URI, Method, Status, HttpVersion, HttpBody, SourceRole, DestinationRole
    /// </summary>
    public class ExpectedNetworkFlow
    {
        public int Stage { get; set; }
        
        // Common fields (TCP and HTTP)
        public string? Flags { get; set; }
        public string? State { get; set; }
        public string? SourceRole { get; set; }
        public string? DestinationRole { get; set; }
        public string? Data { get; set; }  // For TCP payload data
        
        // HTTP-specific fields
        public string? URI { get; set; }
        public string? Method { get; set; }
        public string? Status { get; set; }
        public string? HttpVersion { get; set; }
        public string? HttpBody { get; set; }
    }
    
    #endregion
}
