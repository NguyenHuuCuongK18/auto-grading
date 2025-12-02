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
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using SolutionGrader.Core.Abstractions;
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
    /// Key design decisions:
    /// - NetworkMonitor ALWAYS runs first to capture full network traffic
    /// - Server container port is EXPOSED to host (e.g., -p 8000:8000)
    /// - NetworkMonitor runs on HOST and sniffs localhost:8000 (requires sudo/admin)
    /// - Client connects to server via Docker network (by container name)
    /// - Application output is captured via log files, not docker logs (avoids buffering)
    /// - TTY flag (-t) ensures immediate console output flushing
    /// </summary>
    public sealed class DockerGradingService
    {
        // Timing constants
        private const int StartupDelayMs = 3000;
        private const int InputProcessingDelayMs = 5000;
        private const int OutputRetryMaxAttempts = 5;
        private const int OutputRetryDelayMs = 1000;
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly DockerConsoleManager _consoleManager;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IRunContext _runContext;
        
        /// <summary>
        /// Event raised when grading progress is updated.
        /// </summary>
        public event EventHandler<GradingProgressEventArgs>? ProgressUpdated;
        
        public DockerGradingService(INetworkMonitorService? networkMonitor, IRunContext runContext)
        {
            _dockerExecutor = new DockerCommandExecutor();
            _consoleManager = new DockerConsoleManager();
            _networkMonitor = networkMonitor;
            _runContext = runContext;
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
            
            // Container names
            var serverContainer = $"ag-server-{studentCode}";
            var clientContainer = $"ag-client-{studentCode}";
            
            try
            {
                OnProgress($"Loading test kit configuration from {testKitPath}...");
                var testKitConfig = LoadTestKitConfig(testKitPath, config);
                result.MaxMark = testKitConfig.TotalMaxMark;
                
                // Resolve server/client DLL paths based on examiner configuration:
                // - HasServer=true means student MUST provide server, search student's solution
                // - HasServer=false means use golden server from Meta/Given/Server
                // - HasClient=true means student MUST provide client, search student's solution
                // - HasClient=false means use golden client from Meta/Given/Client
                //
                // The serverDllPath and clientDllPath parameters contain what was found in student's solution.
                // If examiner didn't expect that component, we use the golden version instead.
                
                string? actualServerDllPath = null;
                string? actualClientDllPath = null;
                
                // Server resolution
                if (config.HasServer)
                {
                    // Examiner expects student to provide server
                    actualServerDllPath = serverDllPath;
                    if (string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"WARNING: Student should provide server ({config.ServerProjectName}) but none found!");
                    }
                    else
                    {
                        OnProgress($"Using student's server: {Path.GetFileName(actualServerDllPath)}");
                    }
                }
                else
                {
                    // Examiner doesn't expect student to provide server, use golden server from test kit
                    actualServerDllPath = testKitConfig.GivenServerPath;
                    if (!string.IsNullOrEmpty(actualServerDllPath))
                    {
                        OnProgress($"Using golden server from Meta/Given/Server: {Path.GetFileName(actualServerDllPath)}");
                    }
                    else
                    {
                        OnProgress("WARNING: No golden server found in Meta/Given/Server!");
                    }
                }
                
                // Client resolution
                if (config.HasClient)
                {
                    // Examiner expects student to provide client
                    actualClientDllPath = clientDllPath;
                    if (string.IsNullOrEmpty(actualClientDllPath))
                    {
                        OnProgress($"WARNING: Student should provide client ({config.ClientProjectName}) but none found!");
                    }
                    else
                    {
                        OnProgress($"Using student's client: {Path.GetFileName(actualClientDllPath)}");
                    }
                }
                else
                {
                    // Examiner doesn't expect student to provide client, use golden client from test kit
                    actualClientDllPath = testKitConfig.GivenClientPath;
                    if (!string.IsNullOrEmpty(actualClientDllPath))
                    {
                        OnProgress($"Using golden client from Meta/Given/Client: {Path.GetFileName(actualClientDllPath)}");
                    }
                    else
                    {
                        OnProgress("WARNING: No golden client found in Meta/Given/Client!");
                    }
                }
                
                // Log final resolved paths
                OnProgress($"Final Server DLL: {(actualServerDllPath != null ? Path.GetFileName(actualServerDllPath) : "(NONE)")}");
                OnProgress($"Final Client DLL: {(actualClientDllPath != null ? Path.GetFileName(actualClientDllPath) : "(NONE)")}");
                
                // CRITICAL: Start network monitor FIRST before ANY containers or processes
                // NetworkMonitor runs on HOST and sniffs localhost:{hostPort}
                // It MUST start before containers to capture the full network traffic including:
                // - Initial TCP handshake (SYN, SYN-ACK, ACK)
                // - All data transfers
                // - Connection teardown (FIN-ACK)
                if (_networkMonitor != null)
                {
                    _networkMonitor.MonitorPort = config.CodeContainerHostPort;
                    _networkMonitor.ProtocolType = testKitConfig.Protocol;
                    await _networkMonitor.StartAsync(ct);
                    OnProgress($"Network monitor started on host port {config.CodeContainerHostPort} - capturing all traffic");
                }
                
                OnProgress($"Setting up Docker containers for {studentCode}...");
                await SetupContainersAsync(actualServerDllPath, actualClientDllPath, config, testKitConfig, serverContainer, clientContainer);
                
                // Copy files to containers (use actual resolved paths)
                await CopyFilesToContainersAsync(actualServerDllPath, actualClientDllPath, serverContainer, clientContainer);
                
                // Generate appsettings.json in containers
                GenerateAppsettingsInContainers(actualServerDllPath, actualClientDllPath, config, testKitConfig, serverContainer, clientContainer);
                
                // Execute test cases
                bool isFirstTestCase = true;
                foreach (var testCase in testKitConfig.TestCases)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    // For subsequent test cases, cleanup and re-copy files
                    // This approach is faster than disposing/rebuilding containers
                    if (!isFirstTestCase)
                    {
                        // Cleanup between test cases (kills processes, removes files)
                        await CleanupBetweenTestCasesAsync(serverContainer, clientContainer, config.CodeContainerHostPort);
                        
                        // Re-copy files for next test case
                        OnProgress($"Re-copying files for test case {testCase.Name}...");
                        await CopyFilesToContainersAsync(actualServerDllPath, actualClientDllPath, serverContainer, clientContainer);
                        
                        // Re-generate appsettings.json
                        GenerateAppsettingsInContainers(actualServerDllPath, actualClientDllPath, config, testKitConfig, serverContainer, clientContainer);
                    }
                    isFirstTestCase = false;
                    
                    OnProgress($"Executing test case: {testCase.Name}...");
                    
                    var tcResult = await ExecuteTestCaseAsync(
                        testCase, testKitConfig, config, 
                        actualServerDllPath, actualClientDllPath,
                        serverContainer, clientContainer, ct);
                    
                    result.TestCaseResults.Add(tcResult);
                    
                    // Write test case results (pass protocol to determine Network sheet format)
                    var tcResultPath = Path.Combine(studentResultPath, testCase.Name);
                    Directory.CreateDirectory(tcResultPath);
                    await WriteTestCaseResultAsync(tcResultPath, testCase.Name, tcResult, testKitConfig.Protocol);
                    
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
                    await _networkMonitor.StopAsync(ct);
                }
                
                // Cleanup containers
                await CleanupContainersAsync(serverContainer, clientContainer);
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
            
            await Task.Delay(500);
            
            // Create network if needed
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
            // Client connects to server via Docker network using container name
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
                    }
                };
                _dockerExecutor.RunContainerWithTty(clientBase);
                Console.WriteLine($"[Docker] Client container {clientContainer} created (no port exposed)");
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
                await Task.Delay(5000); // Wait for database to start
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
            
            // Wait for MSSQL to fully start (typically takes 10-15 seconds)
            Console.WriteLine("[Docker] Waiting for MSSQL to start...");
            await Task.Delay(15000);
        }
        
        private async Task CopyFilesToContainersAsync(
            string? serverDllPath,
            string? clientDllPath,
            string serverContainer,
            string clientContainer)
        {
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var serverDir = Path.GetDirectoryName(serverDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    try
                    {
                        _dockerExecutor.MakeDirectory(serverContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(serverDir, $"{serverContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied server files to {serverContainer}:/apps/{folderName}");
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
                        _dockerExecutor.MakeDirectory(clientContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(clientDir, $"{clientContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied client files to {clientContainer}:/apps/{folderName}");
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
            // Connection string for database
            var connectionString = BuildConnectionString(config, testKitConfig);
            
            // Server listens on 0.0.0.0 to accept connections from any interface
            var serverIpAddress = "0.0.0.0";
            // Client connects to server container by Docker DNS name
            var clientIpAddress = serverContainer;
            var port = config.CodeContainerInternalPort.ToString();
            
            // Generate server appsettings.json
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var serverDir = Path.GetDirectoryName(serverDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    
                    var serverConfig = $@"{{
  ""ConnectionStrings"": {{
    ""MyCnn"": ""{connectionString}""
  }},
  ""IpAddress"": ""{serverIpAddress}"",
  ""Port"": ""{port}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_server_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, serverConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{serverContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Server: IP={serverIpAddress}, Port={port}");
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
            
            // Generate client appsettings.json
            if (!string.IsNullOrEmpty(clientDllPath))
            {
                var clientDir = Path.GetDirectoryName(clientDllPath);
                if (clientDir != null)
                {
                    var folderName = Path.GetFileName(clientDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    
                    var clientConfig = $@"{{
  ""IpAddress"": ""{clientIpAddress}"",
  ""Port"": ""{port}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_client_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, clientConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{clientContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Client: IP={clientIpAddress}, Port={port}");
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
        
        private string BuildConnectionString(DockerGradingConfig config, TestKitConfig testKitConfig)
        {
            var server = $"localhost,{config.DatabaseContainerHostPort}";
            var database = testKitConfig.DatabaseName;
            var username = config.DatabaseUsername ?? "sa";
            var password = config.DatabasePassword ?? DefaultDatabasePassword;
            
            return $"server={server};database={database};uid={username};pwd={password};TrustServerCertificate=true";
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
                // CRITICAL: Clear BOTH network monitor AND run context captures for this test case
                // This ensures no network packets from previous test cases leak into this one
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
                
                // Execute actions and capture outputs
                var (clientOutputs, serverOutputs) = await ExecuteActionsAsync(
                    actions, config, testKitConfig,
                    serverDllPath, clientDllPath,
                    serverContainer, clientContainer, ct);
                
                // Compare outputs
                var (earnedMark, passed, comparisons) = CompareOutputs(
                    expectedOutputs, clientOutputs, serverOutputs, testCase.MaxMark);
                
                // Compare network (if expected)
                var networkComparisons = CompareNetwork(expectedNetwork);
                
                // Get captured network packets for Network sheet
                var capturedPackets = GetCapturedNetworkPackets();
                Console.WriteLine($"[NetworkMonitor] Captured {capturedPackets.Count} packets for test case {testCase.Name}");
                
                result.NetworkCaptures = capturedPackets.Select(p => new NetworkCaptureRecord
                {
                    Stage = p.Stage,
                    Timestamp = p.Timestamp,
                    Flags = p.Flags,
                    State = p.State,
                    SourceRole = p.SourceRole,
                    DestinationRole = p.DestinationRole,
                    Data = p.Data,
                    SourcePort = p.SourcePort,
                    DestinationPort = p.DestinationPort,
                    // HTTP-specific fields
                    HttpUri = p.HttpUri,
                    HttpHost = p.HttpHost,
                    HttpMethod = p.HttpMethod,
                    HttpStatus = p.HttpStatus,
                    HttpVersion = p.HttpVersion,
                    HttpHeaders = p.HttpHeaders,
                    HttpBody = p.HttpBody
                }).ToList();
                
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
        private List<CapturedPacketInfo> GetCapturedNetworkPackets()
        {
            var packets = new List<CapturedPacketInfo>();
            
            if (_networkMonitor == null)
                return packets;
            
            // Get ALL captured packets from the RunContext (across all stages)
            var capturedPackets = _runContext.GetAllCapturedNetworkPackets();
            foreach (var packet in capturedPackets)
            {
                packets.Add(new CapturedPacketInfo
                {
                    Stage = _runContext.CurrentStage ?? 0,
                    Timestamp = packet.Timestamp,
                    Flags = packet.Flags,
                    State = packet.State,
                    SourceRole = packet.SourceRole,
                    DestinationRole = packet.DestinationRole,
                    Data = packet.Data,
                    SourcePort = packet.SourcePort,
                    DestinationPort = packet.DestinationPort,
                    // HTTP-specific fields
                    HttpUri = packet.HttpUri,
                    HttpHost = packet.HttpHost,
                    HttpMethod = packet.HttpMethod,
                    HttpStatus = packet.HttpStatus,
                    HttpVersion = packet.HttpVersion,
                    HttpHeaders = packet.HttpHeaders,
                    HttpBody = packet.HttpBody
                });
            }
            
            return packets;
        }
        
        private class CapturedPacketInfo
        {
            public int Stage { get; set; }
            public DateTime Timestamp { get; set; }
            public string Flags { get; set; } = "";
            public string State { get; set; } = "";
            public string SourceRole { get; set; } = "";
            public string DestinationRole { get; set; } = "";
            public string? Data { get; set; }
            public int SourcePort { get; set; }
            public int DestinationPort { get; set; }
            // HTTP-specific fields
            public string? HttpUri { get; set; }
            public string? HttpHost { get; set; }
            public string? HttpMethod { get; set; }
            public string? HttpStatus { get; set; }
            public string? HttpVersion { get; set; }
            public string? HttpHeaders { get; set; }
            public string? HttpBody { get; set; }
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
                        try { _dockerExecutor.ExecDockerCommand($"{clientContainer} pkill -f dotnet", 5000); } catch { }
                        clientBaseline = 0;
                        break;
                        
                    case "CLOSESERVER":
                        try { _dockerExecutor.ExecDockerCommand($"{serverContainer} pkill -f dotnet", 5000); } catch { }
                        serverBaseline = 0;
                        break;
                }
                
                await Task.Delay(200);
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
            
            return (earnedMark, allPassed, comparisons);
        }
        
        private List<ComparisonResult> CompareNetwork(List<ExpectedNetworkFlow> expected)
        {
            var results = new List<ComparisonResult>();
            
            foreach (var exp in expected)
            {
                var capturedPackets = _runContext.GetCapturedNetworkPackets("", exp.Stage.ToString());
                
                bool matched = capturedPackets.Any(p =>
                    (string.IsNullOrEmpty(exp.Flags) || p.Flags.Contains(exp.Flags.Split(',')[0].Trim())) &&
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
                        var tcName = row.Cell(1).GetValue<string>()?.Trim();
                        var mark = row.Cell(2).GetValue<double>();
                        if (!string.IsNullOrEmpty(tcName))
                            tkConfig.TestCaseMarks[tcName] = mark;
                    }
                }
                
                if (wb.TryGetWorksheet("Config", out var configSheet))
                {
                    foreach (var row in configSheet.RowsUsed().Skip(1))
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim();
                        var value = row.Cell(2).GetValue<string>()?.Trim();
                        if (key?.Equals("Protocol", StringComparison.OrdinalIgnoreCase) == true)
                            tkConfig.Protocol = value ?? "TCP";
                    }
                }
            }
            
            // Discover test cases
            tkConfig.TestCases = Directory.GetDirectories(testKitPath)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "Detail.xlsx")))
                .Select(d => new TestCaseInfo
                {
                    Name = Path.GetFileName(d),
                    Path = d,
                    MaxMark = tkConfig.TestCaseMarks.TryGetValue(Path.GetFileName(d), out var m) ? m : 0
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
                    var sourceRole = row.Cell(9).GetValue<string>();
                    var destRole = row.Cell(10).GetValue<string>();
                    
                    if (int.TryParse(stageStr, out var stage))
                    {
                        flows.Add(new ExpectedNetworkFlow
                        {
                            Stage = stage,
                            Flags = flags,
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
        /// 2. Killing ALL child processes (tee, sh, sleep) that may keep ports/pipes open
        /// 3. REMOVING files from /apps folder (this also removes logs)
        /// 4. Clearing network captures from BOTH NetworkMonitor AND RunContext
        /// 5. Waiting for port release (inside container AND host)
        /// 
        /// CRITICAL: This cleanup must be thorough to prevent:
        /// - "Address already in use" errors when restarting applications
        /// - Console output from previous test case bleeding into next test case
        /// - Network packets from previous test case appearing in next test case's results
        /// 
        /// The files will be re-copied before the next test case starts.
        /// This approach is much faster than disposing/rebuilding containers.
        /// </summary>
        private async Task CleanupBetweenTestCasesAsync(string serverContainer, string clientContainer, int hostPort)
        {
            Console.WriteLine("[Cleanup] Stopping applications between test cases...");
            
            // Step 1: Kill ALL processes associated with dotnet application
            // This includes: dotnet, tee, sh, stdbuf - they form a pipeline for console output
            // Using killall in addition to pkill for more thorough cleanup
            var serverKillAllCmd = $"exec {serverContainer} sh -c \"" +
                "pkill -TERM -f dotnet 2>/dev/null; " +
                "pkill -TERM -f 'tee /tmp' 2>/dev/null; " +
                "sleep 1; " +
                "pkill -KILL -f dotnet 2>/dev/null; " +
                "pkill -KILL -f 'tee /tmp' 2>/dev/null; " +
                "pkill -KILL -f 'sh -c.*stdbuf' 2>/dev/null; " +
                "exit 0\"";
            var clientKillAllCmd = $"exec {clientContainer} sh -c \"" +
                "pkill -TERM -f dotnet 2>/dev/null; " +
                "pkill -TERM -f 'tee /tmp' 2>/dev/null; " +
                "sleep 1; " +
                "pkill -KILL -f dotnet 2>/dev/null; " +
                "pkill -KILL -f 'tee /tmp' 2>/dev/null; " +
                "pkill -KILL -f 'sh -c.*stdbuf' 2>/dev/null; " +
                "exit 0\"";
            
            try { _dockerExecutor.ExecDockerCommand(serverKillAllCmd, 15000); } catch { }
            try { _dockerExecutor.ExecDockerCommand(clientKillAllCmd, 15000); } catch { }
            
            // Step 2: Kill sleep processes that keep input pipes open
            // These are created by StartApplicationInContainer to keep the named pipe open
            try { _dockerExecutor.ExecDockerCommand($"exec {serverContainer} sh -c \"pkill -KILL sleep 2>/dev/null; exit 0\"", 5000); } catch { }
            try { _dockerExecutor.ExecDockerCommand($"exec {clientContainer} sh -c \"pkill -KILL sleep 2>/dev/null; exit 0\"", 5000); } catch { }
            
            // Step 3: Wait a moment for processes to fully terminate before removing files
            // This prevents race conditions where files are recreated by dying processes
            await Task.Delay(500);
            
            // Step 4: Remove ALL files from /apps folder and temp files (DLLs, logs, pipes)
            // This effectively resets the container state without disposing it
            var serverCleanFilesCmd = $"exec {serverContainer} sh -c \"rm -rf /apps/* /tmp/*.pid /tmp/*.port /tmp/*_output.log /tmp/*_input_pipe 2>/dev/null; exit 0\"";
            var clientCleanFilesCmd = $"exec {clientContainer} sh -c \"rm -rf /apps/* /tmp/*.pid /tmp/*.port /tmp/*_output.log /tmp/*_input_pipe 2>/dev/null; exit 0\"";
            
            try { _dockerExecutor.ExecDockerCommand(serverCleanFilesCmd, 5000); } catch { }
            try { _dockerExecutor.ExecDockerCommand(clientCleanFilesCmd, 5000); } catch { }
            
            Console.WriteLine("[Cleanup] Processes killed, files removed from containers");
            
            // Step 5: Clear network captures from BOTH NetworkMonitor AND RunContext
            // CRITICAL: Both must be cleared to prevent previous test case data from appearing
            _networkMonitor?.ClearCaptures();
            _runContext.ClearNetworkCaptures();
            
            // Step 6: Clear console manager logs
            _consoleManager.ClearAllLogs();
            
            // Step 7: Wait for port release INSIDE the container (not just host)
            // The port binding is inside the container, so we check there
            var checkPortCmd = $"exec {serverContainer} sh -c \"" +
                "timeout=30; " +
                "while [ $timeout -gt 0 ] && (netstat -tuln 2>/dev/null | grep -q ':{hostPort}' || ss -tuln 2>/dev/null | grep -q ':{hostPort}'); do " +
                "sleep 0.5; " +
                "timeout=$((timeout - 1)); " +
                "done; " +
                "exit 0\"";
            try { _dockerExecutor.ExecDockerCommand(checkPortCmd, 20000); } catch { }
            
            // Step 8: Also verify host port is available (since server port is exposed)
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 10)
            {
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, hostPort);
                    listener.Start();
                    listener.Stop();
                    Console.WriteLine($"[Cleanup] Port {hostPort} is now available on host");
                    break;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
            
            // Give a moment for everything to settle
            await Task.Delay(500);
            Console.WriteLine("[Cleanup] Cleanup complete, ready for next test case");
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
            
            await Task.Delay(2000);
            
            // Recreate the database container
            await SetupDatabaseContainerAsync(config);
            
            Console.WriteLine($"[Database] Database container reset complete");
        }
        
        private async Task CleanupContainersAsync(string serverContainer, string clientContainer)
        {
            _consoleManager.RemoveAllAttachments();
            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }
            await Task.Delay(200);
        }
        
        #endregion
        
        #region Result Writing
        
        /// <summary>
        /// Writes test case result to GradeDetail.xlsx in the EXACT SampleLogging format:
        /// - User sheet: Stage, Input, Action, DataType, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt
        /// - Client sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ClientStdout
        /// - Server sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ServerStdout
        /// - Network sheet: Format depends on protocol parameter:
        ///   - TCP: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        ///   - HTTP: Stage, Time, Info, Source, Destination, Flags, State, URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// - Database sheet: (empty)
        /// </summary>
        /// <param name="tcResultPath">Path to save test case results</param>
        /// <param name="tcName">Test case name</param>
        /// <param name="result">Test case result data</param>
        /// <param name="protocol">Protocol type (TCP or HTTP) - determines Network sheet format</param>
        private async Task WriteTestCaseResultAsync(string tcResultPath, string tcName, TestCaseResult result, string protocol = "TCP")
        {
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();
            
            // Determine if this is HTTP protocol
            bool isHttpProtocol = protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase);
            
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
            // Format depends on protocol: TCP or HTTP
            var netWs = wb.Worksheets.Add("Network");
            
            if (isHttpProtocol)
            {
                // HTTP format with HTTP-specific columns
                SetNetworkSheetHeadersHttp(netWs);
                int netRow = 2;
                foreach (var packet in result.NetworkCaptures)
                {
                    // Determine Info column based on packet content
                    string info = "HTTP";
                    if (!string.IsNullOrEmpty(packet.HttpMethod))
                        info = "HTTP";
                    else if (!string.IsNullOrEmpty(packet.HttpStatus))
                        info = "HTTP";
                    
                    netWs.Cell(netRow, 1).Value = packet.Stage;  // Stage
                    netWs.Cell(netRow, 2).Value = packet.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");  // Time
                    netWs.Cell(netRow, 3).Value = info;  // Info
                    netWs.Cell(netRow, 4).Value = $"::1:{packet.SourcePort}";  // Source (IPv6 localhost format for HTTP)
                    netWs.Cell(netRow, 5).Value = $"::1:{packet.DestinationPort}";  // Destination
                    netWs.Cell(netRow, 6).Value = packet.Flags;  // Flags
                    netWs.Cell(netRow, 7).Value = packet.State;  // State
                    netWs.Cell(netRow, 8).Value = packet.HttpUri ?? "";  // URI
                    netWs.Cell(netRow, 9).Value = packet.HttpHost ?? "";  // Host
                    netWs.Cell(netRow, 10).Value = packet.HttpMethod ?? "";  // Method
                    netWs.Cell(netRow, 11).Value = packet.HttpStatus ?? "";  // Status
                    netWs.Cell(netRow, 12).Value = packet.HttpVersion ?? "";  // HttpVersion
                    netWs.Cell(netRow, 13).Value = packet.HttpHeaders ?? "";  // HttpHeaders
                    netWs.Cell(netRow, 14).Value = packet.HttpBody ?? "";  // HttpBody
                    netWs.Cell(netRow, 15).Value = packet.SourceRole;  // SourceRole
                    netWs.Cell(netRow, 16).Value = packet.DestinationRole;  // DestinationRole
                    netWs.Cell(netRow, 17).Value = packet.Flags;  // ActualFlags
                    netWs.Cell(netRow, 18).Value = packet.State;  // ActualState
                    netWs.Cell(netRow, 19).Value = packet.SourceRole;  // ActualSourceRole
                    netWs.Cell(netRow, 20).Value = packet.DestinationRole;  // ActualDestRole
                    netWs.Cell(netRow, 21).Value = packet.Data ?? "";  // ActualData
                    netWs.Cell(netRow, 22).Value = "PASS";  // NetworkResult
                    netRow++;
                }
                
                // If no captures but we have expected network flows, log them as FAIL
                if (result.NetworkCaptures.Count == 0 && result.NetworkComparisons.Count > 0)
                {
                    foreach (var comp in result.NetworkComparisons)
                    {
                        netWs.Cell(netRow, 1).Value = comp.Stage;
                        netWs.Cell(netRow, 6).Value = comp.Expected;  // Expected flags
                        netWs.Cell(netRow, 22).Value = "FAIL";  // NetworkResult
                        netRow++;
                    }
                }
            }
            else
            {
                // TCP format
                SetNetworkSheetHeadersTcp(netWs);
                int netRow = 2;
                foreach (var packet in result.NetworkCaptures)
                {
                    netWs.Cell(netRow, 1).Value = packet.Stage;  // Stage
                    netWs.Cell(netRow, 2).Value = packet.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");  // Time
                    netWs.Cell(netRow, 3).Value = "TCP";  // Info
                    netWs.Cell(netRow, 4).Value = $"127.0.0.1:{packet.SourcePort}";  // Source
                    netWs.Cell(netRow, 5).Value = $"127.0.0.1:{packet.DestinationPort}";  // Destination
                    netWs.Cell(netRow, 6).Value = packet.Flags;  // Flags
                    netWs.Cell(netRow, 7).Value = packet.State;  // State
                    netWs.Cell(netRow, 8).Value = packet.Data ?? "";  // Data
                    netWs.Cell(netRow, 9).Value = packet.SourceRole;  // SourceRole
                    netWs.Cell(netRow, 10).Value = packet.DestinationRole;  // DestinationRole
                    netWs.Cell(netRow, 11).Value = packet.Flags;  // ActualFlags
                    netWs.Cell(netRow, 12).Value = packet.State;  // ActualState
                    netWs.Cell(netRow, 13).Value = packet.SourceRole;  // ActualSourceRole
                    netWs.Cell(netRow, 14).Value = packet.DestinationRole;  // ActualDestRole
                    netWs.Cell(netRow, 15).Value = packet.Data ?? "";  // ActualData
                    netWs.Cell(netRow, 16).Value = "PASS";  // NetworkResult
                    netRow++;
                }
                
                // If no captures but we have expected network flows, log them as FAIL
                if (result.NetworkCaptures.Count == 0 && result.NetworkComparisons.Count > 0)
                {
                    foreach (var comp in result.NetworkComparisons)
                    {
                        netWs.Cell(netRow, 1).Value = comp.Stage;
                        netWs.Cell(netRow, 6).Value = comp.Expected;  // Expected flags
                        netWs.Cell(netRow, 16).Value = "FAIL";  // NetworkResult
                        netRow++;
                    }
                }
            }
            netWs.Columns().AdjustToContents();
            
            wb.SaveAs(detailPath);
            
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
        /// Sets Network sheet headers for TCP protocol format.
        /// Columns: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// </summary>
        private static void SetNetworkSheetHeadersTcp(IXLWorksheet ws)
        {
            var headers = new[] { 
                "Stage", "Time", "Info", "Source", "Destination", "Flags", "State", "Data", 
                "SourceRole", "DestinationRole", "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", 
                "ActualData", "NetworkResult" 
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        /// <summary>
        /// Sets Network sheet headers for HTTP protocol format.
        /// Columns: Stage, Time, Info, Source, Destination, Flags, State, URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// </summary>
        private static void SetNetworkSheetHeadersHttp(IXLWorksheet ws)
        {
            var headers = new[] { 
                "Stage", "Time", "Info", "Source", "Destination", "Flags", "State", 
                "URI", "Host", "Method", "Status", "HttpVersion", "HttpHeaders", "HttpBody",
                "SourceRole", "DestinationRole", 
                "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData", "NetworkResult" 
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
            Console.WriteLine($"[DockerGrading] {message}");
            ProgressUpdated?.Invoke(this, new GradingProgressEventArgs(message));
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
        
        public int GradingTimeoutSeconds { get; set; } = 60;
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
    }
    
    internal class ExpectedNetworkFlow
    {
        public int Stage { get; set; }
        public string? Flags { get; set; }
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
        public List<NetworkCaptureRecord> NetworkCaptures { get; set; } = new();
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
    /// Network capture record for Network sheet - supports both TCP and HTTP protocols.
    /// TCP format: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
    /// HTTP format: Stage, Time, Info, Source, Destination, Flags, State, URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
    /// </summary>
    public class NetworkCaptureRecord
    {
        public int Stage { get; set; }
        public DateTime Timestamp { get; set; }
        public string Flags { get; set; } = "";
        public string State { get; set; } = "";
        public string SourceRole { get; set; } = "";
        public string DestinationRole { get; set; } = "";
        public string? Data { get; set; }
        public int SourcePort { get; set; }
        public int DestinationPort { get; set; }
        
        // HTTP-specific fields (populated when protocol is HTTP)
        public string? HttpUri { get; set; }
        public string? HttpHost { get; set; }
        public string? HttpMethod { get; set; }
        public string? HttpStatus { get; set; }
        public string? HttpVersion { get; set; }
        public string? HttpHeaders { get; set; }
        public string? HttpBody { get; set; }
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
