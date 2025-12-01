using System.IO;
using System.Text;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Logging service implementation that writes logs to file and raises events.
/// </summary>
public class LoggingService : ILoggingService
{
    private readonly string _logDirectory;
    private readonly StringBuilder _logBuffer = new();
    private string? _currentStudentCode;
    private string? _currentPaperNo;
    private bool _disposed;
    
    /// <summary>
    /// Event raised when a log entry is added.
    /// </summary>
    public event EventHandler<LogEventArgs>? LogAdded;
    
    public LoggingService(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }
    
    /// <summary>
    /// Sets the current student context for logging.
    /// </summary>
    public void SetStudentContext(string? studentCode, string? paperNo = null)
    {
        _currentStudentCode = studentCode;
        _currentPaperNo = paperNo;
    }
    
    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public void LogInfo(string message)
    {
        Log("Info", message);
    }
    
    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public void LogWarning(string message)
    {
        Log("Warning", message);
    }
    
    /// <summary>
    /// Logs an error message.
    /// </summary>
    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
        Log("Error", fullMessage);
    }
    
    private void Log(string level, string message)
    {
        var timestamp = DateTime.Now;
        var logEntry = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        
        lock (_logBuffer)
        {
            _logBuffer.AppendLine(logEntry);
        }
        
        // Write to file if student context is set
        if (!string.IsNullOrEmpty(_currentStudentCode))
        {
            try
            {
                // Organize logs by paper number
                var studentLogDir = !string.IsNullOrEmpty(_currentPaperNo)
                    ? Path.Combine(_logDirectory, _currentPaperNo, $"Log_{_currentStudentCode}_{DateTime.Now:yyyyMMdd}")
                    : Path.Combine(_logDirectory, $"Log_{_currentStudentCode}_{DateTime.Now:yyyyMMdd}");
                
                Directory.CreateDirectory(studentLogDir);
                var logFile = Path.Combine(studentLogDir, "grading.log");
                File.AppendAllText(logFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Ignore file write errors
            }
        }
        
        // Raise event
        LogAdded?.Invoke(this, new LogEventArgs
        {
            Message = message,
            Level = level,
            Timestamp = timestamp
        });
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
