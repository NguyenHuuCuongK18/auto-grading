using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using EnvironmentBuilder.DockerCommand;
using Newtonsoft.Json;
using SolutionGrader.Services;
using Environment = Domain.Entities.Main.Environment;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Main service that orchestrates the grading process for student solutions.
    /// Handles container management, grading execution, and result collection.
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;
        private readonly StudentDiscoveryService _studentDiscovery;
        private readonly TestKitDiscoveryService _testKitDiscovery;
        private readonly DockerCommandExecutor _dockerExecutor;
        
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly object _lockObject = new object();
        
        // Events for UI updates
        public event EventHandler<StudentSolution>? StudentGradingStarted;
        public event EventHandler<StudentSolution>? StudentGradingCompleted;
        public event EventHandler<StudentSolution>? StudentProgressUpdated;
        public event EventHandler<GradingSessionState>? SessionStateChanged;

        public GradingOrchestrationService(ILoggingService logger)
        {
            _logger = logger;
            _studentDiscovery = new StudentDiscoveryService(logger);
            _testKitDiscovery = new TestKitDiscoveryService(logger);
            _dockerExecutor = new DockerCommandExecutor();
        }

        /// <summary>
        /// Discovers all students from the submit folder.
        /// </summary>
        public List<StudentSolution> DiscoverStudents(GradingConfiguration config)
        {
            return _studentDiscovery.DiscoverStudents(config.SubmitFolderPath, config);
        }

        /// <summary>
        /// Starts the grading process for the specified students.
        /// </summary>
        /// <param name="students">Students to grade</param>
        /// <param name="config">Grading configuration</param>
        /// <param name="sessionState">Session state to update</param>
        /// <returns>Task representing the grading operation</returns>
        public async Task StartGradingAsync(
            List<StudentSolution> students, 
            GradingConfiguration config,
            GradingSessionState sessionState)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;

            sessionState.IsRunning = true;
            sessionState.IsPaused = false;
            sessionState.SessionStartTime = DateTime.Now;
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);

            _logger.LogInfo($"Starting grading for {students.Count} students");
            SessionStateChanged?.Invoke(this, sessionState);

            try
            {
                // Grade students one at a time (as per requirement)
                foreach (var student in students.Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused))
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInfo("Grading cancelled");
                        break;
                    }

                    // Check if paused
                    while (sessionState.IsPaused && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                    }

                    if (ct.IsCancellationRequested) break;

                    sessionState.CurrentStudentCode = student.StudentCode;
                    SessionStateChanged?.Invoke(this, sessionState);

                    await GradeStudentAsync(student, config, ct);

                    // Update session state
                    sessionState.GradedStudents++;
                    sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);
                    sessionState.SuccessCount = students.Count(s => s.Status == GradingStatus.Success);
                    sessionState.FailedCount = students.Count(s => s.Status == GradingStatus.Failed);
                    SessionStateChanged?.Invoke(this, sessionState);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grading operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during grading", ex);
            }
            finally
            {
                sessionState.IsRunning = false;
                sessionState.SessionEndTime = DateTime.Now;
                sessionState.CurrentStudentCode = null;
                SessionStateChanged?.Invoke(this, sessionState);
                _logger.LogInfo("Grading session completed");
            }
        }

        /// <summary>
        /// Grades a single student's solution.
        /// </summary>
        private async Task GradeStudentAsync(
            StudentSolution student, 
            GradingConfiguration config,
            CancellationToken ct)
        {
            _logger.SetStudentContext(student.StudentCode);
            student.StartTime = DateTime.Now;
            student.Status = GradingStatus.InProgress;
            student.ProgressPercent = 0;
            StudentGradingStarted?.Invoke(this, student);

            try
            {
                _logger.LogInfo($"Starting grading for student: {student.StudentCode} (Paper {student.PaperNo})");

                // Step 1: Check if test kit exists for this paper
                student.ProgressPercent = 10;
                StudentProgressUpdated?.Invoke(this, student);

                var testKitPath = _testKitDiscovery.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    student.Status = GradingStatus.Not_Run;
                    student.StatusMessage = $"No test kit for paper {student.PaperNo}";
                    _logger.LogWarning(student.StatusMessage);
                    return;
                }

                _logger.LogInfo($"Using test kit: {testKitPath}");

                // Step 2: Load environment configuration
                student.ProgressPercent = 20;
                StudentProgressUpdated?.Invoke(this, student);

                var envPath = _testKitDiscovery.GetEnvironmentPath(testKitPath);
                if (string.IsNullOrEmpty(envPath))
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = "Environment.xlsx not found in test kit";
                    _logger.LogError(student.StatusMessage);
                    return;
                }

                var environment = LoadEnvironment(envPath);
                
                // Step 3: Configure environment for this student
                student.ProgressPercent = 30;
                StudentProgressUpdated?.Invoke(this, student);

                ConfigureEnvironmentForStudent(environment, student, config, testKitPath);

                // Step 4: Setup Docker containers
                student.ProgressPercent = 40;
                StudentProgressUpdated?.Invoke(this, student);

                await SetupContainersAsync(environment, ct);

                // Step 5: Copy student solution files to containers
                student.ProgressPercent = 50;
                StudentProgressUpdated?.Invoke(this, student);

                await CopyFilesToContainersAsync(student, environment, ct);

                // Step 6: Execute grading
                student.ProgressPercent = 70;
                StudentProgressUpdated?.Invoke(this, student);

                var result = await ExecuteGradingAsync(student, environment, testKitPath, config, ct);

                // Step 7: Collect results and cleanup
                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                await WriteResultsAsync(student, result, ct);
                await CleanupContainersAsync(environment, ct);

                // Mark as complete
                student.Status = result.success ? GradingStatus.Success : GradingStatus.Failed;
                student.Mark = result.mark;
                student.StatusMessage = result.message;
                student.ProgressPercent = 100;

                _logger.LogInfo($"Grading completed for {student.StudentCode}. Mark: {student.Mark}, Status: {student.Status}");
            }
            catch (OperationCanceledException)
            {
                student.Status = GradingStatus.Paused;
                student.StatusMessage = "Grading was paused/cancelled";
                _logger.LogWarning($"Grading paused for {student.StudentCode}");
            }
            catch (Exception ex)
            {
                student.Status = GradingStatus.Failed;
                student.StatusMessage = $"Error: {ex.Message}";
                _logger.LogError($"Grading failed for {student.StudentCode}", ex);
            }
            finally
            {
                student.EndTime = DateTime.Now;
                StudentGradingCompleted?.Invoke(this, student);
                _logger.SetStudentContext(null);
            }
        }

        /// <summary>
        /// Loads environment configuration from Excel file.
        /// </summary>
        private Environment LoadEnvironment(string envPath)
        {
            // Use the existing EnvironmentService from SolutionGrader.Services
            try
            {
                return EnvironmentService.GetEnvironment(envPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load environment from {envPath}", ex);
                throw;
            }
        }

        /// <summary>
        /// Configures the environment settings for a specific student.
        /// </summary>
        private void ConfigureEnvironmentForStudent(
            Environment environment, 
            StudentSolution student,
            GradingConfiguration config,
            string testKitPath)
        {
            var configs = environment.Configs;

            // Set container names for this student
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerName, $"ag-server-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.GivenConsoleContainerName, $"ag-client-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.StudentQuestionName, $"ag-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.DatabaseName, $"DB_{student.StudentCode}");

            // Set port configurations
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerInternalPort, config.CodeContainerInternalPort.ToString());
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerHostPort, config.CodeContainerHostPort.ToString());

            // Set file paths
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath)!;
                SetOrAddConfig(configs, EnvironmentConfiguration.CodeFilePath, serverDir);
                SetOrAddConfig(configs, EnvironmentConfiguration.DockerServerPath, GetDockerDllPath(serverDir, student.ServerDllPath));
            }

            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath)!;
                SetOrAddConfig(configs, EnvironmentConfiguration.GivenConsolePath, clientDir);
                SetOrAddConfig(configs, EnvironmentConfiguration.DockerClientPath, GetDockerDllPath(clientDir, student.ClientDllPath));
            }

            // Set runtime folder from test kit
            var runtimesFolder = configs.GetValueOrDefault(EnvironmentConfiguration.RuntimesFolder);
            if (!string.IsNullOrEmpty(runtimesFolder) && !Path.IsPathRooted(runtimesFolder))
            {
                SetOrAddConfig(configs, EnvironmentConfiguration.RuntimesFolder, Path.Combine(testKitPath, runtimesFolder));
            }

            // Set database file path
            var dbFilePath = configs.GetValueOrDefault(EnvironmentConfiguration.DefaultDatabaseFilePath);
            if (!string.IsNullOrEmpty(dbFilePath) && !Path.IsPathRooted(dbFilePath))
            {
                SetOrAddConfig(configs, EnvironmentConfiguration.DefaultDatabaseFilePath, Path.Combine(testKitPath, dbFilePath));
            }
        }

        private string GetDockerDllPath(string baseDir, string dllPath)
        {
            var relativePath = Path.GetRelativePath(baseDir, dllPath);
            var folderName = Path.GetFileName(baseDir);
            return $"/apps/{folderName}/{relativePath}".Replace("\\", "/");
        }

        /// <summary>
        /// Sets up Docker containers for grading.
        /// </summary>
        private async Task SetupContainersAsync(Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Setting up Docker containers...");

            try
            {
                // Use the EnvironmentManagerInvoker to setup containers
                var envJson = JsonConvert.SerializeObject(environment);
                var envBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(envJson));

                // Check if Docker is running
                if (!_dockerExecutor.IsDockerRunning())
                {
                    throw new InvalidOperationException("Docker is not running. Please start Docker Desktop.");
                }

                // The actual container setup is handled by the existing EnvironmentManagerInvoker
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TrySetupContainer(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"Container setup warning: {error}");
                }

                await Task.Delay(1000, ct); // Wait for containers to be ready
                _logger.LogInfo("Docker containers setup complete");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to setup Docker containers", ex);
                throw;
            }
        }

        /// <summary>
        /// Copies student solution files to Docker containers.
        /// </summary>
        private async Task CopyFilesToContainersAsync(StudentSolution student, Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Copying files to containers...");

            try
            {
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TrySetupQuestion(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"File copy warning: {error}");
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Files copied to containers");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to copy files to containers", ex);
                throw;
            }
        }

        /// <summary>
        /// Executes the actual grading process.
        /// </summary>
        private async Task<(bool success, double mark, string message)> ExecuteGradingAsync(
            StudentSolution student,
            Environment environment,
            string testKitPath,
            GradingConfiguration config,
            CancellationToken ct)
        {
            _logger.LogInfo("Executing grading...");

            try
            {
                // For now, return a placeholder result
                // The actual grading logic will be integrated with the existing SuiteRunner
                
                // TODO: Integrate with the actual grading logic from SolutionGrader.Core
                // This includes:
                // 1. Flush network monitor
                // 2. Start server/client based on test kit steps
                // 3. Execute test cases
                // 4. Compare results
                // 5. Calculate marks

                await Task.Delay(2000, ct); // Simulated grading time

                _logger.LogInfo("Grading execution complete");
                return (true, 10.0, "Grading completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Grading execution failed", ex);
                return (false, 0, ex.Message);
            }
        }

        /// <summary>
        /// Writes grading results to Excel files.
        /// </summary>
        private async Task WriteResultsAsync(StudentSolution student, (bool success, double mark, string message) result, CancellationToken ct)
        {
            _logger.LogInfo("Writing results...");

            try
            {
                var resultFolder = (_logger as LoggingService)?.GetStudentResultFolder(student.StudentCode) 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Results", student.StudentCode);

                if (!Directory.Exists(resultFolder))
                {
                    Directory.CreateDirectory(resultFolder);
                }

                // Write result summary
                var summaryPath = Path.Combine(resultFolder, "GradeSummary.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Summary");
                    worksheet.Cell(1, 1).Value = "Student Code";
                    worksheet.Cell(1, 2).Value = student.StudentCode;
                    worksheet.Cell(2, 1).Value = "Paper";
                    worksheet.Cell(2, 2).Value = student.PaperNo;
                    worksheet.Cell(3, 1).Value = "Mark";
                    worksheet.Cell(3, 2).Value = result.mark;
                    worksheet.Cell(4, 1).Value = "Status";
                    worksheet.Cell(4, 2).Value = result.success ? "Success" : "Failed";
                    worksheet.Cell(5, 1).Value = "Message";
                    worksheet.Cell(5, 2).Value = result.message;
                    worksheet.Cell(6, 1).Value = "Start Time";
                    worksheet.Cell(6, 2).Value = student.StartTime?.ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cell(7, 1).Value = "End Time";
                    worksheet.Cell(7, 2).Value = student.EndTime?.ToString("yyyy-MM-dd HH:mm:ss");

                    workbook.SaveAs(summaryPath);
                }

                _logger.LogInfo($"Results written to {resultFolder}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to write results", ex);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Cleans up Docker containers after grading.
        /// </summary>
        private async Task CleanupContainersAsync(Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Cleaning up containers...");

            try
            {
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TryDisposeContainer(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"Container cleanup warning: {error}");
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Container cleanup complete");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to cleanup containers", ex);
            }
        }

        /// <summary>
        /// Pauses the current grading session.
        /// </summary>
        public void PauseGrading(GradingSessionState sessionState)
        {
            _logger.LogInfo("Pausing grading session...");
            sessionState.IsPaused = true;
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Resumes a paused grading session.
        /// </summary>
        public void ResumeGrading(GradingSessionState sessionState)
        {
            _logger.LogInfo("Resuming grading session...");
            sessionState.IsPaused = false;
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Cancels the current grading session.
        /// </summary>
        public void CancelGrading()
        {
            _logger.LogInfo("Cancelling grading session...");
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Resets all student statuses.
        /// </summary>
        public void ResetAllStatuses(List<StudentSolution> students, GradingSessionState sessionState)
        {
            _logger.LogInfo("Resetting all student statuses...");

            foreach (var student in students)
            {
                student.Status = GradingStatus.Not_Run;
                student.Mark = 0;
                student.StartTime = null;
                student.EndTime = null;
                student.StatusMessage = null;
                student.ProgressPercent = 0;
            }

            sessionState.Reset();
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count;
            SessionStateChanged?.Invoke(this, sessionState);

            _logger.LogInfo("All statuses reset");
        }

        /// <summary>
        /// Disposes a specific student's grading state.
        /// </summary>
        public void DisposeStudent(StudentSolution student)
        {
            _logger.LogInfo($"Disposing student: {student.StudentCode}");

            student.Status = GradingStatus.Disposed;
            student.Mark = 0;
            student.StartTime = null;
            student.EndTime = null;
            student.StatusMessage = "State disposed";
            student.ProgressPercent = 0;

            // TODO: Delete associated result files
        }

        private static void SetOrAddConfig(Dictionary<string, string> configs, string key, string value)
        {
            if (configs.ContainsKey(key))
            {
                configs[key] = value;
            }
            else
            {
                configs.Add(key, value);
            }
        }
    }
}
