namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using System.Diagnostics;

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

    public SuiteRunner(
        IFileService files,
        IEnvironmentResetService env,
        ITestSuiteLoader suite,
        ITestCaseParser parser,
        IExecutor exec,
        IReportService report,
        IExecutableManager proc,
        IMiddlewareService mw)
    {
        _files = files; _env = env; _suite = suite; _parser = parser; _exec = exec; _report = report; _proc = proc; _mw = mw;
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

            Console.WriteLine($"\n[TestCase] Starting: {q.Name}");
            
            _env.ReplaceAppsettings(args.ClientAppSettingsTemplate, args.ServerAppSettingsTemplate, args.ClientExePath, args.ServerExePath);
            await _env.RunDatabaseResetAsync(args.DatabaseScriptPath, ct);

            var outDir = Path.Combine(args.ResultRoot, q.Name);
            _files.EnsureDirectory(outDir);
            _env.ClearFolder(outDir);

            _proc.Init(args.ClientExePath, args.ServerExePath);

            var steps = _parser.ParseDetail(q.DetailPath, q.Name);
            if (steps.Count == 0) throw new InvalidOperationException("Test case does not contain any steps.");
            Console.WriteLine($"[TestCase] Loaded {steps.Count} step(s)");

            var results = new List<StepResult>();
            foreach (var step in steps)
            {
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, args.StageTimeoutSeconds)));

                var sw = Stopwatch.StartNew();
                var (ok, msg) = await _exec.ExecuteAsync(step, args, stepCts.Token);
                sw.Stop();
                results.Add(new StepResult { Step = step, Passed = ok, Message = msg, DurationMs = sw.Elapsed.TotalMilliseconds });
                Console.WriteLine($"[Step] Result: {(ok ? "PASS" : "FAIL")} - {msg} ({sw.Elapsed.TotalMilliseconds:F0}ms)");
            }

            Console.WriteLine($"[TestCase] Writing results to: {outDir}");
            await _report.WriteQuestionResultAsync(outDir, steps[0].QuestionCode, results, ct);

            Console.WriteLine($"[TestCase] Cleaning up processes...");
            try { await _proc.StopAllAsync(); } catch { }
            try { await _mw.StopAsync(); } catch { }
            Console.WriteLine($"[TestCase] Completed: {q.Name}\n");
        }

        Console.WriteLine("[Suite] All test cases completed successfully");
        return 1;
    }
}
