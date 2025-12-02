using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Main service that orchestrates the grading process for student solutions.
    /// 
    /// IMPORTANT: This service delegates ALL grading logic to LibGradingService which
    /// calls the Lib folder's SuiteRunner. This ensures IDENTICAL behavior between
    /// SolutionGrader.CLI and SolutionGrader.UI.
    /// 
    /// The UI orchestration service ONLY handles:
    /// 1. Student discovery from submit folder
    /// 2. Session state management (progress, pause, cancel)
    /// 3. UI event notifications
    /// 4. Writing result summaries
    /// 
    /// All Docker container management, file copying, test execution, network monitoring,
    /// and output comparison is handled by Lib/SolutionGrader.Core services via LibGradingService.
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;
        private readonly StudentDiscoveryService _studentDiscovery;
        private readonly TestKitDiscoveryService _testKitDiscovery;
        private readonly LibGradingService _libGrading;
        private ResultWriterService? _resultWriter;
        
        private CancellationTokenSource? _cancellationTokenSource;
        
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
            _libGrading = new LibGradingService(logger);
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
        /// Delegates actual grading to LibGradingService which uses Lib/SolutionGrader.Core.
        /// </summary>
        public async Task StartGradingAsync(
            List<StudentSolution> students, 
            GradingConfiguration config,
            GradingSessionState sessionState)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;

            // Initialize result writer for saving StudentsSolution.xlsx
            var resultPath = !string.IsNullOrEmpty(config.SaveResultFolderPath) 
                ? config.SaveResultFolderPath 
                : Path.Combine(config.SubmitFolderPath, "Results");
            _resultWriter = new ResultWriterService(_logger, resultPath);

            sessionState.IsRunning = true;
            sessionState.IsPaused = false;
            sessionState.SessionStartTime = DateTime.Now;
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);

            _logger.LogInfo($"Starting grading for {students.Count} students");
            _logger.LogInfo($"Results will be saved to: {resultPath}");
            SessionStateChanged?.Invoke(this, sessionState);

            try
            {
                // Grade students one at a time
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

                    await GradeStudentAsync(student, config, resultPath, ct);

                    // Update session state
                    sessionState.GradedStudents++;
                    sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);
                    sessionState.SuccessCount = students.Count(s => s.Status == GradingStatus.Success);
                    sessionState.FailedCount = students.Count(s => s.Status == GradingStatus.Failed);
                    SessionStateChanged?.Invoke(this, sessionState);

                    // Write StudentsSolution.xlsx incrementally
                    _resultWriter.WriteStudentsSolutionSummary(students);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grading operation was cancelled");
                _resultWriter?.WriteStudentsSolutionSummary(students);
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
        /// Grades a single student by delegating to LibGradingService.
        /// 
        /// This method DOES NOT implement its own Docker/grading logic.
        /// All grading is done by Lib/SolutionGrader.Core services:
        /// - SuiteRunner: Main grading orchestration
        /// - TestCaseOrchestrator: Step-by-step test execution
        /// - NetworkMonitorService: Network traffic capture
        /// - ExecutableManager: Process management
        /// - Executor: Step execution and comparison
        /// - ExcelDetailLogService: Result logging
        /// </summary>
        private async Task GradeStudentAsync(
            StudentSolution student, 
            GradingConfiguration config,
            string resultPath,
            CancellationToken ct)
        {
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            student.StartTime = DateTime.Now;
            student.Status = GradingStatus.InProgress;
            student.ProgressPercent = 0;
            StudentGradingStarted?.Invoke(this, student);

            try
            {
                _logger.LogInfo($"Starting grading for student: {student.StudentCode} (Paper {student.PaperNo})");

                // Step 1: Find test kit for this paper
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

                // Step 2: Build paths for LibGradingService
                student.ProgressPercent = 20;
                StudentProgressUpdated?.Invoke(this, student);

                // The suite path is the test kit folder (contains Header.xlsx, Environment.xlsx, test cases)
                var suitePath = testKitPath;
                
                // Result folder for this student: {resultPath}/{paperNo}/student/{studentCode}
                var studentResultPath = Path.Combine(resultPath, student.PaperNo, "student", student.StudentCode);
                
                // Get student's executable paths (client and/or server)
                var clientExePath = GetStudentExecutablePath(student, config, "Client");
                var serverExePath = GetStudentExecutablePath(student, config, "Server");

                _logger.LogDebug($"Client exe path: {clientExePath ?? "(none)"}");
                _logger.LogDebug($"Server exe path: {serverExePath ?? "(none)"}");

                // Step 3: Execute grading via LibGradingService
                // This delegates to Lib/SolutionGrader.Core's SuiteRunner which handles:
                // - Docker container setup via EnvironmentManagerInvoker
                // - File copying to containers
                // - Network monitoring via NetworkMonitorService
                // - Test execution via TestCaseOrchestrator
                // - Output comparison via DataComparisonService
                // - Result logging via ExcelDetailLogService
                student.ProgressPercent = 30;
                StudentProgressUpdated?.Invoke(this, student);

                _logger.LogInfo("Delegating to LibGradingService (Lib/SolutionGrader.Core)...");
                
                var exitCode = await _libGrading.ExecuteSuiteAsync(
                    suitePath,
                    studentResultPath,
                    clientExePath,
                    serverExePath,
                    useInnerEnv: true,
                    ct);

                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                // Step 4: Interpret results
                if (exitCode == 1)
                {
                    student.Status = GradingStatus.Success;
                    student.StatusMessage = "Grading completed successfully";
                    student.Mark = ReadMarkFromResults(studentResultPath);
                }
                else
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = $"Grading failed (exit code: {exitCode})";
                    student.Mark = 0;
                }

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
        /// Gets the path to a student's executable (client or server).
        /// </summary>
        private string? GetStudentExecutablePath(StudentSolution student, GradingConfiguration config, string type)
        {
            if (string.IsNullOrEmpty(student.SolutionPath))
                return null;

            string projectName;
            bool hasProject;
            
            if (type.Equals("Client", StringComparison.OrdinalIgnoreCase))
            {
                projectName = config.ClientProjectName;
                hasProject = config.HasClient;
            }
            else
            {
                projectName = config.ServerProjectName;
                hasProject = config.HasServer;
            }

            if (!hasProject || string.IsNullOrEmpty(projectName))
                return null;

            // Look for the DLL in the student's solution folder
            var dllPath = Path.Combine(student.SolutionPath, $"{projectName}.dll");
            if (File.Exists(dllPath))
                return dllPath;

            // Try alternate names
            var altNames = new[] { "Q11.dll", "Q12.dll", "Project11.dll", "Project12.dll" };
            foreach (var name in altNames)
            {
                var path = Path.Combine(student.SolutionPath, name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Reads the total mark from result files generated by Lib services.
        /// </summary>
        private double ReadMarkFromResults(string resultPath)
        {
            // The Lib services write results to Excel files
            // Try to read the mark from the summary
            try
            {
                // Look for result files in the result path
                if (!Directory.Exists(resultPath))
                    return 0;

                // The ExcelDetailLogService writes GradeDetail.xlsx with marks
                // For now, return 0 - the actual parsing can be added
                // TODO: Parse mark from GradeDetail.xlsx
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Pauses the current grading session.
        /// </summary>
        public void PauseGrading(GradingSessionState sessionState)
        {
            sessionState.IsPaused = true;
            _logger.LogInfo("Grading paused");
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Resumes the paused grading session.
        /// </summary>
        public void ResumeGrading(GradingSessionState sessionState)
        {
            sessionState.IsPaused = false;
            _logger.LogInfo("Grading resumed");
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Cancels the current grading session.
        /// </summary>
        public void CancelGrading(GradingSessionState sessionState)
        {
            _cancellationTokenSource?.Cancel();
            sessionState.IsRunning = false;
            _logger.LogInfo("Grading cancelled by user");
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Checks if Docker is available.
        /// </summary>
        public bool IsDockerAvailable()
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
                if (process == null) return false;
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resets all student statuses to Not_Run.
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
        }
    }
}
