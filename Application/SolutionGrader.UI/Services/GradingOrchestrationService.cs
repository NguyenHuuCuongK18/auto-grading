using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;

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
                // Note: The actual Docker-based grading implementation will be added here.
                // For now, this is a placeholder that simulates the grading process.
                // The implementation will use:
                // - DockerContainerManager for container lifecycle
                // - DockerConsoleReader for reading console output
                // - Detail.xlsx parser from SolutionGrader.Core
                // - NetworkMonitorService for traffic sniffing

                // Step 1: Setup containers (20%)
                _logger.LogInfo("Setting up Docker containers...");
                student.StatusMessage = "Setting up containers...";
                student.ProgressPercent = 10;
                StudentProgressUpdated?.Invoke(this, student);
                await Task.Delay(500, ct); // Placeholder for actual setup

                // Step 2: Execute test cases (60%)
                _logger.LogInfo("Executing test cases...");
                student.StatusMessage = "Running test cases...";
                student.ProgressPercent = 30;
                StudentProgressUpdated?.Invoke(this, student);
                
                // Simulated grading - actual implementation will iterate through Detail.xlsx steps
                double totalMark = 0;
                double maxMark = student.MaxMark;
                
                // TODO: Implement actual grading logic
                // For now, use a simple pass-through that marks as successful
                student.ProgressPercent = 70;
                StudentProgressUpdated?.Invoke(this, student);
                await Task.Delay(500, ct);

                // Step 3: Cleanup (20%)
                _logger.LogInfo("Cleaning up containers...");
                student.StatusMessage = "Cleaning up...";
                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);
                await Task.Delay(300, ct);

                // Set final status
                student.Mark = totalMark;
                student.Status = GradingStatus.Success; // Will be set properly by actual implementation
                student.ProgressPercent = 100;
                student.EndTime = DateTime.Now;
                student.StatusMessage = "Completed";

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
    }
}
