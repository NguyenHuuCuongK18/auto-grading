using Domain.Entities.Constants;
using Domain.Entities.Main;
using Domain.Entities.Main.TestCase;
using EnvironmentBuilder.helper;
using Newtonsoft.Json;
using ProcessLauncher.ProcessLauncher;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

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
        private readonly TestCaseOrchestrator _orchestrator;

        public SuiteRunner()
        {
        }

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

            // Create orchestrator for step-based execution
            _orchestrator = new TestCaseOrchestrator(files, env, parser, exec, report, proc, mw, log, run, appsettings);
        }

        public async Task<int> ExecutePaper(ExecuteSuiteArgs args, CancellationToken ct = default)
        {

            if (args.SubmissionRoot == null)
            {
                throw new ArgumentNullException("No submission folder provided!");
            }

            var fileService = new FileService();
            Dictionary<string, Dictionary<string, (string? Q11, string? Q12)>> studentSubmissions = fileService.GetStudentSubmission(args.SubmissionRoot);

            // This only assume there's only 1 testsuite, current one has no multi suit handler
            string envPath = Path.Combine(args.SuitePath, FileKeywords.FileName_Environment);
            global::Domain.Entities.Main.Environment env = EnvironmentService.GetEnvironment(envPath);

            // Loop through each paper and grade submissions ONE BY ONE - no parallelism here
            foreach (KeyValuePair<string, Dictionary<string, (string? Q11, string? Q12)>> paper in studentSubmissions)
            {
                if (paper.Value.Count == 0)
                {
                    Console.WriteLine($"[ENV] No submissions found for paper no {paper.Key}, skipping...");
                    continue;
                }

                // Start env init for each student submission
                foreach (KeyValuePair<string, (string? Q11, string? Q12)> submission in paper.Value)
                {
                    Console.WriteLine($"[ENV] Start creating enviroment config for {submission.Key}");

                    var (q11, q12) = submission.Value;

                    string? givenPath = null;
                    if (q11 == null || q12 == null)
                    {
                        givenPath = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsolePath, "");
                        if (string.IsNullOrEmpty(givenPath))
                            throw new ArgumentNullException("[ENV] Given console path missing");
                    }

                    q11 ??= givenPath;
                    q12 ??= givenPath;

                    ModifyEnvironmentForContainerInit(env, q11, q12);

                    try
                    {
                        // Mute error, i know this is bad
                        EnvironmentManagerInvoker.TrySetupContainer(env, out _);

                        EnvironmentManagerInvoker.TrySetupQuestion(env, out _);



                        // Dispose all containers
                        EnvironmentManagerInvoker.TryDisposeContainer(env, out _);
                        Console.WriteLine($"[ENV] Disposed environment for {submission.Key}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ENV] Failed to create env for {submission.Key}, error: {ex.Message}");
                    }
                }
            }
            return 1;
        }

        // Will throw ts some where later, currently here because im too lazy to implement abstraction
        // The Code Container will be Q11 and Given Container will be Q12 in the ENV config
        // Why this way? Because i want to cut corners and reuse existing fields
        private void ModifyEnvironmentForContainerInit(global::Domain.Entities.Main.Environment env, string q11, string q12)
        {
            string? givenConsoleImageName = TryGetValueOrDefault(env.Configs, EnvConfig.CodeImageName, null);

            if (!string.IsNullOrEmpty(givenConsoleImageName))
            {
                TryReplaceValue(env.Configs, EnvConfig.GivenConsoleImageName, givenConsoleImageName);
            }
            else
            {
                throw new ArgumentNullException("[ENV] Fym the image is null?");
            }

            TryReplaceValue(env.Configs, EnvConfig.CodeFilePath, q11);
            TryReplaceValue(env.Configs, EnvConfig.CodeContainerName, "Q11");
            TryReplaceValue(env.Configs, EnvConfig.GivenConsolePath, q12);
            TryReplaceValue(env.Configs, EnvConfig.GivenConsoleContainerName, "Q12");
            env.Configs[EnvConfig.DefaultDatabaseName] = "Q1";

            string? givenConsoleInternalPort = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleContainerInternalPort, null);
            if (string.IsNullOrEmpty(givenConsoleInternalPort))
            {
                givenConsoleInternalPort = TryGetValueOrDefault(env.Configs, EnvConfig.CodeContainerInternalPort, "");
                TryReplaceValue(env.Configs, EnvConfig.GivenConsoleContainerInternalPort, givenConsoleInternalPort);
            }

            string? givenConsoleHostPort = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleContainerHostPort, null);
            if (string.IsNullOrEmpty(givenConsoleHostPort))
            {
                givenConsoleHostPort = TryGetValueOrDefault(env.Configs, EnvConfig.CodeContainerHostPort, "");
                TryReplaceValue(env.Configs, EnvConfig.GivenConsoleContainerHostPort, givenConsoleHostPort);
            }
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

                // Step 3: Initialize Processes
                var (initOk, initMsg) = _orchestrator.InitializeProcesses(clientExePath, serverExePath);
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

        protected static string TryGetValueOrDefault(
            Dictionary<string, string> configs,
            string key,
            string defaultValue = "")
        {
            if (configs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;

            return defaultValue;
        }

        protected static void TryReplaceValue(
            Dictionary<string, string> configs,
            string key,
            string? newValue = null)
        {
            if (newValue == null)
                return;

            if (string.IsNullOrEmpty(key))
                return;

            if (!configs.TryGetValue(key, out var oldValue) || !string.Equals(oldValue, newValue))
            {
                configs[key] = newValue;
            }
        }
    }
}
