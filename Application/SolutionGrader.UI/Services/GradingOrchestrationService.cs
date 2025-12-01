using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;
using SolutionGrader.Core.Services.Docker;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Abstractions;
using DomainEnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for orchestrating the Docker-based grading workflow.
    /// 
    /// This service manages the high-level grading flow:
    /// 1. Start Network Monitor (outside containers)
    /// 2. Create/start database container
    /// 3. Create/start server container with student code
    /// 4. Create/start client container with student code  
    /// 5. For each test case:
    ///    a. Reset database
    ///    b. Read steps from Detail.xlsx
    ///    c. Execute steps using docker attach/exec
    ///    d. Compare outputs and grade
    ///    e. Log results
    ///    f. Cleanup for next test case
    /// 6. Cleanup containers
    /// 7. Write final results
    /// 
    /// The service separates UI concerns from grading logic,
    /// allowing for both WPF and console-based testing.
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;
        private readonly TestKitDiscoveryService _testKitService;
        private readonly TestKitConfigService _testKitConfigService;
        
        /// <summary>
        /// Event raised when grading starts for a student.
        /// </summary>
        public event EventHandler<StudentSolution>? StudentGradingStarted;

        /// <summary>
        /// Event raised when grading completes for a student.
        /// </summary>
        public event EventHandler<StudentSolution>? StudentGradingCompleted;

        /// <summary>
        /// Event raised when student progress is updated.
        /// </summary>
        public event EventHandler<StudentSolution>? StudentProgressUpdated;

        /// <summary>
        /// Event raised when session state changes.
        /// </summary>
        public event EventHandler<GradingSessionState>? SessionStateChanged;

        public GradingOrchestrationService(ILoggingService logger)
        {
            _logger = logger;
            _testKitService = new TestKitDiscoveryService(logger);
            _testKitConfigService = new TestKitConfigService(logger);
        }

        /// <summary>
        /// Starts grading for a list of students.
        /// </summary>
        /// <param name="students">List of students to grade.</param>
        /// <param name="config">Grading configuration.</param>
        /// <param name="sessionState">Session state to update during grading.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task StartGradingAsync(
            List<StudentSolution> students,
            GradingConfiguration config,
            GradingSessionState sessionState,
            CancellationToken ct = default)
        {
            sessionState.SessionStartTime = DateTime.Now;
            sessionState.IsRunning = true;
            sessionState.TotalStudents = students.Count;
            SessionStateChanged?.Invoke(this, sessionState);

            _logger.LogInfo($"Starting grading for {students.Count} students");

            for (int i = 0; i < students.Count && !ct.IsCancellationRequested; i++)
            {
                var student = students[i];
                sessionState.CurrentStudentIndex = i;
                sessionState.CurrentStudentCode = student.StudentCode;

                // Skip if already successfully graded
                if (student.Status == GradingStatus.Success)
                {
                    sessionState.SuccessCount++;
                    continue;
                }

                // Wait if paused
                while (sessionState.IsPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }

                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await GradeStudentAsync(student, config, sessionState, ct);

                    if (student.Status == GradingStatus.Success)
                        sessionState.SuccessCount++;
                    else if (student.Status == GradingStatus.Failed)
                        sessionState.FailedCount++;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInfo($"Grading cancelled for {student.StudentCode}");
                    student.Status = GradingStatus.Paused;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error grading {student.StudentCode}", ex);
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = ex.Message;
                    sessionState.FailedCount++;
                }

                SessionStateChanged?.Invoke(this, sessionState);
            }

            sessionState.SessionEndTime = DateTime.Now;
            sessionState.IsRunning = false;
            SessionStateChanged?.Invoke(this, sessionState);

            _logger.LogInfo($"Grading completed: {sessionState.SuccessCount} passed, {sessionState.FailedCount} failed");
        }

        /// <summary>
        /// Grades a single student.
        /// This is the main entry point for grading logic.
        /// Uses DockerGradingService for actual Docker-based grading.
        /// </summary>
        private async Task GradeStudentAsync(
            StudentSolution student,
            GradingConfiguration config,
            GradingSessionState sessionState,
            CancellationToken ct)
        {
            student.Status = GradingStatus.InProgress;
            student.StartTime = DateTime.Now;
            student.ProgressPercent = 0;
            StudentGradingStarted?.Invoke(this, student);

            _logger.LogInfo($"Starting grading for {student.StudentCode} (Paper {student.PaperNo})");

            try
            {
                // Step 1: Find test kit for this student's paper (10%)
                _logger.LogInfo("Finding test kit...");
                student.StatusMessage = "Finding test kit...";
                student.ProgressPercent = 10;
                StudentProgressUpdated?.Invoke(this, student);

                var testKitPath = _testKitService.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    throw new InvalidOperationException($"No test kit found for paper {student.PaperNo}");
                }

                var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
                if (testKitConfig == null)
                {
                    throw new InvalidOperationException($"Failed to load test kit configuration from {testKitPath}");
                }

                student.MaxMark = testKitConfig.TotalMaxMark;
                _logger.LogInfo($"Using test kit: {testKitConfig.Name} (Max: {testKitConfig.TotalMaxMark} marks)");

                // Step 2: Setup grading (20%)
                _logger.LogInfo("Setting up Docker containers...");
                student.StatusMessage = "Setting up containers...";
                student.ProgressPercent = 20;
                StudentProgressUpdated?.Invoke(this, student);

                // Build configuration dictionary for DockerGradingService
                var dockerConfig = BuildDockerConfig(student, config, testKitConfig);

                // Create file service and detail log service
                IFileService fileService = new FileService();
                IRunContext runContext = new RunContext();
                IDetailLogService logService = new ExcelDetailLogService(fileService, runContext);

                // Step 3: Execute grading using DockerGradingService (60%)
                _logger.LogInfo("Executing test cases...");
                student.StatusMessage = "Running test cases...";
                student.ProgressPercent = 40;
                StudentProgressUpdated?.Invoke(this, student);

                using var dockerGrading = new DockerGradingService(fileService, logService);
                dockerGrading.LogMessage += (s, msg) =>
                {
                    _logger.LogInfo(msg);
                };

                var resultPath = Path.Combine(config.SaveResultFolderPath, student.PaperNo, "student", student.StudentCode);
                Directory.CreateDirectory(resultPath);

                var marks = await dockerGrading.GradeStudentAsync(
                    student.SolutionPath,
                    testKitPath,
                    resultPath,
                    dockerConfig,
                    ct);

                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                // Step 4: Finalize (10%)
                student.Mark = marks;
                student.Status = marks > 0 ? GradingStatus.Success : GradingStatus.Failed;
                student.ProgressPercent = 100;
                student.EndTime = DateTime.Now;
                student.StatusMessage = $"Completed: {marks}/{student.MaxMark}";

                _logger.LogInfo($"Grading completed for {student.StudentCode}: {student.Mark}/{student.MaxMark}");
            }
            catch (Exception ex)
            {
                student.Status = GradingStatus.Failed;
                student.StatusMessage = ex.Message;
                student.EndTime = DateTime.Now;
                _logger.LogError($"Grading failed for {student.StudentCode}", ex);
                throw;
            }
            finally
            {
                StudentGradingCompleted?.Invoke(this, student);
            }
        }

        /// <summary>
        /// Builds Docker configuration dictionary from student and config.
        /// </summary>
        private Dictionary<string, string> BuildDockerConfig(
            StudentSolution student,
            GradingConfiguration config,
            TestKitConfig testKitConfig)
        {
            var dockerConfig = new Dictionary<string, string>
            {
                // Network and container names
                [DomainEnvConfig.DockerNetwork] = "ag-network",
                [DomainEnvConfig.CodeContainerName] = "ag-server",
                [DomainEnvConfig.GivenConsoleContainerName] = "ag-client",
                [DomainEnvConfig.DatabaseContainerName] = "ag-db",

                // Images
                [DomainEnvConfig.CodeImageName] = "fptuxaes/aes-dotnet8:latest",
                [DomainEnvConfig.GivenConsoleImageName] = "fptuxaes/aes-dotnet8:latest",
                [DomainEnvConfig.DatabaseImageName] = "mcr.microsoft.com/mssql/server:2022-latest",

                // Ports
                [DomainEnvConfig.CodeContainerInternalPort] = "5001",
                [DomainEnvConfig.CodeContainerHostPort] = "5001",
                [DomainEnvConfig.DatabaseContainerInternalPort] = "1433",
                [DomainEnvConfig.DatabaseContainerHostPort] = "1433",

                // Database credentials
                [DomainEnvConfig.DatabaseUsername] = "SA",
                [DomainEnvConfig.DatabasePassword] = "YourStrong@Passw0rd",

                // Student-specific paths
                [DomainEnvConfig.CodeFilePath] = student.ServerPath ?? "",
                [DomainEnvConfig.GivenConsolePath] = student.ClientPath ?? "",
                [DomainEnvConfig.StudentQuestionName] = Path.GetFileName(student.ServerPath ?? config.ServerProjectName ?? "Server"),
                [DomainEnvConfig.GivenConsoleAppName] = Path.GetFileName(student.ClientPath ?? config.ClientProjectName ?? "Client"),

                // Docker paths for DLLs
                [DomainEnvConfig.DockerServerPath] = $"/apps/{Path.GetFileName(student.ServerPath ?? "Server")}/{Path.GetFileName(student.ServerPath ?? "Server")}.dll",
                [DomainEnvConfig.DockerClientPath] = $"/apps/{Path.GetFileName(student.ClientPath ?? "Client")}/{Path.GetFileName(student.ClientPath ?? "Client")}.dll"
            };

            // Add test kit configuration
            if (testKitConfig.EnvironmentConfig != null)
            {
                foreach (var kvp in testKitConfig.EnvironmentConfig)
                {
                    if (!dockerConfig.ContainsKey(kvp.Key))
                    {
                        dockerConfig[kvp.Key] = kvp.Value;
                    }
                }
            }

            return dockerConfig;
        }

        /// <summary>
        /// Pauses the current grading session.
        /// </summary>
        public void Pause(GradingSessionState sessionState)
        {
            sessionState.IsPaused = true;
            _logger.LogInfo("Grading paused");
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Pauses grading (alias for Pause).
        /// </summary>
        public void PauseGrading(GradingSessionState sessionState) => Pause(sessionState);

        /// <summary>
        /// Resumes a paused grading session.
        /// </summary>
        public void Resume(GradingSessionState sessionState)
        {
            sessionState.IsPaused = false;
            _logger.LogInfo("Grading resumed");
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Resumes grading (alias for Resume).
        /// </summary>
        public void ResumeGrading(GradingSessionState sessionState) => Resume(sessionState);

        /// <summary>
        /// Resets all student statuses to Not_Run.
        /// </summary>
        public void ResetAllStatuses(List<StudentSolution> students, GradingSessionState sessionState)
        {
            foreach (var student in students)
            {
                DisposeStudent(student);
            }
            sessionState.Reset();
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count;
            SessionStateChanged?.Invoke(this, sessionState);
            _logger.LogInfo("All student statuses reset");
        }

        /// <summary>
        /// Resets a student's grading state.
        /// </summary>
        public void DisposeStudent(StudentSolution student)
        {
            student.Status = GradingStatus.Not_Run;
            student.Mark = 0;
            student.StartTime = null;
            student.EndTime = null;
            student.StatusMessage = null;
            student.ProgressPercent = 0;
        }

        /// <summary>
        /// Discovers students in the submit folder.
        /// Uses StudentDiscoveryService to find student submissions.
        /// </summary>
        public List<StudentSolution> DiscoverStudents(GradingConfiguration config)
        {
            var discoveryService = new StudentDiscoveryService(_logger);
            return discoveryService.DiscoverStudents(config.SubmitFolderPath, config);
        }

        #region Validation Methods

        /// <summary>
        /// Validates the grading configuration before allowing user to proceed.
        /// Returns a list of validation errors; empty list means valid.
        /// 
        /// Validation rules:
        /// - If no test case detected → cannot run test
        /// - If no point allocation → cannot run test
        /// - If no testkit for a paper → cannot run test for that paper
        /// - If testkit has no mapping → user cannot proceed to next screen
        /// </summary>
        public List<string> ValidateConfiguration(GradingConfiguration config)
        {
            var errors = new List<string>();

            // Validate paths exist
            if (string.IsNullOrEmpty(config.SubmitFolderPath) || !Directory.Exists(config.SubmitFolderPath))
            {
                errors.Add("Submit folder path is invalid or does not exist.");
            }

            if (string.IsNullOrEmpty(config.TestKitFolderPath) || !Directory.Exists(config.TestKitFolderPath))
            {
                errors.Add("TestKit folder path is invalid or does not exist.");
            }

            if (string.IsNullOrEmpty(config.SaveResultFolderPath))
            {
                errors.Add("Save result folder path is required.");
            }

            // Validate project names are provided when needed
            if (config.HasClient && string.IsNullOrEmpty(config.ClientProjectName))
            {
                errors.Add("Client is enabled but client project name is not provided.");
            }

            if (config.HasServer && string.IsNullOrEmpty(config.ServerProjectName))
            {
                errors.Add("Server is enabled but server project name is not provided.");
            }

            if (!config.HasClient && !config.HasServer)
            {
                errors.Add("At least one of Client or Server must be enabled.");
            }

            return errors;
        }

        /// <summary>
        /// Validates the test kit mapping for available papers.
        /// If testkit has no mapping, user cannot proceed to next screen.
        /// Returns papers without mapping.
        /// </summary>
        public List<string> ValidateTestKitMapping(string testKitFolderPath, IEnumerable<string> paperNumbers)
        {
            var papersWithoutMapping = new List<string>();

            foreach (var paperNo in paperNumbers)
            {
                var testKitPath = _testKitService.GetTestKitForPaper(testKitFolderPath, paperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    papersWithoutMapping.Add(paperNo);
                }
            }

            return papersWithoutMapping;
        }

        /// <summary>
        /// Validates a specific test kit has test cases and point allocation.
        /// If no test case detected → cannot run test.
        /// If no point allocation → cannot run test.
        /// </summary>
        public (bool IsValid, string ErrorMessage) ValidateTestKit(string testKitPath)
        {
            if (string.IsNullOrEmpty(testKitPath) || !Directory.Exists(testKitPath))
            {
                return (false, "Test kit path is invalid or does not exist.");
            }

            var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
            
            if (testKitConfig == null)
            {
                return (false, "Failed to load test kit configuration.");
            }

            // Check for test cases
            if (testKitConfig.TestCases == null || testKitConfig.TestCases.Count == 0)
            {
                return (false, "No test cases detected in test kit. Cannot run grading.");
            }

            // Check for point allocation
            if (testKitConfig.TotalMaxMark <= 0)
            {
                return (false, "No point allocation found in test kit. Cannot run grading.");
            }

            // Verify each test case has a valid mark
            foreach (var tc in testKitConfig.TestCases)
            {
                if (tc.MaxMark <= 0)
                {
                    return (false, $"Test case '{tc.Name}' has no point allocation. Cannot run grading.");
                }
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// Validates all configuration and test kits for the given students.
        /// Returns a comprehensive validation result.
        /// </summary>
        public GradingValidationResult ValidateGradingSetup(
            GradingConfiguration config,
            List<StudentSolution> students)
        {
            var result = new GradingValidationResult();

            // Step 1: Validate basic configuration
            var configErrors = ValidateConfiguration(config);
            result.ConfigurationErrors.AddRange(configErrors);

            if (configErrors.Count > 0)
            {
                result.CanProceed = false;
                return result;
            }

            // Step 2: Get unique papers from students
            var papers = new HashSet<string>(students.Select(s => s.PaperNo));

            // Step 3: Validate mapping for each paper
            var papersWithoutMapping = ValidateTestKitMapping(config.TestKitFolderPath, papers);
            result.PapersWithoutMapping.AddRange(papersWithoutMapping);

            if (papersWithoutMapping.Count == papers.Count)
            {
                // No papers have mapping - cannot proceed
                result.CanProceed = false;
                result.ConfigurationErrors.Add("No papers have valid test kit mapping. Cannot proceed.");
                return result;
            }

            // Step 4: Validate each mapped test kit
            foreach (var paperNo in papers.Except(papersWithoutMapping))
            {
                var testKitPath = _testKitService.GetTestKitForPaper(config.TestKitFolderPath, paperNo);
                if (!string.IsNullOrEmpty(testKitPath))
                {
                    var (isValid, errorMessage) = ValidateTestKit(testKitPath);
                    if (!isValid)
                    {
                        result.TestKitErrors[paperNo] = errorMessage;
                    }
                }
            }

            // Step 5: Determine which students can be graded
            foreach (var student in students)
            {
                if (papersWithoutMapping.Contains(student.PaperNo))
                {
                    result.StudentsWithoutTestKit.Add(student.StudentCode);
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = $"No test kit mapping for paper {student.PaperNo}";
                }
                else if (result.TestKitErrors.ContainsKey(student.PaperNo))
                {
                    result.StudentsWithoutTestKit.Add(student.StudentCode);
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = result.TestKitErrors[student.PaperNo];
                }
            }

            // Can proceed if at least some students can be graded
            result.CanProceed = students.Count > result.StudentsWithoutTestKit.Count;

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Result of grading setup validation.
    /// </summary>
    public class GradingValidationResult
    {
        /// <summary>
        /// Whether user can proceed to grading screen.
        /// </summary>
        public bool CanProceed { get; set; } = true;

        /// <summary>
        /// Configuration-level errors that prevent grading.
        /// </summary>
        public List<string> ConfigurationErrors { get; } = new List<string>();

        /// <summary>
        /// Papers that have no test kit mapping.
        /// </summary>
        public List<string> PapersWithoutMapping { get; } = new List<string>();

        /// <summary>
        /// Test kit errors per paper (no test cases, no point allocation).
        /// </summary>
        public Dictionary<string, string> TestKitErrors { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Students who cannot be graded due to missing/invalid test kit.
        /// </summary>
        public List<string> StudentsWithoutTestKit { get; } = new List<string>();

        /// <summary>
        /// Gets a human-readable summary of validation issues.
        /// </summary>
        public string GetSummary()
        {
            var lines = new List<string>();

            if (ConfigurationErrors.Count > 0)
            {
                lines.Add("Configuration Errors:");
                lines.AddRange(ConfigurationErrors.Select(e => $"  - {e}"));
            }

            if (PapersWithoutMapping.Count > 0)
            {
                lines.Add($"Papers without test kit mapping: {string.Join(", ", PapersWithoutMapping)}");
            }

            foreach (var kvp in TestKitErrors)
            {
                lines.Add($"Paper {kvp.Key}: {kvp.Value}");
            }

            if (StudentsWithoutTestKit.Count > 0)
            {
                lines.Add($"Students that cannot be graded: {StudentsWithoutTestKit.Count}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
