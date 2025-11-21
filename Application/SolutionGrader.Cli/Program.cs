using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Keywords;
using FileHandler;

public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return PrintUsage();
            var verb = args[0].Trim().ToLowerInvariant();
            var map = ParseArgs(args.Skip(1).ToArray());
            return verb switch
            {
                "executesuite" => ExecuteSuite(map).GetAwaiter().GetResult(),
                "executepaper" => ExecutePaper(map).GetAwaiter().GetResult(),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return -1;
        }
    }

    private static async System.Threading.Tasks.Task<int> ExecutePaper(Dictionary<string, string> a)
    {
        if (!Need(a, "submission-root", "suite", "out")) return PrintUsage();

        var submissionRoot = a["submission-root"]; // parent folder containing student submissions
        var suite = a["suite"];                   // suite path (root or Header.xlsx)
        var outRoot = a["out"];                    // overall output root
        var useDocker = a.ContainsKey("use-docker") && (a["use-docker"].Equals("true", StringComparison.OrdinalIgnoreCase) || a["use-docker"].Equals("1"));

        var clientLogPathArg = a.GetValueOrDefault("client-log");
        var serverLogPathArg = a.GetValueOrDefault("server-log");

        // Normalize log paths for docker/unified mode
        string? clientLogPath = clientLogPathArg;
        string? serverLogPath = serverLogPathArg;
        if (useDocker)
        {
            bool userProvidedAny = !string.IsNullOrWhiteSpace(clientLogPathArg) || !string.IsNullOrWhiteSpace(serverLogPathArg);
            if (userProvidedAny)
            {
                if (!string.IsNullOrWhiteSpace(clientLogPath) && IsHostPath(clientLogPath)) clientLogPath = "/logs/client.log";
                if (!string.IsNullOrWhiteSpace(serverLogPath) && IsHostPath(serverLogPath)) serverLogPath = "/logs/server.log";
            }
            else
            {
                clientLogPath = null; // unified docker log mode
                serverLogPath = null;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(clientLogPath)) clientLogPath = System.IO.Path.Combine(submissionRoot, "client.log");
            if (string.IsNullOrWhiteSpace(serverLogPath)) serverLogPath = System.IO.Path.Combine(submissionRoot, "server.log");
        }

        int processed = 0, failed = 0;
        foreach (var submission in StudentFinder.FindSubmissions(submissionRoot))
        {
            Console.WriteLine($"[Paper] Grading student {submission.StudentId} mode={submission.Mode}");
            var argsMap = new Dictionary<string, string>(a, StringComparer.OrdinalIgnoreCase)
            {
                ["suite"] = suite,
                ["out"] = System.IO.Path.Combine(outRoot, submission.StudentId),
                ["use-inner-env"] = "true" // enable per-testcase env usage
            };
            if (clientLogPath != null) argsMap["client-log"] = clientLogPath;
            if (serverLogPath != null) argsMap["server-log"] = serverLogPath;

            // Derive executable paths (submission layout). Environment.xlsx (suite root) will provide container/app settings.
            string? serverPath = null;
            string? clientPath = null;
            if (submission.Mode == SubmissionMode.Single)
            {
                // Single layout: one executable acts as either server or client depending on reference availability
                if (HasReferenceExecutable(suite, referenceType: "Server")) clientPath = submission.SingleCodePath; // reference server exists -> student is client
                else if (HasReferenceExecutable(suite, referenceType: "Client")) serverPath = submission.SingleCodePath; // reference client exists -> student is server
                else if (submission.SingleCodePath!.ToLowerInvariant().Contains("server")) serverPath = submission.SingleCodePath; else clientPath = submission.SingleCodePath;
            }
            else
            {
                serverPath = submission.ServerCodePath;
                clientPath = submission.ClientCodePath;
            }

            var exit = await ExecuteSuiteUsingEnv(argsMap, serverPath, clientPath, useDocker);
            if (exit == 1) processed++; else failed++;
        }
        Console.WriteLine($"[Paper] Completed. OK={processed}, Failed={failed}");
        return failed == 0 ? 1 : -1;
    }

    private static bool HasReferenceExecutable(string suitePath, string referenceType)
    {
        try
        {
            var root = System.IO.Directory.Exists(suitePath) ? suitePath : System.IO.Path.GetDirectoryName(suitePath)!;
            var givenDir = System.IO.Path.Combine(root, "Meta", "Given", referenceType);
            if (!System.IO.Directory.Exists(givenDir)) return false;
            return System.IO.Directory.GetFiles(givenDir, "*.exe", System.IO.SearchOption.TopDirectoryOnly).Any();
        }
        catch { return false; }
    }

    private static string? TryLoadEnvValue(string suiteRoot, string key)
    {
        try
        {
            var envPath = System.IO.Path.Combine(suiteRoot, FileKeywords.FileName_Environment);
            if (!System.IO.File.Exists(envPath)) return null;
            var env = EnvFileHandler.LoadEnvironment(envPath);
            return env.Configs.TryGetValue(key, out var v) ? (string.IsNullOrWhiteSpace(v) ? null : v) : null;
        }
        catch { }
        return null;
    }

    // New environment-driven execution (replaces ExecuteSuiteWithOverrides)
    private static async System.Threading.Tasks.Task<int> ExecuteSuiteUsingEnv(Dictionary<string, string> a, string? serverPath, string? clientPath, bool useDocker)
    {
        if (!Need(a, "suite", "out")) return PrintUsage();

        var suiteArg = a["suite"]; // root folder or Header.xlsx
        string suiteRoot = System.IO.Directory.Exists(suiteArg) ? System.IO.Path.GetFullPath(suiteArg) : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(suiteArg))!;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var timestampedResultRoot = System.IO.Path.Combine(a["out"], string.Format(FileKeywords.Pattern_GradeResult, timestamp));

        var clientLogPath = a.GetValueOrDefault("client-log");
        var serverLogPath = a.GetValueOrDefault("server-log");
        if (useDocker)
        {
            bool userProvidedAny = !string.IsNullOrWhiteSpace(clientLogPath) || !string.IsNullOrWhiteSpace(serverLogPath);
            if (!userProvidedAny) { clientLogPath = null; serverLogPath = null; }
            else
            {
                if (!string.IsNullOrWhiteSpace(clientLogPath) && IsHostPath(clientLogPath)) clientLogPath = "/logs/client.log";
                if (!string.IsNullOrWhiteSpace(serverLogPath) && IsHostPath(serverLogPath)) serverLogPath = "/logs/server.log";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(clientLogPath)) clientLogPath = System.IO.Path.Combine(suiteRoot, "client.log");
            if (string.IsNullOrWhiteSpace(serverLogPath)) serverLogPath = System.IO.Path.Combine(suiteRoot, "server.log");
        }

        // Read container name directly from environment.xlsx if not specified
        string? containerName = a.GetValueOrDefault("Code_Container_Name");
        if (useDocker && string.IsNullOrWhiteSpace(containerName)) containerName = TryLoadEnvValue(suiteRoot, Domain.Entities.Constants.EnvironmentConfiguration.CodeContainerName);
        string? middlewareHost = a.GetValueOrDefault("middleware-host");
        if (useDocker && string.IsNullOrWhiteSpace(middlewareHost)) middlewareHost = TryLoadEnvValue(suiteRoot, "Middleware_Host");
        if (useDocker && string.IsNullOrWhiteSpace(containerName)) { Console.WriteLine("[Suite] Warning: Docker mode enabled but container name not found. Disabling docker monitoring."); useDocker = false; }

        var run = new ExecuteSuiteArgs
        {
            SuitePath = suiteArg,
            ResultRoot = timestampedResultRoot,
            ClientExePath = clientPath,
            ServerExePath = serverPath,
            UseInnerTestCaseEnvironment = true,
            UseDockerContainers = useDocker,
            CodeContainerName = containerName,
            ClientLogPath = clientLogPath,
            ServerLogPath = serverLogPath,
            MiddlewareHost = middlewareHost
            // No overrides: rely fully on environment.xlsx keys (Student_Question_Path, Code_File_Path, etc.)
        };

        IFileService files = new FileService();
        var env = new EnvironmentResetService(files);
        var suiteLoader = new ExcelSuiteLoader();
        var parse = new ExcelDetailParser();
        var appsettings = new AppsettingsCreationService();
        IRunContext runctx = new RunContext();
        IExecutableManager proc = useDocker ? new DockerExecutableManager(runctx) : new ExecutableManager(runctx);
        if (useDocker) { proc.Init(run.CodeContainerName, null); proc.ConfigureDockerLogs(run.ClientLogPath, run.ServerLogPath); }
        IMiddlewareService mw = new MiddlewareProxyService(runctx);
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files, runctx);
        var gradingConfig = GradingConfig.Default;
        IExecutor exec = new Executor(proc, mw, cmp, log, runctx, gradingConfig);
        IReportService rep = new ReportService(files);
        var flow = new SuiteRunner(files, env, suiteLoader, parse, exec, rep, proc, mw, log, runctx, appsettings);

        Console.WriteLine($"[Suite] Results will be saved to: {timestampedResultRoot}");
        if (useDocker)
        {
            Console.WriteLine("[Suite] Docker single-container monitoring enabled.");
            Console.WriteLine($"[Suite] Container: {run.CodeContainerName}");
            Console.WriteLine($"[Suite] Client log: {(run.ClientLogPath ?? "<unified>")}");
            Console.WriteLine($"[Suite] Server log: {(run.ServerLogPath ?? "<unified>")}");
        }
        return await flow.ExecuteSuiteAsync(run);
    }

    private static async System.Threading.Tasks.Task<int> ExecuteSuite(Dictionary<string, string> a)
    {
        if (!Need(a, "suite", "out")) return PrintUsage();
        var suiteArg = a["suite"];
        string suiteRoot = System.IO.Directory.Exists(suiteArg) ? System.IO.Path.GetFullPath(suiteArg) : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(suiteArg))!;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var timestampedResultRoot = System.IO.Path.Combine(a["out"], string.Format(FileKeywords.Pattern_GradeResult, timestamp));
        var useDocker = a.ContainsKey("use-docker") && (a["use-docker"].Equals("true", StringComparison.OrdinalIgnoreCase) || a["use-docker"].Equals("1"));
        var clientLogPath = a.GetValueOrDefault("client-log");
        var serverLogPath = a.GetValueOrDefault("server-log");
        if (useDocker)
        {
            bool userProvidedAny = !string.IsNullOrWhiteSpace(clientLogPath) || !string.IsNullOrWhiteSpace(serverLogPath);
            if (!userProvidedAny) { clientLogPath = null; serverLogPath = null; }
            else
            {
                if (!string.IsNullOrWhiteSpace(clientLogPath) && IsHostPath(clientLogPath)) clientLogPath = "/logs/client.log";
                if (!string.IsNullOrWhiteSpace(serverLogPath) && IsHostPath(serverLogPath)) serverLogPath = "/logs/server.log";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(clientLogPath)) clientLogPath = System.IO.Path.Combine(suiteRoot, "client.log");
            if (string.IsNullOrWhiteSpace(serverLogPath)) serverLogPath = System.IO.Path.Combine(suiteRoot, "server.log");
        }

        string? containerName = a.GetValueOrDefault("code-container");
        if (useDocker && string.IsNullOrWhiteSpace(containerName)) { containerName = TryLoadEnvValue(suiteRoot, Domain.Entities.Constants.EnvironmentConfiguration.CodeContainerName) ?? TryLoadEnvValue(suiteRoot, "Code_Container_Name"); }
        string? middlewareHost = a.GetValueOrDefault("middleware-host");
        if (useDocker && string.IsNullOrWhiteSpace(middlewareHost)) middlewareHost = TryLoadEnvValue(suiteRoot, "Middleware_Host");
        if (useDocker && string.IsNullOrWhiteSpace(containerName)) { Console.WriteLine("[Suite] Warning: Docker mode enabled but container name not found. Disabling docker monitoring."); useDocker = false; }

        var run = new ExecuteSuiteArgs
        {
            SuitePath = suiteArg,
            ResultRoot = timestampedResultRoot,
            ClientExePath = a.GetValueOrDefault("client"),
            ServerExePath = a.GetValueOrDefault("server"),
            UseInnerTestCaseEnvironment = a.ContainsKey("use-inner-env") && (a["use-inner-env"].Equals("true", StringComparison.OrdinalIgnoreCase) || a["use-inner-env"].Equals("1")),
            UseDockerContainers = useDocker,
            CodeContainerName = containerName,
            ClientLogPath = clientLogPath,
            ServerLogPath = serverLogPath,
            MiddlewareHost = middlewareHost
        };
        IFileService files = new FileService();
        var env = new EnvironmentResetService(files);
        var suite = new ExcelSuiteLoader();
        var parse = new ExcelDetailParser();
        var appsettings = new AppsettingsCreationService();
        IRunContext runctx = new RunContext();
        IExecutableManager proc = useDocker ? new DockerExecutableManager(runctx) : new ExecutableManager(runctx);
        if (useDocker) { proc.Init(run.CodeContainerName, null); proc.ConfigureDockerLogs(run.ClientLogPath, run.ServerLogPath); }
        IMiddlewareService mw = new MiddlewareProxyService(runctx);
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files, runctx);
        var gradingConfig = GradingConfig.Default;
        IExecutor exec = new Executor(proc, mw, cmp, log, runctx, gradingConfig);
        IReportService rep = new ReportService(files);
        var flow = new SuiteRunner(files, env, suite, parse, exec, rep, proc, mw, log, runctx, appsettings);
        Console.WriteLine($"[Suite] Results will be saved to: {timestampedResultRoot}");
        if (useDocker)
        {
            Console.WriteLine("[Suite] Docker single-container monitoring enabled.");
            Console.WriteLine($"[Suite] Container: {run.CodeContainerName}");
            Console.WriteLine($"[Suite] Client log: {(run.ClientLogPath ?? "<unified>")}");
            Console.WriteLine($"[Suite] Server log: {(run.ServerLogPath ?? "<unified>")}");
        }
        return await flow.ExecuteSuiteAsync(run);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a.TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { map[key] = args[i + 1]; i++; }
            else { map[key] = "true"; }
        }
        return map;
    }

    private static bool Need(Dictionary<string, string> m, params string[] keys)
    {
        foreach (var k in keys) if (!m.ContainsKey(k)) return false;
        return true;
    }

    private static bool IsHostPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Windows rooted path or UNC
        return System.IO.Path.IsPathRooted(path) || path.Contains(':');
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
Usage:
  SolutionGrader.Cli ExecuteSuite --suite <suiteFolder|Header.xlsx> --out <resultRoot>
                                [--client <client.exe|dll>] [--server <server.exe|dll>]
                                [--use-inner-env]
                                [--use-docker --code-container <name> --client-log <path> --server-log <path> [--middleware-host <host>]]

  SolutionGrader.Cli ExecutePaper --submission-root <root> --suite <suiteFolder|Header.xlsx> --out <resultRoot>
                                [--use-docker --code-container <name>] [--client-log <path>] [--server-log <path>]

Required Arguments:
  --suite            Path to test suite folder or Header.xlsx file
  --out              Output directory for grading results

Optional Arguments:
  --client           Path to client executable (non-docker mode)
  --server           Path to server executable (non-docker mode)
  --use-inner-env    Enable test case-specific environment.xlsx files (default: false) [ExecuteSuite only]

Docker Single Container Monitoring:
  --use-docker       Enable container log monitoring instead of launching processes
  --code-container   Name of running container with both client & server (fallback: environment.xlsx Code_Container_Name)
  --client-log       Path inside container to client log file (fallback if provided host path: /logs/client.log) - leave blank for unified docker logs
  --server-log       Path inside container to server log file (fallback if provided host path: /logs/server.log) - leave blank for unified docker logs
  --middleware-host  Host/IP where middleware listens (fallback: environment.xlsx)

Paper mode (assumptions):
  - Single paper, only Q1
  - StudentSolution/<paper>/<name+code>/<paper>/Q1_<name+code>/Q1.dll|exe OR Q11_/Q12_ dual layout
  - Code format: two letters + six digits (e.g., HE123456)
");
        return -1;
    }
}
