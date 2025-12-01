using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using NetworkMonitor;

namespace GradingServices
{
    /// <summary>
    /// Main grading service that orchestrates the entire grading flow.
    /// 
    /// Grading flow:
    /// 1. Validate configuration (testkit mapping, point allocation, etc.)
    /// 2. For each student:
    ///    a. Setup containers (database, client, server)
    ///    b. Start network monitor
    ///    c. For each test case:
    ///       - Parse Detail.xlsx for stages and expected outputs
    ///       - Execute stages (StartClient, StartServer, Input, etc.)
    ///       - Capture console output per stage (1-2 sec wait after action)
    ///       - Capture network traffic per stage
    ///       - Compare actual vs expected
    ///       - Record results
    ///       - Cleanup between test cases
    ///    d. Dispose containers
    ///    e. Generate logs (matching SampleLogging format)
    /// </summary>
    public class GradingService : IGradingService
    {
        private readonly IDockerContainerService _containerService;
        private readonly INetworkMonitorService? _networkMonitor;
        
        public GradingService(IDockerContainerService? containerService = null, INetworkMonitorService? networkMonitor = null)
        {
            _containerService = containerService ?? new DockerContainerService();
            _networkMonitor = networkMonitor;
        }

        /// <summary>
        /// Validates configuration before grading can proceed.
        /// </summary>
        public (bool IsValid, string? ErrorMessage) ValidateConfiguration(GradingConfiguration config)
        {
            // Check Submit folder
            if (string.IsNullOrWhiteSpace(config.SubmitFolderPath))
                return (false, "Submit folder path is required");

            if (!Directory.Exists(config.SubmitFolderPath))
                return (false, $"Submit folder does not exist: {config.SubmitFolderPath}");

            // Check TestKit folder
            if (string.IsNullOrWhiteSpace(config.TestKitFolderPath))
                return (false, "TestKit folder path is required");

            if (!Directory.Exists(config.TestKitFolderPath))
                return (false, $"TestKit folder does not exist: {config.TestKitFolderPath}");

            // Check Save folder
            if (string.IsNullOrWhiteSpace(config.SaveResultFolderPath))
                return (false, "Save result folder path is required");

            // Check testkit mapping
            if (config.PaperToTestKitMapping.Count == 0)
                return (false, "No testkit mapping provided. Cannot proceed without knowing which testkit to use for each paper.");

            // Check that at least one of client/server is enabled
            if (!config.HasClient && !config.HasServer)
                return (false, "At least one of HasClient or HasServer must be true");

            // Check project names if enabled
            if (config.HasClient && string.IsNullOrWhiteSpace(config.ClientProjectName))
                return (false, "Client project name is required when HasClient is true");

            if (config.HasServer && string.IsNullOrWhiteSpace(config.ServerProjectName))
                return (false, "Server project name is required when HasServer is true");

            // Check that testkits exist and have test cases
            foreach (var mapping in config.PaperToTestKitMapping)
            {
                var testKitPath = Path.Combine(config.TestKitFolderPath, mapping.Value);
                if (!Directory.Exists(testKitPath))
                    return (false, $"TestKit for paper {mapping.Key} not found: {testKitPath}");

                var testCases = GetTestCasesForTestKit(testKitPath);
                if (testCases.Count == 0)
                    return (false, $"No test cases found in testkit for paper {mapping.Key}");

                // Check each test case has Detail.xlsx
                foreach (var tc in testCases)
                {
                    var detailPath = Path.Combine(testKitPath, tc, "Detail.xlsx");
                    if (!File.Exists(detailPath))
                        return (false, $"Test case {tc} is missing Detail.xlsx");

                    // Check for point allocation (Header.xlsx at testkit level)
                    var headerPath = Path.Combine(testKitPath, "Header.xlsx");
                    if (!File.Exists(headerPath))
                        return (false, $"TestKit for paper {mapping.Key} is missing Header.xlsx (point allocation)");
                }
            }

            return (true, null);
        }

        /// <summary>
        /// Gets available students for a paper.
        /// </summary>
        public List<string> GetStudentsForPaper(string paperNo, string submitFolderPath)
        {
            var students = new List<string>();
            var paperPath = Path.Combine(submitFolderPath, paperNo);

            if (!Directory.Exists(paperPath))
                return students;

            foreach (var studentDir in Directory.GetDirectories(paperPath))
            {
                var studentCode = Path.GetFileName(studentDir);
                // Filter out .txt files in student folder name check
                if (!studentCode.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    students.Add(studentCode);
                }
            }

            return students;
        }

