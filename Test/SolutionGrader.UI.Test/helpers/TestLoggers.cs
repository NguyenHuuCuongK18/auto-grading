using System;
using System.Text;
using SolutionGrader.UI.Services;

namespace SolutionGrader.UI.Test.helpers
{
    // Captures all logs for assertions
    public sealed class TestLogger : ILoggingService
    {
        private readonly StringBuilder _sb = new();
        public event EventHandler<LogEventArgs>? LogAdded;
        public void LogInfo(string message) { _sb.AppendLine(message); LogAdded?.Invoke(this, new LogEventArgs { Message = message, Level = LogLevel.Info, Timestamp = DateTime.Now }); }
        public void LogDebug(string message) { _sb.AppendLine(message); }
        public void LogWarning(string message) { _sb.AppendLine(message); }
        public void LogError(string message) { _sb.AppendLine(message); }
        public void LogError(string message, Exception ex) { _sb.AppendLine(message + ": " + ex.Message); }
        public void SetStudentContext(string? studentCode) { }
        public void SetStudentContext(string? studentCode, string? paperNo) { }
        public string GetAllLogs() => _sb.ToString();
        public string GetStudentResultFolder(string studentCode, string? paperNo = null) => string.Empty;
    }

    // Throws OperationCanceledException when logging the saved-path message inside try block
    public sealed class ThrowingLoggerCanceled : ILoggingService
    {
        public event EventHandler<LogEventArgs>? LogAdded;
        private static bool IsTrigger(string message) => message.Contains("Results will be saved to:");
        public void LogInfo(string message)
        {
            if (IsTrigger(message)) throw new OperationCanceledException("cancel-requested");
        }
        public void LogDebug(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
        public void SetStudentContext(string? studentCode) { }
        public void SetStudentContext(string? studentCode, string? paperNo) { }
        public string GetAllLogs() => string.Empty;
        public string GetStudentResultFolder(string studentCode, string? paperNo = null) => string.Empty;
    }

    // Throws a general exception with a provided message when saved-path message is logged
    public sealed class ThrowingLoggerGeneral : ILoggingService
    {
        private readonly string _error;
        public ThrowingLoggerGeneral(string error) { _error = error; }
        public event EventHandler<LogEventArgs>? LogAdded;
        private static bool IsTrigger(string message) => message.Contains("Results will be saved to:");
        public void LogInfo(string message)
        {
            if (IsTrigger(message)) throw new InvalidOperationException(_error);
        }
        public void LogDebug(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception ex) { }
        public void SetStudentContext(string? studentCode) { }
        public void SetStudentContext(string? studentCode, string? paperNo) { }
        public string GetAllLogs() => string.Empty;
        public string GetStudentResultFolder(string studentCode, string? paperNo = null) => string.Empty;
    }
}
