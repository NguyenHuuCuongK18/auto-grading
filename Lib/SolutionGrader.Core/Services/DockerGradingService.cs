using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EnvironmentBuilder.DockerCommand;
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
        private readonly DockerConsoleManager _consoleManager;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IRunContext _runContext;
        private string? _currentStudentCode; // Track current student for logging
        
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
            Console.WriteLine("[Docker] Disposing all containers...");
            
            var databaseContainer = config.DatabaseContainerName;
            var serverContainer = $"server-{databaseContainer}";
            var clientContainer = $"client-{databaseContainer}";
            
            // Remove code containers
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }
            
            // Remove database container
            try { _dockerExecutor.RemoveContainer(databaseContainer); } catch { }
            
            Console.WriteLine("[Docker] All containers disposed");
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
            
            // Container names
            var serverContainer = $"ag-server-{studentCode}";
            var clientContainer = $"ag-client-{studentCode}";
            
            try
            {
                OnProgress($"Loading test kit configuration from {testKitPath}...");
                var testKitConfig = LoadTestKitConfig(testKitPath, config);
                result.MaxMark = testKitConfig.TotalMaxMark;
                
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
                if (config.HasServer)
                {
                    // Examiner expects student to provide server - use discovered path
                    actualServerDllPath = serverDllPath;
                    if (string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"WARNING: Student should provide server ({config.ServerProjectName}) but none found!");
                    }
                    else
                    {
                        OnProgress($"Discovered student's server: {Path.GetFileName(actualServerDllPath)}");
                    }
                }
                else
                {
                    // Examiner doesn't expect student to provide server, prepare golden server from test kit
                    actualServerDllPath = testKitConfig.GivenServerPath;
                    if (!string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"Prepared golden server from Meta/Given/Server: {Path.GetFileName(actualServerDllPath)}");
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
                
                // CRITICAL: Start network monitor FIRST before ANY containers or processes
                // NetworkMonitor runs on HOST and sniffs localhost:{hostPort}
                // It MUST start before containers to capture the full network traffic including:
                // - Initial TCP handshake (SYN, SYN-ACK, ACK)
                // - All data transfers
                // - Connection teardown (FIN-ACK)
                // 
                // PARALLEL GRADING: Each student gets their own NetworkMonitorService instance
                // configured with their specific port (basePort + portOffset) to avoid conflicts
                if (_networkMonitor != null)
                {
                    _networkMonitor.MonitorPort = config.CodeContainerHostPort;
                    _networkMonitor.ProtocolType = testKitConfig.Protocol;
                    Console.WriteLine($"[NetworkMonitor] Starting monitor for student {studentCode} on host port {config.CodeContainerHostPort} (protocol: {testKitConfig.Protocol})");
                    await _networkMonitor.StartAsync(ct);
                    OnProgress($"Network monitor started on host port {config.CodeContainerHostPort} - capturing all traffic");
                    Console.WriteLine($"[NetworkMonitor] Monitor active for student {studentCode} - ready to capture packets");
                }
                else
                {
                    Console.WriteLine($"[NetworkMonitor] WARNING: NetworkMonitor is NULL for student {studentCode} - network traffic will NOT be captured!");
                }
                
                OnProgress($"Setting up Docker containers for {studentCode}...");
                await SetupContainersAsync(actualServerDllPath, actualClientDllPath, config, testKitConfig, serverContainer, clientContainer);
                
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
                            Console.WriteLine($"[TestCase {testCase.Name}] Grade_Content='Client' -> Using golden server + student client");
                        }
                        else if (testCase.GradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                        {
                            // Grading server implementation -> use golden (given) client
                            serverPath = actualServerDllPath;
                            clientPath = testKitConfig.GivenClientPath;
                            Console.WriteLine($"[TestCase {testCase.Name}] Grade_Content='Server' -> Using student server + golden client");
                        }
                    }
                    
                    // For subsequent test cases, cleanup before re-copying
                    if (!isFirstTestCase)
                    {
                        // Cleanup between test cases (kills processes, removes log files)
                        await CleanupBetweenTestCasesAsync(serverContainer, clientContainer, config.CodeContainerHostPort);
                    }
                    else
                    {
                        // CRITICAL FIX for TC1: Network monitor needs time to initialize before first test case
                        // Without this delay, network monitor may not be ready to capture packets for TC1
                        Console.WriteLine("[TC1 Fix] Waiting 3 seconds for network monitor to fully initialize...");
                        await Task.Delay(3000);
                    }
                    
                    // Copy files to containers (will overwrite existing files)
                    OnProgress($"Copying files for test case {testCase.Name}...");
                    await CopyFilesToContainersAsync(serverPath, clientPath, serverContainer, clientContainer, config);
                    
                    // Generate appsettings.json in containers
                    GenerateAppsettingsInContainers(serverPath, clientPath, config, testKitConfig, serverContainer, clientContainer);
                    
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
                            serverContainer, clientContainer, testCaseCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Test case timed out (not overall cancellation)
                        OnProgress($"Test case {testCase.Name} timed out after {testCaseTimeout}s");
                        tcResult = new TestCaseResult
                        {
                            TestCaseName = testCase.Name,
                            MaxMark = testCase.MaxMark,
                            EarnedMark = 0,
                            Passed = false,
                            ErrorMessage = $"Test case timed out after {testCaseTimeout} seconds"
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
                result.Passed = result.TestCaseResults.Any(tc => tc.Passed);
                
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
                // Stop network monitor
                if (_networkMonitor != null)
                {
                    Console.WriteLine($"[NetworkMonitor] [{studentCode}] Stopping monitor for student {studentCode}...");
                    await _networkMonitor.StopAsync(ct);
                    Console.WriteLine($"[NetworkMonitor] [{studentCode}] Monitor stopped for student {studentCode}");
                }
                
                // Cleanup containers
                await CleanupContainersAsync(serverContainer, clientContainer);
                
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
        private async Task SetupContainersAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string serverContainer,
            string clientContainer)
        {
            var databaseContainer = config.DatabaseContainerName;
            
            // Remove existing code containers (keep database running between students for efficiency)
            try
            {
                if (_dockerExecutor.IsContainerExist(serverContainer))
                    _dockerExecutor.RemoveContainer(serverContainer);
                if (_dockerExecutor.IsContainerExist(clientContainer))
                    _dockerExecutor.RemoveContainer(clientContainer);
            }
            catch { }
            
            // Reduced delay after cleanup - Docker handles cleanup asynchronously
            await Task.Delay(100);
            
            // Create network if needed (now has built-in race condition protection)
            try
            {
                _dockerExecutor.CreateNetwork(config.DockerNetwork);
            }
            catch { }
            
            // 1. Setup MSSQL Database Container (if not already running)
            // This container is shared between students for efficiency
            await SetupDatabaseContainerAsync(config);
            
            // 2. Create server container with TTY support and PORT EXPOSED to host
            // This is CRITICAL - NetworkMonitor sniffs on the HOST at this exposed port
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var serverBase = new DockerBase
                {
                    ImageName = testKitConfig.CodeImageName,
                    ContainerName = serverContainer,
                    DockerNetwork = config.DockerNetwork,
                    ContainerPort = config.CodeContainerInternalPort,
                    HostPort = config.CodeContainerHostPort,  // EXPOSED to host for NetworkMonitor
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        { "DOTNET_RUNNING_IN_CONTAINER", "true" },
                        { "DOTNET_SYSTEM_CONSOLE_UNBUFFERED", "1" }
                    }
                };
                _dockerExecutor.RunContainerWithTty(serverBase);
                Console.WriteLine($"[Docker] Server container {serverContainer} created with port {config.CodeContainerHostPort}:{config.CodeContainerInternalPort} exposed");
            }
            
            // 3. Create client container with TTY support (NO port mapping needed)
            // Client connects to server via host.docker.internal to route traffic through the exposed port.
            // This is CRITICAL for network monitoring - traffic must pass through the host's exposed port
            // so the NetworkMonitor (running on the host) can capture it.
            // The --add-host flag ensures host.docker.internal works on Linux (Docker 20.10+)
            if (!string.IsNullOrEmpty(clientDllPath))
            {
                var clientBase = new DockerBase
                {
                    ImageName = testKitConfig.CodeImageName,
                    ContainerName = clientContainer,
                    DockerNetwork = config.DockerNetwork,
                    ContainerPort = 0,  // No port mapping for client
                    HostPort = 0,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        { "DOTNET_RUNNING_IN_CONTAINER", "true" },
                        { "DOTNET_SYSTEM_CONSOLE_UNBUFFERED", "1" }
                    },
                    // Add host.docker.internal mapping to allow client to reach the host
                    // This enables network traffic to flow through the exposed port for capture
                    AdditionalFlags = AppsettingKeywords.DOCKER_ADD_HOST_FLAG
                };
                _dockerExecutor.RunContainerWithTty(clientBase);
                Console.WriteLine($"[Docker] Client container {clientContainer} created with {AppsettingKeywords.DOCKER_HOST_INTERNAL} support (no port exposed)");
            }
            
            await Task.Delay(500);
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
                Console.WriteLine($"[Docker] Database container {databaseContainer} is already running");
                return;
            }
            
            // Check if container exists but stopped
            if (_dockerExecutor.IsContainerExist(databaseContainer))
            {
                Console.WriteLine($"[Docker] Starting existing database container {databaseContainer}...");
                _dockerExecutor.StartExistedContainer(databaseContainer);
                // Wait for container to be running with quick health checks (no logging spam)
                await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 10);
                return;
            }
            
            // Create new MSSQL database container
            Console.WriteLine($"[Docker] Creating new MSSQL database container {databaseContainer}...");
            
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
            Console.WriteLine($"[Docker] Database container {databaseContainer} created with port {config.DatabaseContainerHostPort}:{config.DatabaseContainerInternalPort} exposed");
            
            // Wait for MSSQL to fully start with polling instead of fixed delay
            Console.WriteLine("[Docker] Waiting for MSSQL to start...");
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
            Console.WriteLine($"[Docker] Warning: Container {containerName} may not be fully ready after {maxWaitSeconds}s");
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
                    return;
                }
                await Task.Delay(100); // Check every 100ms without logging
            }
            // If we get here, container still exists but proceed anyway
            Console.WriteLine($"[Docker] Warning: Container {containerName} still exists after {maxWaitSeconds}s");
        }
        
        /// <summary>
        /// OPTIMIZATION: Dynamically waits for processes to be killed in a container.
        /// Checks every 50ms up to maxWaitMs. Returns immediately when no target processes remain.
        /// Much faster than fixed waits - typically returns in 0-100ms vs 100ms+ fixed delay.
        /// </summary>
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
        
        private async Task CopyFilesToContainersAsync(
            string? serverDllPath,
            string? clientDllPath,
            string serverContainer,
            string clientContainer,
            DockerGradingConfig config)
        {
            // CRITICAL: DLL modification MUST happen BEFORE copying to container
            // We modify the DLL files on the HOST machine (where student submissions are)
            // Then the modified DLL is copied into the Docker container
            var dllModService = new DllModificationService();
            
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var serverDir = Path.GetDirectoryName(serverDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    try
                    {
                        // Apply DLL modification fallback on HOST machine BEFORE copying
                        if (config.UseDllModificationFallback)
                        {
                            Console.WriteLine($"[DllMod] Checking server directory on HOST: {serverDir}");
                            var result = dllModService.CheckAndPatchIfNeeded(
                                serverDir,
                                config.ServerProjectName,
                                isServer: true,
                                targetPort: config.CodeContainerHostPort
                            );
                            
                            Console.WriteLine($"[DllMod] Server fallback result: {result.GetSummary()}");
                            
                            // If DLL modification was required but failed, log warning but continue
                            // (appsettings generation will still happen as a second fallback)
                            if (result.RequiresDllModification && !result.Success)
                            {
                                Console.WriteLine($"[DllMod] WARNING: Server DLL modification failed - will attempt appsettings generation");
                            }
                            else if (result.RequiresDllModification && result.Success)
                            {
                                Console.WriteLine($"[DllMod] Server DLL successfully modified on HOST machine at: {result.DllPath}");
                            }
                        }
                        
                        // NOW copy the (potentially modified) files from HOST to container
                        _dockerExecutor.MakeDirectory(serverContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(serverDir, $"{serverContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied server files from HOST {serverDir} to container {serverContainer}:/apps/{folderName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to copy server files: {ex.Message}");
                    }
                }
            }
            
            if (!string.IsNullOrEmpty(clientDllPath))
            {
                var clientDir = Path.GetDirectoryName(clientDllPath);
                if (clientDir != null)
                {
                    var folderName = Path.GetFileName(clientDir);
                    try
                    {
                        // Apply DLL modification fallback on HOST machine BEFORE copying
                        if (config.UseDllModificationFallback)
                        {
                            Console.WriteLine($"[DllMod] Checking client directory on HOST: {clientDir}");
                            var result = dllModService.CheckAndPatchIfNeeded(
                                clientDir,
                                config.ClientProjectName,
                                isServer: false,
                                targetPort: config.CodeContainerHostPort
                            );
                            
                            Console.WriteLine($"[DllMod] Client fallback result: {result.GetSummary()}");
                            
                            // If DLL modification was required but failed, log warning but continue
                            // (appsettings generation will still happen as a second fallback)
                            if (result.RequiresDllModification && !result.Success)
                            {
                                Console.WriteLine($"[DllMod] WARNING: Client DLL modification failed - will attempt appsettings generation");
                            }
                            else if (result.RequiresDllModification && result.Success)
                            {
                                Console.WriteLine($"[DllMod] Client DLL successfully modified on HOST machine at: {result.DllPath}");
                            }
                        }
                        
                        // NOW copy the (potentially modified) files from HOST to container
                        _dockerExecutor.MakeDirectory(clientContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(clientDir, $"{clientContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied client files from HOST {clientDir} to container {clientContainer}:/apps/{folderName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to copy client files: {ex.Message}");
                    }
                }
            }
            
            await Task.Delay(500);
        }
        
        private void GenerateAppsettingsInContainers(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string serverContainer,
            string clientContainer)
        {
            var connectionString = ConnectionStringHelper.BuildForDocker(
                config.DatabaseContainerHostPort,
                testKitConfig.DatabaseName,
                config.DatabaseUsername,
                config.DatabasePassword ?? DefaultDatabasePassword);
            
            // CRITICAL: For network monitoring with libpcap/npcap, we need DIRECT port mapping.
            // Server binds to internal port, which MUST equal the external host port.
            // Client connects to host.docker.internal using the SAME port number.
            // Example: Student 1 uses 8000:8000, Student 2 uses 8001:8001
            var serverIpAddress = AppsettingKeywords.DOCKER_SERVER_BIND_ADDRESS;  // 0.0.0.0
            var clientIpAddress = AppsettingKeywords.DOCKER_HOST_INTERNAL;         // host.docker.internal
            
            // Both server and client use the SAME port number for direct mapping
            var port = config.CodeContainerHostPort;  // e.g., 8000, 8001, 8002, etc.
            var serverPort = port.ToString();
            var clientPort = port.ToString();
            
            Console.WriteLine($"[Appsettings] Direct mapping: Container internal port {config.CodeContainerInternalPort} -> Host port {config.CodeContainerHostPort}");
            
            // Check if DLL modification fallback is enabled and was used
            var dllModService = new DllModificationService();
            bool skipServerAppsettings = false;
            bool skipClientAppsettings = false;
            
            if (config.UseDllModificationFallback)
            {
                // Check if appsettings exists for server
                if (!string.IsNullOrEmpty(serverDllPath))
                {
                    var serverDir = Path.GetDirectoryName(serverDllPath);
                    if (serverDir != null && !dllModService.AppsettingsExists(serverDir))
                    {
                        skipServerAppsettings = true;
                        Console.WriteLine($"[Appsettings] Skipping server appsettings generation - DLL modification fallback was used (appsettings.json not found)");
                    }
                }
                
                // Check if appsettings exists for client
                if (!string.IsNullOrEmpty(clientDllPath))
                {
                    var clientDir = Path.GetDirectoryName(clientDllPath);
                    if (clientDir != null && !dllModService.AppsettingsExists(clientDir))
                    {
                        skipClientAppsettings = true;
                        Console.WriteLine($"[Appsettings] Skipping client appsettings generation - DLL modification fallback was used (appsettings.json not found)");
                    }
                }
            }
            
            if (!skipServerAppsettings && !string.IsNullOrEmpty(serverDllPath))
            {
                var serverDir = Path.GetDirectoryName(serverDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    var serverConfig = $@"{{
  ""ConnectionStrings"": {{ ""MyCnn"": ""{connectionString}"" }},
  ""IpAddress"": ""{serverIpAddress}"",
  ""Port"": ""{serverPort}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        // CRITICAL FIX: Remove any existing appsettings.json from copied files (e.g., from Meta/Given/Server)
                        // This ensures we use the dynamically generated appsettings with correct port for this student
                        Console.WriteLine($"[Appsettings] Removing old server appsettings: {containerPath}");
                        _dockerExecutor.ExecDockerCommand($"{serverContainer} rm -f {containerPath}", 3000);
                        
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_server_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, serverConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{serverContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Server: IP={serverIpAddress}, Port={serverPort} -> {containerPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to generate server appsettings: {ex.Message}");
                    }
                    finally
                    {
                        if (tempFile != null && File.Exists(tempFile))
                            try { File.Delete(tempFile); } catch { }
                    }
                }
            }
            
            if (!skipClientAppsettings && !string.IsNullOrEmpty(clientDllPath))
            {
                var clientDir = Path.GetDirectoryName(clientDllPath);
                if (clientDir != null)
                {
                    var folderName = Path.GetFileName(clientDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    var clientConfig = $@"{{
  ""IpAddress"": ""{clientIpAddress}"",
  ""Port"": ""{clientPort}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        // CRITICAL FIX: Remove any existing appsettings.json from copied files (e.g., from Meta/Given/Client)
                        // This ensures we use the dynamically generated appsettings with correct port for this student
                        Console.WriteLine($"[Appsettings] Removing old client appsettings: {containerPath}");
                        _dockerExecutor.ExecDockerCommand($"{clientContainer} rm -f {containerPath}", 3000);
                        
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_client_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, clientConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{clientContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Client: IP={clientIpAddress}, Port={clientPort} -> {containerPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warning] Failed to generate client appsettings: {ex.Message}");
                    }
                    finally
                    {
                        if (tempFile != null && File.Exists(tempFile))
                            try { File.Delete(tempFile); } catch { }
                    }
                }
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
            string serverContainer,
            string clientContainer,
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
                Console.WriteLine($"[TestCase] {testCase.Name}: Grade_Content = '{gradeContent}'");
                
                // Validate Grade_Content value
                var validValues = new[] { "Client", "Server", "Client/Server" };
                if (!validValues.Contains(gradeContent, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[TestCase] WARNING: Invalid Grade_Content value '{gradeContent}', defaulting to 'Client/Server'");
                    gradeContent = "Client/Server";
                }
                
                if (gradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                {
                    // Grade student's CLIENT only - use golden SERVER
                    actualClientDll = clientDllPath;
                    actualServerDll = testKitConfig.GivenServerPath;
                    Console.WriteLine($"[TestCase] Using student CLIENT + golden SERVER");
                    Console.WriteLine($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    Console.WriteLine($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    
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
                    Console.WriteLine($"[TestCase] Using student SERVER + golden CLIENT");
                    Console.WriteLine($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    Console.WriteLine($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    
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
                    Console.WriteLine($"[TestCase] Using student CLIENT + student SERVER (no golden)");
                    Console.WriteLine($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    Console.WriteLine($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    
                    // Note: For Client/Server mode, we allow one to be missing if the test only uses one
                    // The test will fail naturally if it tries to use a missing component
                }
                
                // Clear network captures for this test case
                // CRITICAL: Must clear BOTH NetworkMonitor AND RunContext to ensure
                // only traffic from this test case is captured
                _networkMonitor?.ClearCaptures();
                _runContext.ClearNetworkCaptures();
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
                
                // Execute actions and capture outputs - use the resolved DLLs
                var (clientOutputs, serverOutputs) = await ExecuteActionsAsync(
                    actions, config, testKitConfig,
                    actualServerDll, actualClientDll,
                    serverContainer, clientContainer, ct);
                
                // Compare outputs
                var (earnedMark, passed, comparisons) = CompareOutputs(
                    expectedOutputs, clientOutputs, serverOutputs, testCase.MaxMark);
                
                // Compare network (if expected)
                var networkComparisons = CompareNetwork(expectedNetwork);
                
                // Get captured network packets for Network sheet
                var capturedPackets = GetCapturedNetworkPackets();
                Console.WriteLine($"[NetworkMonitor] Captured {capturedPackets.Count} packets for test case {testCase.Name}");
                
                result.NetworkCaptures = capturedPackets;
                
                // CRITICAL: Validate network monitoring is working
                // If we expected network data but got none, this indicates a problem with network monitoring
                bool networkCheckPassed = true;
                if (expectedNetwork.Count > 0 && capturedPackets.Count == 0)
                {
                    Console.WriteLine("[NetworkMonitor] WARNING: Expected network traffic but captured NONE!");
                    Console.WriteLine("[NetworkMonitor] This usually means:");
                    Console.WriteLine("  1. Network monitor was not running with proper permissions (sudo on Linux)");
                    Console.WriteLine("  2. libpcap is not installed (Linux) or NPcap is not installed (Windows)");
                    Console.WriteLine("  3. The loopback interface was not found");
                    Console.WriteLine("[NetworkMonitor] Network monitoring is MANDATORY - marking test case as FAILED");
                    networkCheckPassed = false;
                }
                
                // Final result: must pass both output comparison AND network check
                result.EarnedMark = (passed && networkCheckPassed) ? earnedMark : 0;
                result.Passed = passed && networkCheckPassed;
                result.ClientComparisons = comparisons.Where(c => c.Source == "Client").ToList();
                result.ServerComparisons = comparisons.Where(c => c.Source == "Server").ToList();
                result.NetworkComparisons = networkComparisons;
                
                if (!networkCheckPassed)
                {
                    result.ErrorMessage = "Network monitoring failed: No packets captured. Run with sudo and ensure libpcap/NPcap is installed.";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Console.WriteLine($"[Error] Test case {testCase.Name} failed: {ex.Message}");
            }
            
            return result;
        }
        
        /// <summary>
        /// Gets all captured network packets from the NetworkMonitor.
        /// </summary>
        private List<CapturedNetworkPacket> GetCapturedNetworkPackets()
        {
            if (_networkMonitor == null)
                return new List<CapturedNetworkPacket>();
            
            // Get ALL captured packets from the RunContext (across all stages)
            // Returns packets with Stage, Timestamp, Flags, State, SourceRole, DestinationRole, Data, etc.
            return _runContext.GetAllCapturedNetworkPackets().ToList();
        }
        
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsAsync(
            List<(int Stage, string Input, string Action)> actions,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string? serverDllPath,
            string? clientDllPath,
            string serverContainer,
            string clientContainer,
            CancellationToken ct)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();
            
            int clientBaseline = 0;
            int serverBaseline = 0;
            
            foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
            {
                ct.ThrowIfCancellationRequested();
                
                // Update network monitor stage context
                _networkMonitor?.SetCurrentContext("", stage.ToString());
                
                Console.WriteLine($"  [Stage {stage}] {action}" + (string.IsNullOrEmpty(input) ? "" : $" input='{input}'"));
                
                switch (action.ToUpperInvariant())
                {
                    case "STARTSERVER":
                        if (!string.IsNullOrEmpty(serverDllPath))
                        {
                            var serverDirPath = Path.GetDirectoryName(serverDllPath);
                            if (serverDirPath != null)
                            {
                                var serverDir = Path.GetFileName(serverDirPath);
                                var serverDll = Path.GetFileName(serverDllPath);
                                var dockerPath = $"/apps/{serverDir}/{serverDll}";
                                
                                // Start server inside container
                                _dockerExecutor.WaitForPublishConsoleFileDeployment(
                                    serverContainer, serverContainer, dockerPath,
                                    config.CodeContainerInternalPort.ToString(), 30000);
                                
                                await Task.Delay(StartupDelayMs);
                                
                                // Get output from application log
                                var output = _dockerExecutor.GetApplicationLog(serverContainer, serverContainer) ?? "";
                                var newOutput = output.Length > serverBaseline ? output.Substring(serverBaseline) : output;
                                serverBaseline = output.Length;
                                serverOutputs[stage] = newOutput;
                                
                                Console.WriteLine($"    Server started, output: {newOutput.Length} chars");
                            }
                        }
                        break;
                        
                    case "STARTCLIENT":
                        if (!string.IsNullOrEmpty(clientDllPath))
                        {
                            var clientDirPath = Path.GetDirectoryName(clientDllPath);
                            if (clientDirPath != null)
                            {
                                var clientDir = Path.GetFileName(clientDirPath);
                                var clientDll = Path.GetFileName(clientDllPath);
                                var dockerPath = $"/apps/{clientDir}/{clientDll}";
                                
                                // Start client inside container (no port required)
                                _dockerExecutor.WaitForPublishConsoleFileDeployment(
                                    clientContainer, clientContainer, dockerPath, "-1", 30000);
                                
                                await Task.Delay(StartupDelayMs);
                                
                                // Get output from application log with retries
                                string newOutput = "";
                                int retryCount = 0;
                                while (string.IsNullOrEmpty(newOutput) && retryCount < OutputRetryMaxAttempts)
                                {
                                    var output = _dockerExecutor.GetApplicationLog(clientContainer, clientContainer) ?? "";
                                    newOutput = output.Length > clientBaseline ? output.Substring(clientBaseline) : output;
                                    if (!string.IsNullOrEmpty(newOutput))
                                        clientBaseline = output.Length;
                                    else
                                    {
                                        await Task.Delay(OutputRetryDelayMs);
                                        retryCount++;
                                    }
                                }
                                clientOutputs[stage] = newOutput;
                                
                                Console.WriteLine($"    Client started, output: {newOutput.Length} chars");
                            }
                        }
                        break;
                        
                    case "INPUT":
                        if (!string.IsNullOrEmpty(input))
                        {
                            // Send input to client container
                            _dockerExecutor.SendInputToContainer(clientContainer, clientContainer, input);
                            
                            await Task.Delay(InputProcessingDelayMs);
                            
                            // Capture client output
                            var clientOutput = _dockerExecutor.GetApplicationLog(clientContainer, clientContainer) ?? "";
                            var newClientOutput = clientOutput.Length > clientBaseline ? clientOutput.Substring(clientBaseline) : "";
                            if (!string.IsNullOrEmpty(newClientOutput))
                                clientBaseline = clientOutput.Length;
                            
                            // Capture server output
                            var serverOutput = _dockerExecutor.GetApplicationLog(serverContainer, serverContainer) ?? "";
                            var newServerOutput = serverOutput.Length > serverBaseline ? serverOutput.Substring(serverBaseline) : "";
                            if (!string.IsNullOrEmpty(newServerOutput))
                                serverBaseline = serverOutput.Length;
                            
                            clientOutputs[stage] = newClientOutput;
                            serverOutputs[stage] = newServerOutput;
                            
                            Console.WriteLine($"    Input sent, client: {newClientOutput.Length} chars, server: {newServerOutput.Length} chars");
                        }
                        break;
                        
                    case "CLOSECLIENT":
                        // Use safe kill method that excludes PID 1 to avoid killing the container
                        try { _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(clientContainer), 5000); } catch { }
                        clientBaseline = 0;
                        break;
                        
                    case "CLOSESERVER":
                        // Use safe kill method that excludes PID 1 to avoid killing the container
                        try { _dockerExecutor.TryExecDockerCommand(BuildSafeDotnetKillCommand(serverContainer), 5000); } catch { }
                        serverBaseline = 0;
                        break;
                }
                
                // OPTIMIZATION: Very brief delay between test cases (no need to wait longer)
                // Containers remain running, only processes are killed
                await Task.Delay(10);
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
                    Console.WriteLine($"  [Stage {stage}] Client comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        Console.WriteLine($"    Expected (contains): '{exp.ClientConsole}'");
                        Console.WriteLine($"    Actual output: '{actual}'");
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
                    Console.WriteLine($"  [Stage {stage}] Server comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        Console.WriteLine($"    Expected (contains): '{exp.ServerConsole}'");
                        Console.WriteLine($"    Actual output: '{actual}'");
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
            
            // ALL-OR-NOTHING policy
            bool allPassed = passed == total && total > 0;
            double earnedMark = allPassed ? maxMark : 0;
            
            Console.WriteLine($"  Comparison summary: {passed}/{total} checks passed, earned {earnedMark:F2}/{maxMark:F2} marks");
            
            return (earnedMark, allPassed, comparisons);
        }
        
        private List<ComparisonResult> CompareNetwork(List<ExpectedNetworkFlow> expected)
        {
            var results = new List<ComparisonResult>();
            
            foreach (var exp in expected)
            {
                var capturedPackets = _runContext.GetCapturedNetworkPackets("", exp.Stage.ToString());
                
                // Use lenient flag comparison - check if all expected flags are present
                bool matched = capturedPackets.Any(p =>
                    (string.IsNullOrEmpty(exp.Flags) || CompareTcpFlags(exp.Flags, p.Flags)) &&
                    (string.IsNullOrEmpty(exp.SourceRole) || p.SourceRole == exp.SourceRole) &&
                    (string.IsNullOrEmpty(exp.DestinationRole) || p.DestinationRole == exp.DestinationRole));
                
                results.Add(new ComparisonResult
                {
                    Source = "Network",
                    Stage = exp.Stage,
                    Expected = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}",
                    Actual = capturedPackets.Any() ? string.Join("; ", capturedPackets.Select(p => p.Flags)) : "(no captures)",
                    Passed = matched
                });
            }
            
            return results;
        }
        
        private bool NormalizeAndContains(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            var normExpected = expected.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
            var normActual = (actual ?? "").Trim().Replace("\r\n", "\n").Replace("\r", "\n");
            return normActual.Contains(normExpected);
        }
        
        /// <summary>
        /// Compares TCP flags in a lenient way - checks if all expected flags are present
        /// regardless of order. TCP flags are bits (SYN, ACK, PSH, FIN, RST, URG) and their
        /// order may differ between Windows/Linux or packet capture tools.
        /// 
        /// Examples:
        /// - "PSH, ACK" matches "ACK, PSH" -> true
        /// - "SYN" matches "SYN" -> true
        /// - "ACK" matches "ACK, RST" -> true (actual has ACK plus extra RST)
        /// - "SYN, ACK" matches "SYN" -> false (missing ACK)
        /// </summary>
        private bool CompareTcpFlags(string expectedFlags, string actualFlags)
        {
            if (string.IsNullOrEmpty(expectedFlags)) return true;
            if (string.IsNullOrEmpty(actualFlags)) return false;
            
            // Split flags and normalize (trim whitespace, convert to uppercase)
            var expectedSet = expectedFlags.Split(',')
                .Select(f => f.Trim().ToUpperInvariant())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToHashSet();
            
            var actualSet = actualFlags.Split(',')
                .Select(f => f.Trim().ToUpperInvariant())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToHashSet();
            
            // Check if all expected flags are present in actual flags
            // (actual may have additional flags, which is acceptable)
            return expectedSet.IsSubsetOf(actualSet);
        }
        
        /// <summary>
        /// Normalize flags for exact comparison - sorts flags alphabetically and removes whitespace
        /// </summary>
        private string NormalizeFlags(string flags)
        {
            if (string.IsNullOrEmpty(flags)) return "";
            
            var flagList = flags.Split(',')
                .Select(f => f.Trim().ToUpperInvariant())
                .Where(f => !string.IsNullOrEmpty(f))
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
                                Console.WriteLine($"[Warning] Cannot parse mark value '{markStr}' for test case '{tcName}' - defaulting to 0");
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
                        Console.WriteLine($"[TestKit] Found given server: {Path.GetFileName(serverDll)}");
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
                        Console.WriteLine($"[TestKit] Found given client: {Path.GetFileName(clientDll)}");
                    }
                }
            }
            
            // Apply config overrides
            if (config.CodeContainerInternalPort > 0)
                tkConfig.CodeContainerInternalPort = config.CodeContainerInternalPort;
            if (config.CodeContainerHostPort > 0)
                tkConfig.CodeContainerHostPort = config.CodeContainerHostPort;
            
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
                            Console.WriteLine($"[TestKit] {Path.GetFileName(testCasePath)}: Timeout = {timeout}s (from Header.xlsx)");
                        }
                        
                        // Read Grade_Content
                        if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(value))
                        {
                            gradeContent = value;
                            Console.WriteLine($"[TestKit] {Path.GetFileName(testCasePath)}: Grade_Content = '{gradeContent}' (from Header.xlsx)");
                        }
                    }
                    
                    return (timeout, gradeContent);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestKit] Warning: Could not read config from {headerPath}: {ex.Message}");
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
            using var wb = new XLWorkbook(detailPath);
            
            if (wb.TryGetWorksheet("Network", out var ws))
            {
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
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
        private async Task CleanupBetweenTestCasesAsync(string serverContainer, string clientContainer, int hostPort)
        {
            OnProgress("Cleanup: Stopping applications between test cases...");
            
            // Step 1: Kill dotnet application processes (excluding PID 1 - container's main process)
            // This uses PID-based killing to safely terminate only application processes
            await KillDotnetProcessesInContainerAsync(serverContainer, "Server");
            await KillDotnetProcessesInContainerAsync(clientContainer, "Client");
            
            // OPTIMIZATION: Wait for processes to be killed (dynamic check vs fixed 100ms)
            OnProgress("Cleanup: Waiting for graceful shutdown...");
            await WaitForProcessesKilledAsync(serverContainer, "dotnet", maxWaitMs: 200);
            await WaitForProcessesKilledAsync(clientContainer, "dotnet", maxWaitMs: 200);
            
            // Force kill any remaining dotnet application processes (excluding PID 1)
            OnProgress("Cleanup: Force killing any remaining dotnet processes...");
            await ForceKillDotnetProcessesInContainerAsync(serverContainer, "Server");
            await ForceKillDotnetProcessesInContainerAsync(clientContainer, "Client");
            
            // Step 2: Kill sleep processes that keep input pipes open
            // Use safe kill that excludes PID 1
            _dockerExecutor.TryExecDockerCommand($"{serverContainer} sh -c \"ps aux | grep 'sleep 10000' | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"", 3000);
            _dockerExecutor.TryExecDockerCommand($"{clientContainer} sh -c \"ps aux | grep 'sleep 10000' | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"", 3000);
            
            // Step 3: Clean up temp files ONLY (do NOT remove /apps/* - docker cp will overwrite files)
            OnProgress("Cleanup: Removing temp files from containers...");
            _dockerExecutor.TryExecDockerCommand($"{serverContainer} rm -f /tmp/*.pid /tmp/*.port /tmp/*_output.log /tmp/*_input_pipe", 3000);
            _dockerExecutor.TryExecDockerCommand($"{clientContainer} rm -f /tmp/*.pid /tmp/*.port /tmp/*_output.log /tmp/*_input_pipe", 3000);
            
            // Step 4: Clear network captures for next test case
            // CRITICAL: Must clear BOTH NetworkMonitor AND RunContext to prevent
            // previous test case's network packets from appearing in next test case
            _networkMonitor?.ClearCaptures();
            _runContext.ClearNetworkCaptures();
            
            // Step 5: Clear console manager logs
            _consoleManager.ClearAllLogs();
            
            // REMOVED: Port release waiting - NOT NEEDED
            // Ports are assigned incrementally from testkit base port (e.g., 8000, 8001, 8002...)
            // Each student gets their own unique port that is marked as occupied for the entire grading flow.
            // There's no need to wait for port release because:
            // 1. We're not reusing ports during a grading session
            // 2. Even grading 1000 students only occupies ports 8000-8999 (plenty of ports available)
            // 3. Port availability check happens during assignment, not during cleanup
            // This speeds up test case transitions and prevents unnecessary delays.
            
            OnProgress("Cleanup: Complete, ready for next test case");
        }
        
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
            Console.WriteLine($"[Database] Resetting database container {databaseContainer} for new student...");
            
            // Stop and remove existing database container
            try { _dockerExecutor.StopContainer(databaseContainer, 10000); } catch { }
            try { _dockerExecutor.RemoveContainer(databaseContainer, 10000); } catch { }
            
            // OPTIMIZATION: Wait for container to be fully removed (dynamic check vs fixed 500ms)
            await WaitForContainerRemovedAsync(databaseContainer, maxWaitSeconds: 5);
            
            // Recreate the database container
            await SetupDatabaseContainerAsync(config);
            
            Console.WriteLine($"[Database] Database container reset complete");
        }
        
        private async Task CleanupContainersAsync(string serverContainer, string clientContainer)
        {
            _consoleManager.RemoveAllAttachments();
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }
            
            // OPTIMIZATION: Wait for containers to be fully removed (dynamic check vs fixed 50ms)
            await WaitForContainerRemovedAsync(serverContainer, maxWaitSeconds: 3);
            await WaitForContainerRemovedAsync(clientContainer, maxWaitSeconds: 3);
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
                Console.WriteLine($"[Network Sheet] Writing {expectedNetworkFlows.Count} expected network flows...");
                
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
                        // Found matching packet - write actual data (columns 11-15)
                        netWs.Cell(netRow, 11).Value = matchingPacket.Flags;  // ActualFlags
                        netWs.Cell(netRow, 12).Value = matchingPacket.State;  // ActualState
                        netWs.Cell(netRow, 13).Value = matchingPacket.SourceRole;  // ActualSourceRole
                        netWs.Cell(netRow, 14).Value = matchingPacket.DestinationRole;  // ActualDestRole
                        netWs.Cell(netRow, 15).Value = matchingPacket.Data ?? "";  // ActualData
                        
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
                        
                        netWs.Cell(netRow, 16).Value = exactMatch ? "PASS" : "PARTIAL";
                        netWs.Cell(netRow, 16).Style.Fill.BackgroundColor = exactMatch ? XLColor.LightGreen : XLColor.Yellow;
                        
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
                        netWs.Cell(netRow, 16).Value = "FAIL";
                        netWs.Cell(netRow, 16).Style.Fill.BackgroundColor = XLColor.LightPink;
                        
                        Console.WriteLine($"[Network Sheet] Expected flow MISSING at stage {expectedFlow.Stage}: Flags={expectedFlow.Flags}, SourceRole={expectedFlow.SourceRole}, DestRole={expectedFlow.DestinationRole}");
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
                    Console.WriteLine($"[Network Sheet] Found {remainingPackets.Count} additional (not validated) packets at stage {stage}");
                    
                    foreach (var packet in remainingPackets)
                    {
                        // No expected flow for this packet - shown for information only
                        netWs.Cell(netRow, 1).Value = packet.Stage;  // Stage
                        netWs.Cell(netRow, 2).Value = "(Not validated by this test case)";  // Time
                        // Leave expected columns 3-10 empty
                        for (int i = 3; i <= 10; i++) 
                            netWs.Cell(netRow, i).Value = "";
                        
                        // Write actual packet data (columns 11-15)
                        netWs.Cell(netRow, 11).Value = packet.Flags;  // ActualFlags
                        netWs.Cell(netRow, 12).Value = packet.State;  // ActualState
                        netWs.Cell(netRow, 13).Value = packet.SourceRole;  // ActualSourceRole
                        netWs.Cell(netRow, 14).Value = packet.DestinationRole;  // ActualDestRole
                        netWs.Cell(netRow, 15).Value = packet.Data ?? "";  // ActualData
                        netWs.Cell(netRow, 16).Value = "INFO";  // Informational - not validated
                        netWs.Cell(netRow, 16).Style.Fill.BackgroundColor = XLColor.LightGray;
                        
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
            // This ensures consistency across both Docker and regular grading flows.
            var headers = new[] { 
                "Stage",  // Test stage number
                "Time", "Info", "Source", "Destination", 
                "Flags", "State", "Data", "SourceRole", "DestinationRole",
                "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData",
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
            Console.WriteLine($"[DockerGrading] {formattedMessage}");
            ProgressUpdated?.Invoke(this, new GradingProgressEventArgs(formattedMessage));
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
