using System;

namespace SolutionGrader.UI.Services
{
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
}
