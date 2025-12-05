using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using SolutionGrader.Cli.Services;
using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Keywords;

#if WINDOWS
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Services;
#endif

/// <summary>
/// CLI entry point for solution grading.
/// Supports both local (Windows) and Docker-based (cross-platform) grading modes.
/// 
/// This CLI syncs with SolutionGrader.UI, sharing the SAME DockerGradingService from
/// Lib/SolutionGrader.Core. This ensures identical grading behavior between CLI and UI.
/// 
/// Key modes:
/// 1. ExecuteSuite: Local grading using direct process execution (Windows only)
/// 2. ExecutePaper: Local grading for multiple students (Windows only)
/// 3. DockerGrade: Docker-based grading using containers (cross-platform)
///    - Uses SHARED DockerGradingService from SolutionGrader.Core
///    - Student discovery and orchestration via CliDockerGradingOrchestrator
/// 4. List: List students in submit folder (cross-platform)
/// 5. Validate: Validate test kit structure (cross-platform)
/// </summary>
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
#if WINDOWS
                "executesuite" => ExecuteSuite(map).GetAwaiter().GetResult(),
                "executepaper" => ExecutePaper(map).GetAwaiter().GetResult(),
#endif
                "dockergrade" => DockerGrade(map).GetAwaiter().GetResult(),
                "list" => ListStudents(map),
                "validate" => ValidateTestKit(map),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return -1;
        }
    }

    /// <summary>
    /// Docker-based grading for multiple students.
    /// This uses the SHARED DockerGradingService from SolutionGrader.Core to ensure
    /// identical behavior between CLI and UI grading.
    /// 
    /// The orchestration (student discovery, iteration) is done by CliDockerGradingOrchestrator,
    /// but the actual grading per student uses the shared DockerGradingService.
    /// </summary>
    private static async System.Threading.Tasks.Task<int> DockerGrade(Dictionary<string, string> map)
    {
        // Required arguments (matching SolutionGrader.UI)
        if (!Need(map, "submit", "testkit"))
        {
            Console.WriteLine("[ERROR] Missing required arguments: --submit and --testkit");
            return PrintUsage();
        }

        // Build GradingConfiguration matching SolutionGrader.UI model
        var config = new CliGradingConfiguration
        {
            SubmitFolderPath = map["submit"],
            TestKitFolderPath = map["testkit"],
            SaveResultFolderPath = map.GetValueOrDefault("out", Path.Combine(map["submit"], "Results")),
            
            // Project names (matching UI input fields)
            HasClient = !map.ContainsKey("has-client") || ParseBool(map.GetValueOrDefault("has-client", "true")),
            HasServer = !map.ContainsKey("has-server") || ParseBool(map.GetValueOrDefault("has-server", "true")),
            ClientProjectName = map.GetValueOrDefault("client-name", "Project12"),
            ServerProjectName = map.GetValueOrDefault("server-name", "Project11"),
            
            // Docker settings (read from Environment.xlsx but can be overridden)
            CodeContainerInternalPort = int.TryParse(map.GetValueOrDefault("internal-port"), out var ip) ? ip : 8000,
            CodeContainerHostPort = int.TryParse(map.GetValueOrDefault("host-port"), out var hp) ? hp : 8000,
            DockerNetwork = map.GetValueOrDefault("network", "auto-grading-network"),
            GradingTimeoutSeconds = int.TryParse(map.GetValueOrDefault("timeout"), out var t) ? t : 60,
            TestCaseTimeoutSeconds = int.TryParse(map.GetValueOrDefault("tc-timeout"), out var tct) ? tct : 15,
            
            // Database settings
            // Note: Database password is read from Environment.xlsx by DockerGradingService
            // It can be overridden via --db-password or AUTOGRADING_DB_PASSWORD env var
            DatabaseContainerName = map.GetValueOrDefault("db-container", "auto-grading-sqlserver"),
            DatabaseContainerInternalPort = int.TryParse(map.GetValueOrDefault("db-internal-port"), out var dbip) ? dbip : 1433,
            DatabaseContainerHostPort = int.TryParse(map.GetValueOrDefault("db-host-port"), out var dbhp) ? dbhp : 1434,
            DatabaseUsername = map.GetValueOrDefault("db-user", "sa"),
            DatabasePassword = map.GetValueOrDefault("db-password") 
                ?? Environment.GetEnvironmentVariable("AUTOGRADING_DB_PASSWORD") 
                ?? "", // Will be read from Environment.xlsx if not provided
            
            // Parallel grading and index range settings
            MaxParallelStudents = int.TryParse(map.GetValueOrDefault("parallel"), out var parallel) ? Math.Max(1, parallel) : 1,
            StartIndex = int.TryParse(map.GetValueOrDefault("start-index"), out var si) ? Math.Max(0, si) : 0,
            EndIndex = int.TryParse(map.GetValueOrDefault("end-index"), out var ei) ? ei : -1
        };

        // Optional: filter by paper or student
        var paperNo = map.GetValueOrDefault("paper");
        var studentCode = map.GetValueOrDefault("student");

        Console.WriteLine("========================================");
        Console.WriteLine("SolutionGrader CLI - Docker Grading Mode");
        Console.WriteLine("========================================");
        Console.WriteLine();
        Console.WriteLine($"Submit folder: {config.SubmitFolderPath}");
        Console.WriteLine($"TestKit folder: {config.TestKitFolderPath}");
        Console.WriteLine($"Output folder: {config.SaveResultFolderPath}");
        Console.WriteLine($"Server project: {config.ServerProjectName} (has-server: {config.HasServer})");
        Console.WriteLine($"Client project: {config.ClientProjectName} (has-client: {config.HasClient})");
        if (!string.IsNullOrEmpty(paperNo)) Console.WriteLine($"Paper filter: {paperNo}");
        if (!string.IsNullOrEmpty(studentCode)) Console.WriteLine($"Student filter: {studentCode}");
        Console.WriteLine();

        // Execute Docker-based grading
        var gradingService = new CliDockerGradingService();
        return await gradingService.ExecuteAsync(config, paperNo, studentCode);
    }

    /// <summary>
    /// List students in the submit folder.
    /// </summary>
    private static int ListStudents(Dictionary<string, string> map)
    {
        if (!Need(map, "submit"))
        {
            Console.WriteLine("[ERROR] Missing required argument: --submit");
            return PrintUsage();
        }

        var submitPath = map["submit"];
        var paperFilter = map.GetValueOrDefault("paper");

        Console.WriteLine("========================================");
        Console.WriteLine("Students in Submit Folder");
        Console.WriteLine("========================================");

        if (!Directory.Exists(submitPath))
        {
            Console.WriteLine($"[ERROR] Submit folder not found: {submitPath}");
            return 1;
        }

        var paperDirs = Directory.GetDirectories(submitPath)
            .Where(d => int.TryParse(Path.GetFileName(d), out _))
            .OrderBy(d => int.Parse(Path.GetFileName(d)));

        int totalStudents = 0;
        foreach (var paperDir in paperDirs)
        {
            var paper = Path.GetFileName(paperDir);
            if (!string.IsNullOrEmpty(paperFilter) && paper != paperFilter)
                continue;

            var students = Directory.GetDirectories(paperDir)
                .Select(Path.GetFileName)
                .Where(s => !s!.Contains("."))
                .OrderBy(s => s)
                .ToList();

            Console.WriteLine($"\nPaper {paper}: ({students.Count} students)");
            foreach (var student in students)
            {
                var solutionPath = Path.Combine(paperDir, student!, "1", "solution");
                var hasSolution = Directory.Exists(solutionPath);
                Console.WriteLine($"  - {student} {(hasSolution ? "✓" : "✗ (no solution)")}");
            }
            totalStudents += students.Count;
        }

        Console.WriteLine($"\nTotal: {totalStudents} students");
        return 0;
    }

    /// <summary>
    /// Validate test kit structure.
    /// </summary>
    private static int ValidateTestKit(Dictionary<string, string> map)
    {
        if (!Need(map, "testkit"))
        {
            Console.WriteLine("[ERROR] Missing required argument: --testkit");
            return PrintUsage();
        }

        var testkitPath = map["testkit"];

        Console.WriteLine("========================================");
        Console.WriteLine("TestKit Validation");
        Console.WriteLine("========================================");

        if (!Directory.Exists(testkitPath))
        {
            Console.WriteLine($"[ERROR] TestKit folder not found: {testkitPath}");
            return 1;
        }

        bool hasErrors = false;

        // Check Mapping.xlsx
        var mappingPath = Path.Combine(testkitPath, "Mapping.xlsx");
        if (File.Exists(mappingPath))
        {
            Console.WriteLine("✓ Mapping.xlsx found");
            try
            {
                using var wb = new XLWorkbook(mappingPath);
                var ws = wb.Worksheet(1);
                var rows = ws.RowsUsed().Skip(1).ToList();
                Console.WriteLine($"  - {rows.Count} paper mappings found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Error reading Mapping.xlsx: {ex.Message}");
                hasErrors = true;
            }
        }
        else
        {
            Console.WriteLine("⚠ Mapping.xlsx not found (will use convention-based matching)");
        }

        // Check question folders
        var questionDirs = Directory.GetDirectories(testkitPath)
            .Where(d => !Path.GetFileName(d).Equals("Mapping", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var qDir in questionDirs)
        {
            var qName = Path.GetFileName(qDir);
            Console.WriteLine($"\nQuestion: {qName}");

            // Check Environment.xlsx
            var envPath = Path.Combine(qDir, "Environment.xlsx");
            if (File.Exists(envPath))
                Console.WriteLine("  ✓ Environment.xlsx");
            else
            {
                Console.WriteLine("  ✗ Environment.xlsx missing");
                hasErrors = true;
            }

            // Check Header.xlsx
            var headerPath = Path.Combine(qDir, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                Console.WriteLine("  ✓ Header.xlsx");
                try
                {
                    using var wb = new XLWorkbook(headerPath);
                    if (wb.TryGetWorksheet("QuestionMark", out var markSheet))
                    {
                        var testCases = markSheet.RowsUsed().Skip(1).ToList();
                        Console.WriteLine($"    - {testCases.Count} test cases defined");
                    }
                }
                catch { }
            }
            else
            {
                Console.WriteLine("  ✗ Header.xlsx missing");
                hasErrors = true;
            }

            // Check test case folders
            var tcDirs = Directory.GetDirectories(qDir)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var tcDir in tcDirs)
            {
                var tcName = Path.GetFileName(tcDir);
                var detailPath = Path.Combine(tcDir, "Detail.xlsx");
                if (File.Exists(detailPath))
                {
                    Console.WriteLine($"  ✓ {tcName}/Detail.xlsx");
                    try
                    {
                        using var wb = new XLWorkbook(detailPath);
                        var sheets = wb.Worksheets.Select(ws => ws.Name).ToList();
                        var required = new[] { "User", "Client", "Server" };
                        var missing = required.Where(r => !sheets.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (missing.Count > 0)
                            Console.WriteLine($"    ⚠ Missing sheets: {string.Join(", ", missing)}");
                    }
                    catch { }
                }
                else
                {
                    Console.WriteLine($"  ✗ {tcName}/Detail.xlsx missing");
                    hasErrors = true;
                }
            }

            // Check Meta/Given folder
            var givenPath = Path.Combine(qDir, "Meta", "Given");
            if (Directory.Exists(givenPath))
            {
                var serverPath = Path.Combine(givenPath, "Server");
                var clientPath = Path.Combine(givenPath, "Client");
                Console.WriteLine($"  ✓ Meta/Given folder");
                Console.WriteLine($"    - Server: {(Directory.Exists(serverPath) ? "✓" : "✗")}");
                Console.WriteLine($"    - Client: {(Directory.Exists(clientPath) ? "✓" : "✗")}");
            }
        }

        Console.WriteLine($"\nValidation {(hasErrors ? "FAILED" : "PASSED")}");
        return hasErrors ? 1 : 0;
    }

#if WINDOWS
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

        // AppsettingsCreationService now uses GraderPort from GradingConfig
        var appsettings = new AppsettingsCreationService(gradingConfig);

        IRunContext runctx = new RunContext();

        IExecutableManager proc = new ExecutableManager(runctx);
        
        // NEW: Use NetworkMonitorService instead of MiddlewareProxyService
        // The network monitor passively sniffs packets instead of proxying traffic
        INetworkMonitorService networkMonitor = new NetworkMonitorService(runctx);
        
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files, runctx); // <-- Excel logger

        IExecutor exec = new Executor(proc, cmp, log, runctx, gradingConfig);
        IReportService rep = new ReportService(files);
        
        // Create DLL modification service for automatic fallback support when appsettings.json is missing
        // To disable DLL modification: set environment variable DISABLE_DLL_MOD=true or change this line to: IDllModificationService? dllMod = null;
        IDllModificationService? dllMod = Environment.GetEnvironmentVariable("DISABLE_DLL_MOD") == "true" 
            ? null 
            : new DllModificationService();

        var flow = new SuiteRunner(files, env, suite, parse, exec, rep, proc, networkMonitor, log, runctx, appsettings, dllMod);
        
        Console.WriteLine($"[Suite] Results will be saved to: {timestampedResultRoot}");

        return await flow.ExecuteSuiteAsync(run);
    }
