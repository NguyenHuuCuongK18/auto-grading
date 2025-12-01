using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GradingServices;
using NetworkMonitor;

namespace GradingRunner
{
    /// <summary>
    /// Testable command-line grading runner.
    /// 
    /// This program provides a cross-platform way to test the grading flow
    /// without depending on the Windows-only WPF UI.
    /// 
    /// REQUIREMENTS:
    /// - Docker installed and running
    /// - libpcap/Npcap installed (for network monitoring)
    /// - sudo/Administrator privileges (for network capture)
    /// 
    /// USAGE:
    ///   dotnet run --project GradingRunner -- grade --paper 1 --student dongnvhe172649
    ///   dotnet run --project GradingRunner -- grade-all --paper 1
    ///   dotnet run --project GradingRunner -- validate
    ///   dotnet run --project GradingRunner -- list-students --paper 1
    ///   dotnet run --project GradingRunner -- list-testcases
    /// 
    /// ENVIRONMENT VARIABLES:
    ///   SUBMIT_FOLDER - Path to Submit folder (default: ./Submit)
    ///   TESTKIT_FOLDER - Path to TestKit folder (default: ./TestKit)
    ///   OUTPUT_FOLDER - Path to output folder (default: ./GradingResults)
    /// </summary>
    class Program
    {
        private static GradingConfiguration _config = new();
        private static IGradingService? _gradingService;

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== Auto Grading Runner ===");
            Console.WriteLine($"Running on: {Environment.OSVersion}");
            Console.WriteLine($"Runtime: .NET {Environment.Version}");
            Console.WriteLine();

            // Load configuration from environment or defaults
            InitializeConfiguration();

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());

