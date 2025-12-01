using System.IO;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Service that orchestrates the grading process for multiple students.
/// </summary>
public class GradingOrchestrationService
{
    private readonly ILoggingService _logger;
    private readonly StudentDiscoveryService _studentDiscovery;
    
    /// <summary>
    /// Event raised when grading starts for a student.
    /// </summary>
    public event EventHandler<StudentSolution>? StudentGradingStarted;
    
    /// <summary>
    /// Event raised when grading completes for a student.
    /// </summary>
    public event EventHandler<StudentSolution>? StudentGradingCompleted;
    
    /// <summary>
    /// Event raised when progress is updated for a student.
    /// </summary>
    public event EventHandler<StudentSolution>? StudentProgressUpdated;
    
    /// <summary>
    /// Event raised when session state changes.
    /// </summary>
    public event EventHandler<GradingSessionState>? SessionStateChanged;
    
    public GradingOrchestrationService(ILoggingService logger)
    {
        _logger = logger;
        _studentDiscovery = new StudentDiscoveryService(logger);
    }
    
    /// <summary>
    /// Discovers all students from the submit folder.
    /// </summary>
    /// <param name="config">Grading configuration.</param>
    /// <returns>List of student solutions.</returns>
    public List<StudentSolution> DiscoverStudents(GradingConfiguration config)
    {
        return _studentDiscovery.DiscoverStudents(config.SubmitFolderPath, config);
    }
    
    /// <summary>
    /// Starts grading a list of students.
    /// </summary>
    /// <param name="students">List of students to grade.</param>
    /// <param name="config">Grading configuration.</param>
    /// <param name="sessionState">Session state tracker.</param>
    /// <returns>Task that completes when all students are graded.</returns>
    public async Task StartGradingAsync(
        List<StudentSolution> students,
        GradingConfiguration config,
        GradingSessionState sessionState)
    {
        sessionState.TotalStudents = students.Count;
        sessionState.IsRunning = true;
        sessionState.StartTime = DateTime.Now;
        SessionStateChanged?.Invoke(this, sessionState);
        
        foreach (var student in students)
        {
            if (!sessionState.IsRunning)
                break;
            
            // Wait if paused
            while (sessionState.IsPaused)
            {
                await Task.Delay(500);
            }
            
            await GradeStudentAsync(student, config, sessionState);
            sessionState.CurrentIndex++;
        }
        
        sessionState.IsRunning = false;
        sessionState.EndTime = DateTime.Now;
        SessionStateChanged?.Invoke(this, sessionState);
    }
    
    /// <summary>
    /// Grades a single student.
    /// </summary>
    private async Task GradeStudentAsync(
        StudentSolution student,
        GradingConfiguration config,
        GradingSessionState sessionState)
    {
        student.Status = GradingStatus.InProgress;
        student.ProgressPercent = 0;
        StudentGradingStarted?.Invoke(this, student);
        
        try
        {
            _logger.LogInfo($"Starting grading for {student.StudentCode}");
            
            // Simulate grading steps (to be replaced with actual grading logic)
            student.ProgressPercent = 25;
            StudentProgressUpdated?.Invoke(this, student);
            await Task.Delay(100); // Placeholder for actual work
            
            student.ProgressPercent = 50;
            StudentProgressUpdated?.Invoke(this, student);
            await Task.Delay(100);
            
            student.ProgressPercent = 75;
            StudentProgressUpdated?.Invoke(this, student);
            await Task.Delay(100);
            
            // Placeholder - actual grading would go here
            // For now, just mark as success
            student.Status = GradingStatus.Success;
            student.Mark = student.MaxMark * 0.8; // Placeholder mark
            student.ProgressPercent = 100;
            sessionState.SuccessCount++;
            sessionState.NotRunCount--;
            
            _logger.LogInfo($"Completed grading for {student.StudentCode}: {student.Mark}/{student.MaxMark}");
        }
        catch (Exception ex)
        {
            student.Status = GradingStatus.Failed;
            student.StatusMessage = ex.Message;
            sessionState.FailedCount++;
            sessionState.NotRunCount--;
            _logger.LogError($"Grading failed for {student.StudentCode}", ex);
        }
        
        StudentGradingCompleted?.Invoke(this, student);
        SessionStateChanged?.Invoke(this, sessionState);
    }
    
    /// <summary>
    /// Pauses the current grading session.
    /// </summary>
    public void PauseGrading(GradingSessionState sessionState)
    {
        if (sessionState.IsRunning)
        {
            sessionState.IsPaused = true;
            _logger.LogInfo("Grading paused");
            SessionStateChanged?.Invoke(this, sessionState);
        }
    }
    
    /// <summary>
    /// Resumes a paused grading session.
    /// </summary>
    public void ResumeGrading(GradingSessionState sessionState)
    {
        if (sessionState.IsPaused)
        {
            sessionState.IsPaused = false;
            _logger.LogInfo("Grading resumed");
            SessionStateChanged?.Invoke(this, sessionState);
        }
    }
    
    /// <summary>
    /// Resets all student statuses.
    /// </summary>
    public void ResetAllStatuses(List<StudentSolution> students, GradingSessionState sessionState)
    {
        foreach (var student in students)
        {
            ResetStudent(student);
        }
        
        sessionState.SuccessCount = 0;
        sessionState.FailedCount = 0;
        sessionState.NotRunCount = students.Count;
        sessionState.CurrentIndex = 0;
        
        _logger.LogInfo("All statuses reset");
        SessionStateChanged?.Invoke(this, sessionState);
    }
    
    /// <summary>
    /// Disposes/resets a single student.
    /// </summary>
    public void DisposeStudent(StudentSolution student)
    {
        ResetStudent(student);
        _logger.LogInfo($"Reset student: {student.StudentCode}");
    }
    
    /// <summary>
    /// Resets a student's grading status.
    /// </summary>
    private void ResetStudent(StudentSolution student)
    {
        student.Status = GradingStatus.Not_Run;
        student.Mark = 0;
        student.StartTime = null;
        student.EndTime = null;
        student.StatusMessage = null;
        student.ProgressPercent = 0;
    }
}
