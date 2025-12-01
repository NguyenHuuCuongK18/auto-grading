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
            var allTestCaseResults = new Dictionary<StudentSolution, Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)>>();

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
                    var (mark, tcResults) = await GradeStudentWithDockerAsync(
                        student, testKitPath, testKitConfig, config,
                        clientPath, serverPath, clientDllPath, serverDllPath,
                        logger);

                    student.Mark = mark;
                    allTestCaseResults[student] = tcResults;
                    
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

            // Write results in SampleLogging format
            Console.WriteLine($"\n[OUTPUT] Writing results to {config.SaveResultFolderPath}...");
            WriteResultsInSampleLoggingFormat(config, students, allTestCaseResults, logger);
            Console.WriteLine("[OUTPUT] Results written ✓");

            return failCount > 0 ? 1 : 0;
        }

        /// <summary>
        /// Writes grading results in the SampleLogging format to Excel files.
        /// Creates: StudentsSolution.xlsx per paper, OverallSummary.xlsx per student,
        /// and TC{n}_Result.xlsx per test case.
        /// </summary>
        private static void WriteResultsInSampleLoggingFormat(
            GradingConfiguration config,
            List<StudentSolution> students,
            Dictionary<StudentSolution, Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)>> allTestCaseResults,
            ILoggingService logger)
        {
            try
            {
                // Group students by paper
                var groupedByPaper = students.GroupBy(s => s.PaperNo);

                foreach (var paperGroup in groupedByPaper)
                {
                    var paperNo = paperGroup.Key;
                    var paperStudents = paperGroup.ToList();

                    // Create paper folder structure
                    var paperFolder = Path.Combine(config.SaveResultFolderPath, paperNo);
                    var studentFolder = Path.Combine(paperFolder, "student");
                    Directory.CreateDirectory(studentFolder);

                    // Write StudentsSolution.xlsx for this paper
                    WriteStudentsSolution(Path.Combine(paperFolder, "StudentsSolution.xlsx"), paperStudents);
                    Console.WriteLine($"  Written: {paperNo}/StudentsSolution.xlsx");

                    // Write per-student results
                    foreach (var student in paperStudents)
                    {
                        var studentPath = Path.Combine(studentFolder, student.StudentCode);
                        Directory.CreateDirectory(studentPath);

                        if (allTestCaseResults.TryGetValue(student, out var tcResults))
                        {
                            // Write OverallSummary.xlsx
                            WriteOverallSummary(Path.Combine(studentPath, "OverallSummary.xlsx"), tcResults);

                            // Write TC folders and results
                            foreach (var (tcName, result) in tcResults)
                            {
                                var tcFolder = Path.Combine(studentPath, tcName.Replace("_", ""));
                                Directory.CreateDirectory(tcFolder);
                                
                                WriteTcResult(Path.Combine(tcFolder, $"{tcName.Replace("_", "")}_Result.xlsx"), tcName, result);
                                WriteGradeDetail(Path.Combine(tcFolder, "GradeDetail.xlsx"), tcName, result);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OUTPUT] Error writing results: {ex.Message}");
            }
        }

        private static void WriteStudentsSolution(string path, List<StudentSolution> students)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.AddWorksheet("Summary");

            ws.Cell(1, 1).Value = "StudentCode";
            ws.Cell(1, 2).Value = "Status";
            ws.Cell(1, 3).Value = "Mark";
            ws.Cell(1, 4).Value = "MaxMark";
            ws.Cell(1, 5).Value = "Percentage";

            var headerRange = ws.Range(1, 1, 1, 5);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;

            int row = 2;
            foreach (var s in students.OrderBy(s => s.StudentCode))
            {
                ws.Cell(row, 1).Value = s.StudentCode;
                ws.Cell(row, 2).Value = s.Mark >= s.MaxMark ? "PASS" : s.Mark > 0 ? "PARTIAL" : "FAIL";
                ws.Cell(row, 3).Value = s.Mark;
                ws.Cell(row, 4).Value = s.MaxMark;
                ws.Cell(row, 5).Value = s.MaxMark > 0 ? $"{(s.Mark / s.MaxMark * 100):F1}%" : "N/A";
                row++;
            }

            ws.Columns().AdjustToContents();
            workbook.SaveAs(path);
        }

        private static void WriteOverallSummary(string path, Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)> tcResults)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.AddWorksheet("Summary");

            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";
            ws.Cell(1, 5).Value = "ErrorNotes";

            var headerRange = ws.Range(1, 1, 1, 5);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;

            int row = 2;
            foreach (var (tc, result) in tcResults.OrderBy(t => t.Key))
            {
                ws.Cell(row, 1).Value = tc;
                ws.Cell(row, 2).Value = result.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 3).Value = result.Mark;
                ws.Cell(row, 4).Value = result.MaxMark;
                ws.Cell(row, 5).Value = result.ErrorNotes ?? string.Empty;

                ws.Cell(row, 2).Style.Fill.BackgroundColor = result.Passed ? XLColor.LightGreen : XLColor.LightPink;
                row++;
            }

            ws.Columns().AdjustToContents();
            workbook.SaveAs(path);
        }

        private static void WriteTcResult(string path, string tcName, (bool Passed, double Mark, double MaxMark, string? ErrorNotes) result)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.AddWorksheet("Result");

            ws.Cell(1, 1).Value = "StepId";
            ws.Cell(1, 2).Value = "Stage";
            ws.Cell(1, 3).Value = "Action";
            ws.Cell(1, 4).Value = "Passed";
            ws.Cell(1, 5).Value = "Message";
            ws.Cell(1, 6).Value = "DurationMs";

            var headerRange = ws.Range(1, 1, 1, 6);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;

            // Write a summary row for the test case
            ws.Cell(2, 1).Value = $"{tcName}-SUMMARY";
            ws.Cell(2, 2).Value = "ALL";
            ws.Cell(2, 3).Value = "GRADE";
            ws.Cell(2, 4).Value = result.Passed;
            ws.Cell(2, 5).Value = result.ErrorNotes ?? "Test case completed";
            ws.Cell(2, 6).Value = 0;

            ws.Columns().AdjustToContents();
            workbook.SaveAs(path);
        }

        private static void WriteGradeDetail(string path, string tcName, (bool Passed, double Mark, double MaxMark, string? ErrorNotes) result)
        {
            using var workbook = new XLWorkbook();
            
            // User sheet
            var userWs = workbook.AddWorksheet("User");
            userWs.Cell(1, 1).Value = "Stage";
            userWs.Cell(1, 2).Value = "Input";
            userWs.Cell(1, 3).Value = "Action";

            // Client sheet
            var clientWs = workbook.AddWorksheet("Client");
            clientWs.Cell(1, 1).Value = "Stage";
            clientWs.Cell(1, 2).Value = "Console";

            // Server sheet
            var serverWs = workbook.AddWorksheet("Server");
            serverWs.Cell(1, 1).Value = "Stage";
            serverWs.Cell(1, 2).Value = "Console";

            // Database sheet
            var dbWs = workbook.AddWorksheet("Database");
            
            // Network sheet with actual vs expected
            var netWs = workbook.AddWorksheet("Network");
            netWs.Cell(1, 1).Value = "Stage";
            netWs.Cell(1, 2).Value = "Result";
            netWs.Cell(1, 3).Value = "Notes";
            netWs.Cell(2, 1).Value = "ALL";
            netWs.Cell(2, 2).Value = result.Passed ? "PASS" : "FAIL";
            netWs.Cell(2, 3).Value = result.ErrorNotes ?? "";

            workbook.SaveAs(path);
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
        /// Returns (totalMark, testCaseResults) tuple for writing to Excel files.
        /// </summary>
        private static async Task<(double TotalMark, Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)> TestCaseResults)> GradeStudentWithDockerAsync(
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
            var testCaseResults = new Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)>();

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
                    return (0, testCaseResults);
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
                    return (0, testCaseResults);
                }

                await Task.Delay(3000); // Wait for apps to start

                // Execute each test case and collect results for logging
                foreach (var tcName in testKitConfig.TestCases)
                {
                    Console.WriteLine($"\n[TC] {tcName}");

                    var detailPath = Path.Combine(testKitPath, tcName, "Detail.xlsx");
                    if (!File.Exists(detailPath))
                    {
                        Console.WriteLine($"  SKIP: Detail.xlsx not found");
                        testCaseResults[tcName] = (false, 0, 0, "Detail.xlsx not found");
                        continue;
                    }

                    var tcMaxMark = GetTestCaseMark(testKitPath, tcName);
                    var (tcMark, passed, errorNotes) = await ExecuteTestCaseAsync(tcName, detailPath, env, config, logger);

                    Console.WriteLine($"  Result: {tcMark}/{tcMaxMark} ({(passed ? "PASS" : "FAIL")})");
                    totalMark += tcMark;
                    
                    testCaseResults[tcName] = (passed, tcMark, tcMaxMark, errorNotes);
                }

                // Output test case results summary
                Console.WriteLine("\n[SUMMARY] Test Case Results:");
                foreach (var (tc, result) in testCaseResults)
                {
                    var status = result.Passed ? "PASS" : "FAIL";
                    Console.WriteLine($"  {tc}: {status} ({result.Mark}/{result.MaxMark})");
                    if (!result.Passed && !string.IsNullOrEmpty(result.ErrorNotes))
                    {
                        // Truncate error notes for console display
                        var notes = result.ErrorNotes.Length > 200 
                            ? result.ErrorNotes.Substring(0, 200) + "..." 
                            : result.ErrorNotes;
                        Console.WriteLine($"    Error: {notes}");
                    }
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

            return (totalMark, testCaseResults);
        }

        /// <summary>
        /// Executes a single test case.
        /// Reads steps from Detail.xlsx User sheet, expected outputs from Client/Server sheets.
        /// Uses docker attach to capture actual outputs and compares with expected.
        /// Returns (mark, passed, errorNotes) tuple.
        /// </summary>
        private static async Task<(double Mark, bool Passed, string? ErrorNotes)> ExecuteTestCaseAsync(
            string tcName,
            string detailPath,
            Domain.Entities.Main.Environment env,
            GradingConfiguration config,
            ILoggingService logger)
        {
            var errors = new List<string>();
            
            try
            {
                var steps = ReadDetailSteps(detailPath);
                var expectedClient = ReadExpectedOutput(detailPath, "Client");
                var expectedServer = ReadExpectedOutput(detailPath, "Server");
                
                Console.WriteLine($"  Steps: {steps.Count}");

                string clientContainer = env.Configs.GetValueOrDefault(EnvConfig.GivenConsoleContainerName, "ag-client");
                string serverContainer = env.Configs.GetValueOrDefault(EnvConfig.CodeContainerName, "ag-server");
                string clientAppName = config.ClientProjectName;
                string serverAppName = config.ServerProjectName;

                var executor = new DockerCommandExecutor();
                bool allPassed = true;
                var failedStages = new List<int>();

                // Execute each step
                foreach (var step in steps)
                {
                    switch (step.Action?.ToUpperInvariant())
                    {
                        case "STARTCLIENT":
                            Console.WriteLine($"  [StartClient] Stage {step.Stage}");
                            await Task.Delay(1000);
                            break;
                            
                        case "STARTSERVER":
                            Console.WriteLine($"  [StartServer] Stage {step.Stage}");
                            await Task.Delay(1000);
                            break;
                            
                        case "INPUT":
                            if (!string.IsNullOrEmpty(step.Input))
                            {
                                Console.WriteLine($"  [INPUT] {step.Input}");
                                executor.SendInputToContainer(clientContainer, clientAppName, step.Input);
                                await Task.Delay(2000); // Wait for processing
                            }
                            break;
                            
                        case "WAIT":
                            var waitMs = int.TryParse(step.Input, out var ms) ? ms : 1000;
                            Console.WriteLine($"  [WAIT] {waitMs}ms");
                            await Task.Delay(waitMs);
                            break;
                    }
                }

                // Capture and compare outputs
                await Task.Delay(2000); // Wait for all output to be produced

                // Get actual container outputs
                string actualClientOutput = GetContainerOutput(executor, clientContainer);
                string actualServerOutput = GetContainerOutput(executor, serverContainer);

                Console.WriteLine($"  [DEBUG] Client output length: {actualClientOutput.Length}");
                Console.WriteLine($"  [DEBUG] Server output length: {actualServerOutput.Length}");

                // Compare client outputs for each stage with non-empty expectations
                foreach (var (stage, expectedOut) in expectedClient)
                {
                    if (string.IsNullOrEmpty(expectedOut)) continue;
                    
                    var (match, errorMsg) = CompareOutputWithDetails(expectedOut, actualClientOutput, $"Client Stage {stage}");
                    if (!match)
                    {
                        Console.WriteLine($"  [FAIL] {errorMsg}");
                        errors.Add($"Stage {stage}:\n  - Console Output: {errorMsg}");
                        failedStages.Add(stage);
                        allPassed = false;
                    }
                    else
                    {
                        Console.WriteLine($"  [PASS] Client output stage {stage}");
                    }
                }

                // Compare server outputs for each stage with non-empty expectations
                foreach (var (stage, expectedOut) in expectedServer)
                {
                    if (string.IsNullOrEmpty(expectedOut)) continue;
                    
                    var (match, errorMsg) = CompareOutputWithDetails(expectedOut, actualServerOutput, $"Server Stage {stage}");
                    if (!match)
                    {
                        Console.WriteLine($"  [FAIL] {errorMsg}");
                        errors.Add($"Stage {stage}:\n  - Console Output: {errorMsg}");
                        if (!failedStages.Contains(stage))
                            failedStages.Add(stage);
                        allPassed = false;
                    }
                    else
                    {
                        Console.WriteLine($"  [PASS] Server output stage {stage}");
                    }
                }

                // Return full mark if passed, 0 otherwise
                var maxMark = GetTestCaseMark(Path.GetDirectoryName(Path.GetDirectoryName(detailPath)) ?? "", tcName);
                
                var errorNotes = errors.Count > 0 
                    ? $"Failed {failedStages.Count} step(s):\n{string.Join("\n", errors)}"
                    : null;

                return (allPassed ? maxMark : 0, allPassed, errorNotes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error: {ex.Message}");
                return (0, false, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads expected output from a Detail.xlsx sheet (Client or Server).
        /// Returns dictionary of stage -> expected console output.
        /// </summary>
        private static Dictionary<int, string> ReadExpectedOutput(string detailPath, string sheetName)
        {
            var outputs = new Dictionary<int, string>();
            try
            {
                using var wb = new XLWorkbook(detailPath);
                var ws = wb.Worksheet(sheetName);
                if (ws == null) return outputs;

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                for (int row = 2; row <= lastRow; row++)
                {
                    var stageStr = ws.Cell(row, 1).GetString()?.Trim();
                    var console = ws.Cell(row, 2).GetString()?.Trim();
                    
                    if (!string.IsNullOrEmpty(stageStr) && int.TryParse(stageStr, out var stage))
                    {
                        outputs[stage] = console ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Failed to read expected output from {sheetName}: {ex.Message}");
            }
            return outputs;
        }

        /// <summary>
        /// Gets the current output from a container.
        /// </summary>
        private static string GetContainerOutput(DockerCommandExecutor executor, string containerName)
        {
            try
            {
                return executor.GetContainerLogs(containerName) ?? "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Failed to get logs from {containerName}: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Compares expected output with actual output using proper normalization.
        /// This follows the same logic as DataComparisonService for consistent grading.
        /// Returns (match, errorMessage) tuple.
        /// </summary>
        private static (bool Match, string? ErrorMessage) CompareOutputWithDetails(string expected, string actual, string context)
        {
            // Empty expected means no expectation - always pass
            if (string.IsNullOrWhiteSpace(expected)) 
                return (true, null);
            
            // If expected is defined but actual is empty, fail
            if (string.IsNullOrWhiteSpace(actual)) 
                return (false, $"{context}: Expected output defined but no actual output captured");

            // Normalize both using the same logic as DataComparisonService
            var normalizedExpected = NormalizeOutput(expected);
            var normalizedActual = NormalizeOutput(actual);

            // Exact match after normalization
            if (normalizedExpected == normalizedActual)
                return (true, null);

            // Try aggressive normalization (remove all whitespace and punctuation)
            var aggressiveExpected = StripAggressive(normalizedExpected);
            var aggressiveActual = StripAggressive(normalizedActual);

            if (aggressiveExpected == aggressiveActual)
                return (true, null);

            // Failed - build detailed error message
            var firstDiffIdx = FindFirstDifference(normalizedExpected, normalizedActual);
            var errorMsg = $"{context}: Text Content Mismatch: Content differs at position {firstDiffIdx}\n" +
                          $"Expected (normalized): {FormatForDisplay(expected, 100)}\n" +
                          $"Actual (normalized): {FormatForDisplay(actual, 100)}";

            return (false, errorMsg);
        }

        /// <summary>
        /// Normalizes output for comparison - matches DataComparisonService logic.
        /// </summary>
        private static string NormalizeOutput(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // Strip BOM
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);

            // Normalize line endings
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");

            // Replace all Unicode whitespace variants with regular spaces
            s = s.Replace("\u00A0", " ")
                 .Replace("\u2002", " ")
                 .Replace("\u2003", " ")
                 .Replace("\u2009", " ")
                 .Replace("\u200A", " ")
                 .Replace("\u202F", " ")
                 .Replace("\u205F", " ")
                 .Replace("\u3000", " ")
                 .Replace("\t", " ");

            // Trim each line and remove empty lines
            var lines = s.Split('\n')
                         .Select(line => line.Trim())
                         .Where(line => !string.IsNullOrWhiteSpace(line))
                         .ToArray();
            
            // Join with single space
            s = string.Join(" ", lines);

            // Collapse multiple spaces
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

            return s.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Ultra-aggressive normalization - removes all whitespace and common punctuation.
        /// </summary>
        private static string StripAggressive(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");
            s = s.Replace(",", "").Replace(".", "").Replace(":", "").Replace(";", "");
            return s;
        }

        private static int FindFirstDifference(string a, string b)
        {
            var min = Math.Min(a.Length, b.Length);
            for (int i = 0; i < min; i++)
            {
                if (a[i] != b[i]) return i;
            }
            return a.Length != b.Length ? min : -1;
        }

        private static string FormatForDisplay(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (s.Length <= maxLength) return s;
            return s.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Legacy comparison method for backward compatibility.
        /// </summary>
        private static bool CompareOutput(string expected, string actual)
        {
            var (match, _) = CompareOutputWithDetails(expected, actual, "Output");
            return match;
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
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Failed to read Detail.xlsx steps: {ex.Message}");
            }
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
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Failed to read test case mark from Header.xlsx: {ex.Message}");
            }
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
            // Resolve relative paths from testkit Environment.xlsx to absolute paths
            string testKitPath = testKitConfig.Path;
            string defaultDbFile = Path.Combine(testKitPath, 
                testKitConfig.EnvironmentConfig.GetValueOrDefault("Default_Database_File_Path", "Meta\\database.sql").Replace("\\", "/"));
            string runtimesFolder = Path.Combine(testKitPath,
                testKitConfig.EnvironmentConfig.GetValueOrDefault("Runtimes_Folder", "Meta/runtimes").Replace("\\", "/"));
            
            // Generate unique database name per student session using UTC time with milliseconds and a random suffix
            string databaseName = $"AG_{DateTime.UtcNow:HHmmssfff}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

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
                    // Client container doesn't need exposed ports (it connects to server)
                    // But the setup code expects these values, so we set them to 0 to indicate "no port"
                    [EnvConfig.GivenConsoleContainerInternalPort] = "0",
                    [EnvConfig.GivenConsoleContainerHostPort] = "0",
                    [EnvConfig.StudentQuestionName] = config.ServerProjectName,
                    [EnvConfig.DatabaseImageName] = testKitConfig.DatabaseImageName,
                    [EnvConfig.DatabaseContainerName] = testKitConfig.DatabaseContainerName,
                    [EnvConfig.DatabaseContainerInternalPort] = testKitConfig.DatabaseContainerInternalPort.ToString(),
                    [EnvConfig.DatabaseContainerHostPort] = testKitConfig.DatabaseContainerHostPort.ToString(),
                    [EnvConfig.DatabaseUsername] = testKitConfig.DatabaseUsername,
                    [EnvConfig.DatabasePassword] = testKitConfig.DatabasePassword,
                    // Database script and name configuration
                    [EnvConfig.DefaultDatabaseFilePath] = defaultDbFile,
                    [EnvConfig.DefaultDatabaseName] = testKitConfig.EnvironmentConfig.GetValueOrDefault("Default_Database_Name", "PE_PRN"),
                    [EnvConfig.DatabaseName] = databaseName,
                    // Runtimes folder for copying DLLs
                    [EnvConfig.RuntimesFolder] = runtimesFolder,
                },
                // Steps from Environment.xlsx Run sheet - defines the setup sequence
                // These are the actions to perform when setting up the environment
                Steps = new List<string>
                {
                    Domain.Entities.Constants.EnvironmentQAction.GenerateConnectionFile,
                    Domain.Entities.Constants.EnvironmentQAction.CopyEssentialFilesAndFolders,
                    Domain.Entities.Constants.EnvironmentQAction.GenerateDatabaseScript
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
