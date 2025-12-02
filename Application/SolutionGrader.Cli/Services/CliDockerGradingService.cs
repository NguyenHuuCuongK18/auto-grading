using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EnvironmentBuilder.DockerCommand;
using FileMaster.FileEngine;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Services;

namespace SolutionGrader.Cli.Services
{
    /// <summary>
    /// Docker-based grading orchestrator for the CLI.
    /// 
    /// CRITICAL: This service DELEGATES to the shared DockerGradingService from
    /// Lib/SolutionGrader.Core to ensure IDENTICAL grading behavior between CLI and UI.
    /// 
    /// This orchestrator handles:
    /// 1. Discover students from submit folder
    /// 2. Load test kit mapping from Mapping.xlsx
    /// 3. Delegate grading to shared DockerGradingService (same as UI)
    /// 4. Write summary results
    /// 
    /// The actual grading logic (containers, test execution, network monitoring, cleanup) 
    /// is handled by DockerGradingService which is SHARED with SolutionGrader.UI.
    /// </summary>
    public class CliDockerGradingService
    {
        private readonly DockerCommandExecutor _dockerExecutor;

        public CliDockerGradingService()
        {
            _dockerExecutor = new DockerCommandExecutor();
        }

        /// <summary>
        /// Execute grading for students based on configuration.
        /// </summary>
        /// <param name="config">Grading configuration</param>
        /// <param name="paperFilter">Optional paper number filter</param>
        /// <param name="studentFilter">Optional student code filter</param>
        /// <returns>Exit code (0 = success, 1 = failure)</returns>
        public async Task<int> ExecuteAsync(CliGradingConfiguration config, string? paperFilter = null, string? studentFilter = null)
        {
            Console.WriteLine("[CLI] Starting Docker grading using SHARED DockerGradingService...");
            Console.WriteLine("[CLI] This ensures IDENTICAL behavior between CLI and UI.");
            Console.WriteLine();

            // Check if Docker is running
            if (!_dockerExecutor.IsDockerRunning())
            {
                Console.WriteLine("[ERROR] Docker is not running. Please start Docker and try again.");
                return 1;
            }

            // Discover students from submit folder
            var students = DiscoverStudents(config.SubmitFolderPath, config, paperFilter, studentFilter);
            if (students.Count == 0)
            {
                Console.WriteLine("[WARNING] No students found in submit folder.");
                return 0;
            }

            Console.WriteLine($"[CLI] Found {students.Count} student(s) to grade.");
            Console.WriteLine();

            // Create output directory
            Directory.CreateDirectory(config.SaveResultFolderPath);

            // Grade each student using the SHARED DockerGradingService
            var results = new List<StudentGradingResult>();
            int studentIndex = 0;

            foreach (var student in students)
            {
                studentIndex++;
                Console.WriteLine($"\n{'=',-60}");
                Console.WriteLine($"[{studentIndex}/{students.Count}] Grading student: {student.StudentCode} (Paper {student.PaperNo})");
                Console.WriteLine($"{'=',-60}");

                var result = await GradeStudentUsingSharedServiceAsync(student, config);
                results.Add(result);

                Console.WriteLine($"[CLI] Result: {(result.Passed ? "PASSED" : "FAILED")} - {result.TotalMark:F2}/{result.MaxMark:F2}");
            }

            // Write overall summary
            await WriteOverallSummaryAsync(config.SaveResultFolderPath, results);

            Console.WriteLine();
            Console.WriteLine($"{'=',-60}");
            Console.WriteLine("[CLI] Grading Complete!");
            Console.WriteLine($"Total students: {results.Count}");
            Console.WriteLine($"Passed: {results.Count(r => r.Passed)}");
            Console.WriteLine($"Failed: {results.Count(r => !r.Passed)}");
            Console.WriteLine($"Results saved to: {config.SaveResultFolderPath}");
            Console.WriteLine($"{'=',-60}");

            return results.Any(r => r.Passed) ? 0 : 1;
        }

