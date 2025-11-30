using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;
using Domain.Entities.Main;
using Environment = Domain.Entities.Main.Environment;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for executing test cases in Docker containers.
    /// Handles the execution of test actions from Detail.xlsx and compares outputs.
    /// 
    /// This service is separate from DockerGradingService which handles container lifecycle.
    /// DockerTestCaseExecutor focuses on:
    /// - Reading test case actions from Detail.xlsx
    /// - Executing actions in sequence (StartServer, StartClient, Input, etc.)
    /// - Capturing outputs from containers
    /// - Comparing outputs against expected values
    /// - Calculating points based on comparison results
    /// </summary>
    public class DockerTestCaseExecutor
    {
        private readonly ILoggingService _logger;
        private readonly TestKitConfigService _testKitConfigService;
        private readonly DockerGradingService _dockerGrading;

        public DockerTestCaseExecutor(
            ILoggingService logger, 
            TestKitConfigService testKitConfigService,
            DockerGradingService dockerGrading)
        {
            _logger = logger;
            _testKitConfigService = testKitConfigService;
            _dockerGrading = dockerGrading;
        }

        /// <summary>
        /// Executes all test cases for a student.
        /// </summary>
        /// <param name="environment">Docker environment configuration</param>
        /// <param name="testKitPath">Path to the test kit folder</param>
        /// <param name="testKitConfig">Test kit configuration with test case marks</param>
        /// <param name="resultRoot">Root folder for results</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Total earned marks and test case results</returns>
        public async Task<TestCaseExecutionResults> ExecuteAllTestCasesAsync(
            Environment environment,
            string testKitPath,
            TestKitConfigService.TestKitConfig testKitConfig,
            string resultRoot,
            CancellationToken ct)
        {
            var results = new TestCaseExecutionResults();
            
            // Get test case names from test kit
            var testCaseNames = _testKitConfigService.GetTestCaseNames(testKitPath);
            if (testCaseNames.Count == 0)
            {
                _logger.LogError("FATAL: No test cases found in test kit");
                return results;
            }
            
            _logger.LogInfo($"Found {testCaseNames.Count} test cases: {string.Join(", ", testCaseNames)}");

            // Execute each test case
            foreach (var testCaseName in testCaseNames)
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogInfo("-".PadRight(50, '-'));
                _logger.LogInfo($"Executing test case: {testCaseName}");
                _logger.LogInfo("-".PadRight(50, '-'));

                var testCasePath = Path.Combine(testKitPath, testCaseName);
                var testCaseMaxMark = testKitConfig.TestCaseMarks.TryGetValue(testCaseName, out var mark) ? mark : 0;

                // Execute test case in Docker containers
                var (tcPassed, tcMark) = await ExecuteSingleTestCaseAsync(
                    environment, testCasePath, testCaseName, 
                    testCaseMaxMark, testKitConfig.Protocol, ct);

                results.TotalEarnedMark += tcMark;
                if (tcPassed) results.PassedTestCases++;

                var resultMsg = tcPassed 
                    ? $"{testCaseName}: PASSED (+{tcMark:F2})" 
                    : $"{testCaseName}: FAILED ({tcMark:F2})";
                results.TestCaseResults.Add(resultMsg);
                _logger.LogInfo($">>> {resultMsg}");

                // Write test case result to file
                var tcResultDir = Path.Combine(resultRoot, testCaseName);
                if (!Directory.Exists(tcResultDir))
                {
                    Directory.CreateDirectory(tcResultDir);
                }
                await WriteTestCaseResultAsync(tcResultDir, testCaseName, tcPassed, tcMark, testCaseMaxMark, ct);
            }

            results.TotalTestCases = testCaseNames.Count;
            return results;
        }

        /// <summary>
        /// Executes a single test case in Docker containers.
        /// Reads actions from Detail.xlsx and executes them in sequence.
        /// Compares outputs against expected values from Client/Server/Network sheets.
        /// </summary>
        public async Task<(bool passed, double mark)> ExecuteSingleTestCaseAsync(
            Environment environment,
            string testCasePath,
            string testCaseName,
            double maxMark,
            string protocol,
            CancellationToken ct)
        {
            _logger.LogInfo($"Executing Docker test case: {testCaseName} (max: {maxMark} points)");

            try
            {
                // Read actions from Detail.xlsx User sheet
                var actions = _testKitConfigService.GetTestCaseActions(testCasePath);
                if (actions.Count == 0)
                {
                    _logger.LogWarning($"No actions found in Detail.xlsx for {testCaseName}");
                    return (false, 0);
                }
                _logger.LogInfo($"Loaded {actions.Count} actions from Detail.xlsx");

                // Read expected outputs from Client/Server sheets
                var expectedOutputs = _testKitConfigService.GetExpectedOutputs(testCasePath);
                _logger.LogInfo($"Loaded expected outputs for {expectedOutputs.Count} stages");

                // Execute actions and capture outputs
                var (clientOutputs, serverOutputs) = await ExecuteActionsAsync(environment, actions, ct);

                // Compare outputs and calculate points
                var (earnedPoints, passed) = CalculatePoints(expectedOutputs, clientOutputs, serverOutputs, maxMark, testCaseName);

                return (passed, earnedPoints);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Test case execution failed: {ex.Message}");
                return (false, 0);
            }
        }

        /// <summary>
        /// Executes all actions for a test case and captures outputs.
        /// </summary>
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsAsync(
            Environment environment,
            List<(int Stage, string Input, string Action)> actions,
            CancellationToken ct)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();
            bool serverStarted = false;
            bool clientStarted = false;

            // Execute each action in order
            foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogInfo($"[Stage {stage}] Executing: {action}" + (string.IsNullOrEmpty(input) ? "" : $" with input: '{input}'"));

                switch (action.ToUpperInvariant())
                {
                    case "STARTSERVER":
                        if (!serverStarted)
                        {
                            serverStarted = await _dockerGrading.StartServerAsync(environment, ct);
                            await Task.Delay(1000, ct); // Wait for server to start
                            serverOutputs[stage] = await _dockerGrading.GetServerOutputAsync(environment, ct);
                            _logger.LogDebug($"Server output at stage {stage}: {serverOutputs[stage]}");
                        }
                        break;

                    case "STARTCLIENT":
                        if (!clientStarted)
                        {
                            clientStarted = await _dockerGrading.StartClientAsync(environment, ct);
                            await Task.Delay(1000, ct); // Wait for client to start
                            clientOutputs[stage] = await _dockerGrading.GetClientOutputAsync(environment, ct);
                            _logger.LogDebug($"Client output at stage {stage}: {clientOutputs[stage]}");
                        }
                        break;

                    case "INPUT":
                        if (clientStarted)
                        {
                            var response = await _dockerGrading.SendInputToClientAsync(environment, input, ct);
                            await Task.Delay(500, ct); // Wait for processing
                            clientOutputs[stage] = await _dockerGrading.GetClientOutputAsync(environment, ct);
                            serverOutputs[stage] = await _dockerGrading.GetServerOutputAsync(environment, ct);
                            _logger.LogDebug($"After input '{input}' - Client: {clientOutputs[stage]}, Server: {serverOutputs[stage]}");
                        }
                        break;

                    case "CLOSECLIENT":
                        // Client cleanup handled at end
                        break;

                    case "CLOSESERVER":
                        // Server cleanup handled at end
                        break;
                }
            }

            return (clientOutputs, serverOutputs);
        }

        /// <summary>
        /// Compares actual outputs against expected outputs and calculates points.
        /// </summary>
        private (double earnedPoints, bool passed) CalculatePoints(
            Dictionary<int, TestKitConfigService.ExpectedOutput> expectedOutputs,
            Dictionary<int, string> clientOutputs,
            Dictionary<int, string> serverOutputs,
            double maxMark,
            string testCaseName)
        {
            double earnedPoints = 0;
            int totalComparisons = 0;
            int passedComparisons = 0;

            foreach (var (stage, expected) in expectedOutputs)
            {
                // Compare Client output
                if (!string.IsNullOrEmpty(expected.ClientConsole))
                {
                    totalComparisons++;
                    var actualClient = clientOutputs.TryGetValue(stage, out var co) ? co : "";
                    if (TextComparisonUtility.CompareOutput(expected.ClientConsole, actualClient))
                    {
                        passedComparisons++;
                        _logger.LogInfo($"[Stage {stage}] Client comparison: PASS");
                    }
                    else
                    {
                        _logger.LogInfo($"[Stage {stage}] Client comparison: FAIL");
                        _logger.LogDebug($"  Expected: '{expected.ClientConsole}'");
                        _logger.LogDebug($"  Actual: '{actualClient}'");
                    }
                }

                // Compare Server output
                if (!string.IsNullOrEmpty(expected.ServerConsole))
                {
                    totalComparisons++;
                    var actualServer = serverOutputs.TryGetValue(stage, out var so) ? so : "";
                    if (TextComparisonUtility.CompareOutput(expected.ServerConsole, actualServer))
                    {
                        passedComparisons++;
                        _logger.LogInfo($"[Stage {stage}] Server comparison: PASS");
                    }
                    else
                    {
                        _logger.LogInfo($"[Stage {stage}] Server comparison: FAIL");
                        _logger.LogDebug($"  Expected: '{expected.ServerConsole}'");
                        _logger.LogDebug($"  Actual: '{actualServer}'");
                    }
                }
            }

            // Calculate points
            if (totalComparisons > 0)
            {
                earnedPoints = (passedComparisons / (double)totalComparisons) * maxMark;
            }

            bool passed = passedComparisons == totalComparisons && totalComparisons > 0;
            _logger.LogInfo($"Test case {testCaseName}: {passedComparisons}/{totalComparisons} comparisons passed, earned {earnedPoints:F2}/{maxMark} points");

            return (earnedPoints, passed);
        }

        /// <summary>
        /// Writes test case result to Excel file.
        /// </summary>
        private async Task WriteTestCaseResultAsync(
            string tcResultDir, string testCaseName, bool passed, 
            double earnedMark, double maxMark, CancellationToken ct)
        {
            try
            {
                var resultPath = Path.Combine(tcResultDir, $"{testCaseName}_Result.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var ws = workbook.Worksheets.Add("Result");
                    ws.Cell(1, 1).Value = "TestCase";
                    ws.Cell(1, 2).Value = "Passed";
                    ws.Cell(1, 3).Value = "PointsAwarded";
                    ws.Cell(1, 4).Value = "PointsPossible";
                    ws.Row(1).Style.Font.Bold = true;

                    ws.Cell(2, 1).Value = testCaseName;
                    ws.Cell(2, 2).Value = passed ? "PASS" : "FAIL";
                    ws.Cell(2, 3).Value = earnedMark;
                    ws.Cell(2, 4).Value = maxMark;

                    ws.Columns().AdjustToContents();
                    workbook.SaveAs(resultPath);
                }
                _logger.LogDebug($"Test case result written to {resultPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write test case result: {ex.Message}");
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Results from executing all test cases.
    /// </summary>
    public class TestCaseExecutionResults
    {
        public double TotalEarnedMark { get; set; } = 0;
        public int PassedTestCases { get; set; } = 0;
        public int TotalTestCases { get; set; } = 0;
        public List<string> TestCaseResults { get; set; } = new List<string>();
    }
}
