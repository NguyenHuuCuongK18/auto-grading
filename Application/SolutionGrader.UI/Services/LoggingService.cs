using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Interface for logging service used throughout the UI application.
    /// </summary>
    public interface ILoggingService
    {
        void LogInfo(string message);
        void LogDebug(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(string message, Exception ex);
        
        /// <summary>
        /// Sets the current student context for logging.
        /// Logs will be redirected to the student-specific log folder.
        /// Format: Log_{StudentCode}_{Date}
        /// </summary>
        void SetStudentContext(string? studentCode);
        
        /// <summary>
        /// Sets the current student context for logging with paper number organization.
        /// Logs will be redirected to the paper-organized student-specific log folder.
        /// Format: {PaperNo}/Log_{StudentCode}_{Date}
        /// </summary>
        /// <param name="studentCode">Student code identifier</param>
        /// <param name="paperNo">Paper number for organizing logs (e.g., "1", "2")</param>
        void SetStudentContext(string? studentCode, string? paperNo);
        
        /// <summary>
        /// Gets all logs for display in the UI.
        /// </summary>
        string GetAllLogs();
        
        /// <summary>
        /// Gets the result folder path for a student.
        /// </summary>
        /// <param name="studentCode">Student code identifier</param>
        /// <param name="paperNo">Paper number for organizing (optional)</param>
        /// <returns>Path to the student's result folder</returns>
        string GetStudentResultFolder(string studentCode, string? paperNo = null);
        
        /// <summary>
        /// Event raised when a new log entry is added.
        /// </summary>
        event EventHandler<LogEventArgs>? LogAdded;
    }

    /// <summary>
    /// Event arguments for log entries.
    /// </summary>
    public class LogEventArgs : EventArgs
    {
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? StudentCode { get; set; }
    }

    /// <summary>
    /// Log severity levels.
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Logging service implementation that writes to both console and file.
    /// 
    /// All logs are now consolidated into a single "Logs" folder for easy management.
    /// This addresses the requirement that "logging should be in a single folder so its easy to check".
    /// 
    /// Log structure:
    /// - {SaveFolder}/Logs/System_{DateTime}.log - System-wide logs
    /// - {SaveFolder}/Logs/Log_{StudentCode}_{yyyyMMdd}_{Paper}/grading_{HHmmss}.log - Per-student logs
    /// 
    /// All logs are in one place (Logs folder) making it easy to:
    /// - Review grading session results
    /// - Debug issues across multiple students
    /// - Archive or clean up logs
    /// </summary>
    public class LoggingService : ILoggingService, IDisposable
    {
        private readonly string _baseLogPath;
        private readonly StringBuilder _allLogs = new StringBuilder();
        private readonly object _lock = new object();
        private string? _currentStudentCode;
        private string? _currentPaperNo;
        private StreamWriter? _currentStudentLogWriter;
        private StreamWriter? _systemLogWriter;
        private bool _disposed;

        public event EventHandler<LogEventArgs>? LogAdded;

        /// <summary>
        /// Creates a new logging service.
        /// </summary>
        /// <param name="baseLogPath">Base path for log files (e.g., save results folder)</param>
        public LoggingService(string baseLogPath)
        {
            _baseLogPath = baseLogPath;
            
            // Ensure log directory exists
            var logDir = Path.Combine(_baseLogPath, "Logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // Create system log file with FileShare.ReadWrite to allow deletion while app is running
            var systemLogPath = Path.Combine(logDir, $"System_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _systemLogWriter = new StreamWriter(
                new FileStream(systemLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        /// <inheritdoc/>
        public void SetStudentContext(string? studentCode)
        {
            SetStudentContext(studentCode, null);
        }

        /// <summary>
        /// Sets the current student context for logging with paper number.
        /// All logs are now consolidated into a single "Logs" folder for easy management.
        /// Format: Logs/Log_{StudentCode}_{Date}_{Paper}/grading_{Time}.log
        /// 
        /// This consolidation makes it easier to:
        /// - Find all logs in one place
        /// - Review grading session results
        /// - Debug issues across multiple students/papers
        /// </summary>
        /// <param name="studentCode">Student code identifier</param>
        /// <param name="paperNo">Paper number (included in folder name for context)</param>
        public void SetStudentContext(string? studentCode, string? paperNo)
        {
            lock (_lock)
            {
                // Close previous student log if exists with proper error handling
                try
                {
                    if (_currentStudentLogWriter != null)
                    {
                        _currentStudentLogWriter.Flush();
                        _currentStudentLogWriter.Close();
                        _currentStudentLogWriter.Dispose();
                        _currentStudentLogWriter = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - this is fine
                    _currentStudentLogWriter = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoggingService] Error closing previous student log: {ex.Message}");
                    _currentStudentLogWriter = null;
                }

                _currentStudentCode = studentCode;
                _currentPaperNo = paperNo;

                if (!string.IsNullOrEmpty(studentCode) && !_disposed)
                {
                    try
                    {
                        // All logs are consolidated into a single "Logs" folder
                        // Format: Logs/Log_{StudentCode}_{Date}_{Paper}
                        // This makes it easy to find all logs in one place
                        var logDir = Path.Combine(_baseLogPath, "Logs");
                        if (!Directory.Exists(logDir))
                        {
                            Directory.CreateDirectory(logDir);
                        }
                        
                        // Include paper number in folder name if provided (for context)
                        var folderSuffix = !string.IsNullOrEmpty(paperNo) ? $"_Paper{paperNo}" : "";
                        var studentLogDir = Path.Combine(logDir, $"Log_{studentCode}_{DateTime.Now:yyyyMMdd}{folderSuffix}");

                        if (!Directory.Exists(studentLogDir))
                        {
                            Directory.CreateDirectory(studentLogDir);
                        }

                        // Create student log file with FileShare.ReadWrite to allow deletion while app is running
                        var studentLogPath = Path.Combine(studentLogDir, $"grading_{DateTime.Now:HHmmss}.log");
                        _currentStudentLogWriter = new StreamWriter(
                            new FileStream(studentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                            Encoding.UTF8)
                        {
                            AutoFlush = true
                        };

                        LogInfo($"Started logging for student: {studentCode}{(paperNo != null ? $" (Paper {paperNo})" : "")}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LoggingService] Failed to create student log file: {ex.Message}");
                        _currentStudentLogWriter = null;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void LogInfo(string message) => Log(LogLevel.Info, message);

        /// <inheritdoc/>
        public void LogDebug(string message) => Log(LogLevel.Debug, message);

        /// <inheritdoc/>
        public void LogWarning(string message) => Log(LogLevel.Warning, message);

        /// <inheritdoc/>
        public void LogError(string message) => Log(LogLevel.Error, message);

        /// <inheritdoc/>
        public void LogError(string message, Exception ex)
        {
            Log(LogLevel.Error, $"{message}\nException: {ex.Message}\nStack Trace: {ex.StackTrace}");
        }

        /// <inheritdoc/>
        public string GetAllLogs()
        {
            lock (_lock)
            {
                return _allLogs.ToString();
            }
        }

        private void Log(LogLevel level, string message)
        {
            var timestamp = DateTime.Now;
            var formattedMessage = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

            lock (_lock)
            {
                // Check if disposed before proceeding
                if (_disposed)
                    return;

                // Add to in-memory log
                _allLogs.AppendLine(formattedMessage);

                // Write to system log with disposal guard
                if (_systemLogWriter != null && !_disposed)
                {
                    try
                    {
                        _systemLogWriter.WriteLine(formattedMessage);
                    }
                    catch (ObjectDisposedException)
                    {
                        // StreamWriter was disposed - this can happen during shutdown
                    }
                    catch (IOException)
                    {
                        // File I/O error - ignore to prevent cascading failures
                    }
                }

                // Write to student log if context is set with disposal guard
                if (_currentStudentLogWriter != null && !_disposed)
                {
                    try
                    {
                        _currentStudentLogWriter.WriteLine(formattedMessage);
                    }
                    catch (ObjectDisposedException)
                    {
                        // StreamWriter was disposed - this can happen during shutdown
                    }
                    catch (IOException)
                    {
                        // File I/O error - ignore to prevent cascading failures
                    }
                }

                // Write to console for debugging
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Info => ConsoleColor.White,
                    LogLevel.Warning => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.White
                };
                Console.WriteLine(formattedMessage);
                Console.ForegroundColor = originalColor;
            }

            // Raise event for UI binding (outside lock to prevent deadlocks)
            if (!_disposed)
            {
                LogAdded?.Invoke(this, new LogEventArgs
                {
                    Level = level,
                    Message = message,
                    Timestamp = timestamp,
                    StudentCode = _currentStudentCode
                });
            }
        }

        /// <summary>
        /// Creates a student-specific result folder and returns its path.
        /// Format: {baseLogPath}/{PaperNo}/student/{StudentCode}
        /// This matches the SampleLogging folder structure organized by paper.
        /// </summary>
        /// <param name="studentCode">Student code identifier</param>
        /// <param name="paperNo">Paper number for organizing results (optional)</param>
        public string GetStudentResultFolder(string studentCode, string? paperNo = null)
        {
            string resultDir;
            if (!string.IsNullOrEmpty(paperNo))
            {
                // Organize by paper number: e.g., "1/student/cuongnhhe186494"
                resultDir = Path.Combine(_baseLogPath, paperNo, "student", studentCode);
            }
            else
            {
                // Fallback to current paper context or non-paper-organized structure
                if (!string.IsNullOrEmpty(_currentPaperNo))
                {
                    resultDir = Path.Combine(_baseLogPath, _currentPaperNo, "student", studentCode);
                }
                else
                {
                    resultDir = Path.Combine(_baseLogPath, "student", studentCode);
                }
            }
            
            if (!Directory.Exists(resultDir))
            {
                Directory.CreateDirectory(resultDir);
            }
            return resultDir;
        }

        /// <summary>
        /// Gets the current paper number context.
        /// </summary>
        public string? GetCurrentPaperNo() => _currentPaperNo;

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                
                // Close and dispose student log writer with error handling
                try
                {
                    if (_currentStudentLogWriter != null)
                    {
                        _currentStudentLogWriter.Flush();
                        _currentStudentLogWriter.Close();
                        _currentStudentLogWriter.Dispose();
                        _currentStudentLogWriter = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - this is fine
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoggingService] Error disposing student log writer: {ex.Message}");
                }
                
                // Close and dispose system log writer with error handling
                try
                {
                    if (_systemLogWriter != null)
                    {
                        _systemLogWriter.Flush();
                        _systemLogWriter.Close();
                        _systemLogWriter.Dispose();
                        _systemLogWriter = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - this is fine
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LoggingService] Error disposing system log writer: {ex.Message}");
                }
            }
        }
    }
}
