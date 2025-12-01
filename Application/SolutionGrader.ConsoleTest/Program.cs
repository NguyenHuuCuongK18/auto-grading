using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using EnvironmentBuilder.helper;
using EnvironmentBuilder.DockerCommand;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Services.Docker;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Models;
using SolutionGrader.Core.Services.UI;
using EnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

namespace SolutionGrader.ConsoleTest
{
    /// <summary>
    /// Console-based test harness for testing the Docker grading flow.
    /// 
    /// This allows testing on Linux without WPF dependencies.
    /// Requires:
    /// - Docker installed and running
    /// - libpcap installed (for network monitoring)
    /// - Run as sudo/root for network sniffing
    /// 
    /// The grading flow:
    /// 1. Discover students in Submit folder by searching for DLLs recursively
    /// 2. Load TestKit configuration from Header.xlsx and Environment.xlsx
    /// 3. Setup Docker containers (MSSQL, client, server)
    /// 4. Execute test cases from Detail.xlsx
    /// 5. Compare output and compute marks
    /// 6. Write results in SampleLogging format
    /// </summary>
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== Docker Grading Console Test ===");
            Console.WriteLine($"Start time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            try
            {
                var options = ParseArgs(args);

                // Test Docker services (doesn't need submit/testkit folders)
                if (options.ContainsKey("test-docker"))
                {
                    if (!IsDockerRunning())
                    {
                        Console.WriteLine("Error: Docker is not running. Please start Docker first.");
                        return 1;
                    }
                    Console.WriteLine("Docker is running ✓");
                    return await TestDockerServicesAsync() ? 0 : 1;
                }

                // Validate required options
                if (!options.TryGetValue("submit", out var submitFolder))
                    return PrintUsage();
                if (!options.TryGetValue("testkit", out var testKitFolder))
                    return PrintUsage();
                if (!options.TryGetValue("out", out var outputFolder))
                    outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "GradeResults");

                // Verify folders exist
                if (!Directory.Exists(submitFolder))
                {
                    Console.WriteLine($"Error: Submit folder does not exist: {submitFolder}");
                    return 1;
                }
                if (!Directory.Exists(testKitFolder))
                {
                    Console.WriteLine($"Error: TestKit folder does not exist: {testKitFolder}");
                    return 1;
                }

                // Get configuration options
                bool hasClient = options.ContainsKey("has-client");
                bool hasServer = options.ContainsKey("has-server");
                string clientDllName = options.GetValueOrDefault("client", "Project12");
                string serverDllName = options.GetValueOrDefault("server", "Project11");
                string paperFilter = options.GetValueOrDefault("paper", "");

                Console.WriteLine($"Submit folder: {submitFolder}");
                Console.WriteLine($"TestKit folder: {testKitFolder}");
                Console.WriteLine($"Output folder: {outputFolder}");
                Console.WriteLine($"Has Client: {hasClient}, Client DLL: {clientDllName}");
                Console.WriteLine($"Has Server: {hasServer}, Server DLL: {serverDllName}");
                if (!string.IsNullOrEmpty(paperFilter))
                    Console.WriteLine($"Paper filter: {paperFilter}");
                Console.WriteLine();

                // Check Docker
                if (!IsDockerRunning())
                {
                    Console.WriteLine("Error: Docker is not running. Please start Docker first.");
                    return 1;
                }
                Console.WriteLine("Docker is running ✓");

                Directory.CreateDirectory(outputFolder);

                var config = new GradingConfiguration
                {
                    SubmitFolderPath = submitFolder,
                    TestKitFolderPath = testKitFolder,
                    SaveResultFolderPath = outputFolder,
                    HasClient = hasClient,
                    HasServer = hasServer,
                    ClientProjectName = clientDllName,
                    ServerProjectName = serverDllName
                };

                return await RunGradingFlowAsync(config, paperFilter);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        /// <summary>
        /// Main grading flow using the UI services.
        /// </summary>
        private static async Task<int> RunGradingFlowAsync(GradingConfiguration config, string paperFilter)
        {
            Console.WriteLine("Starting grading flow...");
            Console.WriteLine();

            var logger = new ConsoleLoggingService();
            var studentService = new StudentDiscoveryService(logger);
            var testKitService = new TestKitDiscoveryService(logger);
            var testKitConfigService = new TestKitConfigService(logger);

            // Discover students - uses recursive DLL search
            var allStudents = studentService.DiscoverStudents(config.SubmitFolderPath, config);
            Console.WriteLine($"Discovered {allStudents.Count} student submissions");

            var students = allStudents;
            if (!string.IsNullOrEmpty(paperFilter))
            {
                students = allStudents.Where(s => s.PaperNo == paperFilter).ToList();
                Console.WriteLine($"Filtered to {students.Count} students for paper {paperFilter}");
            }

            if (students.Count == 0)
            {
                Console.WriteLine("No students found to grade.");
                return 0;
            }

            // Print student list with found paths
            Console.WriteLine("\nStudent submissions:");
            foreach (var student in students)
            {
                Console.WriteLine($"  - {student.StudentCode} (Paper {student.PaperNo})");
                Console.WriteLine($"    Solution: {student.SolutionPath}");
                if (config.HasClient)
                    Console.WriteLine($"    Client ({config.ClientProjectName}.dll): {student.ClientPath ?? "NOT FOUND"}");
                if (config.HasServer)
                    Console.WriteLine($"    Server ({config.ServerProjectName}.dll): {student.ServerPath ?? "NOT FOUND"}");
            }
            Console.WriteLine();

            int successCount = 0;
            int failCount = 0;
            var results = new List<(string Student, string Paper, double Mark, double MaxMark, string Status)>();

            foreach (var student in students)
            {
                Console.WriteLine($"\n{'=',-60}");
                Console.WriteLine($"Grading: {student.StudentCode} (Paper {student.PaperNo})");
                Console.WriteLine($"{'=',-60}");

                try
                {
                    var testKitPath = testKitService.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                    if (string.IsNullOrEmpty(testKitPath))
                    {
                        Console.WriteLine($"ERROR: No test kit found for paper {student.PaperNo}");
                        results.Add((student.StudentCode, student.PaperNo, 0, 0, "No TestKit"));
                        failCount++;
                        continue;
                    }

                    Console.WriteLine($"Using test kit: {testKitPath}");

                    var testKitConfig = testKitConfigService.LoadTestKitConfig(testKitPath);
                    if (testKitConfig == null)
                    {
                        Console.WriteLine($"ERROR: Failed to load test kit config");
                        results.Add((student.StudentCode, student.PaperNo, 0, 0, "Config Failed"));
                        failCount++;
                        continue;
                    }

                    Console.WriteLine($"Total max marks: {testKitConfig.TotalMaxMark}");
                    Console.WriteLine($"Test cases: {string.Join(", ", testKitConfig.TestCases)}");
                    student.MaxMark = testKitConfig.TotalMaxMark;

                    // Determine actual paths - search for DLLs recursively
                    string? clientPath = config.HasClient ? student.ClientPath : null;
                    string? serverPath = config.HasServer ? student.ServerPath : null;

                    // Use Meta/Given if needed
                    if (!config.HasClient || string.IsNullOrEmpty(clientPath))
                    {
                        var givenClient = Path.Combine(testKitPath, "Meta", "Given", "Client");
                        if (Directory.Exists(givenClient))
                            clientPath = givenClient;
                        else
                            Console.WriteLine("WARN: No client available (not from student, not in Meta/Given)");
                    }

                    if (!config.HasServer || string.IsNullOrEmpty(serverPath))
                    {
                        var givenServer = Path.Combine(testKitPath, "Meta", "Given", "Server");
                        if (Directory.Exists(givenServer))
                            serverPath = givenServer;
                        else
                            Console.WriteLine("WARN: No server available (not from student, not in Meta/Given)");
                    }

                    Console.WriteLine($"Client path: {clientPath}");
                    Console.WriteLine($"Server path: {serverPath}");

                    // Get DLL paths by searching recursively
                    string? clientDllPath = null;
                    string? serverDllPath = null;

                    if (!string.IsNullOrEmpty(clientPath))
                    {
                        clientDllPath = FindDllRecursively(clientPath, config.ClientProjectName);
                        Console.WriteLine($"Client DLL: {clientDllPath ?? "NOT FOUND"}");
                    }

                    if (!string.IsNullOrEmpty(serverPath))
                    {
                        serverDllPath = FindDllRecursively(serverPath, config.ServerProjectName);
                        Console.WriteLine($"Server DLL: {serverDllPath ?? "NOT FOUND"}");
                    }

                    // Grade the student
                    var mark = await GradeStudentWithDockerAsync(
                        student, testKitPath, testKitConfig, config,
                        clientPath, serverPath, clientDllPath, serverDllPath,
                        logger);

                    student.Mark = mark;
                    var status = mark == testKitConfig.TotalMaxMark ? "PASS" :
                                 mark > 0 ? "PARTIAL" : "FAIL";

                    results.Add((student.StudentCode, student.PaperNo, mark, testKitConfig.TotalMaxMark, status));
                    
                    if (mark > 0) successCount++;
                    else failCount++;

                    Console.WriteLine($"Result: {mark}/{testKitConfig.TotalMaxMark} marks ({status})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                    results.Add((student.StudentCode, student.PaperNo, 0, student.MaxMark, $"Error"));
                    failCount++;
                }
            }

            // Print summary
            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine("GRADING SUMMARY");
            Console.WriteLine($"{'=',-60}");
            Console.WriteLine($"Total: {students.Count}, Success: {successCount}, Failed: {failCount}");
            Console.WriteLine("\nResults:");
            foreach (var r in results)
            {
                Console.WriteLine($"  {r.Student} (Paper {r.Paper}): {r.Mark}/{r.MaxMark} - {r.Status}");
            }

            return failCount > 0 ? 1 : 0;
        }

        /// <summary>
        /// Finds a DLL file recursively by project name.
        /// Returns the full path to the DLL file.
        /// </summary>
        private static string? FindDllRecursively(string searchPath, string projectName)
        {
            if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath))
                return null;

            var dllName = $"{projectName}.dll";
            
            try
            {
                var files = Directory.GetFiles(searchPath, dllName, SearchOption.AllDirectories);
                return files.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Grades a student using Docker containers.
        /// Calls DotNetEnvironmentManagerHelper directly instead of via EnvironmentManager.exe
        /// to work cross-platform (Linux).
        /// </summary>
        private static async Task<double> GradeStudentWithDockerAsync(
            StudentSolution student,
            string testKitPath,
            TestKitConfig testKitConfig,
            GradingConfiguration config,
            string? clientPath,
            string? serverPath,
            string? clientDllPath,
            string? serverDllPath,
            ILoggingService logger)
        {
            double totalMark = 0;

            // Build environment config
            var env = BuildEnvironmentConfig(config, testKitConfig, clientPath, serverPath, clientDllPath, serverDllPath);

            // Use EnvironmentSetupService directly (cross-platform, no external executable)
            var envService = new DotNetEnvironmentManagerHelper.Services.EnvironmentSetupService();

            try
            {
                Console.WriteLine("\n[ENV] Setting up Docker containers...");

                // Setup containers directly via service
                try
                {
                    envService.SetupContainerForTestKit(env);
                    Console.WriteLine("[ENV] Containers created ✓");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ENV] Container setup failed: {ex.Message}");
                    return 0;
                }

                // Setup environment for question (deploy files and start apps)
                try
                {
                    envService.SetupEnvironmentForQuestion(env);
                    envService.ExecuteSetupEnvironmentForQuestionBySteps();
                    Console.WriteLine("[ENV] Files deployed and apps started ✓");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ENV] Question setup failed: {ex.Message}");
                    return 0;
                }

                await Task.Delay(3000); // Wait for apps to start

                // Execute each test case
                foreach (var tcName in testKitConfig.TestCases)
                {
                    Console.WriteLine($"\n[TC] {tcName}");

                    var detailPath = Path.Combine(testKitPath, tcName, "Detail.xlsx");
                    if (!File.Exists(detailPath))
                    {
                        Console.WriteLine($"  SKIP: Detail.xlsx not found");
                        continue;
                    }

                    var tcMaxMark = GetTestCaseMark(testKitPath, tcName);
                    var tcMark = await ExecuteTestCaseAsync(tcName, detailPath, env, config, logger);

                    Console.WriteLine($"  Result: {tcMark}/{tcMaxMark}");
                    totalMark += tcMark;
                }
            }
            finally
            {
                Console.WriteLine("\n[ENV] Cleaning up...");
                try
                {
                    envService.DisposeContainerForTestKit(env);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ENV] Cleanup error: {ex.Message}");
                }
            }

