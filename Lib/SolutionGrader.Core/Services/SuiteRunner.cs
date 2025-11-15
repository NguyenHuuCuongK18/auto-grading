using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Core.Domain.Errors;
using System.Diagnostics;

namespace SolutionGrader.Core.Services
{
    public sealed class SuiteRunner
    {
        private readonly IFileService _files;
        private readonly IEnvironmentResetService _env;
        private readonly ITestSuiteLoader _suite;
        private readonly ITestCaseParser _parser;
        private readonly IExecutor _exec;
        private readonly IReportService _report;
        private readonly IExecutableManager _proc;
        private readonly IMiddlewareService _mw;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly IAppsettingsCreationService _appsettings;

        public SuiteRunner(
            IFileService files,
            IEnvironmentResetService env,
            ITestSuiteLoader suite,
            ITestCaseParser parser,
            IExecutor exec,
            IReportService report,
            IExecutableManager proc,
            IMiddlewareService mw,
            IDetailLogService log,
            IRunContext run,
            IAppsettingsCreationService appsettings)
        {
            _files = files; _env = env; _suite = suite; _parser = parser; _exec = exec; _report = report; _proc = proc; _mw = mw; _log = log; _run = run; _appsettings = appsettings;
        }

        public async Task<int> ExecuteSuiteAsync(ExecuteSuiteArgs args, CancellationToken ct = default)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_LOADING, args.SuitePath)}");
            var def = _suite.Load(args.SuitePath);
            args.Protocol = def.Protocol;
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_PROTOCOL, args.Protocol)}");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_CASES_FOUND, def.Cases.Count)}");
            _files.EnsureDirectory(args.ResultRoot);

            foreach (var q in def.Cases)
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_STARTING, q.Name, q.Mark)}");

                // Use command-line executables if provided, otherwise use from environment
                string? clientExePath = args.ClientExePath;
                string? serverExePath = args.ServerExePath;
                
                // If not provided via command-line, try to use from environment
                if (string.IsNullOrWhiteSpace(clientExePath) && !string.IsNullOrWhiteSpace(def.Environment?.GivenClientPath))
                {
                    clientExePath = def.Environment.GivenClientPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using client from environment: {clientExePath}");
                }
                
                if (string.IsNullOrWhiteSpace(serverExePath) && !string.IsNullOrWhiteSpace(def.Environment?.GivenServerPath))
                {
                    serverExePath = def.Environment.GivenServerPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using server from environment: {serverExePath}");
                }
                
                // Handle Grade_Content field to determine which executable to use
                if (!string.IsNullOrWhiteSpace(q.GradeContent))
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Grade_Content: {q.GradeContent}");
                    
                    if (q.GradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                    {
                        // Grading client only - use given/reference server if available
                        if (!string.IsNullOrWhiteSpace(q.Environment?.GivenServerPath))
                        {
                            serverExePath = q.Environment.GivenServerPath;
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference server: {serverExePath}");
                        }
                    }
                    else if (q.GradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    {
                        // Grading server only - use given/reference client if available
                        if (!string.IsNullOrWhiteSpace(q.Environment?.GivenClientPath))
                        {
                            clientExePath = q.Environment.GivenClientPath;
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference client: {clientExePath}");
                        }
                    }
                }

                // Always generate appsettings from header
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} {AppsettingKeywords.MSG_GENERATING_FROM_HEADER}");
                var (proxyPort, serverPort) = _appsettings.GenerateAppsettings(def.DatabaseConfig, clientExePath, serverExePath, q.Environment);
                
                // Configure middleware with the generated ports
                _mw.ConfigurePorts(proxyPort, serverPort);
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS} {string.Format(AppsettingKeywords.MSG_CONFIGURED_MIDDLEWARE, proxyPort, serverPort)}");
                
                // Determine database script path - use from environment or test case specific
                string? dbScriptPath = null;
                
                // First check test case specific database path
                if (!string.IsNullOrWhiteSpace(q.Environment?.DatabaseFilePath))
                {
                    // Normalize path separators for cross-platform compatibility
                    var normalizedPath = q.Environment.DatabaseFilePath.Replace('\\', Path.DirectorySeparatorChar);
                    var testCaseDbPath = Path.Combine(def.RootDirectory, normalizedPath);
                    if (File.Exists(testCaseDbPath))
                    {
                        dbScriptPath = testCaseDbPath;
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using test case database script: {dbScriptPath}");
                    }
                }
                
                // If not found, try suite environment database path
                if (string.IsNullOrWhiteSpace(dbScriptPath) && !string.IsNullOrWhiteSpace(def.Environment?.DatabaseFilePath))
                {
                    // Normalize path separators for cross-platform compatibility
                    var normalizedPath = def.Environment.DatabaseFilePath.Replace('\\', Path.DirectorySeparatorChar);
                    var suiteDbPath = Path.Combine(def.RootDirectory, normalizedPath);
                    if (File.Exists(suiteDbPath))
                    {
                        dbScriptPath = suiteDbPath;
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using suite database script: {dbScriptPath}");
                    }
                }
                
                // Default timeout to 10 seconds, and database reset to false (local)
                await _env.RunDatabaseResetAsync(dbScriptPath, def.DatabaseConfig, false, q.Environment, ct);

                var outDir = Path.Combine(args.ResultRoot, q.Name);
                _files.EnsureDirectory(outDir);
                _env.ClearFolder(outDir);

                _proc.Init(clientExePath!, serverExePath!);

                var steps = _parser.ParseDetail(q.DetailPath, q.Name);
                if (steps.Count == 0) throw new InvalidOperationException("Test case does not contain any steps.");
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_LOADED_STEPS, steps.Count)}");

                _run.ResultRoot = outDir;

                // NEW: Begin Excel case log; pass the case's Detail.xlsx template path and mark
                _log.BeginCase(outDir, q.Name, q.DetailPath, q.Mark);
                _log.SetTestCaseMark(q.Mark);

                // Inform the log service how many compare steps will be executed so it can
                // calculate per-step points even if the Detail.xlsx template contains no data rows.
                // Only count comparison steps that are NOT from InputClients (IC-* prefix)
                // Focus on test case flow validation, not input validation
                var compareCount = steps.Count(s =>
                    s.Action != null && (
                        string.Equals(s.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    ) 
                    && !string.Equals(s.Stage, StageKeywords.Input, StringComparison.OrdinalIgnoreCase)
                    && !s.Id.StartsWith("IC-", StringComparison.OrdinalIgnoreCase) // Exclude InputClients
                );
                _log.SetTotalCompareSteps(compareCount);

                var results = new List<StepResult>();
                int? previousStage = null;
                bool hasSeenInputStep = false;
                bool hasAddedComparisonDelay = false;
                
                foreach (var step in steps)
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(TimeSpan.FromSeconds(10)); // Default timeout: 10 seconds

                    var currentStage = TryParseStage(step.Id);
                    
                    // If stage changed, give a delay for async/buffered output to be captured
                    if (previousStage.HasValue && currentStage.HasValue && currentStage != previousStage)
                    {
                        await Task.Delay(500, ct); // 500ms buffer for async output
                    }
                    
                    // Track if we've seen input steps
                    if (string.Equals(step.Action, ActionKeywords.ClientInput, StringComparison.OrdinalIgnoreCase))
                    {
                        hasSeenInputStep = true;
                    }
                    
                    // Add extra delay before first comparison step if we've sent input
                    // This allows time for async HTTP responses to complete and be captured
                    bool isComparison = step.Action != null && (
                        string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    );
                    
                    if (isComparison && hasSeenInputStep && !hasAddedComparisonDelay)
                    {
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {LoggingKeywords.MSG_TESTCASE_EXTRA_DELAY}");
                        await Task.Delay(1000, ct); // Extra delay for HTTP responses
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
                    
                    // Log to detail service for grading
                    // Determine if this is a comparison step (has points)
                    // Exclude InputClients (IC-*) from grading - focus on test case flow only
                    bool isComparisonStep = step.Action != null && (
                        string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    ) && !step.Id.StartsWith(GradingKeywords.StepPrefix_InputClient, StringComparison.OrdinalIgnoreCase);
                    
                    // Determine error code from step action and result using action keywords
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
                    
                    double pointsPossible = isComparisonStep ? 1.0 : 0.0; // Actual points calculated by log service
                    _log.LogStepGrade(step, ok, msg, 0, pointsPossible, sw.Elapsed.TotalMilliseconds, 
                        errorCode, null, null);
                    
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_STEP} {string.Format(LoggingKeywords.MSG_STEP_RESULT, ok ? LoggingKeywords.RESULT_PASS : LoggingKeywords.RESULT_FAIL, msg, sw.Elapsed.TotalMilliseconds)}");
                }

                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_WRITING_RESULTS, outDir)}");
                await _report.WriteQuestionResultAsync(outDir, steps[0].QuestionCode, results, ct);

                _log.EndCase();

                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {LoggingKeywords.MSG_TESTCASE_CLEANING_PROCESSES}");
                try { await _proc.StopAllAsync(); } catch { }
                try { await _mw.StopAsync(); } catch { }
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_COMPLETED, q.Name)}\n");
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} All test cases completed successfully");
            return 1;
        }

        private static int? TryParseStage(string id)
        {
            var lastDash = id?.LastIndexOf('-') ?? -1;
            if (lastDash >= 0 && lastDash + 1 < id!.Length && int.TryParse(id.Substring(lastDash + 1), out var s)) return s;
            return null;
        }
    }
}
