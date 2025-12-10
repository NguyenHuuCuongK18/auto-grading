using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Models;
using SolutionGrader.UI.Models;
using SolutionGrader.Core.Services;

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
        private ExcelLogCoordinator? _excelCoordinator;
        private GradingMessageLogger? _messageLogger;
        private bool _ownsMessageLogger; // True if we created the logger, false if it was shared
        
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
        /// <param name="sharedMessageLogger">Optional shared GradingMessageLogger for batch grading. 
        /// If provided, uses this instead of creating a new instance, preventing file access conflicts in parallel grading scenarios. 
        /// IMPORTANT: When providing a shared logger, the CALLER retains ownership and is responsible for disposal. 
        /// This service will NOT dispose a shared logger. The shared logger MUST be thread-safe for concurrent writes.</param>
        public async Task StartGradingAsync(
            List<StudentSolution> students, 
            GradingConfiguration config,
            GradingSessionState sessionState,
            CancellationToken ct = default,
            GradingMessageLogger? sharedMessageLogger = null)
        {
            // Use provided cancellation token, or create a new one if not provided
            if (ct == default)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                ct = _cancellationTokenSource.Token;
            }

            // Initialize result writer for saving StudentsSolution.xlsx
            var resultPath = config.GetEffectiveResultPath();
            _resultWriter = new ResultWriterService(_logger, resultPath);

            // Initialize Excel log coordinator for centralized, thread-safe Excel updates
            _excelCoordinator = new ExcelLogCoordinator(_logger, resultPath);

            // Initialize centralized message logger for structured error/message logging
            // Use shared logger if provided (for batch grading), otherwise create a new one
            if (sharedMessageLogger != null)
            {
                _messageLogger = sharedMessageLogger;
                _ownsMessageLogger = false; // We don't own this logger, so don't dispose it
                _logger.LogInfo($"[GradingOrchestrationService] Using shared GradingMessageLogger for batch grading");
            }
            else
            {
                _messageLogger = new GradingMessageLogger(resultPath);
                _ownsMessageLogger = true; // We created this logger, so we must dispose it
                _messageLogger.LogInfo($"Starting grading session for {students.Count} students");
                _logger.LogInfo($"[GradingOrchestrationService] Created new GradingMessageLogger instance");
            }

            sessionState.IsRunning = true;
            sessionState.IsPaused = false;
            sessionState.SessionStartTime = DateTime.Now;
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);

            _logger.LogInfo($"Starting grading for {students.Count} students");
            _logger.LogInfo($"Results will be saved to: {resultPath}");
            SessionStateChanged?.Invoke(this, sessionState);

            // PRE-POPULATE Excel file with all students and predetermined information
            // This solves the batch grading issue where multiple processes overwrite the file
            try
            {
                // Collect max marks for each paper from test kits
                var testKitMaxMarks = new Dictionary<string, double>();
                foreach (var student in students)
                {
                    if (!testKitMaxMarks.ContainsKey(student.PaperNo))
                    {
                        var testKitPath = _testKitDiscovery.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                        if (string.IsNullOrEmpty(testKitPath))
                        {
                            // Test kit not found - this is a test kit error
                            var errorMsg = GradingMessageCatalog.Format(
                                GradingMessageCatalog.TestKitError.MappingNotFound, 
                                student.PaperNo);
                            _messageLogger.LogTestKitError(errorMsg, student.StudentCode);
                            _logger.LogWarning($"[{student.StudentCode}] {errorMsg}");
                            continue;
                        }
                        
                        var maxMark = _testKitDiscovery.GetTestKitMaxMark(testKitPath);
                        testKitMaxMarks[student.PaperNo] = maxMark;
                    }
                }

                _excelCoordinator.InitializeExcelFile(students, testKitMaxMarks);
                _logger.LogInfo($"[ExcelCoordinator] Pre-populated StudentsSolution.xlsx with {students.Count} students");
                _messageLogger.LogInfo("Excel log file initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize Excel file", ex);
                _messageLogger.LogGraderError(
                    GradingMessageCatalog.Format(GradingMessageCatalog.GraderError.ExcelFileWriteFailed, ex.Message), 
                    null, ex);
            }

            // PORT ALLOCATION NOTE:
            // Port allocation is now handled by the caller (GradingWindow) which creates a shared
            // PortAllocator and allocates unique ports for each student before calling this service.
            // The allocated ports are passed in via the GradingConfiguration parameter for each student.
            // This service simply uses the ports provided in the configuration.

            try
            {
                // CRITICAL FIX: Do NOT filter students by status here!
                // The caller (GradingWindow or CLI) already decided which students to grade.
                // Filtering again here causes students to be skipped unexpectedly.
                // 
                // Previous buggy code:
                // var studentsToGrade = students.Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused).ToList();
                // 
                // This caused the "Start All/Start Selected not always correctly sending all students through grading" bug
                // where selected students would be filtered out if their status didn't match the filter criteria.
                //
                // The orchestration service should grade ALL students it receives from the caller.
                
                _logger.LogInfo($"[Grading Loop] Total students to grade: {students.Count}");
                
                // Diagnostic logging: Report student statuses for debugging (optimized single-pass count)
                if (students.Count > 0)
                {
                    var statusCounts = new Dictionary<GradingStatus, int>();
                    foreach (var s in students)
                    {
                        if (statusCounts.ContainsKey(s.Status))
                            statusCounts[s.Status]++;
                        else
                            statusCounts[s.Status] = 1;
                    }
                    
                    foreach (var kvp in statusCounts.OrderBy(x => x.Key))
                    {
                        _logger.LogInfo($"[Grading Loop]   - {kvp.Value} student(s) with Status={kvp.Key}");
                    }
                }
                
                // Grade ALL students passed to this service - no filtering by status
                foreach (var student in students)
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

                    // Update session state with single-pass counting for better performance
                    sessionState.GradedStudents++;
                    
                    // OPTIMIZED: Count statuses in single pass instead of 3 separate iterations
                    int notRun = 0, success = 0, failed = 0;
                    foreach (var s in students)
                    {
                        if (s.Status == GradingStatus.Not_Run) notRun++;
                        else if (s.Status == GradingStatus.Success) success++;
                        else if (s.Status == GradingStatus.Failed) failed++;
                    }
                    
                    sessionState.NotRunCount = notRun;
                    sessionState.SuccessCount = success;
                    sessionState.FailedCount = failed;
                    SessionStateChanged?.Invoke(this, sessionState);

                    // NO LONGER NEEDED: Old approach that recreated entire Excel file on each update
                    // _resultWriter.WriteStudentsSolutionSummary(students);
                    // Now handled by ExcelLogCoordinator which updates individual rows in-place
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grading operation was cancelled");
                _messageLogger?.LogInfo("Grading session cancelled by user");
                // NO LONGER NEEDED: Excel file already has all students
                // _resultWriter?.WriteStudentsSolutionSummary(students);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during grading", ex);
                _messageLogger?.LogGraderError(
                    GradingMessageCatalog.Format(GradingMessageCatalog.GraderError.UnexpectedError, ex.Message), 
                    null, ex);
            }
            finally
            {
                sessionState.IsRunning = false;
                sessionState.SessionEndTime = DateTime.Now;
                sessionState.CurrentStudentCode = null;
                
                _excelCoordinator?.Dispose();
                
                // Dispose message logger only if we created it (not shared)
                // If shared, the owner (GradingWindow) will dispose it
                if (_ownsMessageLogger)
                {
                    _messageLogger?.LogInfo($"Grading session completed. Total students: {students.Count}");
                    _messageLogger?.Dispose();
                    _logger.LogInfo("[GradingOrchestrationService] Disposed owned GradingMessageLogger");
                }
                else
                {
                    _logger.LogInfo("[GradingOrchestrationService] Skipped disposal of shared GradingMessageLogger (owned by caller)");
                }
                
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
            CancellationToken ct)
        {
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            student.StartTime = DateTime.Now;
            student.Status = GradingStatus.InProgress;
            student.ProgressPercent = 0;
            
            // Update Excel: Student started grading
            _excelCoordinator?.UpdateStudentStarted(student.StudentCode, student.PaperNo, student.StartTime.Value);
            
            StudentGradingStarted?.Invoke(this, student);

            try
            {
                _logger.LogInfo($"[{student.StudentCode}] Starting DOCKER grading for student: {student.StudentCode} (Paper {student.PaperNo})");
                _messageLogger?.LogInfo(
                    GradingMessageCatalog.Format(GradingMessageCatalog.Info.GradingStarted, student.StudentCode, student.PaperNo),
                    student.StudentCode);

                // Step 1: Find test kit for this paper
                student.ProgressPercent = 10;
                StudentProgressUpdated?.Invoke(this, student);

                var testKitPath = _testKitDiscovery.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    // Test kit error - log but don't abort other students
                    var errorMsg = GradingMessageCatalog.Format(
                        GradingMessageCatalog.TestKitError.MappingNotFound, 
                        student.PaperNo);
                    
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = errorMsg;
                    _logger.LogWarning($"[{student.StudentCode}] {errorMsg}");
                    _messageLogger?.LogTestKitError(errorMsg, student.StudentCode);
                    return;
                }

                _logger.LogInfo($"[{student.StudentCode}] Using test kit: {testKitPath}");

                // Step 2: Ensure solution is extracted (lazy extraction)
                // This happens here, right before grading, to avoid UI lag during student discovery
                student.ProgressPercent = 15;
                StudentProgressUpdated?.Invoke(this, student);
                
                if (!string.IsNullOrEmpty(student.SolutionPath))
                {
                    _logger.LogInfo($"[{student.StudentCode}] Ensuring solution is extracted from zip if needed...");
                    bool solutionReady = SharedDiscoveryServices.EnsureSolutionExtracted(
                        student.SolutionPath,
                        msg => _logger.LogDebug($"[{student.StudentCode}] {msg}"));
                    
                    if (!solutionReady)
                    {
                        var errorMsg = $"Failed to extract or locate solution folder - will attempt to continue with grading";
                        _logger.LogWarning($"[{student.StudentCode}] {errorMsg}");
                        _messageLogger?.LogStudentError(student.StudentCode, errorMsg);
                        // Don't return early - let the grading service handle this and report appropriate errors
                        // The DockerGradingService will fail gracefully if files are truly missing
                    }
                    else
                    {
                        _logger.LogInfo($"[{student.StudentCode}] Solution ready at: {student.SolutionPath}");
                    }
                }

                // Step 3: Build paths for Docker grading
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
                
                // Check for missing DLLs and log student errors (but continue grading attempt)
                if (config.HasClient && string.IsNullOrEmpty(clientDllPath))
                {
                    var errorMsg = GradingMessageCatalog.Format(
                        GradingMessageCatalog.StudentError.MissingClientDll, 
                        config.ClientProjectName);
                    _messageLogger?.LogStudentError(student.StudentCode, errorMsg);
                    _logger.LogWarning($"[{student.StudentCode}] {errorMsg}");
                }
                
                if (config.HasServer && string.IsNullOrEmpty(serverDllPath))
                {
                    var errorMsg = GradingMessageCatalog.Format(
                        GradingMessageCatalog.StudentError.MissingServerDll, 
                        config.ServerProjectName);
                    _messageLogger?.LogStudentError(student.StudentCode, errorMsg);
                    _logger.LogWarning($"[{student.StudentCode}] {errorMsg}");
                }

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
                
                // PORT ALLOCATION FIX:
                // Ports are allocated by the caller (GradingWindow) using a shared PortAllocator before
                // this method is called. The allocated ports are already present in the config parameter:
                // - config.CodeContainerInternalPort (port inside container)
                // - config.CodeContainerHostPort (port exposed on host)
                //
                // We simply use these pre-allocated ports instead of allocating new ones here.
                // This ensures:
                // - Each student in batch grading gets a unique port (allocated by shared PortAllocator)
                // - No port conflicts when grading multiple students in parallel
                // - Sequential port allocation (8000, 8001, 8002, etc.) as intended
                
                int portToUse = config.CodeContainerHostPort;
                
                // Validate that port was provided
                if (portToUse <= 0 || portToUse > 65535)
                {
                    var errorMsg = $"Invalid port configuration: {portToUse}. Port must be between 1-65535.";
                    _logger.LogError(errorMsg);
                    _messageLogger?.LogGraderError(errorMsg, student.StudentCode, null);
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = errorMsg;
                    student.Mark = 0;
                    return;
                }
                
                _logger.LogInfo($"[Port Config] [{student.StudentCode}] Using pre-allocated port {portToUse} for container, DLL modification, and network monitoring");
                
                // CRITICAL FIX: Read Grade_Content from test kit's OUTER Header.xlsx
                // This determines what the student should provide (Server or Client)
                // Overrides UI checkboxes for single-file scenarios
                bool hasClient = config.HasClient;
                bool hasServer = config.HasServer;
                string? gradeContent = ReadGradeContentFromTestKit(testKitPath);
                
                if (!string.IsNullOrEmpty(gradeContent))
                {
                    _logger.LogInfo($"[Grade_Content] Test kit specifies Grade_Content='{gradeContent}' - overriding UI checkboxes");
                    
                    if (gradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    {
                        // Student provides SERVER, use golden CLIENT
                        hasServer = true;
                        hasClient = false;
                        _logger.LogInfo($"[Grade_Content] Student provides SERVER → HasServer=true, HasClient=false (use golden client)");
                    }
                    else if (gradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                    {
                        // Student provides CLIENT, use golden SERVER
                        hasServer = false;
                        hasClient = true;
                        _logger.LogInfo($"[Grade_Content] Student provides CLIENT → HasServer=false, HasClient=true (use golden server)");
                    }
                }
                else
                {
                    _logger.LogInfo($"[Grade_Content] No Grade_Content in test kit - using UI checkboxes: HasClient={hasClient}, HasServer={hasServer}");
                }
                
                _logger.LogInfo($"[Port Config] Creating DockerGradingConfig with CodeContainerInternalPort={portToUse}, CodeContainerHostPort={portToUse}");
                
                var dockerConfig = new DockerGradingConfig
                {
                    // Component requirements (from Grade_Content or UI checkboxes)
                    HasClient = hasClient,
                    HasServer = hasServer,
                    ClientProjectName = config.ClientProjectName,
                    ServerProjectName = config.ServerProjectName,
                    
                    // Container settings - USE MONITOR PORT DIRECTLY from environment.xlsx
                    // CRITICAL: Both ports must be the same for network monitoring to work correctly
                    // The server binds to this port inside the container, and it's exposed to host on the same port
                    // DLL modification also uses this same port to patch hardcoded values
                    CodeContainerInternalPort = portToUse,
                    CodeContainerHostPort = portToUse,
                    DockerNetwork = config.DockerNetwork ?? "auto-grading-network",
                    
                    // Database container settings
                    DatabaseImageName = config.DatabaseImageName ?? "mcr.microsoft.com/mssql/server:2019-latest",
                    DatabaseContainerName = config.DatabaseContainerName ?? "auto-grading-sqlserver",
                    DatabaseContainerInternalPort = config.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = config.DatabaseContainerHostPort,
                    DatabaseUsername = config.DatabaseUsername ?? "sa",
                    DatabasePassword = config.DatabasePassword,
                    
                    GradingTimeoutSeconds = config.GradingTimeoutSeconds,
                    
                    // DLL modification fallback setting
                    UseDllModificationFallback = config.UseDllModificationFallback
                };
                
                _logger.LogInfo($"[{student.StudentCode}] Grading config: HasClient={config.HasClient}, HasServer={config.HasServer}");
                _logger.LogInfo($"[{student.StudentCode}] Project names: Client={config.ClientProjectName}, Server={config.ServerProjectName}");
                
                var result = await _libGrading.ExecuteDockerGradingAsync(
                    testKitPath,
                    studentResultPath,
                    serverDllPath,
                    clientDllPath,
                    student.StudentCode,
                    dockerConfig,
                    ct);

                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                // Step 4: Interpret results from DockerGradingResult
                if (string.IsNullOrEmpty(result.ErrorMessage) || result.TotalMark > 0)
                {
                    student.Status = GradingStatus.Success;
                    student.StatusMessage = $"Grading completed: {result.TotalMark:F2}/{result.MaxMark:F2}";
                    student.Mark = result.TotalMark;
                    
                    _messageLogger?.LogInfo(
                        GradingMessageCatalog.Format(GradingMessageCatalog.Info.GradingCompleted, 
                            student.StudentCode, result.TotalMark, result.MaxMark),
                        student.StudentCode);
                }
                else
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = result.ErrorMessage ?? $"Grading failed: 0/{result.MaxMark:F2}";
                    student.Mark = 0;
                    
                    // Log as student error if there's a specific error message
                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                    {
                        _messageLogger?.LogStudentError(student.StudentCode, result.ErrorMessage);
                    }
                }

                student.ProgressPercent = 100;
                _logger.LogInfo($"[{student.StudentCode}] Docker grading completed for {student.StudentCode}. Mark: {student.Mark:F2}, Status: {student.Status}");
            }
            catch (OperationCanceledException)
            {
                student.Status = GradingStatus.Paused;
                student.StatusMessage = "Grading was paused/cancelled";
                _logger.LogWarning($"[{student.StudentCode}] Grading paused for {student.StudentCode}");
                _messageLogger?.LogInfo("Grading cancelled by user", student.StudentCode);
            }
            catch (Exception ex)
            {
                // Student error - log but don't abort grading for other students
                student.Status = GradingStatus.Failed;
                student.StatusMessage = $"Error: {ex.Message}";
                _logger.LogError($"[{student.StudentCode}] Grading failed for {student.StudentCode}", ex);
                
                // Determine if this is a student error or grader error based on exception type
                if (ex.Message.Contains("DLL") || ex.Message.Contains("executable") || ex.Message.Contains("project"))
                {
                    _messageLogger?.LogStudentError(student.StudentCode, 
                        GradingMessageCatalog.Format(GradingMessageCatalog.StudentError.ProjectCrashed, "project", ex.Message),
                        ex: ex);
                }
                else if (ex.Message.Contains("Docker") || ex.Message.Contains("container"))
                {
                    _messageLogger?.LogGraderError(
                        GradingMessageCatalog.Format(GradingMessageCatalog.GraderError.DockerContainerFailed, ex.Message),
                        student.StudentCode, ex);
                }
                else
                {
                    _messageLogger?.LogGraderError(
                        GradingMessageCatalog.Format(GradingMessageCatalog.GraderError.UnexpectedError, ex.Message),
                        student.StudentCode, ex);
                }
            }
            finally
            {
                student.EndTime = DateTime.Now;
                
                // Update Excel: Student completed grading
                // Pass the StatusMessage which contains exception details (if any)
                _excelCoordinator?.UpdateStudentCompleted(
                    student.StudentCode, 
                    student.PaperNo, 
                    student.EndTime.Value, 
                    student.Mark, 
                    student.Status,
                    student.StatusMessage);
                
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
            // The actual solution folder is at: {studentCode}/1/solution/
            if (!string.IsNullOrEmpty(projectName) && Directory.Exists(student.SolutionPath))
            {
                try
                {
                    // Priority 1: Search in solution subfolder ({studentCode}/1/solution/)
                    var solutionSubfolder = Path.Combine(student.SolutionPath, "solution");
                    var searchFolders = Directory.Exists(solutionSubfolder) 
                        ? new[] { solutionSubfolder, student.SolutionPath } 
                        : new[] { student.SolutionPath };
                    
                    foreach (var searchFolder in searchFolders)
                    {
                        // Search for the DLL recursively, excluding runtime folders
                        var dllFiles = Directory.GetFiles(searchFolder, $"{projectName}.dll", SearchOption.AllDirectories)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                            .ToArray();

                        if (dllFiles.Length > 0)
                        {
                            var result = dllFiles[0];
                            _logger.LogDebug($"Found {type} DLL via recursive search in {Path.GetFileName(searchFolder)}: {result}");
                            return result;
                        }

                        // Try alternate names (Q11, Q12) for compatibility
                        var altNames = type.Equals("Client", StringComparison.OrdinalIgnoreCase)
                            ? new[] { "Q12.dll", "Project12.dll" }
                            : new[] { "Q11.dll", "Project11.dll" };

                        foreach (var altName in altNames)
                        {
                            dllFiles = Directory.GetFiles(searchFolder, altName, SearchOption.AllDirectories)
                                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                                .ToArray();

                            if (dllFiles.Length > 0)
                            {
                                var result = dllFiles[0];
                                _logger.LogDebug($"Found {type} DLL via fallback search ({altName}) in {Path.GetFileName(searchFolder)}: {result}");
                                return result;
                            }
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
            
            var dockerConfig = new DockerGradingConfig
            {
                DatabaseContainerName = config.DatabaseContainerName ?? "auto-grading-sqlserver",
                DockerNetwork = config.DockerNetwork ?? "auto-grading-network"
            };
            
            _libGrading.DisposeAllContainers(dockerConfig);
        }

        /// <summary>
        /// Reads Grade_Content from the test kit's outer Header.xlsx file.
        /// This determines whether the student should provide Server or Client.
        /// Returns null if Grade_Content is not specified (two-file scenario).
        /// </summary>
        private string? ReadGradeContentFromTestKit(string testKitPath)
        {
            try
            {
                var headerPath = Path.Combine(testKitPath, "Header.xlsx");
                if (!File.Exists(headerPath))
                {
                    return null;
                }

                using var wb = new ClosedXML.Excel.XLWorkbook(headerPath);
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null) return null;

                // Look for Grade_Content key
                for (int r = 1; r <= Math.Min(50, ws.RowCount()); r++)
                {
                    var key = ws.Cell(r, 1).GetValue<string>()?.Trim() ?? "";
                    if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = ws.Cell(r, 2).GetValue<string>()?.Trim() ?? "";
                        return string.IsNullOrEmpty(value) ? null : value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not read Grade_Content from test kit Header.xlsx: {ex.Message}");
                return null;
            }
        }

    }
}
