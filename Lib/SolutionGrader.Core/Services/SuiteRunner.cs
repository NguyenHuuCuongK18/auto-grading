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
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly IAppsettingsCreationService _appsettings;
        private readonly TestCaseOrchestrator _orchestrator;

        public SuiteRunner(
            IFileService files,
            IEnvironmentResetService env,
            ITestSuiteLoader suite,
            ITestCaseParser parser,
            IExecutor exec,
            IReportService report,
            IExecutableManager proc,
            INetworkMonitorService? networkMonitor,
            IDetailLogService log,
            IRunContext run,
            IAppsettingsCreationService appsettings)
        {
            _files = files; _env = env; _suite = suite; _parser = parser; _exec = exec; _report = report; _proc = proc; _networkMonitor = networkMonitor; _log = log; _run = run; _appsettings = appsettings;
            
            // Create orchestrator for step-based execution
            _orchestrator = new TestCaseOrchestrator(files, env, parser, exec, report, proc, networkMonitor, log, run, appsettings);
        }

        public async Task<int> ExecuteSuiteAsync(ExecuteSuiteArgs args, CancellationToken ct = default)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_LOADING, args.SuitePath)}");
            var def = _suite.Load(args.SuitePath, args.UseInnerTestCaseEnvironment);
            args.Protocol = def.Protocol;
            
            // Set datetime format in RunContext if available from header
            if (!string.IsNullOrWhiteSpace(def.DateTimeFormat))
            {
                _run.DateTimeFormat = def.DateTimeFormat;
            }
            
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_PROTOCOL, args.Protocol)}");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_CASES_FOUND, def.Cases.Count)}");
            _files.EnsureDirectory(args.ResultRoot);

            foreach (var q in def.Cases)
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_STARTING, q.Name, q.Mark)}");

                // Determine executables to use
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

                var outDir = Path.Combine(args.ResultRoot, q.Name);

                // ***** STEP-BASED ORCHESTRATION *****
                // Each step is separated for clarity and can be monitored/logged independently
                
                // Step 1: Setup Environment
                var (setupOk, setupMsg) = await _orchestrator.SetupEnvironmentAsync(q, def, args, clientExePath, serverExePath, ct);
                if (!setupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at environment setup: {setupMsg}");
                    continue;
                }

                // Step 2: Read Test Kit Information
                var (readOk, readMsg, steps) = _orchestrator.ReadTestKitInfo(q, outDir);
                if (!readOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at test kit reading: {readMsg}");
                    continue;
                }

                // Step 3: Initialize Processes and Start Network Monitor
                var (initOk, initMsg) = await _orchestrator.InitializeProcessesAsync(clientExePath, serverExePath, ct);
                if (!initOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at process initialization: {initMsg}");
                    continue;
                }

                // Step 4: Execute and Grade Steps
                var (execOk, execMsg, results) = await _orchestrator.ExecuteAndGradeStepsAsync(steps, args, ct);
                if (!execOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case execution encountered issues: {execMsg}");
                }

                // Step 5: Write Results
                var (writeOk, writeMsg) = await _orchestrator.WriteResultsAsync(outDir, steps[0].QuestionCode, results, ct);
                if (!writeOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Failed to write results: {writeMsg}");
                }

                // Step 6: Cleanup
                var (cleanupOk, cleanupMsg) = await _orchestrator.CleanupAsync();
                if (!cleanupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Cleanup encountered issues: {cleanupMsg}");
                }

                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_COMPLETED, q.Name)}\n");
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} All test cases completed successfully");
            return 1;
        }
    }
}
