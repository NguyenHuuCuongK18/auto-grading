using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Services;

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

    private static async System.Threading.Tasks.Task<int> ExecutePaper(Dictionary<string, string> map)
    {
        if (!Need(map, "suite", "out", "submission-root")) return PrintUsage();

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var timestampedResultRoot = System.IO.Path.Combine(map["out"], string.Format(FileKeywords.Pattern_GradeResult, timestamp));

        var run = new ExecuteSuiteArgs
        {
            SuitePath = map["suite"],
            ResultRoot = timestampedResultRoot,
            SubmissionRoot = map["submission-root"],
            UseInnerTestCaseEnvironment = true
        };

        var runner = new SuiteRunner();
        return await runner.ExecutePaper(run);
    }

    private static async System.Threading.Tasks.Task<int> ExecuteSuite(Dictionary<string, string> a)
    {
        if (!Need(a, "suite", "out")) return PrintUsage();

        // Create timestamped results folder
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var timestampedResultRoot = System.IO.Path.Combine(a["out"], string.Format(FileKeywords.Pattern_GradeResult, timestamp));

        var run = new ExecuteSuiteArgs
        {
            SuitePath = a["suite"],
            ResultRoot = timestampedResultRoot,
            ClientExePath = a.GetValueOrDefault("client"),
            ServerExePath = a.GetValueOrDefault("server"),
            UseInnerTestCaseEnvironment = a.ContainsKey("use-inner-env") &&
                                         (a["use-inner-env"].Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                          a["use-inner-env"].Equals("1"))
        };

        IFileService files = new FileService();
        var env = new EnvironmentResetService(files);
        var suite = new ExcelSuiteLoader();
        var parse = new ExcelDetailParser();

        // Use default grading configuration (DateTime/Time excluded from grading, GraderPort = 8888)
        var gradingConfig = GradingConfig.Default;

        // AppsettingsCreationService - parameterless constructor
        var appsettings = new AppsettingsCreationService();

        IRunContext runctx = new RunContext();

        IExecutableManager proc = new ExecutableManager(runctx);
        
        // Use MiddlewareProxyService for network monitoring (simpler constructor)
        IMiddlewareService middleware = new MiddlewareProxyService(runctx);
        
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files, runctx);

        // Executor constructor: proc, middleware, cmp, log, runctx, gradingConfig
        IExecutor exec = new Executor(proc, middleware, cmp, log, runctx, gradingConfig);
        IReportService rep = new ReportService(files);

        var flow = new SuiteRunner(files, env, suite, parse, exec, rep, proc, middleware, log, runctx, appsettings);
        
        Console.WriteLine($"[Suite] Results will be saved to: {timestampedResultRoot}");

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

    private static int PrintUsage()
    {
        Console.WriteLine(@"
Usage:
  SolutionGrader.Cli ExecuteSuite --suite <suiteFolder|Header.xlsx> --out <resultRoot>
                                [--client <client.exe>] [--server <server.exe>]
                                [--use-inner-env]

Required Arguments:
  --suite   Path to test suite folder or Header.xlsx file
  --out     Output directory for grading results

Optional Arguments:
  --client  Path to client executable (overrides Meta/Given/Client if provided)
  --server  Path to server executable (overrides Meta/Given/Server if provided)
  --use-inner-env  Enable test case-specific environment.xlsx files
                   When specified, each test case can have its own environment.xlsx
                   to override database paths and configurations (default: false)

Configuration:
  All other configuration (database script, ports, timeouts, etc.) is read from:
  - environment.xlsx: Database script path, given executables, ports
  - Header.xlsx: Protocol, database configuration, test case marks
  
  The grading system will:
  - Use executables from Meta/Given folder when --client/--server not specified
  - Auto-generate appsettings.json from Header.xlsx with database configuration
  - Use database script from environment.xlsx (Default_Database_File_Path)
  - Use default timeout of 10 seconds per stage
  - Use suite-level environment.xlsx by default (unless --use-inner-env is specified)
");
        return -1;
    }
}