        /// <summary>
        /// Grade a single student using the SHARED DockerGradingService.
        /// This ensures identical grading logic between CLI and UI.
        /// </summary>
        private async Task<StudentGradingResult> GradeStudentUsingSharedServiceAsync(StudentInfo student, CliGradingConfiguration config)
        {
            var result = new StudentGradingResult
            {
                StudentCode = student.StudentCode,
                PaperNo = student.PaperNo
            };

            try
            {
                // Get test kit for this paper
                var testKitPath = GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    Console.WriteLine($"[WARNING] No test kit found for paper {student.PaperNo}");
                    result.ErrorMessage = $"No test kit for paper {student.PaperNo}";
                    return result;
                }

                // Create student result path - simplified to: {saveResultFolder}/{studentCode}
                // This matches the UI's simplified structure
                var studentResultPath = Path.Combine(config.SaveResultFolderPath, student.StudentCode);
                Directory.CreateDirectory(studentResultPath);

                // Build DockerGradingConfig from CLI config
                var dockerConfig = new DockerGradingConfig
                {
                    HasClient = config.HasClient,
                    HasServer = config.HasServer,
                    ClientProjectName = config.ClientProjectName,
                    ServerProjectName = config.ServerProjectName,
                    CodeContainerInternalPort = config.CodeContainerInternalPort,
                    CodeContainerHostPort = config.CodeContainerHostPort,
                    DockerNetwork = config.DockerNetwork,
                    DatabaseImageName = config.DatabaseImageName,
                    DatabaseContainerName = config.DatabaseContainerName,
                    DatabaseContainerInternalPort = config.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = config.DatabaseContainerHostPort,
                    DatabaseUsername = config.DatabaseUsername,
                    DatabasePassword = config.DatabasePassword,
                    GradingTimeoutSeconds = config.GradingTimeoutSeconds,
                    TestCaseTimeoutSeconds = config.TestCaseTimeoutSeconds
                };

                // Create the SHARED services (same as SolutionGrader.UI)
                IRunContext runContext = new RunContext();
                INetworkMonitorService networkMonitor = new NetworkMonitorService(runContext);

                // Create the SHARED DockerGradingService
                var dockerGradingService = new DockerGradingService(networkMonitor, runContext);

                // Subscribe to progress events
                dockerGradingService.ProgressUpdated += (sender, args) =>
                    Console.WriteLine($"  {args.Message}");

                // Reset database for this student (ensures clean state)
                await dockerGradingService.ResetDatabaseForNewStudentAsync(dockerConfig);

                // Grade the student using the SHARED service
                var dockerResult = await dockerGradingService.GradeStudentAsync(
                    dockerConfig,
                    testKitPath,
                    studentResultPath,
                    student.ServerDllPath,
                    student.ClientDllPath,
                    student.StudentCode,
                    CancellationToken.None);

                // Convert DockerGradingResult to StudentGradingResult
                result.TotalMark = dockerResult.TotalMark;
                result.MaxMark = dockerResult.MaxMark;
                result.Passed = dockerResult.Passed;
                result.ErrorMessage = dockerResult.ErrorMessage;

                // Convert test case results
                foreach (var tcResult in dockerResult.TestCaseResults)
                {
                    result.TestCaseResults.Add(new TestCaseResult
                    {
                        TestCaseName = tcResult.TestCaseName,
                        EarnedMark = tcResult.EarnedMark,
                        MaxMark = tcResult.MaxMark,
                        Passed = tcResult.Passed,
                        ErrorMessage = tcResult.ErrorMessage
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Grading failed for {student.StudentCode}: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #region Student Discovery

        /// <summary>
        /// Discover students from the submit folder.
        /// </summary>
        private List<StudentInfo> DiscoverStudents(string submitPath, CliGradingConfiguration config, string? paperFilter, string? studentFilter)
        {
            var students = new List<StudentInfo>();

            if (!Directory.Exists(submitPath))
            {
                Console.WriteLine($"[ERROR] Submit folder not found: {submitPath}");
                return students;
            }

            // Get paper folders (numbered folders like "1", "2", etc.)
            var paperDirs = Directory.GetDirectories(submitPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .OrderBy(d => int.Parse(Path.GetFileName(d)!));

            foreach (var paperDir in paperDirs)
            {
                var paperNo = Path.GetFileName(paperDir);
                if (!string.IsNullOrEmpty(paperFilter) && paperNo != paperFilter)
                    continue;

                // Get student folders
                var studentDirs = Directory.GetDirectories(paperDir)
                    .Where(d => !Path.GetFileName(d)!.Contains("."))
                    .OrderBy(d => d);

                foreach (var studentDir in studentDirs)
                {
                    var studentCode = Path.GetFileName(studentDir);
                    if (!string.IsNullOrEmpty(studentFilter) && studentCode != studentFilter)
                        continue;

                    // Find solution folder, or try to extract if missing
                    var solutionPath = Path.Combine(studentDir, "1", "solution");
                    if (!Directory.Exists(solutionPath))
                    {
                        // Try to find and extract zip file (matching UI behavior)
                        var questionFolder = Path.Combine(studentDir, "1");
                        if (Directory.Exists(questionFolder))
                        {
                            var zipFiles = Directory.GetFiles(questionFolder, "*.zip");
                            if (zipFiles.Length > 0)
                            {
                                try
                                {
                                    // Use FileMaster for consistent extraction with UI
                                    FileExtractor.ExtractDestination(zipFiles[0], solutionPath);
                                    Console.WriteLine($"[CLI] Extracted solution from zip for {studentCode}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[ERROR] Failed to extract zip for {studentCode}: {ex.Message}");
                                    continue;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[WARNING] No solution folder and no zip file for {studentCode}");
                                continue;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[WARNING] No question folder for {studentCode}");
                            continue;
                        }
                    }

                    // Find server and client DLLs
                    var serverDllPath = FindDll(solutionPath, config.ServerProjectName);
                    var clientDllPath = FindDll(solutionPath, config.ClientProjectName);

                    // At least one component should exist
                    if (string.IsNullOrEmpty(serverDllPath) && string.IsNullOrEmpty(clientDllPath))
                    {
                        Console.WriteLine($"[WARNING] No DLLs found for {studentCode}");
                        continue;
                    }

                    students.Add(new StudentInfo
                    {
                        StudentCode = studentCode!,
                        PaperNo = paperNo!,
                        SolutionPath = solutionPath,
                        ServerDllPath = serverDllPath,
                        ClientDllPath = clientDllPath
                    });

                    Console.WriteLine($"[CLI] Found student: {studentCode} (Server: {(serverDllPath != null ? "✓" : "✗")}, Client: {(clientDllPath != null ? "✓" : "✗")})");
                }
            }

            return students;
        }

        /// <summary>
        /// Find a DLL file for a given project name.
        /// </summary>
        private string? FindDll(string solutionPath, string projectName)
        {
            // Common folder patterns
            var patterns = new[]
            {
                $"{projectName}*",
                $"Q*_{projectName}*",
                $"*{projectName}*"
            };

            foreach (var pattern in patterns)
            {
                var folders = Directory.GetDirectories(solutionPath, pattern, SearchOption.TopDirectoryOnly);
                foreach (var folder in folders)
                {
                    // Look for the main DLL
                    var dllPath = Path.Combine(folder, $"{projectName}.dll");
                    if (File.Exists(dllPath))
                        return dllPath;

                    // Search recursively but skip runtimes folder
                    var dlls = Directory.GetFiles(folder, $"{projectName}.dll", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                        .ToList();

                    if (dlls.Count > 0)
                        return dlls[0];
                }
            }

            // Try Q11/Q12 patterns (common exam patterns)
            var qFolder = projectName.Replace("Project", "Q");
            var qFolders = Directory.GetDirectories(solutionPath, $"{qFolder}*", SearchOption.TopDirectoryOnly);
            foreach (var folder in qFolders)
            {
                var dlls = Directory.GetFiles(folder, $"{projectName}.dll", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                    .ToList();

                if (dlls.Count > 0)
                    return dlls[0];
            }

            return null;
        }

        #endregion

        #region Test Kit Loading

        /// <summary>
        /// Get the test kit path for a specific paper using Mapping.xlsx.
        /// </summary>
        private string? GetTestKitForPaper(string testKitRoot, string paperNo)
        {
            // Try to find mapping
            var mappingPath = Path.Combine(testKitRoot, "Mapping.xlsx");
            if (File.Exists(mappingPath))
            {
                using var wb = new XLWorkbook(mappingPath);
                var ws = wb.Worksheet(1);

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var paper = row.Cell(1).GetValue<string>();
                    var question = row.Cell(2).GetValue<string>();

                    if (paper == paperNo && !string.IsNullOrEmpty(question))
                    {
                        var questionPath = Path.Combine(testKitRoot, question);
                        if (Directory.Exists(questionPath))
                            return questionPath;
                    }
                }
            }

            // Fallback: try direct folder matching
            var directPath = Path.Combine(testKitRoot, paperNo);
            if (Directory.Exists(directPath))
                return directPath;

            // Try Q1, Q2, etc.
            var qPath = Path.Combine(testKitRoot, $"Q{paperNo}");
            if (Directory.Exists(qPath))
                return qPath;

            return null;
        }

        #endregion

        #region Result Writing

        /// <summary>
        /// Write overall grading summary.
        /// </summary>
        private async Task WriteOverallSummaryAsync(string resultPath, List<StudentGradingResult> results)
        {
            var summaryPath = Path.Combine(resultPath, "StudentsSolution.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Summary");

            // Headers
            ws.Cell(1, 1).Value = "StudentCode";
            ws.Cell(1, 2).Value = "Paper";
            ws.Cell(1, 3).Value = "Status";
            ws.Cell(1, 4).Value = "TotalMark";
            ws.Cell(1, 5).Value = "MaxMark";
            ws.Cell(1, 6).Value = "Error";
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var result in results)
            {
                ws.Cell(row, 1).Value = result.StudentCode;
                ws.Cell(row, 2).Value = result.PaperNo;
                ws.Cell(row, 3).Value = result.Passed ? "PASSED" : "FAILED";
                ws.Cell(row, 4).Value = result.TotalMark;
                ws.Cell(row, 5).Value = result.MaxMark;
                ws.Cell(row, 6).Value = result.ErrorMessage ?? "";
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(summaryPath);

            await Task.CompletedTask;
        }

        #endregion
    }

    #region Model Classes

    /// <summary>
    /// Information about a student to be graded.
    /// </summary>
    public class StudentInfo
    {
        public string StudentCode { get; set; } = "";
        public string PaperNo { get; set; } = "";
        public string SolutionPath { get; set; } = "";
        public string? ServerDllPath { get; set; }
        public string? ClientDllPath { get; set; }
    }

    /// <summary>
    /// Result of grading a single student.
    /// </summary>
    public class StudentGradingResult
    {
        public string StudentCode { get; set; } = "";
        public string PaperNo { get; set; } = "";
        public double TotalMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new();
    }

    /// <summary>
    /// Result of a single test case (simplified for CLI).
    /// The detailed results are written by DockerGradingService.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = "";
        public double EarnedMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
