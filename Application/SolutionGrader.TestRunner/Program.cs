using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Abstractions.Docker;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Services.Docker;
using SolutionGrader.Services;

namespace SolutionGrader.TestRunner;

/// <summary>
/// Test runner for Docker-based grading.
/// 
/// This console application provides a testable entry point for running
/// the grading flow without the WPF UI. It can be used to:
/// 1. Test the grading services on Linux
/// 2. Run automated grading via command line
/// 3. Debug grading issues
/// 
/// Usage:
///   SolutionGrader.TestRunner --submit <submitFolder> --testkit <testKitFolder> --out <outputFolder>
///   SolutionGrader.TestRunner --student <studentCode> --paper <paperNo> --testkit <testKitFolder> --out <outputFolder>
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== SolutionGrader.TestRunner ===");
        Console.WriteLine("Docker-based grading test runner");
        Console.WriteLine();
        
        try
        {
            var options = ParseArgs(args);
            
            if (options.Count == 0 || options.ContainsKey("help"))
            {
                PrintUsage();
                return 0;
            }
            
            // Check required arguments
            if (!options.ContainsKey("testkit"))
            {
                Console.Error.WriteLine("Error: --testkit is required");
                return 1;
            }
            
            var testKitPath = options["testkit"];
            var outputPath = options.GetValueOrDefault("out", Path.Combine(Directory.GetCurrentDirectory(), "Results"));
            
            // Create timestamped output folder
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var timestampedOutputPath = Path.Combine(outputPath, $"GradeResult_{timestamp}");
            Directory.CreateDirectory(timestampedOutputPath);
            
            Console.WriteLine($"TestKit: {testKitPath}");
            Console.WriteLine($"Output: {timestampedOutputPath}");
            Console.WriteLine();
            
            // Check for single student or batch mode
            if (options.ContainsKey("student"))
            {
                return await RunSingleStudentAsync(options, testKitPath, timestampedOutputPath);
            }
            else if (options.ContainsKey("submit"))
            {
                return await RunBatchAsync(options, testKitPath, timestampedOutputPath);
            }
            else
            {
                // Run local grading (non-Docker mode) using existing CLI
                return await RunLocalGradingAsync(options, testKitPath, timestampedOutputPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
    
    /// <summary>
    /// Runs grading for a single student using Docker containers.
    /// </summary>
    private static async Task<int> RunSingleStudentAsync(
        Dictionary<string, string> options, 
        string testKitPath, 
        string outputPath)
    {
        var studentCode = options["student"];
        var paperNo = options.GetValueOrDefault("paper", "1");
        var solutionPath = options.GetValueOrDefault("solution", "");
        
        Console.WriteLine($"Running Docker grading for student: {studentCode}");
        Console.WriteLine($"Paper: {paperNo}");
        Console.WriteLine($"Solution: {solutionPath}");
        
        if (string.IsNullOrEmpty(solutionPath) || !Directory.Exists(solutionPath))
        {
            Console.Error.WriteLine("Error: --solution path is required and must exist");
            return 1;
        }
        
        // Check for libpcap
        if (!CheckLibpcap())
        {
            Console.WriteLine("Warning: libpcap not detected. Network monitoring will be in no-op mode.");
            Console.WriteLine("Install libpcap-dev and run with sudo for full functionality.");
        }
        
        // Set up services
        IRunContext runctx = new RunContext { ResultRoot = outputPath };
        
        // Create Docker grading service
        var dockerGrading = new DockerGradingService(runctx);
        
        // Configure student
        var studentConfig = new DockerStudentConfig
        {
            StudentCode = studentCode,
            PaperNo = paperNo,
            SolutionPath = solutionPath,
            HasClient = options.ContainsKey("has-client") && options["has-client"].ToLower() == "true",
            HasServer = !options.ContainsKey("has-server") || options["has-server"].ToLower() == "true",
            ClientProjectName = options.GetValueOrDefault("client-name", "Client"),
            ServerProjectName = options.GetValueOrDefault("server-name", "Server")
        };
        
        // Find DLL paths
        studentConfig.ServerDllPath = FindDllPath(solutionPath, studentConfig.ServerProjectName);
        studentConfig.ClientDllPath = FindDllPath(solutionPath, studentConfig.ClientProjectName);
        
        Console.WriteLine($"Server DLL: {studentConfig.ServerDllPath ?? "Not found"}");
        Console.WriteLine($"Client DLL: {studentConfig.ClientDllPath ?? "Not found"}");
        
        try
        {
            // Setup Docker environment
            Console.WriteLine("\n=== Setting up Docker environment ===");
            var setupResult = await dockerGrading.SetupEnvironmentAsync(studentConfig, testKitPath);
            if (!setupResult.Success)
            {
                Console.Error.WriteLine($"Setup failed: {setupResult.Message}");
                return 1;
            }
            
            // Start network monitor
            var networkMonitor = new NetworkMonitorService(runctx);
            networkMonitor.ConfigurePorts(8888, studentConfig.ServerInternalPort);
            await networkMonitor.StartAsync(false);
            
            // Start server
            Console.WriteLine("\n=== Starting server ===");
            var serverResult = await dockerGrading.StartServerAsync();
            if (!serverResult.Success)
            {
                Console.Error.WriteLine($"Server start failed: {serverResult.Message}");
                return 1;
            }
            await Task.Delay(2000); // Wait for server to initialize
            
            // Start client
            Console.WriteLine("\n=== Starting client ===");
            var clientResult = await dockerGrading.StartClientAsync();
            if (!clientResult.Success)
            {
                Console.Error.WriteLine($"Client start failed: {clientResult.Message}");
                return 1;
            }
            
            // Now run the grading using test case steps from Detail.xlsx
            Console.WriteLine("\n=== Running grading ===");
            
            // Find test cases
            var testCaseDirs = Directory.GetDirectories(testKitPath, "TC*");
            Console.WriteLine($"Found {testCaseDirs.Length} test case(s)");
            
            foreach (var tcDir in testCaseDirs.OrderBy(d => d))
            {
                var tcName = Path.GetFileName(tcDir);
                var detailPath = Path.Combine(tcDir, "Detail.xlsx");
                
                if (!File.Exists(detailPath))
                {
                    Console.WriteLine($"Skipping {tcName}: No Detail.xlsx found");
                    continue;
                }
                
                Console.WriteLine($"\n--- Running {tcName} ---");
                
                // TODO: Parse Detail.xlsx and execute steps
                // For now, just demonstrate the flow
                await RunTestCaseAsync(dockerGrading, networkMonitor, runctx, tcDir, tcName);
                
                // Cleanup between test cases
                await dockerGrading.CleanupTestCaseAsync();
            }
            
            // Stop network monitor
            await networkMonitor.StopAsync();
            
            Console.WriteLine("\n=== Grading complete ===");
            Console.WriteLine($"Server output length: {dockerGrading.GetServerOutput().Length}");
            Console.WriteLine($"Client output length: {dockerGrading.GetClientOutput().Length}");
            
            return 0;
        }
        finally
        {
            // Clean up Docker environment
            Console.WriteLine("\n=== Cleaning up ===");
            await dockerGrading.DisposeEnvironmentAsync();
        }
    }
    
    /// <summary>
    /// Runs a single test case.
    /// </summary>
    private static async Task RunTestCaseAsync(
        DockerGradingService dockerGrading,
        NetworkMonitorService networkMonitor,
        IRunContext runctx,
        string tcDir,
        string tcName)
    {
        networkMonitor.BeginStage(tcName);
        
        // Example: Send some inputs based on test case
        // In real implementation, parse Detail.xlsx and execute steps
        
        Console.WriteLine($"Waiting for initial output...");
        await dockerGrading.WaitForClientOutputAsync(5);
        
        // Example input
        Console.WriteLine("Sending test input...");
        await dockerGrading.SendClientInputAsync("test");
        
        await dockerGrading.WaitForClientOutputAsync(3);
        
        networkMonitor.EndStage(tcName);
        
        Console.WriteLine($"{tcName} completed");
    }
    
    /// <summary>
    /// Runs batch grading for all students in a submit folder.
    /// </summary>
    private static async Task<int> RunBatchAsync(
        Dictionary<string, string> options,
        string testKitPath,
        string outputPath)
    {
        var submitPath = options["submit"];
        
        Console.WriteLine($"Running batch grading for: {submitPath}");
        
        // Discover students
        var studentDirs = new List<(string StudentCode, string PaperNo, string SolutionPath)>();
        
        foreach (var paperDir in Directory.GetDirectories(submitPath))
        {
            var paperNo = Path.GetFileName(paperDir);
            
            foreach (var studentDir in Directory.GetDirectories(paperDir))
            {
                var studentCode = Path.GetFileName(studentDir);
                var solutionPath = Path.Combine(studentDir, "1", "solution");
                
                if (Directory.Exists(solutionPath))
                {
                    studentDirs.Add((studentCode, paperNo, solutionPath));
                }
            }
        }
        
        Console.WriteLine($"Found {studentDirs.Count} students");
        
        int successCount = 0;
        int failCount = 0;
        
        foreach (var (studentCode, paperNo, solutionPath) in studentDirs)
        {
            Console.WriteLine($"\n========================================");
            Console.WriteLine($"Grading: {studentCode} (Paper {paperNo})");
            Console.WriteLine($"========================================");
            
            var studentOptions = new Dictionary<string, string>(options)
            {
                ["student"] = studentCode,
                ["paper"] = paperNo,
                ["solution"] = solutionPath
            };
            studentOptions.Remove("submit");
            
            try
            {
                var result = await RunSingleStudentAsync(studentOptions, testKitPath, outputPath);
                if (result == 0)
                    successCount++;
                else
                    failCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to grade {studentCode}: {ex.Message}");
                failCount++;
            }
        }
        
        Console.WriteLine($"\n========================================");
        Console.WriteLine($"Batch complete: {successCount} success, {failCount} failed");
        Console.WriteLine($"========================================");
        
        return failCount > 0 ? 1 : 0;
    }
    
    /// <summary>
    /// Runs local (non-Docker) grading using the existing SuiteRunner.
    /// </summary>
    private static async Task<int> RunLocalGradingAsync(
        Dictionary<string, string> options,
        string testKitPath,
        string outputPath)
    {
        Console.WriteLine("Running local grading (non-Docker mode)");
        
        var clientPath = options.GetValueOrDefault("client", "");
        var serverPath = options.GetValueOrDefault("server", "");
        
        if (string.IsNullOrEmpty(clientPath) || string.IsNullOrEmpty(serverPath))
        {
            // Try to find executables in Meta/Given folder
            var givenPath = Path.Combine(testKitPath, "Meta", "Given");
            
            if (string.IsNullOrEmpty(clientPath))
            {
                var clientDir = Path.Combine(givenPath, "Client");
                if (Directory.Exists(clientDir))
                {
                    clientPath = FindDllPath(clientDir, "Client") ?? FindDllPath(clientDir, "Project12");
                }
            }
            
            if (string.IsNullOrEmpty(serverPath))
            {
                var serverDir = Path.Combine(givenPath, "Server");
                if (Directory.Exists(serverDir))
                {
                    serverPath = FindDllPath(serverDir, "Server") ?? FindDllPath(serverDir, "Project11");
                }
            }
        }
        
        Console.WriteLine($"Client: {clientPath ?? "Not found"}");
        Console.WriteLine($"Server: {serverPath ?? "Not found"}");
        
        // Set up grading services
        IFileService files = new FileService();
        var env = new EnvironmentResetService(files);
        var suite = new ExcelSuiteLoader();
        var parse = new ExcelDetailParser();
        var gradingConfig = GradingConfig.Default;
        var appsettings = new AppsettingsCreationService();
        
        IRunContext runctx = new RunContext();
        IExecutableManager proc = new ExecutableManager(runctx);
        var networkMonitor = new NetworkMonitorService(runctx);
        IDataComparisonService cmp = new DataComparisonService(runctx);
        IDetailLogService log = new ExcelDetailLogService(files, runctx);
        IExecutor exec = new Executor(proc, networkMonitor, cmp, log, runctx, gradingConfig);
        IReportService rep = new ReportService(files);
        
        var runner = new SuiteRunner(files, env, suite, parse, exec, rep, proc, networkMonitor, log, runctx, appsettings);
        
        var runArgs = new ExecuteSuiteArgs
        {
            SuitePath = testKitPath,
            ResultRoot = outputPath,
            ClientExePath = clientPath,
            ServerExePath = serverPath,
            UseInnerTestCaseEnvironment = true
        };
        
        return await runner.ExecuteSuiteAsync(runArgs);
    }
    
    /// <summary>
    /// Finds a DLL path in a solution folder.
    /// </summary>
    private static string? FindDllPath(string solutionPath, string projectName)
    {
        if (!Directory.Exists(solutionPath))
            return null;
        
        // Look for DLLs with matching names
        var searchPatterns = new[] { $"{projectName}.dll", "Project11.dll", "Project12.dll" };
        
        foreach (var pattern in searchPatterns)
        {
            var files = Directory.GetFiles(solutionPath, pattern, SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                return files[0];
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Checks if libpcap is available.
    /// </summary>
    private static bool CheckLibpcap()
    {
        try
        {
            // Check for libpcap on Linux using RuntimeInformation for better compatibility
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ldconfig",
                    Arguments = "-p",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    return output.Contains("libpcap");
                }
            }
            
            // On Windows, assume Npcap/WinPcap is installed if SharpPcap can be loaded
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Parses command line arguments.
    /// </summary>
    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    options[key] = args[i + 1];
                    i++;
                }
                else
                {
                    options[key] = "true";
                }
            }
        }
        
        return options;
    }
    
    /// <summary>
    /// Prints usage information.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine(@"
SolutionGrader.TestRunner - Docker-based grading test runner

Usage:
  SolutionGrader.TestRunner [options]

Options:
  --help                Show this help message
  --testkit <path>      Path to the test kit folder (required)
  --out <path>          Output directory for results (default: ./Results)
  
  Single student mode:
  --student <code>      Student code to grade
  --paper <no>          Paper number (default: 1)
  --solution <path>     Path to student's solution folder
  --has-client <bool>   Whether student has client (default: true)
  --has-server <bool>   Whether student has server (default: true)
  --client-name <name>  Client project name (default: Client)
  --server-name <name>  Server project name (default: Server)
  
  Batch mode:
  --submit <path>       Path to Submit folder for batch grading
  
  Local mode (non-Docker):
  --client <path>       Path to client executable
  --server <path>       Path to server executable

Examples:
  # Single student Docker grading
  sudo SolutionGrader.TestRunner --testkit ./TestKit/Q1 --student cuongnhhe186494 --solution ./Submit/1/cuongnhhe186494/1/solution

  # Batch Docker grading
  sudo SolutionGrader.TestRunner --testkit ./TestKit/Q1 --submit ./Submit

  # Local grading (non-Docker)
  SolutionGrader.TestRunner --testkit ./TestKit/Q1 --client ./Client.dll --server ./Server.dll

Note: Docker-based grading requires:
  - Docker installed and running
  - libpcap-dev (Linux) or Npcap (Windows) for network monitoring
  - sudo/admin privileges for packet capture
");
    }
}
