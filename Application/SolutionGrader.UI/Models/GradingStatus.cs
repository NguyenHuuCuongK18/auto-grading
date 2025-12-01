namespace SolutionGrader.UI.Models;

/// <summary>
/// Represents the grading status of a student submission.
/// </summary>
public enum GradingStatus
{
    /// <summary>
    /// Grading has not been started.
    /// </summary>
    Not_Run,
    
    /// <summary>
    /// Grading is currently in progress.
    /// </summary>
    InProgress,
    
    /// <summary>
    /// Grading has been paused.
    /// </summary>
    Paused,
    
    /// <summary>
    /// Grading completed successfully.
    /// </summary>
    Success,
    
    /// <summary>
    /// Grading failed with an error.
    /// </summary>
    Failed,
    
    /// <summary>
    /// Resources have been disposed/cleaned up.
    /// </summary>
    Disposed
}
