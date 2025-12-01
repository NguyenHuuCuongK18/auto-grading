namespace SolutionGrader.UI.Models;

/// <summary>
/// Tracks the state of a grading session.
/// </summary>
public class GradingSessionState
{
    /// <summary>
    /// Gets or sets the total number of students.
    /// </summary>
    public int TotalStudents { get; set; }
    
    /// <summary>
    /// Gets or sets the number of students successfully graded.
    /// </summary>
    public int SuccessCount { get; set; }
    
    /// <summary>
    /// Gets or sets the number of students that failed grading.
    /// </summary>
    public int FailedCount { get; set; }
    
    /// <summary>
    /// Gets or sets the number of students not yet run.
    /// </summary>
    public int NotRunCount { get; set; }
    
    /// <summary>
    /// Gets or sets the current student index being graded.
    /// </summary>
    public int CurrentIndex { get; set; }
    
    /// <summary>
    /// Gets or sets whether the session is currently running.
    /// </summary>
    public bool IsRunning { get; set; }
    
    /// <summary>
    /// Gets or sets whether the session is paused.
    /// </summary>
    public bool IsPaused { get; set; }
    
    /// <summary>
    /// Gets or sets the session start time.
    /// </summary>
    public DateTime? StartTime { get; set; }
    
    /// <summary>
    /// Gets or sets the session end time.
    /// </summary>
    public DateTime? EndTime { get; set; }
    
    /// <summary>
    /// Gets whether the session can be paused.
    /// </summary>
    public bool CanPause => IsRunning && !IsPaused;
    
    /// <summary>
    /// Gets the percentage of completion.
    /// </summary>
    public int ProgressPercent => TotalStudents > 0 
        ? (int)((double)(SuccessCount + FailedCount) / TotalStudents * 100) 
        : 0;
}
