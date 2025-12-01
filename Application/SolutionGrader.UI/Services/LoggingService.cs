using System;
using System.IO;
using System.Text;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Event arguments for log events.
    /// </summary>
    public class LogEventArgs : EventArgs
    {
        /// <summary>Timestamp of the log entry</summary>
        public DateTime Timestamp { get; init; }
        
        /// <summary>Log level (INFO, WARN, ERROR, DEBUG)</summary>
        public string Level { get; init; } = "INFO";
        
        /// <summary>Log message content</summary>
        public string Message { get; init; } = string.Empty;
        
        /// <summary>Optional exception associated with the log entry</summary>
        public Exception? Exception { get; init; }
        
        /// <summary>Student code context, if any</summary>
        public string? StudentCode { get; init; }
        
        /// <summary>Paper number context, if any</summary>
        public string? PaperNo { get; init; }
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
        /// Sets the current student context for logging.
        /// When set, logs are written to paper-organized paths.
        /// </summary>
        void SetStudentContext(string? studentCode, string? paperNo = null);
        
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? ex = null);
        void LogDebug(string message);
    }

    /// <summary>
    /// Logging service for grading operations.
    /// 
    /// Logs are organized by paper number and student code:
    /// - {SaveResultPath}/{PaperNo}/Log_{StudentCode}_{Date}.txt - Per-student logs
    /// - {SaveResultPath}/Session_{Date}.txt - Session-level logs
    /// 
    /// Implements ILoggingService for use by grading services.
    /// Raises LogAdded events for UI display.
    /// </summary>
    public class LoggingService : ILoggingService
    {
        private readonly string _resultPath;
        private readonly StringBuilder _sessionLog = new();
        private StreamWriter? _fileWriter;
        
        private string? _currentStudentCode;
        private string? _currentPaperNo;
        private string? _currentStudentLogPath;

        public event EventHandler<LogEventArgs>? LogAdded;

        /// <summary>
        /// Creates a new logging service.
        /// </summary>
        /// <param name="resultPath">Root path for saving log files.</param>
        public LoggingService(string resultPath)
        {
            _resultPath = resultPath;
            
            // Ensure result path exists
            if (!string.IsNullOrEmpty(resultPath) && !Directory.Exists(resultPath))
            {
                Directory.CreateDirectory(resultPath);
            }
        }

        /// <summary>
        /// Sets the current student context for logging.
        /// When set, logs are written to student-specific files organized by paper.
        /// </summary>
        /// <param name="studentCode">Student code, or null to clear context.</param>
        /// <param name="paperNo">Paper number for organizing logs.</param>
        public void SetStudentContext(string? studentCode, string? paperNo = null)
        {
            // Close previous student log if exists
            CloseStudentLog();

            _currentStudentCode = studentCode;
            _currentPaperNo = paperNo;

            if (!string.IsNullOrEmpty(studentCode))
            {
                // Create student log file path organized by paper
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string logFolder;
                
                if (!string.IsNullOrEmpty(paperNo))
                {
                    // Organize by paper: {ResultPath}/{PaperNo}/student/{StudentCode}/
                    logFolder = Path.Combine(_resultPath, paperNo, "student", studentCode);
                }
                else
                {
                    // Legacy path without paper organization
                    logFolder = Path.Combine(_resultPath, "student", studentCode);
                }
                
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                }

                _currentStudentLogPath = Path.Combine(logFolder, $"Log_{studentCode}_{timestamp}.txt");
                _fileWriter = new StreamWriter(_currentStudentLogPath, append: true, encoding: Encoding.UTF8);
                _fileWriter.AutoFlush = true;
            }
        }

        /// <summary>
        /// Logs an informational message.
        /// </summary>
        public void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// Logs an error message with optional exception.
        /// </summary>
        public void LogError(string message, Exception? ex = null)
        {
            var fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
            WriteLog("ERROR", fullMessage);
            
            if (ex != null)
            {
                WriteLog("ERROR", $"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public void LogDebug(string message)
        {
            WriteLog("DEBUG", message);
        }

        private void WriteLog(string level, string message)
        {
            var timestamp = DateTime.Now;
            var logLine = $"[{timestamp:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

            // Write to session log buffer
            _sessionLog.AppendLine(logLine);

            // Write to student-specific file if context is set
            _fileWriter?.WriteLine(logLine);

            // Write to console for debugging
            Console.WriteLine(logLine);

            // Raise event for UI
            LogAdded?.Invoke(this, new LogEventArgs
            {
                Timestamp = timestamp,
                Level = level,
                Message = message,
                StudentCode = _currentStudentCode,
                PaperNo = _currentPaperNo
            });
        }

        private void CloseStudentLog()
        {
            if (_fileWriter != null)
            {
                _fileWriter.Flush();
                _fileWriter.Close();
                _fileWriter.Dispose();
                _fileWriter = null;
            }
            _currentStudentLogPath = null;
        }

        /// <summary>
        /// Saves the session log to a file.
        /// </summary>
        public void SaveSessionLog()
        {
            if (string.IsNullOrEmpty(_resultPath)) return;

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var sessionLogPath = Path.Combine(_resultPath, $"Session_{timestamp}.txt");
            File.WriteAllText(sessionLogPath, _sessionLog.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Gets the current session log content.
        /// </summary>
        public string GetSessionLog()
        {
            return _sessionLog.ToString();
        }

        /// <summary>
        /// Disposes the logging service and closes all open files.
        /// </summary>
        public void Dispose()
        {
            CloseStudentLog();
            SaveSessionLog();
        }
    }
}
