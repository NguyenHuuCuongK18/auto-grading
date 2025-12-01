using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;
using Domain.Entities.Main;
using Domain.Entities.Constants;
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

                // Execute test case in Docker containers and get detailed results
                var tcResult = await ExecuteSingleTestCaseWithDetailsAsync(
                    environment, testCasePath, testCaseName, 
                    testCaseMaxMark, testKitConfig.Protocol, ct);

                results.TotalEarnedMark += tcResult.EarnedMark;
                if (tcResult.Passed) results.PassedTestCases++;

                var resultMsg = tcResult.Passed 
                    ? $"{testCaseName}: PASSED (+{tcResult.EarnedMark:F2})" 
                    : $"{testCaseName}: FAILED ({tcResult.EarnedMark:F2})";
                results.TestCaseResults.Add(resultMsg);
                _logger.LogInfo($">>> {resultMsg}");

                // Write test case result to file with all details (for SampleLogging format)
                var tcResultDir = Path.Combine(resultRoot, testCaseName);
                if (!Directory.Exists(tcResultDir))
                {
                    Directory.CreateDirectory(tcResultDir);
                }
                await WriteTestCaseResultAsync(
                    tcResultDir, testCaseName, tcResult.Passed, tcResult.EarnedMark, testCaseMaxMark,
                    tcResult.ClientOutputs, tcResult.ServerOutputs, tcResult.ExpectedOutputs, 
                    tcResult.ExpectedNetworkFlows, tcResult.Actions, ct);
                
                // IMPORTANT: Stop all dotnet processes in containers between test cases
                // This kills the server/client processes to release ports before the next test case
                _logger.LogInfo("Stopping applications in containers before next test case...");
                await _dockerGrading.StopAllApplicationsAsync(environment, ct);
                
                // Wait for port to be released before next test case
                // This prevents "Address already in use" errors when the socket is in TIME_WAIT state
                await WaitForPortReleaseAsync(testKitConfig.CodeContainerHostPort, ct);
            }

            results.TotalTestCases = testCaseNames.Count;
            return results;
        }

        /// <summary>
        /// Waits for a TCP port to be released (no longer in use).
        /// This is crucial between test cases to prevent "Address already in use" errors.
        /// Uses SO_REUSEADDR socket option to properly handle TIME_WAIT state.
        /// </summary>
        /// <param name="port">The TCP port to wait for</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="maxWaitSeconds">Maximum time to wait in seconds (default: 10)</param>
        /// <returns>True if port is available, false if timeout exceeded</returns>
        private async Task<bool> WaitForPortReleaseAsync(int port, CancellationToken ct, int maxWaitSeconds = 10)
        {
            // Simple 100ms polling interval - no exponential backoff needed with SO_REUSEADDR
            const int pollIntervalMs = 100;
            var startTime = DateTime.UtcNow;
            
            _logger.LogInfo($"Waiting for port {port} to be released...");
            
            while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds && !ct.IsCancellationRequested)
            {
                if (IsPortAvailable(port))
                {
                    _logger.LogInfo($"Port {port} is now available");
                    return true;
                }
                
                await Task.Delay(pollIntervalMs, ct);
            }
            
            // Even if timeout, try one more time - SO_REUSEADDR handles TIME_WAIT gracefully
            if (IsPortAvailable(port))
            {
                _logger.LogInfo($"Port {port} is now available (with SO_REUSEADDR)");
                return true;
            }
            
            _logger.LogWarning($"Warning: Port {port} may still be in use after {maxWaitSeconds}s wait");
            return false;
        }

        /// <summary>
        /// Checks if a TCP port is available for binding.
        /// Uses SO_REUSEADDR to handle TIME_WAIT state - allows binding even if socket is closing.
        /// This is the proper way to handle port reuse without arbitrary delays.
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                
                // SO_REUSEADDR allows binding to a port in TIME_WAIT state
                // This is the standard solution for quick server restart
                socket.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ReuseAddress,
                    true);
                
                // Try to bind to the port
                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Port is actively in use (not just TIME_WAIT)
                return false;
            }
        }

        /// <summary>
        /// Detailed result from a single test case execution.
        /// Contains all information needed to write SampleLogging format files.
        /// </summary>
        private class SingleTestCaseResult
        {
            public bool Passed { get; set; }
            public double EarnedMark { get; set; }
            public Dictionary<int, string> ClientOutputs { get; set; } = new Dictionary<int, string>();
            public Dictionary<int, string> ServerOutputs { get; set; } = new Dictionary<int, string>();
            public Dictionary<int, TestKitConfigService.ExpectedOutput> ExpectedOutputs { get; set; } = new Dictionary<int, TestKitConfigService.ExpectedOutput>();
            public List<TestKitConfigService.ExpectedNetworkFlow> ExpectedNetworkFlows { get; set; } = new List<TestKitConfigService.ExpectedNetworkFlow>();
            public List<(int Stage, string Input, string Action)> Actions { get; set; } = new List<(int, string, string)>();
        }

        /// <summary>
        /// Executes a single test case and returns detailed results for SampleLogging format output.
        /// </summary>
        private async Task<SingleTestCaseResult> ExecuteSingleTestCaseWithDetailsAsync(
            Environment environment,
            string testCasePath,
            string testCaseName,
            double maxMark,
            string protocol,
            CancellationToken ct)
        {
            var result = new SingleTestCaseResult();
            
            _logger.LogInfo($"Executing Docker test case: {testCaseName} (max: {maxMark} points)");

            try
            {
                // Read actions from Detail.xlsx User sheet
                var actions = _testKitConfigService.GetTestCaseActions(testCasePath);
                result.Actions = actions;
                
                if (actions.Count == 0)
                {
                    _logger.LogWarning($"No actions found in Detail.xlsx for {testCaseName}");
                    return result;
                }
                _logger.LogInfo($"Loaded {actions.Count} actions from Detail.xlsx");

                // Read expected outputs from Client/Server sheets
                var expectedOutputs = _testKitConfigService.GetExpectedOutputs(testCasePath);
                result.ExpectedOutputs = expectedOutputs;
                _logger.LogInfo($"Loaded expected outputs for {expectedOutputs.Count} stages");
                
                // Read expected network flow from Network sheet
                var expectedNetworkFlows = _testKitConfigService.GetExpectedNetworkFlow(testCasePath);
                result.ExpectedNetworkFlows = expectedNetworkFlows;
                if (expectedNetworkFlows.Count > 0)
                {
                    _logger.LogInfo($"Loaded {expectedNetworkFlows.Count} expected network flow entries from Detail.xlsx");
                }

                // Execute actions and capture outputs
                var (clientOutputs, serverOutputs) = await ExecuteActionsAsync(environment, actions, ct);
                result.ClientOutputs = clientOutputs;
                result.ServerOutputs = serverOutputs;

                // Compare outputs and calculate points
                var (earnedPoints, passed) = CalculatePoints(expectedOutputs, clientOutputs, serverOutputs, maxMark, testCaseName);
                result.EarnedMark = earnedPoints;
                result.Passed = passed;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Test case execution failed: {ex.Message}");
                return result;
            }
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
        /// Actions are executed in stage order: StartServer, StartClient, Input, Close*
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
            
            // Track cumulative output to detect new output for each stage
            // docker logs returns ALL output from container start, so we need to track what's already been seen
            string previousClientOutput = "";
            string previousServerOutput = "";

            _logger.LogInfo($"Executing {actions.Count} actions in Docker containers...");

            // Execute each action in order
            foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
            {
                ct.ThrowIfCancellationRequested();
                var actionUpper = action.ToUpperInvariant();
                
                _logger.LogInfo($"[Stage {stage}] Action: {actionUpper}" + 
                    (string.IsNullOrEmpty(input) ? "" : $", Input: '{input}'"));

                switch (actionUpper)
                {
                    case "STARTSERVER":
                        if (!serverStarted)
                        {
                            _logger.LogInfo($"[Stage {stage}] Starting server application in container...");
                            serverStarted = await _dockerGrading.StartServerAsync(environment, ct);
                            if (serverStarted)
                            {
                                _logger.LogInfo($"[Stage {stage}] Server started successfully, waiting for initialization...");
                                
                                // Wait for server output using cumulative approach
                                // Keep checking until we see new output or timeout
                                string currentServerOutput = "";
                                int retries = 0;
                                const int maxRetries = 10; // 10 retries * 500ms = 5 seconds max
                                
                                while (retries < maxRetries && !ct.IsCancellationRequested)
                                {
                                    await Task.Delay(500, ct);
                                    currentServerOutput = await _dockerGrading.GetServerOutputAsync(environment, ct) ?? "";
                                    
                                    // If we have more output than before, we've captured something
                                    if (currentServerOutput.Length > previousServerOutput.Length)
                                    {
                                        _logger.LogDebug($"[Stage {stage}] Server output grew: {previousServerOutput.Length} -> {currentServerOutput.Length}");
                                        break;
                                    }
                                    retries++;
                                }
                                
                                // Store the NEW output for this stage (output that wasn't there before)
                                string newServerOutput = currentServerOutput.Length > previousServerOutput.Length
                                    ? currentServerOutput.Substring(previousServerOutput.Length)
                                    : currentServerOutput;
                                
                                serverOutputs[stage] = newServerOutput;
                                previousServerOutput = currentServerOutput;
                                
                                _logger.LogInfo($"[Stage {stage}] Server output captured ({newServerOutput.Length} chars new, {currentServerOutput.Length} chars total)");
                                if (!string.IsNullOrEmpty(newServerOutput))
                                {
                                    _logger.LogDebug($"Server output: {newServerOutput}");
                                }
                            }
                            else
                            {
                                _logger.LogError($"[Stage {stage}] Server failed to start!");
                            }
                        }
                        else
                        {
                            _logger.LogDebug($"[Stage {stage}] Server already started, skipping StartServer action");
                        }
                        break;

                    case "STARTCLIENT":
                        if (!clientStarted)
                        {
                            _logger.LogInfo($"[Stage {stage}] Starting client application in container...");
                            clientStarted = await _dockerGrading.StartClientAsync(environment, ct);
                            if (clientStarted)
                            {
                                _logger.LogInfo($"[Stage {stage}] Client started successfully, waiting for initialization...");
                                
                                // Wait for client output using cumulative approach
                                // Keep checking until we see new output or timeout
                                string currentClientOutput = "";
                                int retries = 0;
                                const int maxRetries = 10; // 10 retries * 500ms = 5 seconds max
                                
                                while (retries < maxRetries && !ct.IsCancellationRequested)
                                {
                                    await Task.Delay(500, ct);
                                    currentClientOutput = await _dockerGrading.GetClientOutputAsync(environment, ct) ?? "";
                                    
                                    // If we have more output than before, we've captured something
                                    if (currentClientOutput.Length > previousClientOutput.Length)
                                    {
                                        _logger.LogDebug($"[Stage {stage}] Client output grew: {previousClientOutput.Length} -> {currentClientOutput.Length}");
                                        break;
                                    }
                                    retries++;
                                }
                                
                                // Store the NEW output for this stage (output that wasn't there before)
                                string newClientOutput = currentClientOutput.Length > previousClientOutput.Length
                                    ? currentClientOutput.Substring(previousClientOutput.Length)
                                    : currentClientOutput;
                                
                                clientOutputs[stage] = newClientOutput;
                                previousClientOutput = currentClientOutput;
                                
                                _logger.LogInfo($"[Stage {stage}] Client output captured ({newClientOutput.Length} chars new, {currentClientOutput.Length} chars total)");
                                if (!string.IsNullOrEmpty(newClientOutput))
                                {
                                    _logger.LogDebug($"Client output: {newClientOutput}");
                                }
                            }
                            else
                            {
                                _logger.LogError($"[Stage {stage}] Client failed to start!");
                            }
                        }
                        else
                        {
                            _logger.LogDebug($"[Stage {stage}] Client already started, skipping StartClient action");
                        }
                        break;

                    case "INPUT":
                        if (string.IsNullOrEmpty(input))
                        {
                            _logger.LogDebug($"[Stage {stage}] Empty input, skipping");
                            break;
                        }
                        
                        if (!clientStarted)
                        {
                            _logger.LogWarning($"[Stage {stage}] Cannot send input - client not started yet!");
                            break;
                        }
                        
                        _logger.LogInfo($"[Stage {stage}] Sending input to client: '{input}'");
                        var response = await _dockerGrading.SendInputToClientAsync(environment, input, ct);
                        
                        // Wait for input to be processed and capture new outputs using cumulative approach
                        {
                            string currentClientOutput = "";
                            string currentServerOutput = "";
                            int retries = 0;
                            const int maxRetries = 10; // 10 retries * 300ms = 3 seconds max
                            
                            while (retries < maxRetries && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(300, ct);
                                currentClientOutput = await _dockerGrading.GetClientOutputAsync(environment, ct) ?? "";
                                currentServerOutput = await _dockerGrading.GetServerOutputAsync(environment, ct) ?? "";
                                
                                // If either output grew, we've captured something
                                if (currentClientOutput.Length > previousClientOutput.Length || 
                                    currentServerOutput.Length > previousServerOutput.Length)
                                {
                                    _logger.LogDebug($"[Stage {stage}] Output after input - Client: {previousClientOutput.Length} -> {currentClientOutput.Length}, Server: {previousServerOutput.Length} -> {currentServerOutput.Length}");
                                    break;
                                }
                                retries++;
                            }
                            
                            // Store NEW outputs for this stage
                            string newClientOutput = currentClientOutput.Length > previousClientOutput.Length
                                ? currentClientOutput.Substring(previousClientOutput.Length)
                                : "";
                            string newServerOutput = currentServerOutput.Length > previousServerOutput.Length
                                ? currentServerOutput.Substring(previousServerOutput.Length)
                                : "";
                            
                            clientOutputs[stage] = newClientOutput;
                            serverOutputs[stage] = newServerOutput;
                            previousClientOutput = currentClientOutput;
                            previousServerOutput = currentServerOutput;
                            
                            _logger.LogInfo($"[Stage {stage}] After input - Client: {newClientOutput.Length} chars new, Server: {newServerOutput.Length} chars new");
                            
                            if (!string.IsNullOrEmpty(newClientOutput))
                            {
                                _logger.LogDebug($"Client output after input: {newClientOutput}");
                            }
                            if (!string.IsNullOrEmpty(newServerOutput))
                            {
                                _logger.LogDebug($"Server output after input: {newServerOutput}");
                            }
                        }
                        break;

                    case "CLOSECLIENT":
                        _logger.LogInfo($"[Stage {stage}] CloseClient action - stopping client process...");
                        if (clientStarted)
                        {
                            await _dockerGrading.StopApplicationsInContainerAsync(
                                environment.Configs.GetValueOrDefault(EnvironmentConfiguration.GivenConsoleContainerName, "ag-client"), ct);
                            clientStarted = false; // Reset so it can be started again
                            // Reset cumulative client output since process was stopped
                            previousClientOutput = "";
                            _logger.LogInfo($"[Stage {stage}] Client stopped - can be restarted by a subsequent StartClient action");
                        }
                        else
                        {
                            _logger.LogDebug($"[Stage {stage}] Client was not running, skipping CloseClient");
                        }
                        break;

                    case "CLOSESERVER":
                        _logger.LogInfo($"[Stage {stage}] CloseServer action - stopping server process...");
                        if (serverStarted)
                        {
                            await _dockerGrading.StopApplicationsInContainerAsync(
                                environment.Configs.GetValueOrDefault(EnvironmentConfiguration.CodeContainerName, "ag-server"), ct);
                            serverStarted = false; // Reset so it can be started again
                            // Reset cumulative server output since process was stopped
                            previousServerOutput = "";
                            _logger.LogInfo($"[Stage {stage}] Server stopped - can be restarted by a subsequent StartServer action");
                        }
                        else
                        {
                            _logger.LogDebug($"[Stage {stage}] Server was not running, skipping CloseServer");
                        }
                        break;

                    default:
                        _logger.LogWarning($"[Stage {stage}] Unknown action: {action} - skipping");
                        break;
                }

                // Brief delay between actions
                await Task.Delay(200, ct);
            }

            _logger.LogInfo($"Action execution complete. Captured {clientOutputs.Count} client outputs, {serverOutputs.Count} server outputs");
            return (clientOutputs, serverOutputs);
        }

        /// <summary>
        /// Compares actual outputs against expected outputs and calculates points.
        /// Uses ALL-OR-NOTHING policy: If all comparisons pass, award full marks; otherwise 0.
        /// This matches the behavior of the Lib folder's ExcelDetailLogService.ComputeCaseTotals.
        /// </summary>
        private (double earnedPoints, bool passed) CalculatePoints(
            Dictionary<int, TestKitConfigService.ExpectedOutput> expectedOutputs,
            Dictionary<int, string> clientOutputs,
            Dictionary<int, string> serverOutputs,
            double maxMark,
            string testCaseName)
        {
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

            // ALL-OR-NOTHING policy: If all comparisons pass, award full marks; otherwise 0
            // This matches the Lib folder's SolutionGrader.Core.Services.ExcelDetailLogService.ComputeCaseTotals behavior
            // See: Lib/SolutionGrader.Core/Services/ExcelDetailLogService.cs, method ComputeCaseTotals()
            bool passed = passedComparisons == totalComparisons && totalComparisons > 0;
            double earnedPoints = passed ? maxMark : 0;
            
            _logger.LogInfo($"Test case {testCaseName}: {passedComparisons}/{totalComparisons} comparisons passed");
            _logger.LogInfo($"  ALL-OR-NOTHING policy: earned {earnedPoints:F2}/{maxMark} points ({(passed ? "PASS" : "FAIL")})");

            return (earnedPoints, passed);
        }

        /// <summary>
        /// Writes test case result files in SampleLogging format.
        /// Creates TC_Result.xlsx and GradeDetail.xlsx with proper sheets:
        /// - User sheet: Actions executed
        /// - Client sheet: Client console comparisons
        /// - Server sheet: Server console comparisons
        /// - Database sheet: Database comparisons (placeholder)
        /// - Network sheet: Network traffic comparisons with expected flow from Detail.xlsx
        /// 
        /// The Network sheet is populated with expected network flow from the test kit,
        /// which defines the TCP handshake and data patterns that should occur.
        /// </summary>
        private async Task WriteTestCaseResultAsync(
            string tcResultDir, string testCaseName, bool passed, 
            double earnedMark, double maxMark, 
            Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs,
            Dictionary<int, TestKitConfigService.ExpectedOutput> expectedOutputs,
            List<TestKitConfigService.ExpectedNetworkFlow> expectedNetworkFlows,
            List<(int Stage, string Input, string Action)> actions,
            CancellationToken ct)
        {
            try
            {
                // Write TC_Result.xlsx (matches SampleLogging/1/student/X/TC1/TC1_Result.xlsx format)
                var resultPath = Path.Combine(tcResultDir, $"{testCaseName}_Result.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var ws = workbook.Worksheets.Add("Result");
                    // Header matching SampleLogging format
                    ws.Cell(1, 1).Value = "StepId";
                    ws.Cell(1, 2).Value = "Stage";
                    ws.Cell(1, 3).Value = "Action";
                    ws.Cell(1, 4).Value = "Passed";
                    ws.Cell(1, 5).Value = "Message";
                    ws.Cell(1, 6).Value = "DurationMs";
                    ws.Row(1).Style.Font.Bold = true;

                    int row = 2;
                    // Write action results
                    foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
                    {
                        ws.Cell(row, 1).Value = $"USER-{action.ToUpper()}-{stage}";
                        ws.Cell(row, 2).Value = stage;
                        ws.Cell(row, 3).Value = action.ToUpper();
                        ws.Cell(row, 4).Value = true; // Actions executed
                        ws.Cell(row, 5).Value = $"{action} action executed";
                        ws.Cell(row, 6).Value = 0;
                        row++;
                    }

                    // Write comparison results
                    foreach (var (stage, expected) in expectedOutputs)
                    {
                        if (!string.IsNullOrEmpty(expected.ClientConsole))
                        {
                            var actualClient = clientOutputs.TryGetValue(stage, out var co) ? co : "";
                            var clientPassed = TextComparisonUtility.CompareOutput(expected.ClientConsole, actualClient);
                            ws.Cell(row, 1).Value = $"CLIENT-CONSOLE-{stage}";
                            ws.Cell(row, 2).Value = stage;
                            ws.Cell(row, 3).Value = "COMPARE_TEXT";
                            ws.Cell(row, 4).Value = clientPassed;
                            ws.Cell(row, 5).Value = clientPassed ? 
                                "Text comparison passed: client output matches exactly" : 
                                "Text comparison failed: client output does not match";
                            ws.Cell(row, 6).Value = 0;
                            row++;
                        }
                        if (!string.IsNullOrEmpty(expected.ServerConsole))
                        {
                            var actualServer = serverOutputs.TryGetValue(stage, out var so) ? so : "";
                            var serverPassed = TextComparisonUtility.CompareOutput(expected.ServerConsole, actualServer);
                            ws.Cell(row, 1).Value = $"SERVER-CONSOLE-{stage}";
                            ws.Cell(row, 2).Value = stage;
                            ws.Cell(row, 3).Value = "COMPARE_TEXT";
                            ws.Cell(row, 4).Value = serverPassed;
                            ws.Cell(row, 5).Value = serverPassed ? 
                                "Text comparison passed: server output matches exactly" : 
                                "Text comparison failed: server output does not match";
                            ws.Cell(row, 6).Value = 0;
                            row++;
                        }
                    }

                    ws.Columns().AdjustToContents();
                    workbook.SaveAs(resultPath);
                }
                _logger.LogDebug($"TC_Result.xlsx written to {resultPath}");

                // Write GradeDetail.xlsx (matches SampleLogging format with multiple sheets)
                var detailPath = Path.Combine(tcResultDir, "GradeDetail.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    // Calculate points per comparison
                    int totalComparisons = expectedOutputs.Sum(e => 
                        (!string.IsNullOrEmpty(e.Value.ClientConsole) ? 1 : 0) + 
                        (!string.IsNullOrEmpty(e.Value.ServerConsole) ? 1 : 0));
                    double pointsPerComparison = totalComparisons > 0 ? maxMark / totalComparisons : 0;

                    // User sheet - actions executed
                    var userWs = workbook.Worksheets.Add("User");
                    userWs.Cell(1, 1).Value = "Stage";
                    userWs.Cell(1, 2).Value = "Input";
                    userWs.Cell(1, 3).Value = "Action";
                    userWs.Cell(1, 4).Value = "DataType";
                    userWs.Cell(1, 5).Value = "Result";
                    userWs.Cell(1, 6).Value = "ErrorCode";
                    userWs.Cell(1, 7).Value = "ErrorCategory";
                    userWs.Cell(1, 8).Value = "PointsAwarded";
                    userWs.Cell(1, 9).Value = "PointsPossible";
                    userWs.Cell(1, 10).Value = "DurationMs";
                    userWs.Cell(1, 11).Value = "DetailPath";
                    userWs.Cell(1, 12).Value = "Message";
                    userWs.Row(1).Style.Font.Bold = true;

                    int userRow = 2;
                    foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
                    {
                        userWs.Cell(userRow, 1).Value = stage;
                        userWs.Cell(userRow, 2).Value = input;
                        userWs.Cell(userRow, 3).Value = action;
                        userWs.Cell(userRow, 5).Value = "PASS";
                        userWs.Cell(userRow, 6).Value = "NONE";
                        userWs.Cell(userRow, 7).Value = "None";
                        userWs.Cell(userRow, 12).Value = $"{action} action executed";
                        userRow++;
                    }
                    userWs.Columns().AdjustToContents();

                    // Client sheet - client console comparisons
                    var clientWs = workbook.Worksheets.Add("Client");
                    clientWs.Cell(1, 1).Value = "Stage";
                    clientWs.Cell(1, 2).Value = "Console";
                    clientWs.Cell(1, 3).Value = "Input";
                    clientWs.Cell(1, 4).Value = "DataType";
                    clientWs.Cell(1, 5).Value = "Action";
                    clientWs.Cell(1, 6).Value = "Result";
                    clientWs.Cell(1, 7).Value = "ErrorCode";
                    clientWs.Cell(1, 8).Value = "ErrorCategory";
                    clientWs.Cell(1, 9).Value = "PointsAwarded";
                    clientWs.Cell(1, 10).Value = "PointsPossible";
                    clientWs.Cell(1, 11).Value = "DurationMs";
                    clientWs.Cell(1, 12).Value = "DetailPath";
                    clientWs.Cell(1, 13).Value = "Message";
                    clientWs.Cell(1, 14).Value = "DiffIndex";
                    clientWs.Cell(1, 15).Value = "ExpectedOutput";
                    clientWs.Cell(1, 16).Value = "ActualOutput";
                    clientWs.Cell(1, 17).Value = "ExpectedExcerpt";
                    clientWs.Cell(1, 18).Value = "ActualExcerpt";
                    clientWs.Cell(1, 19).Value = "ClientStdout";
                    clientWs.Row(1).Style.Font.Bold = true;

                    int clientRow = 2;
                    foreach (var (stage, expected) in expectedOutputs.OrderBy(e => e.Key))
                    {
                        if (!string.IsNullOrEmpty(expected.ClientConsole))
                        {
                            var actualClient = clientOutputs.TryGetValue(stage, out var co) ? co : "";
                            var clientPassed = TextComparisonUtility.CompareOutput(expected.ClientConsole, actualClient);
                            clientWs.Cell(clientRow, 1).Value = stage;
                            clientWs.Cell(clientRow, 2).Value = expected.ClientConsole;
                            clientWs.Cell(clientRow, 6).Value = clientPassed ? "PASS" : "FAIL";
                            clientWs.Cell(clientRow, 7).Value = clientPassed ? "NONE" : "TEXT_MISMATCH";
                            clientWs.Cell(clientRow, 8).Value = clientPassed ? "None" : "Comparison";
                            clientWs.Cell(clientRow, 9).Value = clientPassed ? pointsPerComparison : 0;
                            clientWs.Cell(clientRow, 10).Value = pointsPerComparison;
                            clientWs.Cell(clientRow, 13).Value = clientPassed ? 
                                "Text comparison passed: client output matches exactly" : 
                                "Text comparison failed: client output does not match";
                            clientWs.Cell(clientRow, 19).Value = actualClient;
                            clientRow++;
                        }
                    }
                    clientWs.Columns().AdjustToContents();

                    // Server sheet - server console comparisons
                    var serverWs = workbook.Worksheets.Add("Server");
                    serverWs.Cell(1, 1).Value = "Stage";
                    serverWs.Cell(1, 2).Value = "Console";
                    serverWs.Cell(1, 3).Value = "Input";
                    serverWs.Cell(1, 4).Value = "DataType";
                    serverWs.Cell(1, 5).Value = "Action";
                    serverWs.Cell(1, 6).Value = "Result";
                    serverWs.Cell(1, 7).Value = "ErrorCode";
                    serverWs.Cell(1, 8).Value = "ErrorCategory";
                    serverWs.Cell(1, 9).Value = "PointsAwarded";
                    serverWs.Cell(1, 10).Value = "PointsPossible";
                    serverWs.Cell(1, 11).Value = "DurationMs";
                    serverWs.Cell(1, 12).Value = "DetailPath";
                    serverWs.Cell(1, 13).Value = "Message";
                    serverWs.Cell(1, 14).Value = "DiffIndex";
                    serverWs.Cell(1, 15).Value = "ExpectedOutput";
                    serverWs.Cell(1, 16).Value = "ActualOutput";
                    serverWs.Cell(1, 17).Value = "ExpectedExcerpt";
                    serverWs.Cell(1, 18).Value = "ActualExcerpt";
                    serverWs.Cell(1, 19).Value = "ServerStdout";
                    serverWs.Row(1).Style.Font.Bold = true;

                    int serverRow = 2;
                    foreach (var (stage, expected) in expectedOutputs.OrderBy(e => e.Key))
                    {
                        if (!string.IsNullOrEmpty(expected.ServerConsole))
                        {
                            var actualServer = serverOutputs.TryGetValue(stage, out var so) ? so : "";
                            var serverPassed = TextComparisonUtility.CompareOutput(expected.ServerConsole, actualServer);
                            serverWs.Cell(serverRow, 1).Value = stage;
                            serverWs.Cell(serverRow, 2).Value = expected.ServerConsole;
                            serverWs.Cell(serverRow, 6).Value = serverPassed ? "PASS" : "FAIL";
                            serverWs.Cell(serverRow, 7).Value = serverPassed ? "NONE" : "TEXT_MISMATCH";
                            serverWs.Cell(serverRow, 8).Value = serverPassed ? "None" : "Comparison";
                            serverWs.Cell(serverRow, 9).Value = serverPassed ? pointsPerComparison : 0;
                            serverWs.Cell(serverRow, 10).Value = pointsPerComparison;
                            serverWs.Cell(serverRow, 13).Value = serverPassed ? 
                                "Text comparison passed: server output matches exactly" : 
                                "Text comparison failed: server output does not match";
                            serverWs.Cell(serverRow, 19).Value = actualServer;
                            serverRow++;
                        }
                    }
                    serverWs.Columns().AdjustToContents();

                    // Database sheet (placeholder)
                    var dbWs = workbook.Worksheets.Add("Database");
                    dbWs.Cell(1, 1).Value = "Stage";
                    dbWs.Row(1).Style.Font.Bold = true;

                    // Network sheet - populated with expected network flow from Detail.xlsx
                    // This is the whole point of network monitoring - to show the expected TCP handshake
                    // and data flow patterns that should occur during the test case.
                    var netWs = workbook.Worksheets.Add("Network");
                    netWs.Cell(1, 1).Value = "Stage";
                    netWs.Cell(1, 2).Value = "Time";
                    netWs.Cell(1, 3).Value = "Info";
                    netWs.Cell(1, 4).Value = "Source";
                    netWs.Cell(1, 5).Value = "Destination";
                    netWs.Cell(1, 6).Value = "Flags";
                    netWs.Cell(1, 7).Value = "State";
                    netWs.Cell(1, 8).Value = "Data";
                    netWs.Cell(1, 9).Value = "SourceRole";
                    netWs.Cell(1, 10).Value = "DestinationRole";
                    netWs.Cell(1, 11).Value = "ActualFlags";
                    netWs.Cell(1, 12).Value = "ActualState";
                    netWs.Cell(1, 13).Value = "ActualSourceRole";
                    netWs.Cell(1, 14).Value = "ActualDestRole";
                    netWs.Cell(1, 15).Value = "ActualData";
                    netWs.Cell(1, 16).Value = "NetworkResult";
                    netWs.Row(1).Style.Font.Bold = true;
                    
                    // Populate Network sheet with expected network flow from Detail.xlsx
                    // The expected flows define the TCP handshake and data patterns to verify
                    int netRow = 2;
                    if (expectedNetworkFlows != null && expectedNetworkFlows.Count > 0)
                    {
                        foreach (var flow in expectedNetworkFlows)
                        {
                            netWs.Cell(netRow, 1).Value = flow.Stage;
                            netWs.Cell(netRow, 2).Value = flow.Time ?? "";
                            netWs.Cell(netRow, 3).Value = flow.Info ?? "TCP";
                            netWs.Cell(netRow, 4).Value = flow.Source ?? "";
                            netWs.Cell(netRow, 5).Value = flow.Destination ?? "";
                            netWs.Cell(netRow, 6).Value = flow.Flags ?? "";
                            netWs.Cell(netRow, 7).Value = flow.State ?? "";
                            netWs.Cell(netRow, 8).Value = flow.Data ?? "";
                            netWs.Cell(netRow, 9).Value = flow.SourceRole ?? "";
                            netWs.Cell(netRow, 10).Value = flow.DestinationRole ?? "";
                            // Actual columns left empty - would be populated by network monitor capture
                            // ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData
                            // NetworkResult would be set after comparison
                            netWs.Cell(netRow, 16).Value = "NOT_CAPTURED"; // Default to not captured
                            netRow++;
                        }
                        _logger.LogInfo($"Populated Network sheet with {expectedNetworkFlows.Count} expected flow entries");
                    }
                    else
                    {
                        // No expected network flow defined - add informational row
                        netWs.Cell(2, 1).Value = "-";
                        netWs.Cell(2, 16).Value = "No expected network flow defined in Detail.xlsx";
                    }
                    netWs.Columns().AdjustToContents();

                    workbook.SaveAs(detailPath);
                }
                _logger.LogDebug($"GradeDetail.xlsx written to {detailPath}");
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