            return totalMark;
        }

        /// <summary>
        /// Executes a single test case.
        /// </summary>
        private static async Task<double> ExecuteTestCaseAsync(
            string tcName,
            string detailPath,
            Domain.Entities.Main.Environment env,
            GradingConfiguration config,
            ILoggingService logger)
        {
            try
            {
                var steps = ReadDetailSteps(detailPath);
                Console.WriteLine($"  Steps: {steps.Count}");

                string clientContainer = env.Configs.GetValueOrDefault(EnvConfig.GivenConsoleContainerName, "ag-client");
                string clientAppName = config.ClientProjectName;

                var executor = new DockerCommandExecutor();

                foreach (var step in steps)
                {
                    switch (step.Action?.ToUpperInvariant())
                    {
                        case "INPUT":
                            if (!string.IsNullOrEmpty(step.Input))
                            {
                                Console.WriteLine($"  [INPUT] {step.Input}");
                                executor.SendInputToContainer(clientContainer, clientAppName, step.Input);
                                await Task.Delay(2000);
                            }
                            break;
                    }
                }

                // TODO: Implement actual comparison logic
                // For now, placeholder
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.Message}");
                return 0;
            }
        }

        private static List<(int Stage, string? Action, string? Input)> ReadDetailSteps(string detailPath)
        {
            var steps = new List<(int Stage, string? Action, string? Input)>();
            try
            {
                using var wb = new XLWorkbook(detailPath);
                var ws = wb.Worksheet("User");
                if (ws == null) return steps;

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                for (int row = 2; row <= lastRow; row++)
                {
                    var stage = ws.Cell(row, 1).TryGetValue<int>(out var s) ? s : 0;
                    var input = ws.Cell(row, 2).GetString()?.Trim();
                    var action = ws.Cell(row, 3).GetString()?.Trim();
                    if (stage > 0) steps.Add((stage, action, input));
                }
            }
            catch { }
            return steps;
        }

        private static double GetTestCaseMark(string testKitPath, string tcName)
        {
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (!File.Exists(headerPath)) return 1.0;

            try
            {
                using var wb = new XLWorkbook(headerPath);
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null) return 1.0;

                for (int row = 2; row <= (ws.LastRowUsed()?.RowNumber() ?? 1); row++)
                {
                    var name = ws.Cell(row, 1).GetString()?.Trim();
                    if (name == tcName && ws.Cell(row, 2).TryGetValue<double>(out var mark))
                        return mark;
                }
            }
            catch { }
            return 1.0;
        }

        private static Domain.Entities.Main.Environment BuildEnvironmentConfig(
            GradingConfiguration config,
            TestKitConfig testKitConfig,
            string? clientPath,
            string? serverPath,
            string? clientDllPath,
            string? serverDllPath)
        {
            var env = new Domain.Entities.Main.Environment
            {
                Configs = new Dictionary<string, string>
                {
                    [EnvConfig.DockerNetwork] = config.DockerNetwork,
                    [EnvConfig.CodeImageName] = config.CodeImageName,
                    [EnvConfig.CodeContainerName] = config.ServerContainerName,
                    [EnvConfig.CodeFilePath] = serverPath ?? "",
                    [EnvConfig.CodeContainerInternalPort] = testKitConfig.CodeContainerInternalPort.ToString(),
                    [EnvConfig.CodeContainerHostPort] = testKitConfig.CodeContainerHostPort.ToString(),
                    [EnvConfig.GivenConsoleImageName] = config.CodeImageName,
                    [EnvConfig.GivenConsoleContainerName] = config.ClientContainerName,
                    [EnvConfig.GivenConsolePath] = clientPath ?? "",
                    [EnvConfig.GivenConsoleAppName] = config.ClientProjectName,
                    [EnvConfig.StudentQuestionName] = config.ServerProjectName,
                    [EnvConfig.DatabaseImageName] = testKitConfig.DatabaseImageName,
                    [EnvConfig.DatabaseContainerName] = testKitConfig.DatabaseContainerName,
                    [EnvConfig.DatabaseContainerInternalPort] = testKitConfig.DatabaseContainerInternalPort.ToString(),
                    [EnvConfig.DatabaseContainerHostPort] = testKitConfig.DatabaseContainerHostPort.ToString(),
                    [EnvConfig.DatabaseUsername] = testKitConfig.DatabaseUsername,
                    [EnvConfig.DatabasePassword] = testKitConfig.DatabasePassword,
                }
            };

            // Build Docker paths from DLL locations
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(serverDllPath));
                var dll = Path.GetFileName(serverDllPath);
                env.Configs[EnvConfig.DockerServerPath] = $"/apps/{folder}/{dll}";
            }

            if (!string.IsNullOrEmpty(clientDllPath))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(clientDllPath));
                var dll = Path.GetFileName(clientDllPath);
                env.Configs[EnvConfig.DockerClientPath] = $"/apps/{folder}/{dll}";
            }

            return env;
        }

        private static async Task<bool> TestDockerServicesAsync()
        {
            Console.WriteLine("Testing Docker services...");
            try
            {
                using var cm = new DockerContainerManager();
                Console.WriteLine("DockerContainerManager: OK ✓");
                using var cr = new DockerConsoleReader();
                Console.WriteLine("DockerConsoleReader: OK ✓");
                Console.WriteLine("\nAll tests passed! ✓");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsDockerRunning()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "info",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                var key = args[i].TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    options[key] = args[++i];
                else
                    options[key] = "true";
            }
            return options;
        }

        private static int PrintUsage()
        {
            Console.WriteLine(@"
Usage:
  dotnet run -- --submit <folder> --testkit <folder> [options]

Required:
  --submit    Submit folder path
  --testkit   TestKit folder path

Options:
  --out         Output folder (default: ./GradeResults)
  --client      Client DLL name (default: Project12)
  --server      Server DLL name (default: Project11)
  --has-client  Student has client code
  --has-server  Student has server code
  --paper       Filter by paper number
  --test-docker Test Docker services only

Example:
  dotnet run -- --submit ./Submit --testkit ./TestKit/TestKit --client Project12 --has-client --paper 1
");
            return 1;
        }
    }

    public class ConsoleLoggingService : ILoggingService
    {
        public event EventHandler<LogEventArgs>? LogAdded;
        
        public void SetStudentContext(string? studentCode, string? paperNo = null) { }
        
        public void LogDebug(string msg) => Log("DEBUG", msg);
        public void LogInfo(string msg) => Log("INFO", msg);
        public void LogWarning(string msg) => Log("WARN", msg);
        public void LogError(string msg, Exception? ex = null)
        {
            Log("ERROR", msg);
            if (ex != null) Console.WriteLine($"  {ex.Message}");
        }
        
        private void Log(string level, string msg)
        {
            Console.WriteLine($"[{level}] {msg}");
            LogAdded?.Invoke(this, new LogEventArgs
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = msg
            });
        }
        
        public void Dispose() { }
    }
}