        /// <summary>
        /// Gets available test cases for a testkit.
        /// Test cases are subdirectories starting with "TC".
        /// </summary>
        public List<string> GetTestCasesForTestKit(string testKitPath)
        {
            var testCases = new List<string>();

            if (!Directory.Exists(testKitPath))
                return testCases;

            foreach (var dir in Directory.GetDirectories(testKitPath))
            {
                var name = Path.GetFileName(dir);
                // Test cases start with TC
                if (name.StartsWith("TC", StringComparison.OrdinalIgnoreCase))
                {
                    // Verify Detail.xlsx exists
                    if (File.Exists(Path.Combine(dir, "Detail.xlsx")))
                    {
                        testCases.Add(name);
                    }
                }
            }

            return testCases.OrderBy(tc => tc).ToList();
        }

        /// <summary>
        /// Grades all students in a paper.
        /// </summary>
        public async Task<List<StudentGradingResult>> GradeAllStudentsAsync(
            string paperNo,
            GradingConfiguration config,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var results = new List<StudentGradingResult>();
            var students = GetStudentsForPaper(paperNo, config.SubmitFolderPath);

            progress?.Report($"Found {students.Count} students for paper {paperNo}");

            foreach (var student in students)
            {
                ct.ThrowIfCancellationRequested();
                
                progress?.Report($"Grading student: {student}");
                var result = await GradeStudentAsync(student, paperNo, config, progress, ct);
                results.Add(result);
                
                progress?.Report($"Completed {student}: {(result.Success ? "PASS" : "FAIL")} - {result.TotalPointsAwarded}/{result.TotalPointsPossible}");
            }

            // Generate summary Excel file
            await GenerateStudentsSolutionExcelAsync(results, config.SaveResultFolderPath, paperNo);

            return results;
        }

        /// <summary>
        /// Grades a single student submission.
        /// </summary>
        public async Task<StudentGradingResult> GradeStudentAsync(
            string studentCode,
            string paperNo,
            GradingConfiguration config,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new StudentGradingResult
            {
                StudentCode = studentCode,
                PaperNo = paperNo,
                StartTime = DateTime.Now
            };

            try
            {
                // Get testkit for this paper
                if (!config.PaperToTestKitMapping.TryGetValue(paperNo, out var testKitName))
                {
                    result.ErrorMessage = $"No testkit mapping for paper {paperNo}";
                    result.Success = false;
                    result.EndTime = DateTime.Now;
                    return result;
                }

                var testKitPath = Path.Combine(config.TestKitFolderPath, testKitName);
                var testCases = GetTestCasesForTestKit(testKitPath);

                if (testCases.Count == 0)
                {
                    result.ErrorMessage = "No test cases found in testkit";
                    result.Success = false;
                    result.EndTime = DateTime.Now;
                    return result;
                }

                // Find student solution paths
                var studentPath = Path.Combine(config.SubmitFolderPath, paperNo, studentCode, "1", "solution");
                var (clientPath, serverPath) = FindStudentProjectPaths(studentPath, config);

                // If student doesn't have client/server, use given from testkit
                var givenPath = Path.Combine(testKitPath, "Meta", "Given");
                if (string.IsNullOrEmpty(clientPath) && config.HasClient)
                {
                    clientPath = Path.Combine(givenPath, "Client");
                    if (!Directory.Exists(clientPath))
                        clientPath = Path.Combine(givenPath, "Server"); // Sometimes client is named Server in given
                }
                if (string.IsNullOrEmpty(serverPath) && config.HasServer)
                {
                    serverPath = Path.Combine(givenPath, "Server");
                }

                progress?.Report($"Client path: {clientPath}");
                progress?.Report($"Server path: {serverPath}");

                // Read point allocation from Header.xlsx
                var pointAllocation = ReadPointAllocationFromHeader(testKitPath);

                // Grade each test case
                foreach (var testCase in testCases)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    progress?.Report($"Running test case: {testCase}");
                    
                    var tcResult = await GradeTestCaseAsync(
                        testCase,
                        testKitPath,
                        clientPath,
                        serverPath,
                        config,
                        pointAllocation.GetValueOrDefault(testCase, 1.0),
                        progress,
                        ct);

                    result.TestCaseResults.Add(tcResult);
                    result.TotalPointsAwarded += tcResult.PointsAwarded;
                    result.TotalPointsPossible += tcResult.PointsPossible;

                    // Cleanup between test cases
                    await _containerService.DisposeAllContainersAsync(ct);
                    progress?.Report($"Cleaned up after {testCase}");
                }

                result.Success = result.TotalPointsAwarded >= result.TotalPointsPossible * 0.5; // Pass if 50% or more
                result.EndTime = DateTime.Now;

                // Generate per-student logs
                await GenerateStudentLogsAsync(result, config.SaveResultFolderPath);

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                result.Success = false;
                result.EndTime = DateTime.Now;
                
                // Ensure cleanup on error
                try { await _containerService.DisposeAllContainersAsync(ct); } catch { }
                
                return result;
            }
        }

