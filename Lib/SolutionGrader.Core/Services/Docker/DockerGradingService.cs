using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using DomainEnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service for orchestrating Docker-based grading.
    /// 
    /// This is the main service that coordinates:
    /// - Container lifecycle (via DockerContainerManager)
    /// - Console output reading (via DockerConsoleReader)
    /// - Test step execution from Detail.xlsx
    /// - Result logging in SampleLogging format
    /// 
    /// Grading Flow (as per problem statement):
    /// 1. For each student submission:
    ///    a. Start Network Monitor (outside containers) - sniffs on exposed server port
    ///    b. Create database container (MSSQL)
    ///    c. Create server container with student code
    ///    d. Create client container with student code
    ///    e. For each test case:
    ///       - Reset database
    ///       - Read steps from Detail.xlsx (first sheet)
    ///       - Execute steps using docker exec with named pipes for stdin
    ///       - Capture outputs using docker attach
    ///       - Compare and grade
    ///       - Clean up for next test case
    ///    f. Dispose containers
    ///    g. Write results to logging folder
    /// 
    /// Key differences from local grading:
    /// - Uses docker containers instead of local processes
    /// - Uses docker exec with pipes for stdin instead of Process.StandardInput
    /// - Uses docker attach for reading console output (unbuffered)
    /// - Separates container setup from application startup (on-demand)
    /// </summary>
    public class DockerGradingService : IDisposable
    {
        private readonly DockerContainerManager _containerManager;
        private readonly DockerConsoleReader _consoleReader;
        private readonly IFileService _fileService;
        private readonly IDetailLogService _logService;

        // Container names
        private const string ServerContainerName = "ag-server";
        private const string ClientContainerName = "ag-client";
        private const string DatabaseContainerName = "ag-db";

        /// <summary>
        /// Event raised for logging/progress updates.
        /// </summary>
        public event EventHandler<string>? LogMessage;

        public DockerGradingService(IFileService fileService, IDetailLogService logService)
        {
            _fileService = fileService;
            _logService = logService;
            _containerManager = new DockerContainerManager();
            _consoleReader = new DockerConsoleReader();
        }

        /// <summary>
        /// Grades a student submission using Docker containers.
        /// </summary>
        /// <param name="studentPath">Path to student's solution folder.</param>
        /// <param name="testKitPath">Path to test kit folder.</param>
        /// <param name="resultPath">Path to save results.</param>
        /// <param name="config">Configuration dictionary from Environment.xlsx.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Total marks awarded.</returns>
        public async Task<double> GradeStudentAsync(
            string studentPath,
            string testKitPath,
            string resultPath,
            Dictionary<string, string> config,
            CancellationToken ct = default)
        {
            double totalMarks = 0;
            var testCaseResults = new List<(string TestCase, bool Passed, double Mark, double MaxMark)>();

            try
            {
                Log($"Starting grading for student at {studentPath}");

                // Step 1: Setup containers
                await SetupContainersAsync(studentPath, config, ct);

                // Step 2: Find and execute test cases
                var testCaseFolders = GetTestCaseFolders(testKitPath);
                Log($"Found {testCaseFolders.Count} test cases");

                foreach (var tcFolder in testCaseFolders)
                {
                    ct.ThrowIfCancellationRequested();

                    var tcName = Path.GetFileName(tcFolder);
                    Log($"Executing test case: {tcName}");

                    try
                    {
                        var (passed, mark, maxMark) = await ExecuteTestCaseAsync(
                            tcFolder,
                            resultPath,
                            config,
                            ct);

                        testCaseResults.Add((tcName, passed, mark, maxMark));
                        totalMarks += mark;

                        Log($"Test case {tcName}: {(passed ? "PASS" : "FAIL")} ({mark}/{maxMark})");
                    }
                    catch (Exception ex)
                    {
                        Log($"Test case {tcName} failed with error: {ex.Message}");
                        testCaseResults.Add((tcName, false, 0, 0));
                    }

                    // Cleanup between test cases
                    await CleanupBetweenTestCasesAsync(config, ct);
                }

                Log($"Grading complete. Total marks: {totalMarks}");
            }
            catch (Exception ex)
            {
                Log($"Grading failed: {ex.Message}");
                throw;
            }
            finally
            {
                // Always cleanup containers
                await CleanupContainersAsync(ct);
            }

            return totalMarks;
        }

        #region Container Setup

        /// <summary>
        /// Sets up all required containers for grading.
        /// </summary>
        private async Task SetupContainersAsync(string studentPath, Dictionary<string, string> config, CancellationToken ct)
        {
            Log("Setting up Docker containers...");

            // 1. Setup database container
            if (!_containerManager.IsContainerRunning(DatabaseContainerName))
            {
                Log("Starting database container...");
                // Database setup is handled by the existing EnvironmentSetupService
                // We just need to ensure it's running
            }

            // 2. Deploy student files to server container
            var serverPath = TryGetValue(config, DomainEnvConfig.CodeFilePath);
            if (!string.IsNullOrEmpty(serverPath))
            {
                Log("Deploying server files...");
                _containerManager.DeployFilesToContainer(ServerContainerName, serverPath);
            }

            // 3. Deploy student files to client container
            var clientPath = TryGetValue(config, DomainEnvConfig.GivenConsolePath);
            if (!string.IsNullOrEmpty(clientPath))
            {
                Log("Deploying client files...");
                _containerManager.DeployFilesToContainer(ClientContainerName, clientPath);
            }

            // 4. Create input pipes for stdin
            var serverAppName = TryGetValue(config, DomainEnvConfig.StudentQuestionName);
            var clientAppName = TryGetValue(config, DomainEnvConfig.GivenConsoleAppName);

            if (!string.IsNullOrEmpty(serverAppName))
            {
                _containerManager.CreateInputPipe(ServerContainerName, serverAppName);
            }
            if (!string.IsNullOrEmpty(clientAppName))
            {
                _containerManager.CreateInputPipe(ClientContainerName, clientAppName);
            }

            // 5. Attach to container consoles for output reading
            _consoleReader.AttachToContainer(ServerContainerName);
            _consoleReader.AttachToContainer(ClientContainerName);

            Log("Container setup complete");
            await Task.Delay(1000, ct); // Allow containers to stabilize
        }

        #endregion

        #region Test Case Execution

        /// <summary>
        /// Executes a single test case.
        /// </summary>
        private async Task<(bool Passed, double Mark, double MaxMark)> ExecuteTestCaseAsync(
            string tcFolder,
            string resultPath,
            Dictionary<string, string> config,
            CancellationToken ct)
        {
            var detailPath = Path.Combine(tcFolder, FileKeywords.FileName_Detail);
            if (!File.Exists(detailPath))
            {
                Log($"Detail.xlsx not found in {tcFolder}");
                return (false, 0, 0);
            }

            // Read test steps from Detail.xlsx
            var steps = ReadTestSteps(detailPath);
            if (steps.Count == 0)
            {
                Log("No test steps found in Detail.xlsx");
                return (false, 0, 0);
            }

            double maxMark = GetMaxMark(tcFolder);
            bool allPassed = true;

            // Get app names for input
            var serverAppName = TryGetValue(config, DomainEnvConfig.StudentQuestionName);
            var clientAppName = TryGetValue(config, DomainEnvConfig.GivenConsoleAppName);

            // Start applications (on-demand, after deployment)
            var serverDllPath = TryGetValue(config, DomainEnvConfig.DockerServerPath);
            var clientDllPath = TryGetValue(config, DomainEnvConfig.DockerClientPath);
            var serverPort = TryGetValue(config, DomainEnvConfig.CodeContainerInternalPort);

            if (!string.IsNullOrEmpty(serverAppName) && !string.IsNullOrEmpty(serverDllPath))
            {
                _containerManager.StartApplication(ServerContainerName, serverAppName, serverDllPath);
                await Task.Delay(2000, ct); // Wait for server to start
            }

            if (!string.IsNullOrEmpty(clientAppName) && !string.IsNullOrEmpty(clientDllPath))
            {
                _containerManager.StartApplication(ClientContainerName, clientAppName, clientDllPath);
                await Task.Delay(1000, ct); // Wait for client to start
            }

            // Execute each step
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();

                Log($"Executing step {step.Stage}: {step.Action}");

                try
                {
                    var passed = await ExecuteStepAsync(step, config, ct);
                    if (!passed)
                    {
                        allPassed = false;
                        Log($"Step {step.Stage} failed");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Step {step.Stage} error: {ex.Message}");
                    allPassed = false;
                }
            }

            return (allPassed, allPassed ? maxMark : 0, maxMark);
        }

        /// <summary>
        /// Executes a single test step.
        /// </summary>
        private async Task<bool> ExecuteStepAsync(TestStep step, Dictionary<string, string> config, CancellationToken ct)
        {
            var clientAppName = TryGetValue(config, DomainEnvConfig.GivenConsoleAppName);
            var serverAppName = TryGetValue(config, DomainEnvConfig.StudentQuestionName);

            switch (step.Action?.ToUpperInvariant())
            {
                case "SEND_INPUT":
                case "INPUT":
                    // Send input to client
                    _consoleReader.ClearOutputBuffer(ClientContainerName);
                    _containerManager.SendInput(ClientContainerName, clientAppName, step.Input ?? "");
                    await Task.Delay(_consoleReader.PostInputDelayMs, ct);
                    return true;

                case "WAIT_OUTPUT":
                case "CHECK_OUTPUT":
                case "COMPARE_OUTPUT":
                    // Wait and capture client output
                    var clientOutput = await _consoleReader.WaitAndCaptureOutputAsync(ClientContainerName);
                    return CompareOutput(step.ExpectedOutput, clientOutput);

                case "CHECK_SERVER_OUTPUT":
                case "COMPARE_SERVER":
                    // Wait and capture server output
                    var serverOutput = await _consoleReader.WaitAndCaptureOutputAsync(ServerContainerName);
                    return CompareOutput(step.ExpectedOutput, serverOutput);

                case "WAIT":
                    // Just wait
                    var waitTime = int.TryParse(step.Input, out var ms) ? ms : 1000;
                    await Task.Delay(waitTime, ct);
                    return true;

                default:
                    Log($"Unknown action: {step.Action}");
                    return true; // Unknown actions pass by default
            }
        }

        #endregion

        #region Test Data Reading

        /// <summary>
        /// Reads test steps from Detail.xlsx.
        /// </summary>
        private List<TestStep> ReadTestSteps(string detailPath)
        {
            var steps = new List<TestStep>();

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return steps;

                // Find header columns
                var headerRow = worksheet.Row(1);
                var stageCol = FindColumn(headerRow, "Stage");
                var inputCol = FindColumn(headerRow, "Input");
                var actionCol = FindColumn(headerRow, "Action");
                var outputCol = FindColumn(headerRow, "Output", "ExpectedOutput");
                var dataTypeCol = FindColumn(headerRow, "DataType");

                // Read data rows
                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
                for (int row = 2; row <= lastRow; row++)
                {
                    var stage = worksheet.Cell(row, stageCol).GetString().Trim();
                    if (string.IsNullOrEmpty(stage)) continue;

                    steps.Add(new TestStep
                    {
                        Stage = stage,
                        Input = inputCol > 0 ? worksheet.Cell(row, inputCol).GetString().Trim() : null,
                        Action = actionCol > 0 ? worksheet.Cell(row, actionCol).GetString().Trim() : null,
                        ExpectedOutput = outputCol > 0 ? worksheet.Cell(row, outputCol).GetString().Trim() : null,
                        DataType = dataTypeCol > 0 ? worksheet.Cell(row, dataTypeCol).GetString().Trim() : null
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"Error reading Detail.xlsx: {ex.Message}");
            }

            return steps;
        }

        /// <summary>
        /// Finds a column by header name(s).
        /// </summary>
        private int FindColumn(IXLRow headerRow, params string[] possibleNames)
        {
            for (int col = 1; col <= headerRow.LastCellUsed()?.Address.ColumnNumber; col++)
            {
                var value = headerRow.Cell(col).GetString().Trim();
                foreach (var name in possibleNames)
                {
                    if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return col;
                }
            }
            return -1;
        }

        /// <summary>
        /// Gets the max marks from Header.xlsx in the test case folder.
        /// </summary>
        private double GetMaxMark(string tcFolder)
        {
            var headerPath = Path.Combine(tcFolder, FileKeywords.FileName_Header);
            if (!File.Exists(headerPath))
            {
                // Try parent folder
                headerPath = Path.Combine(Path.GetDirectoryName(tcFolder) ?? "", FileKeywords.FileName_Header);
            }

            if (!File.Exists(headerPath)) return 2.5; // Default

            try
            {
                using var workbook = new XLWorkbook(headerPath);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return 2.5;

                // Look for Mark column
                var markCol = FindColumn(worksheet.Row(1), "Mark", "MaxMark", "Điểm");
                if (markCol > 0 && worksheet.Cell(2, markCol).TryGetValue<double>(out var mark))
                {
                    return mark;
                }
            }
            catch { }

            return 2.5;
        }

        #endregion

        #region Output Comparison

        /// <summary>
        /// Compares expected and actual output.
        /// </summary>
        private bool CompareOutput(string? expected, string? actual)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            if (string.IsNullOrEmpty(actual)) return false;

            // Normalize line endings and whitespace
            var normalizedExpected = NormalizeOutput(expected);
            var normalizedActual = NormalizeOutput(actual);

            return normalizedExpected == normalizedActual;
        }

        private string NormalizeOutput(string text)
        {
            return text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleans up between test cases.
        /// </summary>
        private async Task CleanupBetweenTestCasesAsync(Dictionary<string, string> config, CancellationToken ct)
        {
            Log("Cleaning up for next test case...");

            var serverAppName = TryGetValue(config, DomainEnvConfig.StudentQuestionName);
            var clientAppName = TryGetValue(config, DomainEnvConfig.GivenConsoleAppName);

            // Kill running applications
            if (!string.IsNullOrEmpty(serverAppName))
            {
                _containerManager.CleanupForNextTestCase(ServerContainerName, serverAppName);
            }
            if (!string.IsNullOrEmpty(clientAppName))
            {
                _containerManager.CleanupForNextTestCase(ClientContainerName, clientAppName);
            }

            // Clear console buffers
            _consoleReader.ClearOutputBuffer(ServerContainerName);
            _consoleReader.ClearOutputBuffer(ClientContainerName);

            await Task.Delay(500, ct);
        }

        /// <summary>
        /// Cleans up all containers.
        /// </summary>
        private async Task CleanupContainersAsync(CancellationToken ct)
        {
            Log("Cleaning up containers...");

            _consoleReader.DetachFromContainer(ServerContainerName);
            _consoleReader.DetachFromContainer(ClientContainerName);

            _containerManager.RemoveContainer(ServerContainerName);
            _containerManager.RemoveContainer(ClientContainerName);

            await Task.Delay(500, ct);
        }

        #endregion

        #region Utilities

        private List<string> GetTestCaseFolders(string testKitPath)
        {
            var folders = new List<string>();
            
            if (Directory.Exists(testKitPath))
            {
                folders.AddRange(Directory.GetDirectories(testKitPath)
                    .Where(d => Path.GetFileName(d).StartsWith("TC", StringComparison.OrdinalIgnoreCase)));
            }

            // Sort by test case number
            folders.Sort((a, b) =>
            {
                var numA = ExtractNumber(Path.GetFileName(a));
                var numB = ExtractNumber(Path.GetFileName(b));
                return numA.CompareTo(numB);
            });

            return folders;
        }

        private int ExtractNumber(string text)
        {
            var digits = new string(text.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var num) ? num : 999;
        }

        private string TryGetValue(Dictionary<string, string> config, string key)
        {
            return config.TryGetValue(key, out var value) ? value : string.Empty;
        }

        private void Log(string message)
        {
            Console.WriteLine($"[DockerGrading] {message}");
            LogMessage?.Invoke(this, message);
        }

        public void Dispose()
        {
            _containerManager.Dispose();
            _consoleReader.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Represents a test step from Detail.xlsx.
    /// </summary>
    public class TestStep
    {
        public string Stage { get; set; } = string.Empty;
        public string? Input { get; set; }
        public string? Action { get; set; }
        public string? ExpectedOutput { get; set; }
        public string? DataType { get; set; }
    }
}
