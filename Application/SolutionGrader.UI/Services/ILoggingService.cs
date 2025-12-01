namespace SolutionGrader.UI.Services;

/// <summary>
/// Event arguments for log events.
/// </summary>
public class LogEventArgs : EventArgs
{
    /// <summary>
    /// Gets or sets the log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the log level (Info, Warning, Error).
    /// </summary>
    public string Level { get; set; } = "Info";
    
    /// <summary>
    /// Gets or sets the timestamp of the log entry.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// Interface for logging services.
/// </summary>
public interface ILoggingService : IDisposable
{
    /// <summary>
    /// Event raised when a log entry is added.
    /// </summary>
    event EventHandler<LogEventArgs>? LogAdded;
    
    /// <summary>
    /// Logs an informational message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void LogInfo(string message);
    
    /// <summary>
    /// Logs a warning message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void LogWarning(string message);
    
    /// <summary>
    /// Logs an error message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">Optional exception.</param>
    void LogError(string message, Exception? ex = null);
    
    /// <summary>
    /// Sets the current student context for logging.
    /// </summary>
    /// <param name="studentCode">The student code, or null to clear context.</param>
    /// <param name="paperNo">The paper number (optional).</param>
    void SetStudentContext(string? studentCode, string? paperNo = null);
}
