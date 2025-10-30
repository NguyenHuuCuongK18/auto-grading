using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Keywords;

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
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return -1;
        }
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
            ClientAppSettingsTemplate = a.GetValueOrDefault("client-appsettings"),
            ServerAppSettingsTemplate = a.GetValueOrDefault("server-appsettings"),
            DatabaseScriptPath = a.GetValueOrDefault("db-script"),
            StageTimeoutSeconds = a.TryGetValue("timeout", out var t) && int.TryParse(t, out var sec) ? Math.Max(1, sec) : 10,
        };

        IFileService files = new FileService();
        var env = new EnvironmentResetService(files);
        var suite = new ExcelSuiteLoader();
        var parse = new ExcelDetailParser();

        IRunContext runctx = new RunContext();

        IExecutableManager proc = new ExecutableManager(runctx);
        IMiddlewareService mw = new MiddlewareProxyService(runctx);
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files); // <-- Excel logger

        IExecutor exec = new Executor(proc, mw, cmp, log, runctx);
        IReportService rep = new ReportService(files);

        var flow = new SuiteRunner(files, env, suite, parse, exec, rep, proc, mw, log, runctx);
        
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
                                [--client-appsettings <path>] [--server-appsettings <path>]
                                [--db-script <sql>] [--timeout <sec>]

Notes:
  - Protocol (HTTP/TCP) is read from Header.xlsx (if provided by the suite); no CLI flag needed.
");
        return -1;
    }
}