        /// <summary>
        /// Grades a single test case with stage-based execution.
        /// </summary>
        private async Task<TestCaseResult> GradeTestCaseAsync(
            string testCaseName,
            string testKitPath,
            string? clientPath,
            string? serverPath,
            GradingConfiguration config,
            double pointsPossible,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var result = new TestCaseResult
            {
                TestCaseName = testCaseName,
                PointsPossible = pointsPossible
            };

            try
            {
                // Read Detail.xlsx for stages
                var detailPath = Path.Combine(testKitPath, testCaseName, "Detail.xlsx");
                var stages = ParseDetailExcel(detailPath);

                if (stages.Count == 0)
                {
                    result.ErrorMessage = "No stages found in Detail.xlsx";
                    return result;
                }

                // Start network monitor
                if (_networkMonitor != null)
                {
                    await _networkMonitor.StartAsync(config.ServerPort, ct);
                }

                // Setup containers
                await _containerService.StartDatabaseContainerAsync(ct);
                
                if (config.HasClient && !string.IsNullOrEmpty(clientPath))
                {
                    await _containerService.CreateClientContainerAsync("student", clientPath, ct);
                }
                
                if (config.HasServer && !string.IsNullOrEmpty(serverPath))
                {
                    await _containerService.CreateServerContainerAsync("student", serverPath, ct);
                }

                // Execute stages
                int currentStage = 0;
                var dockerService = _containerService as DockerContainerService;

                foreach (var stage in stages)
                {
                    ct.ThrowIfCancellationRequested();

                    progress?.Report($"Executing stage {stage.StageNumber}: {stage.Action}");

                    // Execute the action
                    switch (stage.Action.ToUpperInvariant())
                    {
                        case "STARTCLIENT":
                            await _containerService.StartClientApplicationAsync(ct);
                            currentStage = stage.StageNumber;
                            _networkMonitor?.SetStage(currentStage);
                            await Task.Delay(Common.GradingConstants.PostStageChangeDelayMs, ct);
                            break;

                        case "STARTSERVER":
                            await _containerService.StartServerApplicationAsync(ct);
                            currentStage = stage.StageNumber;
                            _networkMonitor?.SetStage(currentStage);
                            await Task.Delay(Common.GradingConstants.PostStageChangeDelayMs, ct);
                            break;

                        case "INPUT":
                            await _containerService.SendClientInputAsync(stage.Input ?? "", ct);
                            currentStage = stage.StageNumber;
                            _networkMonitor?.SetStage(currentStage);
                            await Task.Delay(Common.GradingConstants.PostInputDelayMs, ct);
                            break;

                        case "CLOSECLIENT":
                            await _containerService.StopClientContainerAsync(ct);
                            currentStage = stage.StageNumber;
                            _networkMonitor?.SetStage(currentStage);
                            break;

                        case "CLOSESERVER":
                            await _containerService.StopServerContainerAsync(ct);
                            currentStage = stage.StageNumber;
                            _networkMonitor?.SetStage(currentStage);
                            break;
                    }

                    // Capture stage output
                    if (dockerService != null)
                    {
                        await dockerService.CaptureStageOutputAsync(currentStage, ct);
                    }
                }

                // Stop network monitor
                if (_networkMonitor != null)
                {
                    await _networkMonitor.StopAsync();
                }

                // Compare outputs per stage
                result.StageResults = await CompareStageOutputsAsync(
                    stages, 
                    dockerService, 
                    _networkMonitor, 
                    testKitPath, 
                    testCaseName);

                // Calculate pass/fail
                bool allPassed = result.StageResults.All(s => 
                    s.ClientOutputMatches && s.ServerOutputMatches && s.NetworkMatches);
                
                result.Passed = allPassed;
                result.PointsAwarded = allPassed ? pointsPossible : 0;

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
            finally
            {
                // Ensure network monitor is stopped
                if (_networkMonitor != null)
                {
                    try { await _networkMonitor.StopAsync(); } catch { }
                }
            }
        }

        #region Helper Methods

        /// <summary>
        /// Finds student project paths (client and server DLL folders).
        /// </summary>
        private (string? ClientPath, string? ServerPath) FindStudentProjectPaths(
            string studentSolutionPath,
            GradingConfiguration config)
        {
            string? clientPath = null;
            string? serverPath = null;

            if (!Directory.Exists(studentSolutionPath))
                return (null, null);

            // Look for Q11, Q12 or configured project names
            foreach (var dir in Directory.GetDirectories(studentSolutionPath))
            {
                var dirName = Path.GetFileName(dir);
                
                // Look for published DLL files
                var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
                if (dllFiles.Length == 0) continue;

                // Match client
                if (config.HasClient && clientPath == null)
                {
                    if (dirName.Contains(config.ClientProjectName, StringComparison.OrdinalIgnoreCase) ||
                        dirName.StartsWith("Q12", StringComparison.OrdinalIgnoreCase))
                    {
                        clientPath = dir;
                    }
                }

                // Match server
                if (config.HasServer && serverPath == null)
                {
                    if (dirName.Contains(config.ServerProjectName, StringComparison.OrdinalIgnoreCase) ||
                        dirName.StartsWith("Q11", StringComparison.OrdinalIgnoreCase))
                    {
                        serverPath = dir;
                    }
                }
            }

            return (clientPath, serverPath);
        }

        /// <summary>
        /// Reads point allocation from Header.xlsx.
        /// </summary>
        private Dictionary<string, double> ReadPointAllocationFromHeader(string testKitPath)
        {
            var allocation = new Dictionary<string, double>();
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");

            if (!File.Exists(headerPath))
                return allocation;

            try
            {
                using var wb = new XLWorkbook(headerPath);
                var ws = wb.Worksheet(1);
                
                // Look for test case marks
                var rows = ws.RowsUsed().Skip(1); // Skip header
                foreach (var row in rows)
                {
                    var tcName = row.Cell(1).GetString();
                    if (double.TryParse(row.Cell(2).GetString(), out var mark))
                    {
                        allocation[tcName] = mark;
                    }
                }
            }
            catch
            {
                // Return empty allocation on error
            }

            return allocation;
        }

        /// <summary>
        /// Parses Detail.xlsx to get stage information.
        /// </summary>
        private List<StageInfo> ParseDetailExcel(string detailPath)
        {
            var stages = new List<StageInfo>();

            try
            {
                using var wb = new XLWorkbook(detailPath);
                
                // Look for User sheet (actions)
                if (wb.TryGetWorksheet("User", out var userSheet))
                {
                    foreach (var row in userSheet.RowsUsed().Skip(1))
                    {
                        var stageCell = row.Cell(1).GetString();
                        if (!int.TryParse(stageCell, out var stageNum)) continue;

                        stages.Add(new StageInfo
                        {
                            StageNumber = stageNum,
                            Input = row.Cell(2).GetString(),
                            Action = row.Cell(3).GetString()
                        });
                    }
                }

                // Read expected outputs from Client sheet
                if (wb.TryGetWorksheet("Client", out var clientSheet))
                {
                    foreach (var row in clientSheet.RowsUsed().Skip(1))
                    {
                        var stageCell = row.Cell(1).GetString();
                        if (!int.TryParse(stageCell, out var stageNum)) continue;

                        var existing = stages.FirstOrDefault(s => s.StageNumber == stageNum);
                        if (existing != null)
                        {
                            existing.ExpectedClientOutput = row.Cell(2).GetString();
                        }
                    }
                }

                // Read expected outputs from Server sheet
                if (wb.TryGetWorksheet("Server", out var serverSheet))
                {
                    foreach (var row in serverSheet.RowsUsed().Skip(1))
                    {
                        var stageCell = row.Cell(1).GetString();
                        if (!int.TryParse(stageCell, out var stageNum)) continue;

                        var existing = stages.FirstOrDefault(s => s.StageNumber == stageNum);
                        if (existing != null)
                        {
                            existing.ExpectedServerOutput = row.Cell(2).GetString();
                        }
                    }
                }

                // Read expected network from Network sheet
                if (wb.TryGetWorksheet("Network", out var networkSheet))
                {
                    foreach (var row in networkSheet.RowsUsed().Skip(1))
                    {
                        var stageCell = row.Cell(1).GetString();
                        if (!int.TryParse(stageCell, out var stageNum)) continue;

                        var existing = stages.FirstOrDefault(s => s.StageNumber == stageNum);
                        if (existing != null)
                        {
                            existing.ExpectedNetworkPackets.Add(new ExpectedNetworkPacket
                            {
                                StageNumber = stageNum,
                                Flags = row.Cell(6).GetString(),
                                State = row.Cell(7).GetString(),
                                SourceRole = row.Cell(9).GetString(),
                                DestinationRole = row.Cell(10).GetString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GradingService] Error parsing Detail.xlsx: {ex.Message}");
            }

            return stages;
        }

        /// <summary>
        /// Compares actual outputs with expected outputs for all stages.
        /// </summary>
        private async Task<List<StageResult>> CompareStageOutputsAsync(
            List<StageInfo> stages,
            DockerContainerService? dockerService,
            INetworkMonitorService? networkMonitor,
            string testKitPath,
            string testCaseName)
        {
            var results = new List<StageResult>();

            foreach (var stage in stages.Where(s => !string.IsNullOrEmpty(s.Action)))
            {
                var stageResult = new StageResult
                {
                    StageNumber = stage.StageNumber,
                    Action = stage.Action
                };

                // Get actual outputs
                stageResult.ActualClientOutput = dockerService?.GetClientOutputForStage(stage.StageNumber);
                stageResult.ActualServerOutput = dockerService?.GetServerOutputForStage(stage.StageNumber);
                stageResult.ExpectedClientOutput = stage.ExpectedClientOutput;
                stageResult.ExpectedServerOutput = stage.ExpectedServerOutput;

                // Compare client output
                stageResult.ClientOutputMatches = CompareOutput(
                    stage.ExpectedClientOutput,
                    stageResult.ActualClientOutput);

                // Compare server output
                stageResult.ServerOutputMatches = CompareOutput(
                    stage.ExpectedServerOutput,
                    stageResult.ActualServerOutput);

                // Compare network
                if (networkMonitor != null && stage.ExpectedNetworkPackets.Count > 0)
                {
                    var actualPackets = networkMonitor.GetPacketsForStage(stage.StageNumber);
                    stageResult.NetworkPackets = CompareNetworkPackets(
                        stage.ExpectedNetworkPackets,
                        actualPackets);
                    
                    stageResult.NetworkMatches = stageResult.NetworkPackets.All(p => p.Matches);
                }
                else
                {
                    stageResult.NetworkMatches = true; // No network to compare
                }

                results.Add(stageResult);
            }

            return results;
        }

        /// <summary>
        /// Compares expected vs actual output.
        /// </summary>
        private bool CompareOutput(string? expected, string? actual)
        {
            if (string.IsNullOrEmpty(expected))
                return true; // No expected output means pass

            if (string.IsNullOrEmpty(actual))
                return false;

            // Normalize whitespace and compare
            var normalizedExpected = NormalizeOutput(expected);
            var normalizedActual = NormalizeOutput(actual);

            return normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes output for comparison.
        /// </summary>
        private string NormalizeOutput(string output)
        {
            return output
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        /// <summary>
        /// Compares expected vs actual network packets.
        /// </summary>
        private List<NetworkPacketResult> CompareNetworkPackets(
            List<ExpectedNetworkPacket> expected,
            IReadOnlyList<NetworkPacketInfo> actual)
        {
            var results = new List<NetworkPacketResult>();

            foreach (var exp in expected)
            {
                var matching = actual.FirstOrDefault(a =>
                    a.Stage == exp.StageNumber &&
                    (string.IsNullOrEmpty(exp.Flags) || a.Flags.Contains(exp.Flags, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(exp.SourceRole) || a.SourceRole.Equals(exp.SourceRole, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(exp.DestinationRole) || a.DestinationRole.Equals(exp.DestinationRole, StringComparison.OrdinalIgnoreCase)));

                results.Add(new NetworkPacketResult
                {
                    Timestamp = matching?.Timestamp ?? DateTime.Now,
                    Source = matching?.Source ?? "",
                    Destination = matching?.Destination ?? "",
                    Flags = matching?.Flags ?? exp.Flags,
                    State = matching?.State ?? exp.State,
                    SourceRole = matching?.SourceRole ?? exp.SourceRole,
                    DestinationRole = matching?.DestinationRole ?? exp.DestinationRole,
                    Matches = matching != null
                });
            }

            return results;
        }

        /// <summary>
        /// Generates StudentsSolution.xlsx summary file.
        /// </summary>
        private async Task GenerateStudentsSolutionExcelAsync(
            List<StudentGradingResult> results,
            string saveFolder,
            string paperNo)
        {
            try
            {
                Directory.CreateDirectory(saveFolder);
                var filePath = Path.Combine(saveFolder, paperNo, "StudentsSolution.xlsx");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Sheet1");

                // Header
                ws.Cell(1, 1).Value = "No";
                ws.Cell(1, 2).Value = "StudentCode";
                ws.Cell(1, 3).Value = "ExamPaper";
                ws.Cell(1, 4).Value = "Status";
                ws.Cell(1, 5).Value = "FinalResult";
                ws.Cell(1, 6).Value = "StartDate";
                ws.Cell(1, 7).Value = "EndDate";
                ws.Row(1).Style.Font.Bold = true;

                int row = 2;
                foreach (var result in results)
                {
                    ws.Cell(row, 1).Value = row - 1;
                    ws.Cell(row, 2).Value = result.StudentCode;
                    ws.Cell(row, 3).Value = result.PaperNo;
                    ws.Cell(row, 4).Value = result.Success ? "Success" : "Failed";
                    ws.Cell(row, 5).Value = result.TotalPointsAwarded;
                    ws.Cell(row, 6).Value = result.StartTime.ToString("dd-MM-yyyy HH:mm:ss");
                    ws.Cell(row, 7).Value = result.EndTime.ToString("dd-MM-yyyy HH:mm:ss");
                    row++;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(filePath);

                Console.WriteLine($"[GradingService] Generated StudentsSolution.xlsx at {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GradingService] Error generating StudentsSolution.xlsx: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates per-student log files matching SampleLogging format.
        /// </summary>
        private async Task GenerateStudentLogsAsync(StudentGradingResult result, string saveFolder)
        {
            try
            {
                // Create student folder: saveFolder/paperNo/student/StudentCode/
                var studentFolder = Path.Combine(saveFolder, result.PaperNo, "student", result.StudentCode);
                Directory.CreateDirectory(studentFolder);

                // Generate OverallSummary.xlsx
                await GenerateOverallSummaryAsync(result, studentFolder);

                // Generate per-test case results
                foreach (var tcResult in result.TestCaseResults)
                {
                    var tcFolder = Path.Combine(studentFolder, tcResult.TestCaseName);
                    Directory.CreateDirectory(tcFolder);

                    // Generate GradeDetail.xlsx
                    await GenerateGradeDetailAsync(tcResult, tcFolder);

                    // Generate TC_Result.xlsx
                    await GenerateTCResultAsync(tcResult, tcFolder);
                }

                Console.WriteLine($"[GradingService] Generated logs for {result.StudentCode} at {studentFolder}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GradingService] Error generating student logs: {ex.Message}");
            }
        }

        private async Task GenerateOverallSummaryAsync(StudentGradingResult result, string folder)
        {
            var filePath = Path.Combine(folder, "OverallSummary.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Summary");

            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var tc in result.TestCaseResults)
            {
                ws.Cell(row, 1).Value = tc.TestCaseName;
                ws.Cell(row, 2).Value = tc.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 3).Value = tc.PointsAwarded;
                ws.Cell(row, 4).Value = tc.PointsPossible;
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        private async Task GenerateGradeDetailAsync(TestCaseResult result, string folder)
        {
            var filePath = Path.Combine(folder, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();

            // User sheet
            var userWs = wb.AddWorksheet("User");
            userWs.Cell(1, 1).Value = "Stage";
            userWs.Cell(1, 2).Value = "Input";
            userWs.Cell(1, 3).Value = "Action";
            userWs.Row(1).Style.Font.Bold = true;

            // Client sheet
            var clientWs = wb.AddWorksheet("Client");
            clientWs.Cell(1, 1).Value = "Stage";
            clientWs.Cell(1, 2).Value = "Console";
            clientWs.Cell(1, 3).Value = "Result";
            clientWs.Row(1).Style.Font.Bold = true;

            // Server sheet
            var serverWs = wb.AddWorksheet("Server");
            serverWs.Cell(1, 1).Value = "Stage";
            serverWs.Cell(1, 2).Value = "Console";
            serverWs.Cell(1, 3).Value = "Result";
            serverWs.Row(1).Style.Font.Bold = true;

            // Network sheet
            var networkWs = wb.AddWorksheet("Network");
            networkWs.Cell(1, 1).Value = "Stage";
            networkWs.Cell(1, 2).Value = "Source";
            networkWs.Cell(1, 3).Value = "Destination";
            networkWs.Cell(1, 4).Value = "Flags";
            networkWs.Cell(1, 5).Value = "State";
            networkWs.Cell(1, 6).Value = "Result";
            networkWs.Row(1).Style.Font.Bold = true;

            int clientRow = 2, serverRow = 2, networkRow = 2, userRow = 2;
            foreach (var stage in result.StageResults)
            {
                // User data
                userWs.Cell(userRow, 1).Value = stage.StageNumber;
                userWs.Cell(userRow, 3).Value = stage.Action;
                userRow++;

                // Client data
                if (!string.IsNullOrEmpty(stage.ActualClientOutput))
                {
                    clientWs.Cell(clientRow, 1).Value = stage.StageNumber;
                    clientWs.Cell(clientRow, 2).Value = stage.ActualClientOutput;
                    clientWs.Cell(clientRow, 3).Value = stage.ClientOutputMatches ? "PASS" : "FAIL";
                    clientRow++;
                }

                // Server data
                if (!string.IsNullOrEmpty(stage.ActualServerOutput))
                {
                    serverWs.Cell(serverRow, 1).Value = stage.StageNumber;
                    serverWs.Cell(serverRow, 2).Value = stage.ActualServerOutput;
                    serverWs.Cell(serverRow, 3).Value = stage.ServerOutputMatches ? "PASS" : "FAIL";
                    serverRow++;
                }

                // Network data
                foreach (var packet in stage.NetworkPackets)
                {
                    networkWs.Cell(networkRow, 1).Value = stage.StageNumber;
                    networkWs.Cell(networkRow, 2).Value = packet.Source;
                    networkWs.Cell(networkRow, 3).Value = packet.Destination;
                    networkWs.Cell(networkRow, 4).Value = packet.Flags;
                    networkWs.Cell(networkRow, 5).Value = packet.State;
                    networkWs.Cell(networkRow, 6).Value = packet.Matches ? "PASS" : "FAIL";
                    networkRow++;
                }
            }

            wb.Worksheets.ToList().ForEach(ws => ws.Columns().AdjustToContents());
            wb.SaveAs(filePath);
        }

        private async Task GenerateTCResultAsync(TestCaseResult result, string folder)
        {
            var filePath = Path.Combine(folder, $"{result.TestCaseName}_Result.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Result");

            ws.Cell(1, 1).Value = "StepId";
            ws.Cell(1, 2).Value = "Stage";
            ws.Cell(1, 3).Value = "Action";
            ws.Cell(1, 4).Value = "Passed";
            ws.Cell(1, 5).Value = "Message";
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var stage in result.StageResults)
            {
                ws.Cell(row, 1).Value = $"USER-{stage.Action.ToUpper()}-{stage.StageNumber}";
                ws.Cell(row, 2).Value = stage.StageNumber;
                ws.Cell(row, 3).Value = stage.Action;
                ws.Cell(row, 4).Value = stage.ClientOutputMatches && stage.ServerOutputMatches && stage.NetworkMatches;
                ws.Cell(row, 5).Value = stage.ErrorMessage ?? "OK";
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        #endregion
    }

    /// <summary>
    /// Stage information from Detail.xlsx.
    /// </summary>
    internal class StageInfo
    {
        public int StageNumber { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Input { get; set; }
        public string? ExpectedClientOutput { get; set; }
        public string? ExpectedServerOutput { get; set; }
        public List<ExpectedNetworkPacket> ExpectedNetworkPackets { get; set; } = new();
    }

    /// <summary>
    /// Expected network packet from Detail.xlsx Network sheet.
    /// </summary>
    internal class ExpectedNetworkPacket
    {
        public int StageNumber { get; set; }
        public string Flags { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string SourceRole { get; set; } = string.Empty;
        public string DestinationRole { get; set; } = string.Empty;
    }
}
