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
        /// <param name="ct">Optional cancellation token from caller. If provided, uses this instead of internal token.</param>
        /// <param name="onContainersReady">Optional callback when containers are ready (for staggered startup)</param>
        public async Task StartGradingAsync(
            List<StudentSolution> students, 
            GradingConfiguration config,
            GradingSessionState sessionState,
            CancellationToken ct = default,
            Action? onContainersReady = null)
        {
            // Use provided cancellation token, or create a new one if not provided
            if (ct == default)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                ct = _cancellationTokenSource.Token;
            }

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

                    await GradeStudentAsync(student, config, resultPath, ct, onContainersReady);

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
        /// Grades a single student by delegating to LibGradingService's Docker grading.
        /// 
        /// IMPORTANT: This method uses DOCKER-BASED GRADING via DockerGradingService:
        /// 1. Sets up Docker containers (server and client) with TTY support
        /// 2. Copies student DLLs to containers
        /// 3. Generates appsettings.json with proper networking config
        /// 4. NetworkMonitor runs on HOST and sniffs the exposed server port
        /// 5. Test execution happens INSIDE containers
        /// 6. Output captured via application log files (bypasses docker logs buffering)
        /// 
        /// The architecture:
        /// - Server container: port EXPOSED to host (e.g., -p 8000:8000) for NetworkMonitor
        /// - Client container: connects to server via Docker network (container name as hostname)
        /// - NetworkMonitor: runs on HOST, sniffs localhost:{exposed_port}
        /// </summary>
        private async Task GradeStudentAsync(
            StudentSolution student, 
            GradingConfiguration config,
            string resultPath,
            CancellationToken ct,
            Action? onContainersReady = null)
        {
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            student.StartTime = DateTime.Now;
            student.Status = GradingStatus.InProgress;
            student.ProgressPercent = 0;
            StudentGradingStarted?.Invoke(this, student);

            try
            {
                _logger.LogInfo($"Starting DOCKER grading for student: {student.StudentCode} (Paper {student.PaperNo})");

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

                // Step 2: Build paths for Docker grading
                student.ProgressPercent = 20;
                StudentProgressUpdated?.Invoke(this, student);

                // Result folder for this student - organized by paper number to match SampleLogging structure:
                // Structure: {resultPath}/{paperNo}/student/{studentCode}
                // Example: Results/1/student/CuongNHE186494
                // This ensures results are properly organized by exam paper for easier review
                var studentResultPath = Path.Combine(resultPath, student.PaperNo, "student", student.StudentCode);
                
                // Get student's DLL paths (client and/or server)
                var clientDllPath = GetStudentExecutablePath(student, config, "Client");
                var serverDllPath = GetStudentExecutablePath(student, config, "Server");

                _logger.LogDebug($"Client DLL path: {clientDllPath ?? "(none)"}");
                _logger.LogDebug($"Server DLL path: {serverDllPath ?? "(none)"}");

                // Step 3: Execute DOCKER grading via LibGradingService
                // This delegates to Lib/SolutionGrader.Core's DockerGradingService which handles:
                // - Docker container setup with TTY support (-t flag)
                // - Server port EXPOSED to host for NetworkMonitor sniffing
                // - File copying to containers
                // - Network monitoring via NetworkMonitorService (runs on HOST)
                // - Test execution INSIDE containers
                // - Output captured via application log files
                // - Result logging to Excel files
                student.ProgressPercent = 30;
                StudentProgressUpdated?.Invoke(this, student);

                _logger.LogInfo("Delegating to LibGradingService.ExecuteDockerGradingAsync (Docker-based grading)...");
                
                // Build Docker configuration from UI config
                // The examiner sets HasClient/HasServer to indicate what the student should provide:
                // - HasClient=true, HasServer=true  → student provides both
                // - HasClient=true, HasServer=false → student provides client, use golden server
                // - HasClient=false, HasServer=true → student provides server, use golden client
                var dockerConfig = new SolutionGrader.Core.Services.DockerGradingConfig
                {
                    // Examiner's component requirements
                    HasClient = config.HasClient,
                    HasServer = config.HasServer,
                    ClientProjectName = config.ClientProjectName,
                    ServerProjectName = config.ServerProjectName,
                    
                    // Container settings
                    CodeContainerInternalPort = config.CodeContainerInternalPort,
                    CodeContainerHostPort = config.CodeContainerHostPort,
                    DockerNetwork = config.DockerNetwork ?? "auto-grading-network",
                    
                    // Database container settings
                    DatabaseImageName = config.DatabaseImageName ?? "mcr.microsoft.com/mssql/server:2019-latest",
                    DatabaseContainerName = config.DatabaseContainerName ?? "auto-grading-sqlserver",
                    DatabaseContainerInternalPort = config.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = config.DatabaseContainerHostPort,
                    DatabaseUsername = config.DatabaseUsername ?? "sa",
                    DatabasePassword = config.DatabasePassword,
                    
                    GradingTimeoutSeconds = config.GradingTimeoutSeconds
                };
                
                _logger.LogInfo($"Grading config: HasClient={config.HasClient}, HasServer={config.HasServer}");
                _logger.LogInfo($"Project names: Client={config.ClientProjectName}, Server={config.ServerProjectName}");
                
                var result = await _libGrading.ExecuteDockerGradingAsync(
                    testKitPath,
                    studentResultPath,
                    serverDllPath,
                    clientDllPath,
                    student.StudentCode,
                    dockerConfig,
                    ct,
                    onContainersReady);

                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                // Step 4: Interpret results from DockerGradingResult
                if (result.Passed || result.TotalMark > 0)
                {
                    student.Status = GradingStatus.Success;
                    student.StatusMessage = $"Docker grading completed: {result.TotalMark:F2}/{result.MaxMark:F2}";
                    student.Mark = result.TotalMark;
                }
                else
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = result.ErrorMessage ?? $"Docker grading failed: 0/{result.MaxMark:F2}";
                    student.Mark = 0;
                }

                student.ProgressPercent = 100;
                _logger.LogInfo($"Docker grading completed for {student.StudentCode}. Mark: {student.Mark:F2}, Status: {student.Status}");
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
        /// 
        /// This method uses the DLL paths that were discovered during student discovery
        /// by StudentDiscoveryService.FindDllPath(), which performs a recursive search
        /// through the solution folder. The student.ClientDllPath and student.ServerDllPath
        /// properties contain the full paths to the DLLs.
        /// 
        /// If the pre-discovered paths are not available, falls back to a recursive search.
        /// </summary>
        private string? GetStudentExecutablePath(StudentSolution student, GradingConfiguration config, string type)
        {
            if (string.IsNullOrEmpty(student.SolutionPath))
                return null;

            bool hasProject;
            string? preDiscoveredPath;
            string projectName;
            
            if (type.Equals("Client", StringComparison.OrdinalIgnoreCase))
            {
                hasProject = config.HasClient;
                preDiscoveredPath = student.ClientDllPath;
                projectName = config.ClientProjectName;
            }
            else
            {
                hasProject = config.HasServer;
                preDiscoveredPath = student.ServerDllPath;
                projectName = config.ServerProjectName;
            }

            if (!hasProject)
                return null;

            // Use the pre-discovered DLL path from StudentDiscoveryService if available
            // This path was found using recursive search during student discovery
            if (!string.IsNullOrEmpty(preDiscoveredPath) && File.Exists(preDiscoveredPath))
            {
                _logger.LogDebug($"Using pre-discovered {type} DLL: {preDiscoveredPath}");
                return preDiscoveredPath;
            }

            // Fallback: Search recursively for the DLL (same logic as StudentDiscoveryService)
            // This handles cases where the DLL might not have been found during initial discovery
            if (!string.IsNullOrEmpty(projectName) && Directory.Exists(student.SolutionPath))
            {
                try
                {
                    // Search for the DLL recursively, excluding runtime folders
                    var dllFiles = Directory.GetFiles(student.SolutionPath, $"{projectName}.dll", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                        .ToArray();

                    if (dllFiles.Length > 0)
                    {
                        var result = dllFiles[0];
                        _logger.LogDebug($"Found {type} DLL via recursive search: {result}");
                        return result;
                    }

                    // Try alternate names (Q11, Q12) for compatibility
                    var altNames = type.Equals("Client", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "Q12.dll", "Project12.dll" }
                        : new[] { "Q11.dll", "Project11.dll" };

                    foreach (var altName in altNames)
                    {
                        dllFiles = Directory.GetFiles(student.SolutionPath, altName, SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                            .ToArray();

                        if (dllFiles.Length > 0)
                        {
                            var result = dllFiles[0];
                            _logger.LogDebug($"Found {type} DLL via fallback search ({altName}): {result}");
                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error searching for {type} DLL: {ex.Message}");
                }
            }

            _logger.LogWarning($"No {type} DLL found for student {student.StudentCode}");
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
        
        /// <summary>
        /// Disposes all Docker containers including the database container.
        /// Call this at the end of a grading session to clean up all resources.
        /// </summary>
        /// <param name="config">Grading configuration containing Docker settings</param>
        public void DisposeAllContainers(GradingConfiguration config)
        {
            _logger.LogInfo("Disposing all Docker containers...");
            
            var dockerConfig = new SolutionGrader.Core.Services.DockerGradingConfig
            {
                DatabaseContainerName = config.DatabaseContainerName ?? "auto-grading-sqlserver",
                DockerNetwork = config.DockerNetwork ?? "auto-grading-network"
            };
            
            _libGrading.DisposeAllContainers(dockerConfig);
        }
    }
}
