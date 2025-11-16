using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Diagnostics;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Orchestrates test case execution in discrete steps similar to test-grader flow:
    /// 1. Environment setup (database reset, appsettings generation)
    /// 2. Test kit reading (parse detail steps)
    /// 3. Process execution (start server/client, run middleware)
    /// 4. Grading (execute steps and comparisons)
    /// 5. Logging (record results)
    /// 6. Cleanup (stop processes, finalize)
    /// </summary>
    public sealed class TestCaseOrchestrator
    {
        private readonly IFileService _files;
        private readonly IEnvironmentResetService _env;
        private readonly ITestCaseParser _parser;
        private readonly IExecutor _exec;
        private readonly IReportService _report;
        private readonly IExecutableManager _proc;
        private readonly IMiddlewareService _mw;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly IAppsettingsCreationService _appsettings;

        public TestCaseOrchestrator(
            IFileService files,
            IEnvironmentResetService env,
            ITestCaseParser parser,
            IExecutor exec,
            IReportService report,
            IExecutableManager proc,
            IMiddlewareService mw,
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
            _mw = mw;
            _log = log;
            _run = run;
            _appsettings = appsettings;
        }

        /// <summary>
        /// Step 1: Setup environment for test case
        /// - Reset database with appropriate script
        /// - Generate appsettings.json files
        /// - Configure ports for middleware and server
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
                
                // Always generate appsettings from header
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} {AppsettingKeywords.MSG_GENERATING_FROM_HEADER}");
                var (proxyPort, serverPort) = _appsettings.GenerateAppsettings(
                    suite.DatabaseConfig, 
                    clientExePath, 
                    serverExePath, 
                    testCase.Environment, 
                    suite.Protocol);
                
                // Configure middleware with the generated ports
                _mw.ConfigurePorts(proxyPort, serverPort);
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} {string.Format(AppsettingKeywords.MSG_CONFIGURED_MIDDLEWARE, proxyPort, serverPort)}");
                
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
                
                // Reset database
                await _env.RunDatabaseResetAsync(dbScriptPath, suite.DatabaseConfig, false, testCase.Environment, ct);
                
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
        /// Step 3: Initialize processes
        /// - Initialize executable manager with client/server paths
        /// </summary>
        public (bool Success, string Message) InitializeProcesses(
            string? clientExePath,
            string? serverExePath)
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
        /// Step 4: Execute and grade test steps
        /// - Run through each test step
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
                
                // Start a background task to monitor process status
                var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var monitorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!monitorCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, monitorCts.Token);
                            
                            if (!_proc.IsClientRunning || !_proc.IsServerRunning)
                            {
                                try { await _mw.StopAsync(); } catch { }
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

                    var currentStage = TryParseStage(step.Id);
                    
                    // Stage change delay
                    if (previousStage.HasValue && currentStage.HasValue && currentStage != previousStage)
                    {
                        await Task.Delay(500, ct);
                    }
                    
                    // Track input steps
                    if (string.Equals(step.Action, ActionKeywords.ClientInput, StringComparison.OrdinalIgnoreCase))
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
        /// Step 6: Cleanup processes and middleware
        /// - Stop all running processes
        /// - Stop middleware proxy
        /// </summary>
        public async Task<(bool Success, string Message)> CleanupAsync()
        {
            try
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} [Step 6] Cleaning up processes...");
                
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {LoggingKeywords.MSG_TESTCASE_CLEANING_PROCESSES}");
                try { await _proc.StopAllAsync(); } catch { }
                try { await _mw.StopAsync(); } catch { }
                
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
