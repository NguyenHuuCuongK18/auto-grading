using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
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
            IRunContext run)
        {
            _files = files; _env = env; _suite = suite; _parser = parser; _exec = exec; _report = report; _proc = proc; _mw = mw; _log = log; _run = run;
        }

        public async Task<int> ExecuteSuiteAsync(ExecuteSuiteArgs args, CancellationToken ct = default)
        {
            Console.WriteLine($"[Suite] Loading test suite from: {args.SuitePath}");
            var def = _suite.Load(args.SuitePath);
            args.Protocol = def.Protocol;
            Console.WriteLine($"[Suite] Protocol: {args.Protocol}");
            Console.WriteLine($"[Suite] Found {def.Cases.Count} test case(s)");
            _files.EnsureDirectory(args.ResultRoot);

            foreach (var q in def.Cases)
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n[TestCase] Starting: {q.Name} (Mark: {q.Mark})");

                _env.ReplaceAppsettings(args.ClientAppSettingsTemplate, args.ServerAppSettingsTemplate, args.ClientExePath, args.ServerExePath);
                await _env.RunDatabaseResetAsync(args.DatabaseScriptPath, ct);

                var outDir = Path.Combine(args.ResultRoot, q.Name);
                _files.EnsureDirectory(outDir);
                _env.ClearFolder(outDir);

                _proc.Init(args.ClientExePath, args.ServerExePath);

                var steps = _parser.ParseDetail(q.DetailPath, q.Name);
                if (steps.Count == 0) throw new InvalidOperationException("Test case does not contain any steps.");
                Console.WriteLine($"[TestCase] Loaded {steps.Count} step(s)");

                _run.ResultRoot = outDir;

                // NEW: Begin Excel case log; pass the case's Detail.xlsx template path and mark
                _log.BeginCase(outDir, q.Name, q.DetailPath, q.Mark);
                _log.SetTestCaseMark(q.Mark);

                // Inform the log service how many compare steps will be executed so it can
                // calculate per-step points even if the Detail.xlsx template contains no data rows.
                var compareCount = steps.Count(s =>
                    s.Action != null && (
                        string.Equals(s.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    ) && !string.Equals(s.Stage, "INPUT", StringComparison.OrdinalIgnoreCase)
                );
                _log.SetTotalCompareSteps(compareCount);

                var results = new List<StepResult>();
                int? previousStage = null;
                bool hasSeenInputStep = false;
                bool hasAddedComparisonDelay = false;
                
                foreach (var step in steps)
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stepCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, args.StageTimeoutSeconds)));

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
                        Console.WriteLine("[TestCase] Adding extra delay before comparisons to ensure async output is captured...");
                        await Task.Delay(1000, ct); // Extra delay for HTTP responses
                        hasAddedComparisonDelay = true;
                    }
                    
                    previousStage = currentStage;

                    _run.CurrentQuestionCode = step.QuestionCode;
                    _run.CurrentStage = currentStage;
                    _run.CurrentStageLabel = step.Stage;

                    Console.WriteLine($"[Step] Executing: {step.Action} (Stage: {step.Stage}, ID: {step.Id})");

                    var sw = Stopwatch.StartNew();
                    var (ok, msg) = await _exec.ExecuteAsync(step, args, stepCts.Token);
                    sw.Stop();
                    
                    var result = new StepResult { Step = step, Passed = ok, Message = msg, DurationMs = sw.Elapsed.TotalMilliseconds };
                    results.Add(result);
                    
                    // Log to detail service for grading
                    // Determine if this is a comparison step (has points)
                    bool isComparisonStep = step.Action != null && (
                        string.Equals(step.Action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(step.Action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase)
                    );
                    
                    // Determine error code from step action and result
                    string errorCode = "NONE";
                    if (!ok)
                    {
                        if (step.Action?.Contains("Compare", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (step.Action.Contains("Json", StringComparison.OrdinalIgnoreCase))
                                errorCode = "JSON_MISMATCH";
                            else if (step.Action.Contains("Csv", StringComparison.OrdinalIgnoreCase))
                                errorCode = "CSV_MISMATCH";
                            else
                                errorCode = "TEXT_MISMATCH";
                        }
                        else if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                            errorCode = "TIMEOUT";
                        else
                            errorCode = "UNKNOWN";
                    }
                    
                    double pointsPossible = isComparisonStep ? 1.0 : 0.0; // Actual points calculated by log service
                    _log.LogStepGrade(step, ok, msg, 0, pointsPossible, sw.Elapsed.TotalMilliseconds, 
                        errorCode, null, null);
                    
                    Console.WriteLine($"[Step] Result: {(ok ? "PASS" : "FAIL")} - {msg} ({sw.Elapsed.TotalMilliseconds:F0}ms)");
                }

                Console.WriteLine($"[TestCase] Writing results to: {outDir}");
                await _report.WriteQuestionResultAsync(outDir, steps[0].QuestionCode, results, ct);

                _log.EndCase();

                Console.WriteLine($"[TestCase] Cleaning up processes...");
                try { await _proc.StopAllAsync(); } catch { }
                try { await _mw.StopAsync(); } catch { }
                Console.WriteLine($"[TestCase] Completed: {q.Name}\n");
            }

            Console.WriteLine("[Suite] All test cases completed successfully");
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
