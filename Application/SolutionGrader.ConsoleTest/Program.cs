using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Services.Docker;
using SolutionGrader.Core.Abstractions;
using DomainEnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

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
    /// Usage:
    ///   sudo dotnet run -- --submit <submit_folder> --testkit <testkit_folder> --out <output_folder>
    /// </summary>
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== Docker Grading Console Test ===");
            Console.WriteLine();

            try
            {
                var options = ParseArgs(args);

                // Validate options
                if (!options.TryGetValue("submit", out var submitFolder))
                {
                    return PrintUsage();
                }
                if (!options.TryGetValue("testkit", out var testKitFolder))
                {
                    return PrintUsage();
                }
                if (!options.TryGetValue("out", out var outputFolder))
                {
                    outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "GradeResults");
                }

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

                // Ensure output folder exists
                Directory.CreateDirectory(outputFolder);

                Console.WriteLine($"Submit folder: {submitFolder}");
                Console.WriteLine($"TestKit folder: {testKitFolder}");
                Console.WriteLine($"Output folder: {outputFolder}");
                Console.WriteLine();

                // Check Docker is running
                if (!IsDockerRunning())
                {
                    Console.WriteLine("Error: Docker is not running. Please start Docker first.");
                    return 1;
                }
                Console.WriteLine("Docker is running ✓");

                // Test the grading flow
                if (options.ContainsKey("test-docker"))
                {
                    return await TestDockerServicesAsync() ? 0 : 1;
                }

                // Run the full grading flow
                return await RunGradingAsync(submitFolder, testKitFolder, outputFolder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        /// <summary>
        /// Runs the full grading flow.
        /// </summary>
        private static async Task<int> RunGradingAsync(string submitFolder, string testKitFolder, string outputFolder)
        {
            Console.WriteLine("Starting grading...");
            Console.WriteLine();

            // Discover students
            var students = DiscoverStudents(submitFolder);
            Console.WriteLine($"Found {students.Count} students");

            if (students.Count == 0)
            {
                Console.WriteLine("No students found to grade.");
                return 0;
            }

            // Create services
            IFileService fileService = new FileService();
            IRunContext runContext = new RunContext();
            IDetailLogService logService = new ExcelDetailLogService(fileService, runContext);

            using var gradingService = new DockerGradingService(fileService, logService);
            gradingService.LogMessage += (s, msg) => Console.WriteLine($"[Grader] {msg}");

            // Grade each student
            int successCount = 0;
            int failCount = 0;

            foreach (var student in students)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Grading {student.Code} (Paper {student.Paper}) ===");

                try
                {
                    // Build config from Environment.xlsx
                    var config = LoadEnvironmentConfig(testKitFolder);

                    // Add student-specific paths
                    config[DomainEnvConfig.CodeFilePath] = student.ServerPath ?? "";
                    config[DomainEnvConfig.GivenConsolePath] = student.ClientPath ?? "";
                    config[DomainEnvConfig.StudentQuestionName] = Path.GetFileName(student.ServerPath ?? "Server");
                    config[DomainEnvConfig.GivenConsoleAppName] = Path.GetFileName(student.ClientPath ?? "Client");

                    // Run grading
                    var studentResultPath = Path.Combine(outputFolder, student.Paper, "student", student.Code);
                    Directory.CreateDirectory(studentResultPath);

                    var marks = await gradingService.GradeStudentAsync(
                        student.Path,
                        testKitFolder,
                        studentResultPath,
                        config);

                    Console.WriteLine($"Result: {marks} marks");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed: {ex.Message}");
                    failCount++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Total students: {students.Count}");
            Console.WriteLine($"Successful: {successCount}");
            Console.WriteLine($"Failed: {failCount}");

            return failCount > 0 ? 1 : 0;
        }

        /// <summary>
        /// Tests the Docker services without running full grading.
        /// </summary>
        private static async Task<bool> TestDockerServicesAsync()
        {
            Console.WriteLine("Testing Docker services...");
            Console.WriteLine();

            // Test DockerContainerManager
            Console.WriteLine("Testing DockerContainerManager...");
            using var containerManager = new DockerContainerManager();

            // Check if test containers exist
            var testContainerName = "ag-test-container";
            if (containerManager.ContainerExists(testContainerName))
            {
                Console.WriteLine($"Cleaning up existing test container: {testContainerName}");
                containerManager.RemoveContainer(testContainerName);
            }

            Console.WriteLine("DockerContainerManager: OK ✓");

            // Test DockerConsoleReader
            Console.WriteLine();
            Console.WriteLine("Testing DockerConsoleReader...");
            using var consoleReader = new DockerConsoleReader();
            Console.WriteLine("DockerConsoleReader: OK ✓");

            // Test DockerGradingService instantiation
            Console.WriteLine();
            Console.WriteLine("Testing DockerGradingService...");
            IFileService fileService = new FileService();
            IRunContext runContext = new RunContext();
            IDetailLogService logService = new ExcelDetailLogService(fileService, runContext);
            using var gradingService = new DockerGradingService(fileService, logService);
            Console.WriteLine("DockerGradingService: OK ✓");

            Console.WriteLine();
            Console.WriteLine("All Docker service tests passed! ✓");
            return true;
        }

        /// <summary>
        /// Discovers students in the submit folder.
        /// </summary>
        private static List<StudentInfo> DiscoverStudents(string submitFolder)
        {
            var students = new List<StudentInfo>();

            // Structure: Submit/{PaperNo}/{StudentCode}/{QuestionNo}/solution
            foreach (var paperDir in Directory.GetDirectories(submitFolder))
            {
                var paperNo = Path.GetFileName(paperDir);
                if (!int.TryParse(paperNo, out _)) continue;

                foreach (var studentDir in Directory.GetDirectories(paperDir))
                {
                    var studentCode = Path.GetFileName(studentDir);
                    if (studentCode.StartsWith(".")) continue;

                    foreach (var questionDir in Directory.GetDirectories(studentDir))
                    {
                        var questionNo = Path.GetFileName(questionDir);
                        if (!int.TryParse(questionNo, out _)) continue;

                        var solutionPath = Path.Combine(questionDir, "solution");
                        if (!Directory.Exists(solutionPath)) continue;

                        // Find client and server paths
                        string? clientPath = null;
                        string? serverPath = null;

                        foreach (var projectDir in Directory.GetDirectories(solutionPath))
                        {
                            var projectName = Path.GetFileName(projectDir).ToLowerInvariant();
                            if (projectName.Contains("client"))
                                clientPath = projectDir;
                            else if (projectName.Contains("server"))
                                serverPath = projectDir;
                        }

                        students.Add(new StudentInfo
                        {
                            Code = studentCode,
                            Paper = paperNo,
                            Question = questionNo,
                            Path = solutionPath,
                            ClientPath = clientPath,
                            ServerPath = serverPath
                        });
                    }
                }
            }

            return students;
        }

        /// <summary>
        /// Loads configuration from Environment.xlsx in test kit.
        /// </summary>
        private static Dictionary<string, string> LoadEnvironmentConfig(string testKitFolder)
        {
            var config = new Dictionary<string, string>();

            // Set defaults
            config[DomainEnvConfig.DockerNetwork] = "ag-network";
            config[DomainEnvConfig.CodeImageName] = "fptuxaes/aes-dotnet8:latest";
            config[DomainEnvConfig.CodeContainerName] = "ag-server";
            config[DomainEnvConfig.CodeContainerInternalPort] = "5001";
            config[DomainEnvConfig.CodeContainerHostPort] = "5001";
            config[DomainEnvConfig.GivenConsoleContainerName] = "ag-client";
            config[DomainEnvConfig.GivenConsoleImageName] = "fptuxaes/aes-dotnet8:latest";
            config[DomainEnvConfig.DatabaseContainerName] = "ag-db";
            config[DomainEnvConfig.DatabaseImageName] = "mcr.microsoft.com/mssql/server:2022-latest";
            config[DomainEnvConfig.DatabaseContainerInternalPort] = "1433";
            config[DomainEnvConfig.DatabaseContainerHostPort] = "1433";
            config[DomainEnvConfig.DatabaseUsername] = "SA";
            config[DomainEnvConfig.DatabasePassword] = "YourStrong@Passw0rd";

            // Try to load from Environment.xlsx
            var envPath = Path.Combine(testKitFolder, "Environment.xlsx");
            if (File.Exists(envPath))
            {
                try
                {
                    using var workbook = new ClosedXML.Excel.XLWorkbook(envPath);
                    var ws = workbook.Worksheets.FirstOrDefault();
                    if (ws != null)
                    {
                        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                        for (int row = 1; row <= lastRow; row++)
                        {
                            var key = ws.Cell(row, 1).GetString().Trim();
                            var value = ws.Cell(row, 2).GetString().Trim();
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                            {
                                config[key] = value;
                            }
                        }
                    }
                }
                catch { /* Use defaults */ }
            }

            return config;
        }

        /// <summary>
        /// Checks if Docker is running.
        /// </summary>
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

                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(5000);
                return process?.ExitCode == 0;
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
                if (!args[i].StartsWith("--")) continue;

                var key = args[i].TrimStart('-');
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

            return options;
        }

        private static int PrintUsage()
        {
            Console.WriteLine(@"
Usage:
  sudo dotnet run -- --submit <submit_folder> --testkit <testkit_folder> [--out <output_folder>]
  sudo dotnet run -- --test-docker

Required Arguments:
  --submit    Path to Submit folder (containing paper/student/question/solution structure)
  --testkit   Path to TestKit folder (containing TC* folders with Detail.xlsx)

Optional Arguments:
  --out       Output folder for grading results (default: ./GradeResults)
  --test-docker  Run Docker service tests only

Examples:
  # Run grading
  sudo dotnet run -- --submit ./Submit --testkit ./TestKit --out ./Results

  # Test Docker services
  sudo dotnet run -- --test-docker

Note: Must run with sudo/root for network monitoring capabilities.
");
            return 1;
        }

        /// <summary>
        /// Student information for grading.
        /// </summary>
        private class StudentInfo
        {
            public string Code { get; set; } = string.Empty;
            public string Paper { get; set; } = string.Empty;
            public string Question { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public string? ClientPath { get; set; }
            public string? ServerPath { get; set; }
        }
    }
}