            try
            {
                return command switch
                {
                    "grade" => await GradeStudentCommand(options),
                    "grade-all" => await GradeAllCommand(options),
                    "validate" => ValidateCommand(),
                    "list-students" => ListStudentsCommand(options),
                    "list-testcases" => ListTestCasesCommand(),
                    "test-docker" => await TestDockerCommand(),
                    "test-network" => await TestNetworkCommand(options),
                    "help" or "--help" or "-h" => PrintUsage(),
                    _ => PrintUsage()
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        /// <summary>
        /// Initializes configuration from environment variables or defaults.
        /// </summary>
        private static void InitializeConfiguration()
        {
            var baseDir = AppContext.BaseDirectory;
            
            // Navigate to repository root (from bin/Debug/net8.0/)
            var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            if (!Directory.Exists(Path.Combine(repoRoot, "Submit")))
            {
                repoRoot = Directory.GetCurrentDirectory();
            }

            _config = new GradingConfiguration
            {
                SubmitFolderPath = Environment.GetEnvironmentVariable("SUBMIT_FOLDER") 
                    ?? Path.Combine(repoRoot, "Submit"),
                
                TestKitFolderPath = Environment.GetEnvironmentVariable("TESTKIT_FOLDER") 
                    ?? Path.Combine(repoRoot, "TestKit"),
                
                SaveResultFolderPath = Environment.GetEnvironmentVariable("OUTPUT_FOLDER") 
                    ?? Path.Combine(repoRoot, "GradingResults"),

                HasClient = true,
                HasServer = true,
                ClientProjectName = "Q12",
                ServerProjectName = "Q11",
                ServerPort = 5000,

                // Default mapping: Paper 1 -> Q1 testkit
                PaperToTestKitMapping = new Dictionary<string, string>
                {
                    { "1", "Q1" },
                    { "2", "Q2" }
                }
            };

            Console.WriteLine($"Submit folder: {_config.SubmitFolderPath}");
            Console.WriteLine($"TestKit folder: {_config.TestKitFolderPath}");
            Console.WriteLine($"Output folder: {_config.SaveResultFolderPath}");
            Console.WriteLine();
        }

        /// <summary>
        /// Parses command line options.
        /// </summary>
        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var key = args[i].Substring(2);
                    var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) 
                        ? args[++i] 
                        : "true";
                    options[key] = value;
                }
            }

            return options;
        }

        /// <summary>
        /// Grades a single student.
        /// </summary>
        private static async Task<int> GradeStudentCommand(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("paper", out var paper))
            {
                Console.WriteLine("ERROR: --paper is required");
                return 1;
            }

            if (!options.TryGetValue("student", out var student))
            {
                Console.WriteLine("ERROR: --student is required");
                return 1;
            }

            // Update config with options
            if (options.TryGetValue("client-name", out var clientName))
                _config.ClientProjectName = clientName;
            if (options.TryGetValue("server-name", out var serverName))
                _config.ServerProjectName = serverName;
            if (options.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var port))
                _config.ServerPort = port;

            Console.WriteLine($"Grading student: {student} for paper: {paper}");
            Console.WriteLine();

            // Validate configuration
            var gradingService = new GradingService();
            var (isValid, error) = gradingService.ValidateConfiguration(_config);
            if (!isValid)
            {
                Console.WriteLine($"Configuration error: {error}");
                return 1;
            }

            // Create network monitor if running with sufficient privileges
            INetworkMonitorService? networkMonitor = null;
            try
            {
                networkMonitor = new NetworkMonitorService();
                Console.WriteLine("Network monitor initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Network monitor not available: {ex.Message}");
                Console.WriteLine("Continuing without network monitoring. Run with sudo for full functionality.");
            }

            // Create grading service with network monitor
            var service = new GradingService(null, networkMonitor);

            var progress = new Progress<string>(msg => Console.WriteLine($"  {msg}"));
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\nCancellation requested...");
            };

            var result = await service.GradeStudentAsync(student, paper, _config, progress, cts.Token);

            Console.WriteLine();
            Console.WriteLine("=== GRADING RESULT ===");
            Console.WriteLine($"Student: {result.StudentCode}");
            Console.WriteLine($"Paper: {result.PaperNo}");
            Console.WriteLine($"Status: {(result.Success ? "PASS" : "FAIL")}");
            Console.WriteLine($"Points: {result.TotalPointsAwarded}/{result.TotalPointsPossible}");
            Console.WriteLine($"Duration: {(result.EndTime - result.StartTime).TotalSeconds:F2} seconds");

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                Console.WriteLine($"Error: {result.ErrorMessage}");
            }

            Console.WriteLine();
            Console.WriteLine("Test Cases:");
            foreach (var tc in result.TestCaseResults)
            {
                Console.WriteLine($"  {tc.TestCaseName}: {(tc.Passed ? "PASS" : "FAIL")} ({tc.PointsAwarded}/{tc.PointsPossible})");
                if (!string.IsNullOrEmpty(tc.ErrorMessage))
                {
                    Console.WriteLine($"    Error: {tc.ErrorMessage}");
                }
            }

            return result.Success ? 0 : 1;
        }

        /// <summary>
        /// Grades all students in a paper.
        /// </summary>
        private static async Task<int> GradeAllCommand(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("paper", out var paper))
            {
                Console.WriteLine("ERROR: --paper is required");
                return 1;
            }

            Console.WriteLine($"Grading all students for paper: {paper}");
            Console.WriteLine();

            // Validate configuration
            var gradingService = new GradingService();
            var (isValid, error) = gradingService.ValidateConfiguration(_config);
            if (!isValid)
            {
                Console.WriteLine($"Configuration error: {error}");
                return 1;
            }

            var students = gradingService.GetStudentsForPaper(paper, _config.SubmitFolderPath);
            Console.WriteLine($"Found {students.Count} students: {string.Join(", ", students)}");
            Console.WriteLine();

            // Filter out problematic students
            if (options.TryGetValue("exclude", out var exclude))
            {
                var excludeList = exclude.Split(',').Select(s => s.Trim()).ToList();
                students = students.Where(s => !excludeList.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"After exclusion: {students.Count} students");
            }

            // Create network monitor if available
            INetworkMonitorService? networkMonitor = null;
            try
            {
                networkMonitor = new NetworkMonitorService();
            }
            catch
            {
                Console.WriteLine("WARNING: Network monitor not available");
            }

            var service = new GradingService(null, networkMonitor);
            var progress = new Progress<string>(msg => Console.WriteLine($"  {msg}"));
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var results = await service.GradeAllStudentsAsync(paper, _config, progress, cts.Token);

            Console.WriteLine();
            Console.WriteLine("=== SUMMARY ===");
            Console.WriteLine($"Total students: {results.Count}");
            Console.WriteLine($"Passed: {results.Count(r => r.Success)}");
            Console.WriteLine($"Failed: {results.Count(r => !r.Success)}");
            Console.WriteLine();

            foreach (var result in results)
            {
                Console.WriteLine($"{result.StudentCode}: {(result.Success ? "PASS" : "FAIL")} - {result.TotalPointsAwarded}/{result.TotalPointsPossible}");
            }

            return results.All(r => r.Success) ? 0 : 1;
        }

        /// <summary>
        /// Validates the configuration.
        /// </summary>
        private static int ValidateCommand()
        {
            Console.WriteLine("Validating configuration...");

            var gradingService = new GradingService();
            var (isValid, error) = gradingService.ValidateConfiguration(_config);

            if (isValid)
            {
                Console.WriteLine("Configuration is valid!");
                return 0;
            }
            else
            {
                Console.WriteLine($"Configuration error: {error}");
                return 1;
            }
        }

        /// <summary>
        /// Lists students for a paper.
        /// </summary>
        private static int ListStudentsCommand(Dictionary<string, string> options)
        {
            if (!options.TryGetValue("paper", out var paper))
            {
                Console.WriteLine("ERROR: --paper is required");
                return 1;
            }

            var gradingService = new GradingService();
            var students = gradingService.GetStudentsForPaper(paper, _config.SubmitFolderPath);

            Console.WriteLine($"Students for paper {paper}:");
            foreach (var student in students)
            {
                Console.WriteLine($"  {student}");
            }

            return 0;
        }

        /// <summary>
        /// Lists test cases in testkits.
        /// </summary>
        private static int ListTestCasesCommand()
        {
            var gradingService = new GradingService();

            foreach (var mapping in _config.PaperToTestKitMapping)
            {
                var testKitPath = Path.Combine(_config.TestKitFolderPath, mapping.Value);
                var testCases = gradingService.GetTestCasesForTestKit(testKitPath);

                Console.WriteLine($"Paper {mapping.Key} ({mapping.Value}):");
                foreach (var tc in testCases)
                {
                    Console.WriteLine($"  {tc}");
                }
                Console.WriteLine();
            }

            return 0;
        }

        /// <summary>
        /// Tests Docker connectivity.
        /// </summary>
        private static async Task<int> TestDockerCommand()
        {
            Console.WriteLine("Testing Docker connectivity...");

            try
            {
                var containerService = new DockerContainerService();
                Console.WriteLine("Docker service initialized successfully");

                Console.WriteLine("Creating test container...");
                await containerService.CreateClientContainerAsync("test", null, default);
                Console.WriteLine("Container created successfully");

                Console.WriteLine("Disposing container...");
                await containerService.DisposeAllContainersAsync();
                Console.WriteLine("Container disposed successfully");

                Console.WriteLine("Docker test passed!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Docker test failed: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// Tests network monitoring capability.
        /// </summary>
        private static async Task<int> TestNetworkCommand(Dictionary<string, string> options)
        {
            Console.WriteLine("Testing network monitoring...");
            Console.WriteLine("NOTE: This requires sudo/Administrator privileges and libpcap/Npcap");
            Console.WriteLine();

            if (!options.TryGetValue("port", out var portStr))
                portStr = "5000";
            
            var port = int.Parse(portStr);

            try
            {
                var monitor = new NetworkMonitorService();
                Console.WriteLine("Network monitor initialized successfully");

                Console.WriteLine($"Starting capture on port {port}...");
                await monitor.StartAsync(port);
                Console.WriteLine("Capture started. Press any key to stop...");

                Console.ReadKey(true);

                await monitor.StopAsync();
                var packets = monitor.GetAllPackets();
                Console.WriteLine($"Captured {packets.Count} packets");

                foreach (var packet in packets.Take(10))
                {
                    Console.WriteLine($"  {packet.Timestamp:HH:mm:ss.fff} {packet.Source} -> {packet.Destination} [{packet.Flags}]");
                }

                Console.WriteLine("Network test passed!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Network test failed: {ex.Message}");
                Console.WriteLine("Make sure you are running with sudo/Administrator privileges");
                return 1;
            }
        }

        /// <summary>
        /// Prints usage information.
        /// </summary>
        private static int PrintUsage()
        {
            Console.WriteLine(@"
Auto Grading Runner - Testable Command-Line Interface

USAGE:
  GradingRunner <command> [options]

COMMANDS:
  grade           Grade a single student
  grade-all       Grade all students in a paper
  validate        Validate configuration
  list-students   List students for a paper
  list-testcases  List test cases in testkits
  test-docker     Test Docker connectivity
  test-network    Test network monitoring

OPTIONS:
  --paper <n>         Paper number (required for grade/grade-all/list-students)
  --student <code>    Student code (required for grade)
  --exclude <list>    Comma-separated list of students to exclude
  --client-name <n>   Client project name (default: Q12)
  --server-name <n>   Server project name (default: Q11)
  --port <n>          Server port (default: 5000)

ENVIRONMENT VARIABLES:
  SUBMIT_FOLDER       Path to Submit folder
  TESTKIT_FOLDER      Path to TestKit folder
  OUTPUT_FOLDER       Path to output folder

EXAMPLES:
  # Grade a specific student
  dotnet run -- grade --paper 1 --student dongnvhe172649

  # Grade all students in paper 1, excluding cuongnhhe186494
  dotnet run -- grade-all --paper 1 --exclude cuongnhhe186494

  # Validate configuration
  dotnet run -- validate

  # List students
  dotnet run -- list-students --paper 1

NOTES:
  - Network monitoring requires sudo/Administrator privileges
  - Docker must be running for grading to work
  - libpcap (Linux) or Npcap (Windows) must be installed for network capture
");
            return 1;
        }
    }
}