#endif

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

    private static bool ParseBool(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"
SolutionGrader CLI - Cross-platform grading tool
==================================================

Commands:
  dockergrade  Docker-based grading (cross-platform, Linux/macOS/Windows)
  executesuite Local grading for a test suite (Windows only)
  executepaper Local grading for multiple students (Windows only)
  list         List students in submit folder
  validate     Validate test kit structure

Docker Grading (syncs with SolutionGrader.UI):
  SolutionGrader.Cli dockergrade --submit <submitFolder> --testkit <testkitFolder>
                                 [--out <outputFolder>]
                                 [--paper <paperNo>] [--student <studentCode>]
                                 [--server-name <name>] [--client-name <name>]
                                 [--has-server <true|false>] [--has-client <true|false>]
                                 [--internal-port <port>] [--host-port <port>]
                                 [--network <networkName>] [--timeout <seconds>]
                                 [--tc-timeout <seconds>]

  Required:
    --submit     Path to Submit folder (e.g., ./Submit)
    --testkit    Path to TestKit folder (e.g., ./TestKit/TestKit)

  Optional (matching SolutionGrader.UI configuration):
    --out          Output folder for results (default: {submit}/Results)
    --paper        Filter by paper number (e.g., '1')
    --student      Filter by student code (e.g., 'dongnvhe172649')
    --server-name  Server project name for DLL lookup (default: Project11)
    --client-name  Client project name for DLL lookup (default: Project12)
    --has-server   Whether students submit server code (default: true)
    --has-client   Whether students submit client code (default: true)
    --internal-port Container internal port (default: from Environment.xlsx or 8000)
    --host-port    Container host port (default: from Environment.xlsx or 8000)
    --network      Docker network name (default: auto-grading-network)
    --timeout      Overall grading timeout in seconds (default: 60)
    --tc-timeout   Per-test-case timeout in seconds (default: 15)
    --parallel     Number of students to grade simultaneously (default: 1)
    --start-index  Start grading from this index, 0-based (default: 0)
    --end-index    End grading at this index, -1 for all (default: -1)

Local Grading (Windows only):
  SolutionGrader.Cli executesuite --suite <suiteFolder> --out <resultRoot>
                                  [--client <client.exe>] [--server <server.exe>]
                                  [--use-inner-env]

  SolutionGrader.Cli executepaper --suite <suiteFolder> --out <resultRoot>
                                  --submission-root <submitFolder>

Utility Commands:
  SolutionGrader.Cli list --submit <submitFolder> [--paper <paperNo>]
  SolutionGrader.Cli validate --testkit <testkitFolder>

Examples:
  # Docker grading for all students in paper 1
  dotnet run -- dockergrade --submit ./Submit --testkit ./TestKit/TestKit --paper 1

  # Docker grading for 3 students in parallel from paper 1
  dotnet run -- dockergrade --submit ./Submit --testkit ./TestKit/TestKit --paper 1 --parallel 3

  # Docker grading from index 5 to 10 (restart after incident)
  dotnet run -- dockergrade --submit ./Submit --testkit ./TestKit/TestKit --paper 1 --start-index 5 --end-index 10

  # Docker grading for a specific student
  dotnet run -- dockergrade --submit ./Submit --testkit ./TestKit/TestKit --paper 1 --student dongnvhe172649

  # List all students
  dotnet run -- list --submit ./Submit

  # Validate test kit
  dotnet run -- validate --testkit ./TestKit/TestKit

Environment Variables:
  AUTOGRADING_DB_PASSWORD  Database password override (optional - defaults to Environment.xlsx)

Notes:
  - Docker must be running for dockergrade command
  - Run with sudo on Linux for NetworkMonitor to capture packets (requires libpcap)
  - The output format matches SampleLogging structure for consistency
");
        return -1;
    }
}
