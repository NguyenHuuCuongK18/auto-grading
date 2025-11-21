using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using DomainLib = Domain.Entities.Constants;
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
        private readonly TestCaseOrchestrator _orchestrator;

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

        public async Task<int> ExecuteSuiteAsync(ExecuteSuiteArgs args, CancellationToken ct = default)
        {
            Console.WriteLine("[Suite] Performing pre-suite quick reset of environment (containers/network/database)...");
            try
            {
                var preDef = _suite.Load(args.SuitePath, args.UseInnerTestCaseEnvironment);
                if (preDef.DomainEnvironment != null && args.UseDockerContainers)
                {
                    ExternalEnvironmentManagerInvoker.TryQuickReset(preDef.DomainEnvironment, out _);
                }
            }
            catch { }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_LOADING, args.SuitePath)}");
            var def = _suite.Load(args.SuitePath, args.UseInnerTestCaseEnvironment);
            args.Protocol = def.Protocol;

            if (!string.IsNullOrWhiteSpace(def.DateTimeFormat))
            {
                _run.DateTimeFormat = def.DateTimeFormat;
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_PROTOCOL, args.Protocol)}");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_CASES_FOUND, def.Cases.Count)}");
            _files.EnsureDirectory(args.ResultRoot);

            if (def.DomainEnvironment != null && args.UseDockerContainers)
            {
                Console.WriteLine("[Suite] Invoking EnvironmentManager: setupcontainer...");
                if (!ExternalEnvironmentManagerInvoker.TrySetupContainer(def.DomainEnvironment, out var emErr))
                {
                    Console.WriteLine($"[Suite] EnvironmentManager setupcontainer failed: {emErr}");
                    return -1;
                }
                _proc.ConfigureDockerLogs(args.ClientLogPath, args.ServerLogPath);
                Console.WriteLine("[Suite] EnvironmentManager container setup complete.");
            }

            foreach (var q in def.Cases)
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_STARTING, q.Name, q.Mark)}");

                if (def.DomainEnvironment != null && args.UseDockerContainers)
                {
                    ExternalEnvironmentManagerInvoker.TrySetupQuestion(def.DomainEnvironment, out _);
                }

                // Consider inner test case environment; only setup if meaningfully different
                global::Domain.Entities.Main.Environment? tcDomainEnvRaw = null;
                global::Domain.Entities.Main.Environment? tcDomainEnvMerged = null;
                bool innerApplied = false;
                if (def.DomainEnvironment != null)
                {
                    var tcEnvPath = System.IO.Path.Combine(q.DirectoryPath, FileKeywords.FileName_Environment);
                    if (System.IO.File.Exists(tcEnvPath))
                    {
                        try
                        {
                            using var wb = new ClosedXML.Excel.XLWorkbook(tcEnvPath);
                            var envObj = new global::Domain.Entities.Main.Environment
                            {
                                Configs = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                Steps = new System.Collections.Generic.List<string>()
                            };
                            var wsCfg = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));
                            if (wsCfg != null)
                            {
                                int startRow = wsCfg.Cell(1, 1).GetString().Trim().Equals("Key", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                                for (int r = startRow; r <= wsCfg.RowCount(); r++)
                                {
                                    var key = wsCfg.Cell(r, 1).GetString().Trim();
                                    var val = wsCfg.Cell(r, 2).GetString().Trim();
                                    if (!string.IsNullOrEmpty(key)) envObj.Configs[key] = val ?? string.Empty;
                                }
                            }
                            var wsRun = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Run", StringComparison.OrdinalIgnoreCase));
                            if (wsRun != null)
                            {
                                int hdr = wsRun.Cell(1, 1).GetString().Trim().Equals("Step", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                                for (int r = hdr; r <= wsRun.RowCount(); r++)
                                {
                                    var kw = wsRun.Cell(r, 2).GetString().Trim();
                                    if (!string.IsNullOrWhiteSpace(kw)) envObj.Steps.Add(kw);
                                }
                            }
                            tcDomainEnvRaw = envObj;

                            // Merge with master/root env then override with inner values
                            tcDomainEnvMerged = CloneEnvironment(def.DomainEnvironment);
                            foreach (var kv in tcDomainEnvRaw.Configs)
                            {
                                if (!string.IsNullOrWhiteSpace(kv.Value)) tcDomainEnvMerged.Configs[kv.Key] = kv.Value;
                            }
                            if ((tcDomainEnvRaw.Steps?.Count ?? 0) > 0)
                            {
                                tcDomainEnvMerged.Steps = new System.Collections.Generic.List<string>(tcDomainEnvRaw.Steps);
                            }

                            // Extra condition: test case Meta folder present with content
                            bool hasOwnMeta = false;
                            try
                            {
                                var tcMeta = System.IO.Path.Combine(q.DirectoryPath, "Meta");
                                hasOwnMeta = System.IO.Directory.Exists(tcMeta) && System.IO.Directory.EnumerateFileSystemEntries(tcMeta).Any();
                            }
                            catch { }

                            if (ShouldApplyInnerEnvironment(def.DomainEnvironment, tcDomainEnvRaw) || hasOwnMeta)
                            {
                                Console.WriteLine($"[TestCase] Inner environment differs for {q.Name} (apply={true}, meta={hasOwnMeta}), invoking setupenvironmenttc...");
                                if (!ExternalEnvironmentManagerInvoker.TrySetupTestCase(tcDomainEnvMerged, out var tcErr))
                                {
                                    Console.WriteLine($"[TestCase] setupenvironmenttc failed: {tcErr}");
                                }
                                else
                                {
                                    innerApplied = true;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[TestCase] Inner environment is equivalent to master. Skipping setup for {q.Name}.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TestCase] Failed to load inner environment for {q.Name}: {ex.Message}");
                        }
                    }
                }

                string? clientExePath = args.ClientExePath;
                string? serverExePath = args.ServerExePath;

                if (string.IsNullOrWhiteSpace(clientExePath) && !string.IsNullOrWhiteSpace(q.Environment?.GivenClientPath))
                {
                    clientExePath = q.Environment.GivenClientPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using client from environment: {clientExePath}");
                }

                if (string.IsNullOrWhiteSpace(serverExePath) && !string.IsNullOrWhiteSpace(q.Environment?.GivenServerPath))
                {
                    serverExePath = q.Environment.GivenServerPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using server from environment: {serverExePath}");
                }

                if (!string.IsNullOrWhiteSpace(q.GradeContent))
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Grade_Content: {q.GradeContent}");

                    if (q.GradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(q.Environment?.GivenServerPath))
                        {
                            serverExePath = q.Environment.GivenServerPath;
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference server: {serverExePath}");
                        }
                    }
                    else if (q.GradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(q.Environment?.GivenClientPath))
                        {
                            clientExePath = q.Environment.GivenClientPath;
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference client: {clientExePath}");
                        }
                    }
                }

                var outDir = Path.Combine(args.ResultRoot, q.Name);

                var (setupOk, setupMsg) = await _orchestrator.SetupEnvironmentAsync(q, def, args, clientExePath, serverExePath, ct);
                if (!setupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at environment setup: {setupMsg}");
                    if (innerApplied && tcDomainEnvMerged != null)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeTestCase(tcDomainEnvMerged, out _);
                    }
                    if (def.DomainEnvironment != null && args.UseDockerContainers)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeQuestion(def.DomainEnvironment, out _);
                    }
                    continue;
                }

                var (readOk, readMsg, steps) = _orchestrator.ReadTestKitInfo(q, outDir);
                if (!readOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at test kit reading: {readMsg}");
                    if (innerApplied && tcDomainEnvMerged != null)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeTestCase(tcDomainEnvMerged, out _);
                    }
                    if (def.DomainEnvironment != null && args.UseDockerContainers)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeQuestion(def.DomainEnvironment, out _);
                    }
                    continue;
                }

                var (initOk, initMsg) = _orchestrator.InitializeProcesses(clientExePath, serverExePath, args.UseDockerContainers);
                if (!initOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at process initialization: {initMsg}");
                    if (innerApplied && tcDomainEnvMerged != null)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeTestCase(tcDomainEnvMerged, out _);
                    }
                    if (def.DomainEnvironment != null && args.UseDockerContainers)
                    {
                        ExternalEnvironmentManagerInvoker.TryDisposeQuestion(def.DomainEnvironment, out _);
                    }
                    continue;
                }

                var (execOk, execMsg, results) = await _orchestrator.ExecuteAndGradeStepsAsync(steps, args, ct);
                if (!execOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case execution encountered issues: {execMsg}");
                }

                var (writeOk, writeMsg) = await _orchestrator.WriteResultsAsync(outDir, steps[0].QuestionCode, results, ct);
                if (!writeOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Failed to write results: {writeMsg}");
                }

                var (cleanupOk, cleanupMsg) = await _orchestrator.CleanupAsync();
                if (!cleanupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Cleanup encountered issues: {cleanupMsg}");
                }

                if (innerApplied && tcDomainEnvMerged != null)
                {
                    Console.WriteLine($"[TestCase] Disposing inner environment for {q.Name}...");
                    ExternalEnvironmentManagerInvoker.TryDisposeTestCase(tcDomainEnvMerged, out _);
                }

                if (def.DomainEnvironment != null && args.UseDockerContainers)
                {
                    ExternalEnvironmentManagerInvoker.TryDisposeQuestion(def.DomainEnvironment, out _);
                }

                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_COMPLETED, q.Name)}\n");
            }

            // Dispose top-level containers after suite
            if (def.DomainEnvironment != null && args.UseDockerContainers)
            {
                Console.WriteLine("[Suite] Invoking EnvironmentManager: disposecontainer...");
                ExternalEnvironmentManagerInvoker.TryDisposeContainer(def.DomainEnvironment, out _);
            }

            Console.WriteLine("[Suite] Performing post-suite quick reset (final cleanup)...");
            if (def.DomainEnvironment != null && args.UseDockerContainers)
            {
                ExternalEnvironmentManagerInvoker.TryQuickReset(def.DomainEnvironment, out _);
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} All test cases completed successfully");
            return 1;
        }

        private static bool ShouldApplyInnerEnvironment(global::Domain.Entities.Main.Environment master, global::Domain.Entities.Main.Environment inner)
        {
            if (inner == null) return false;

            string Get(global::Domain.Entities.Main.Environment e, string k)
                => (e.Configs != null && e.Configs.TryGetValue(k, out var v) ? v ?? string.Empty : string.Empty).Trim();

            // 1) Environment type difference
            var kEnvType = DomainLib.EnvironmentConfiguration.EnvironmentType;
            var innerType = Get(inner, kEnvType);
            if (!string.IsNullOrWhiteSpace(innerType))
            {
                var masterType = Get(master, kEnvType);
                if (!innerType.Equals(masterType, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // 2) Steps: apply if inner has any steps not present in master (e.g., reset_database)
            var masterSteps = new HashSet<string>(master.Steps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var s in inner.Steps ?? new List<string>())
            {
                if (!masterSteps.Contains(s)) return true;
            }

            // 3) Config diffs for DB-related keys
            var kDbSys = DomainLib.EnvironmentConfiguration.DatabaseManagementSystem;
            var kDbName = DomainLib.EnvironmentConfiguration.DefaultDatabaseName;
            var kDbFile = DomainLib.EnvironmentConfiguration.DefaultDatabaseFilePath;
            var kDbUser = DomainLib.EnvironmentConfiguration.DatabaseUsername;
            var kDbPwd = DomainLib.EnvironmentConfiguration.DatabasePassword;

            // Treat "database" placeholder as no override
            string innerDbName = Get(inner, kDbName);
            if (!string.IsNullOrWhiteSpace(innerDbName) && !innerDbName.Equals("database", StringComparison.OrdinalIgnoreCase))
            {
                var masterDbName = Get(master, kDbName);
                if (!innerDbName.Equals(masterDbName, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // DB script path
            var innerDbFile = Get(inner, kDbFile);
            if (!string.IsNullOrWhiteSpace(innerDbFile))
            {
                var masterDbFile = Get(master, kDbFile);
                if (!innerDbFile.Equals(masterDbFile, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // DBMS
            var innerDbSys = Get(inner, kDbSys);
            if (!string.IsNullOrWhiteSpace(innerDbSys))
            {
                var masterDbSys = Get(master, kDbSys);
                if (!innerDbSys.Equals(masterDbSys, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Credentials
            var innerUser = Get(inner, kDbUser);
            if (!string.IsNullOrWhiteSpace(innerUser) && !innerUser.Equals(Get(master, kDbUser), StringComparison.OrdinalIgnoreCase)) return true;
            var innerPwd = Get(inner, kDbPwd);
            if (!string.IsNullOrWhiteSpace(innerPwd) && !innerPwd.Equals(Get(master, kDbPwd), StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static global::Domain.Entities.Main.Environment CloneEnvironment(global::Domain.Entities.Main.Environment source)
        {
            return new global::Domain.Entities.Main.Environment
            {
                Configs = new Dictionary<string, string>(source.Configs ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                Steps = new List<string>(source.Steps ?? new List<string>())
            };
        }
    }
}
