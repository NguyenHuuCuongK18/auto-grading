using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Diagnostics;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Orchestrates test case execution in discrete steps:
    /// 1. Environment setup (database reset, appsettings generation, port configuration)
    /// 2. Test kit reading (parse detail steps)
    /// 3. Network monitor start (must start BEFORE client/server to capture full network flow)
    /// 4. Process execution (start server/client)
    /// 5. Grading (execute steps and comparisons)
    /// 6. Logging (record results)
    /// 7. Cleanup (stop processes, stop network monitor, finalize)
    /// 
    /// NOTE: Network monitor replaces the old middleware proxy approach.
    /// The monitor sniffs packets on the configured port without intercepting traffic.
    /// Both client and server connect directly to each other on the same port.
    /// </summary>
    public sealed class TestCaseOrchestrator
    {
        private readonly IFileService _files;
        private readonly IEnvironmentResetService _env;
        private readonly ITestCaseParser _parser;
        private readonly IExecutor _exec;
        private readonly IReportService _report;
        private readonly IExecutableManager _proc;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly IAppsettingsCreationService _appsettings;
        
        // Configured port for all communication (server listen, client connect, monitor sniff)
        private int _monitorPort;
        private string _protocol = NetworkKeywords.Protocol_TCP;

        public TestCaseOrchestrator(
            IFileService files,
            IEnvironmentResetService env,
            ITestCaseParser parser,
            IExecutor exec,
            IReportService report,
            IExecutableManager proc,
            INetworkMonitorService? networkMonitor,
            IDetailLogService log,
            IRunContext run,
            IAppsettingsCreationService appsettings)
        {
            _files = files;
            _env = env;
            _parser = parser;
            _exec = exec;
            _report = report;
            _proc = proc;
            _networkMonitor = networkMonitor;
            _log = log;
            _run = run;
            _appsettings = appsettings;
        }

        /// <summary>
        /// Step 1: Setup environment for test case
        /// - Reset database with appropriate script
        /// - Generate appsettings.json files with single port for client and server
        /// - Configure and START network monitor for packet capture (must start before any processes)
        /// </summary>
        public async Task<(bool Success, string Message)> SetupEnvironmentAsync(
            TestCaseDefinition testCase,
            SuiteDefinition suite,
            ExecuteSuiteArgs args,
            string? clientExePath,
            string? serverExePath,
            CancellationToken ct)
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 1] Setting up environment...");
                
                // Store protocol type for network monitoring
                _protocol = suite.Protocol ?? NetworkKeywords.Protocol_TCP;
                
                // Use test case environment if available, otherwise fall back to suite environment
                var envConfig = testCase.Environment ?? suite.Environment;
                
                // Always generate appsettings from header
                // This now generates a single port for both client and server
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} {AppsettingKeywords.MSG_GENERATING_FROM_HEADER}");
                var (proxyPort, serverPort) = _appsettings.GenerateAppsettings(
                    suite.DatabaseConfig, 
                    clientExePath, 
                    serverExePath, 
                    envConfig, 
                    suite.Protocol);
                
                // Store the monitor port (same as server port in new architecture)
                _monitorPort = serverPort;
                
                // Configure and START network monitor IMMEDIATELY
                // This ensures we capture ALL network traffic from the very beginning
                // The monitor will be flushed before each user action step to clear health check noise
                if (_networkMonitor != null)
                {
                    _networkMonitor.MonitorPort = _monitorPort;
                    _networkMonitor.ProtocolType = _protocol;
                    Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Configured for port {_monitorPort} ({_protocol} mode)");
                    
                    // Start network monitor NOW - before any processes
                    Console.WriteLine($"{NetworkKeywords.LOG_PREFIX_MONITOR} Starting network monitor early (before processes)...");
                    _networkMonitor.ClearCaptures();
                    await _networkMonitor.StartAsync(ct);
                }
                
                // Configure executor with the server port for health checks
                _exec.ConfigureServerPort(serverPort);
                
                // Determine database script path - use from environment or test case specific
                string? dbScriptPath = null;
                
                // First check test case specific database path
                if (!string.IsNullOrWhiteSpace(testCase.Environment?.DatabaseFilePath))
                {
                    var normalizedPath = testCase.Environment.DatabaseFilePath.Replace('\\', Path.DirectorySeparatorChar);
                    var testCaseDbPath = Path.Combine(suite.RootDirectory, normalizedPath);
                    if (File.Exists(testCaseDbPath))
                    {
                        dbScriptPath = testCaseDbPath;
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using test case database script: {dbScriptPath}");
                    }
                }
                
                // If not found, try suite environment database path
                if (string.IsNullOrWhiteSpace(dbScriptPath) && !string.IsNullOrWhiteSpace(suite.Environment?.DatabaseFilePath))
                {
                    var normalizedPath = suite.Environment.DatabaseFilePath.Replace('\\', Path.DirectorySeparatorChar);
                    var suiteDbPath = Path.Combine(suite.RootDirectory, normalizedPath);
                    if (File.Exists(suiteDbPath))
                    {
                        dbScriptPath = suiteDbPath;
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using suite database script: {dbScriptPath}");
                    }
                }
                
                // Reset database using effective environment config
                await _env.RunDatabaseResetAsync(dbScriptPath, suite.DatabaseConfig, false, envConfig, ct);
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 1] Environment setup completed");
                return (true, "Environment setup successful");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 1] Environment setup failed: {ex.Message}");
                return (false, $"Environment setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Step 2: Read test kit information
        /// - Parse detail.xlsx to get test steps
        /// - Initialize output directory
        /// - Begin logging
        /// </summary>
        public (bool Success, string Message, IReadOnlyList<Step> Steps) ReadTestKitInfo(
            TestCaseDefinition testCase,
            string outDir)
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 2] Reading test kit information...");
                
                // Ensure output directory exists
                _files.EnsureDirectory(outDir);
                _env.ClearFolder(outDir);
                
                // Parse detail steps
                var steps = _parser.ParseDetail(testCase.DetailPath, testCase.Name);
                if (steps.Count == 0)
                {
                    return (false, "Test case does not contain any steps.", new List<Step>());
                }
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_LOADED_STEPS, steps.Count)}");
                
                _run.ResultRoot = outDir;
                
                // Begin Excel case log
                _log.BeginCase(outDir, testCase.Name, testCase.DetailPath, testCase.Mark);
                _log.SetTestCaseMark(testCase.Mark);
                
                // Calculate comparison step count for grading
                var compareCount = steps.Count(s =>
                    s.Action != null && (
                        string.Equals(s.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    ) 
                    && !string.Equals(s.Stage, StageKeywords.Input, StringComparison.OrdinalIgnoreCase)
                    && !s.Id.StartsWith("IC-", StringComparison.OrdinalIgnoreCase)
                );
                _log.SetTotalCompareSteps(compareCount);
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 2] Test kit information loaded successfully");
                return (true, "Test kit information loaded", steps);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 2] Failed to read test kit: {ex.Message}");
                return (false, $"Failed to read test kit: {ex.Message}", new List<Step>());
            }
        }

        /// <summary>
        /// Step 3: Initialize processes for execution.
        /// Network monitor is already started in SetupEnvironmentAsync to capture full traffic.
        /// - Initialize executable manager with client/server paths
        /// - Flush any stale network captures
        /// </summary>
        public async Task<(bool Success, string Message)> InitializeProcessesAsync(
            string? clientExePath,
            string? serverExePath,
            CancellationToken ct = default)
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 3] Initializing processes...");
                
                if (string.IsNullOrWhiteSpace(clientExePath))
                {
                    return (false, "Client executable path is not specified");
                }
                
                if (string.IsNullOrWhiteSpace(serverExePath))
                {
                    return (false, "Server executable path is not specified");
                }
                
                // Initialize executable manager
                // NOTE: Do NOT flush network captures here - some projects initialize connections 
                // immediately when processes start, and we need to capture that traffic
                _proc.Init(clientExePath, serverExePath);
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 3] Processes initialized");
                return (true, "Processes initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 3] Failed to initialize processes: {ex.Message}");
                return (false, $"Failed to initialize processes: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Flush network captures - used to clear stale data BEFORE any processes start.
        /// WARNING: Do NOT call this after client/server processes have started as it will 
        /// lose important connection initialization traffic.
        /// </summary>
        public void FlushNetworkCaptures()
        {
            if (_networkMonitor != null)
            {
                _networkMonitor.ClearCaptures();
            }
            _run.ClearNetworkCaptures();
        }
        
        /// <summary>
        /// [DEPRECATED] Synchronous version for backward compatibility.
        /// Use InitializeProcessesAsync instead.
        /// </summary>
        [Obsolete("Use InitializeProcessesAsync instead")]
        public (bool Success, string Message) InitializeProcesses(
            string? clientExePath,
            string? serverExePath)
        {
            return InitializeProcessesAsync(clientExePath, serverExePath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Step 4: Execute and grade test steps
        /// - Run through each test step
        /// - Update network monitor context for packet association
        /// - Flush network captures before action steps to clear health check noise
        /// - Perform comparisons and validations
        /// - Log results for grading
        /// </summary>
        public async Task<(bool Success, string Message, List<StepResult> Results)> ExecuteAndGradeStepsAsync(
            IReadOnlyList<Step> steps,
            ExecuteSuiteArgs args,
            CancellationToken ct)
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 4] Executing and grading test steps...");
                
                var results = new List<StepResult>();
                int? previousStage = null;
                bool hasSeenInputStep = false;
                bool hasAddedComparisonDelay = false;
                bool serverStarted = false;  // Track if server has started
                bool clientStarted = false;  // Track if client has started
                
                // Start a background task to monitor process status
                var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bool processesHaveStarted = false;
                
                var monitorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!monitorCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, monitorCts.Token);
                            
                            // Track if processes have been started at least once
                            if (_proc.IsClientRunning || _proc.IsServerRunning)
                            {
                                processesHaveStarted = true;
                            }
                            
                            // Only check for process exit after they have started
                            // This prevents premature monitor shutdown before processes begin
                            if (processesHaveStarted && !_proc.IsClientRunning && !_proc.IsServerRunning)
                            {
                                // Both processes have exited after being started
                                break;
                            }
                        }
                    }
                    catch { }
                }, monitorCts.Token);
                
                foreach (var step in steps)
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(TimeSpan.FromSeconds(10));
                    
                    // Identify action types for tracking
                    var isServerStart = string.Equals(step.Action, ActionKeywords.ServerStart, StringComparison.OrdinalIgnoreCase);
                    var isClientStart = string.Equals(step.Action, ActionKeywords.ClientStart, StringComparison.OrdinalIgnoreCase);
                    var isClientInput = string.Equals(step.Action, ActionKeywords.ClientInput, StringComparison.OrdinalIgnoreCase);
                    
                    // NOTE: No network capture flush is needed here because:
                    // 1. The server health check uses IsTcpPortInListeningState which is a pure local
                    //    socket bind operation - it does NOT generate any network traffic
                    // 2. CLIENTSTART and SERVERSTART are test kit steps, so any network traffic they
                    //    generate (e.g., automatic connections on startup) is intentional and should
                    //    be captured as part of the test

                    var currentStage = TryParseStage(step.Id);
                    
                    // Update network monitor context for packet association
                    if (_networkMonitor != null)
                    {
                        _networkMonitor.SetCurrentContext(step.QuestionCode, step.Stage);
                    }
                    
                    // Stage change delay
                    if (previousStage.HasValue && currentStage.HasValue && currentStage != previousStage)
                    {
                        await Task.Delay(500, ct);
                    }
                    
                    // Track input steps and process start states
                    if (isClientInput)
                    {
                        hasSeenInputStep = true;
                    }
                    
                    // Add delay before first comparison
                    bool isComparison = step.Action != null && (
                        string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    );
                    
                    if (isComparison && hasSeenInputStep && !hasAddedComparisonDelay)
                    {
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {LoggingKeywords.MSG_TESTCASE_EXTRA_DELAY}");
                        await Task.Delay(1000, ct);
                        hasAddedComparisonDelay = true;
                    }
                    
                    previousStage = currentStage;

                    _run.CurrentQuestionCode = step.QuestionCode;
                    _run.CurrentStage = currentStage;
                    _run.CurrentStageLabel = step.Stage;

                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_STEP} {string.Format(LoggingKeywords.MSG_STEP_EXECUTING, step.Action, step.Stage, step.Id)}");

                    var sw = Stopwatch.StartNew();
                    var (ok, msg) = await _exec.ExecuteAsync(step, args, stepCts.Token);
                    sw.Stop();
                    
                    // Track process start states after execution
                    if (isServerStart && ok) serverStarted = true;
                    if (isClientStart && ok) clientStarted = true;
                    
                    var result = new StepResult { Step = step, Passed = ok, Message = msg, DurationMs = sw.Elapsed.TotalMilliseconds };
                    results.Add(result);
                    
                    // Log step grade
                    bool isComparisonStep = step.Action != null && (
                        string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    ) && !step.Id.StartsWith(GradingKeywords.StepPrefix_InputClient, StringComparison.OrdinalIgnoreCase);
                    
                    string errorCode = Domain.Errors.ErrorCodes.NONE;
                    if (!ok)
                    {
                        if (string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase))
                            errorCode = Domain.Errors.ErrorCodes.JSON_MISMATCH;
                        else if (string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase))
                            errorCode = Domain.Errors.ErrorCodes.CSV_MISMATCH;
                        else if (string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase))
                            errorCode = Domain.Errors.ErrorCodes.TEXT_MISMATCH;
                        else if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                            errorCode = Domain.Errors.ErrorCodes.TIMEOUT;
                        else
                            errorCode = Domain.Errors.ErrorCodes.UNKNOWN;
                    }
                    
                    double pointsPossible = isComparisonStep ? 1.0 : 0.0;
                    _log.LogStepGrade(step, ok, msg, 0, pointsPossible, sw.Elapsed.TotalMilliseconds, 
                        errorCode, null, null);
                    
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_STEP} {string.Format(LoggingKeywords.MSG_STEP_RESULT, ok ? LoggingKeywords.RESULT_PASS : LoggingKeywords.RESULT_FAIL, msg, sw.Elapsed.TotalMilliseconds)}");
                }
                
                // Stop monitoring
                monitorCts.Cancel();
                try { await monitorTask; } catch { }
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 4] Test steps execution completed");
                return (true, "All steps executed", results);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 4] Failed to execute steps: {ex.Message}");
                return (false, $"Failed to execute steps: {ex.Message}", new List<StepResult>());
            }
        }

        /// <summary>
        /// Step 5: Write results and finalize logging
        /// - Write test case results
        /// - End case logging
        /// </summary>
        public async Task<(bool Success, string Message)> WriteResultsAsync(
            string outDir,
            string questionCode,
            List<StepResult> results,
            CancellationToken ct)
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 5] Writing results...");
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_WRITING_RESULTS, outDir)}");
                await _report.WriteQuestionResultAsync(outDir, questionCode, results, ct);

                _log.EndCase();
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 5] Results written successfully");
                return (true, "Results written");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 5] Failed to write results: {ex.Message}");
                return (false, $"Failed to write results: {ex.Message}");
            }
        }

        /// <summary>
        /// Step 6: Cleanup processes and network monitor
        /// - Stop all running processes (but NOT Docker containers - those are reused)
        /// - Clear console output buffers for next test case
        /// - Stop network monitor (finalizes captured data)
        /// - Clear network captures for next test case
        /// </summary>
        public async Task<(bool Success, string Message)> CleanupAsync()
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 6] Cleaning up processes...");
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {LoggingKeywords.MSG_TESTCASE_CLEANING_PROCESSES}");
                
                // Stop all processes but keep Docker containers running for reuse
                try { await _proc.StopAllAsync(); } catch { }
                
                // Clear console buffers for next test case - each test case should start fresh
                try { _proc.ClearConsoleBuffers(); } catch { }
                
                // Stop network monitor
                if (_networkMonitor != null)
                {
                    try { await _networkMonitor.StopAsync(); } catch { }
                    
                    // Clear network captures for next test case
                    try { _networkMonitor.ClearCaptures(); } catch { }
                }
                
                // Clear RunContext network captures
                try { _run.ClearNetworkCaptures(); } catch { }
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 6] Cleanup completed");
                return (true, "Cleanup successful");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 6] Cleanup failed: {ex.Message}");
                return (false, $"Cleanup failed: {ex.Message}");
            }
        }

        private static int? TryParseStage(string id)
        {
            var lastDash = id?.LastIndexOf('-') ?? -1;
            if (lastDash >= 0 && lastDash + 1 < id!.Length && int.TryParse(id.Substring(lastDash + 1), out var s)) return s;
            return null;
        }
    }
}
