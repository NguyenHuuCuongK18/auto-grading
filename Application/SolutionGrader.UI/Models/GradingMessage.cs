using System;
using System.Collections.Generic;
using SolutionGrader.UI.Services;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Represents a structured grading message with severity and context.
    /// Used for detailed logging during the grading process.
    /// </summary>
    public class GradingMessage
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string StudentCode { get; set; } = "";
        public string? TestCase { get; set; }
        public int? Stage { get; set; }
        public GradingMessageCatalog.ErrorSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
        public Exception? Exception { get; set; }

        public override string ToString()
        {
            var parts = new List<string>
            {
                $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}]",
                $"[{Severity}]"
            };

            if (!string.IsNullOrEmpty(StudentCode))
                parts.Add($"[{StudentCode}]");

            if (!string.IsNullOrEmpty(TestCase))
                parts.Add($"[{TestCase}]");

            if (Stage.HasValue)
                parts.Add($"[Stage {Stage}]");

            parts.Add(Message);

            return string.Join(" ", parts);
        }
    }
}
