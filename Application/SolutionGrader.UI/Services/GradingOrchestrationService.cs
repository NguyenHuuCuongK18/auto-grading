using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        
        private CancellationTokenSource? _cancellationTokenSource;
        private SolutionGrader.Core.Services.PortAllocator? _portAllocator;
        
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

            // Initialize Excel log coordinator for centralized, thread-safe Excel updates
            _excelCoordinator = new ExcelLogCoordinator(_logger, resultPath);

            // Initialize centralized message logger for structured error/message logging
            _messageLogger = new GradingMessageLogger(resultPath);
            _messageLogger.LogInfo($"Starting grading session for {students.Count} students");

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

            // Initialize PortAllocator with starting port from Environment.xlsx
            // This ensures each student gets a unique sequential port (no port reuse)
            // User requirement: Never reuse ports to avoid waiting and potential issues
            //
            // CRITICAL: Clear tracking file at START of each grading session to ensure
            // we always start from the port specified in Environment.xlsx.
            // This fixes the issue where editing Environment.xlsx didn't take effect
            // because the old tracking file was still present.
            try
            {
                var firstStudent = students.FirstOrDefault();
                if (firstStudent != null)
                {
                    var firstTestKitPath = _testKitDiscovery.GetTestKitForPaper(config.TestKitFolderPath, firstStudent.PaperNo);
                    if (!string.IsNullOrEmpty(firstTestKitPath))
                    {
                        int startingPort = ReadStartingPortFromEnvironmentXlsx(firstTestKitPath);
                        if (startingPort <= 0)
                        {
                            startingPort = 8000; // Fallback default
                            _logger.LogWarning($"[Port Config] Could not read starting port from Environment.xlsx, using default {startingPort}");
                        }
                        else
                        {
                            _logger.LogInfo($"[Port Config] Read starting port {startingPort} from Environment.xlsx");
                        }
                        
                        // CRITICAL FIX: Clear tracking file before creating PortAllocator
                        // This ensures each UI grading session starts fresh from Environment.xlsx
                        // User requirement: When Environment.xlsx is changed, use the new port
                        try
                        {
                            SolutionGrader.Core.Services.PortAllocator.ClearAllAllocatedPorts();
                            _logger.LogInfo($"[Port Config] Cleared port tracking file - session will start from Environment.xlsx port {startingPort}");
                        }
                        catch (Exception clearEx)
                        {
                            _logger.LogWarning($"[Port Config] Failed to clear port tracking file: {clearEx.Message}");
                        }
                        
                        _portAllocator = new SolutionGrader.Core.Services.PortAllocator(startingPort);
                        _logger.LogInfo($"[Port Config] Initialized PortAllocator with starting port {startingPort} - each student will get sequential unique port");
                        _messageLogger.LogInfo($"Port allocation initialized at {startingPort} (sequential, no reuse within session)");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Port Config] Failed to initialize PortAllocator: {ex.Message}. Will fallback to default port.");
            }

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
                
                // Dispose message logger - this will export all messages to Excel
                _messageLogger?.LogInfo($"Grading session completed. Total students: {students.Count}");
                _messageLogger?.Dispose();
                
                // Dispose PortAllocator
                _portAllocator?.Dispose();
                _portAllocator = null;
                
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
                        var errorMsg = $"Failed to extract or locate solution folder";
                        student.Status = GradingStatus.Failed;
                        student.StatusMessage = errorMsg;
                        _logger.LogError($"[{student.StudentCode}] {errorMsg}");
                        _messageLogger?.LogStudentError(student.StudentCode, errorMsg);
                        return;
                    }
                    _logger.LogInfo($"[{student.StudentCode}] Solution ready at: {student.SolutionPath}");
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
                
                // CRITICAL: Use PortAllocator for sequential port allocation (no port reuse)
                // User requirement: Never reuse ports - each student gets a unique sequential port
                // This avoids:
                // - Waiting for ports to be released
                // - Potential conflicts if cleanup fails
                // - Issues when something unexpected happens
                //
                // Port allocation strategy:
                // - PortAllocator initialized with starting port from Environment.xlsx (e.g., 8000)
                // - Student 1: allocate port 8000 (no wait, no cleanup needed)
                // - Student 2: allocate port 8001 (no wait, no cleanup needed)
                // - Student 3: allocate port 8002 (no wait, no cleanup needed)
                // - And so on...
                //
                // Benefits:
                // - Fast: No waiting for port release or container cleanup
                // - Reliable: No port conflicts even if previous cleanup fails
                // - Scalable: Can grade hundreds of students without port exhaustion
                int portToUse;
                if (_portAllocator != null)
                {
                    portToUse = _portAllocator.AllocatePort();
                    if (portToUse == -1)
                    {
                        // Port allocation failed (exhausted all ports)
                        var errorMsg = "Failed to allocate port for grading - all ports exhausted";
                        _logger.LogError(errorMsg);
                        _messageLogger?.LogGraderError(errorMsg, student.StudentCode, null);
                        student.Status = GradingStatus.Failed;
                        student.StatusMessage = errorMsg;
                        student.Mark = 0;
                        return;
                    }
                    _logger.LogInfo($"[Port Config] [{student.StudentCode}] Allocated port {portToUse} via PortAllocator (sequential, no reuse)");
                }
                else
                {
                    // Fallback if PortAllocator failed to initialize
                    portToUse = 8000;
                    _logger.LogWarning($"[Port Config] [{student.StudentCode}] PortAllocator not initialized, using fallback port {portToUse}");
                }
                
                _logger.LogInfo($"[Port Config] [{student.StudentCode}] Using port {portToUse} for container, DLL modification, and network monitoring");
                
                // Build Docker configuration from UI config
                // The examiner sets HasClient/HasServer to indicate what the student should provide:
                // - HasClient=true, HasServer=true  → student provides both
                // - HasClient=true, HasServer=false → student provides client, use golden server
                // - HasClient=false, HasServer=true → student provides server, use golden client
                
                _logger.LogInfo($"[Port Config] Creating DockerGradingConfig with CodeContainerInternalPort={portToUse}, CodeContainerHostPort={portToUse}");
                
                var dockerConfig = new SolutionGrader.Core.Services.DockerGradingConfig
                {
                    // Examiner's component requirements
                    HasClient = config.HasClient,
                    HasServer = config.HasServer,
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
                    
                    _messageLogger?.LogInfo(
                        GradingMessageCatalog.Format(GradingMessageCatalog.Info.GradingCompleted, 
                            student.StudentCode, result.TotalMark, result.MaxMark),
                        student.StudentCode);
                }
                else
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = result.ErrorMessage ?? $"Docker grading failed: 0/{result.MaxMark:F2}";
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
                _excelCoordinator?.UpdateStudentCompleted(
                    student.StudentCode, 
                    student.PaperNo, 
                    student.EndTime.Value, 
                    student.Mark, 
                    student.Status);
                
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
            
            var dockerConfig = new SolutionGrader.Core.Services.DockerGradingConfig
            {
                DatabaseContainerName = config.DatabaseContainerName ?? "auto-grading-sqlserver",
                DockerNetwork = config.DockerNetwork ?? "auto-grading-network"
            };
            
            _libGrading.DisposeAllContainers(dockerConfig);
        }

        /// <summary>
        /// Reads the starting port from environment.xlsx in the question-specific test kit folder.
        /// This determines the base port number from which sequential allocation begins.
        /// 
        /// IMPORTANT: This reads from the QUESTION-SPECIFIC test kit folder 
        /// (e.g., C:\Testkit_Q1_PRN222\Q12\environment.xlsx), not the root.
        /// Each question can have its own environment.xlsx with MonitorPort configuration.
        /// </summary>
        /// <param name="testKitPath">Path to question-specific test kit folder containing environment.xlsx</param>
        /// <returns>Starting port number from MonitorPort field, or 0 if not found</returns>
        private int ReadStartingPortFromEnvironmentXlsx(string testKitPath)
        {
            try
            {
                // Look for Environment.xlsx in the question-specific test kit folder
                // Note: The actual file is "Environment.xlsx" with capital E
                var environmentPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (!File.Exists(environmentPath))
                {
                    // Try lowercase as fallback
                    environmentPath = Path.Combine(testKitPath, "environment.xlsx");
                    if (!File.Exists(environmentPath))
                    {
                        _logger.LogWarning($"Environment.xlsx not found at {testKitPath}. Container port will default to 8000.");
                        return 0;
                    }
                }

                _logger.LogInfo($"Reading port configuration from Environment.xlsx: {environmentPath}");

                using (var workbook = new ClosedXML.Excel.XLWorkbook(environmentPath))
                {
                    // Look for "Config" sheet which contains port configuration
                    var worksheet = workbook.Worksheet("Config");
                    if (worksheet == null)
                    {
                        _logger.LogWarning($"'Config' sheet not found in Environment.xlsx at {environmentPath}");
                        return 0;
                    }
                    
                    // Find Code_Container_Host_Port in the Config sheet (column 1 = Key, column 2 = Value)
                    // The actual field names are:
                    // - Code_Container_Internal_Port (port inside container)
                    // - Code_Container_Host_Port (port exposed on host)
                    foreach (var row in worksheet.RowsUsed().Skip(1)) // Skip header row
                    {
                        var keyCell = row.Cell(1).GetString().Trim();
                        
                        // Normalize key by removing underscores and making lowercase for comparison
                        var normalizedKey = keyCell.Replace("_", "").ToLowerInvariant();
                        
                        if (normalizedKey == "codecontainerhostport" || normalizedKey == "codecontainerinternalport")
                        {
                            var valueCell = row.Cell(2);
                            int port = 0;
                            
                            // Try to get as integer first
                            if (valueCell.TryGetValue<int>(out var intValue))
                            {
                                port = intValue;
                            }
                            else
                            {
                                // Fallback to string parsing
                                var valueStr = valueCell.GetString().Trim();
                                int.TryParse(valueStr, out port);
                            }
                            
                            if (port > 0 && port <= 65535)
                            {
                                _logger.LogInfo($"Successfully read {keyCell}={port} from Environment.xlsx. Container will use this port.");
                                return port;
                            }
                        }
                    }
                    
                    _logger.LogWarning($"Code_Container_Host_Port or Code_Container_Internal_Port not found in Environment.xlsx at {environmentPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error reading port from Environment.xlsx: {ex.Message}");
            }

            return 0;
        }
    }
}
