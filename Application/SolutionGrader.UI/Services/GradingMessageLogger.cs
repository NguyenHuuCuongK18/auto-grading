using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClosedXML.Excel;
using static SolutionGrader.UI.Services.GradingMessageCatalog;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Centralized message logger that captures all grading messages to structured log files.
    /// 
    /// PURPOSE: Creates easily accessible log files with all messages categorized by:
    /// - Student errors (missing DLL, crashes, etc.) - logged but don't abort flow
    /// - Grader errors (Docker issues, network monitor fails, etc.)
    /// - Test kit errors (malformed test kits, missing files, etc.)
    /// - Debug messages
    /// 
    /// LOG OUTPUT:
    /// - GradingMessages.xlsx: Structured Excel file with all messages (easy to filter/search)
    /// - GradingMessages.txt: Text file for quick viewing
    /// 
    /// ERROR HANDLING BEHAVIOR:
    /// - Student errors: Logged, student marked as failed, grading continues for other students
    /// - Grader errors: Logged, may skip current student, session continues
    /// - Test kit errors: Logged, skip affected test case/student
    /// - Critical errors: Logged, may abort entire session
    /// </summary>
    public class GradingMessageLogger : IDisposable
    {
        private readonly string _logFolder;
        private readonly ConcurrentBag<GradingMessage> _messages = new();
        private readonly object _fileLock = new object();
        private StreamWriter? _textLogWriter;
        private bool _disposed = false;

        public GradingMessageLogger(string baseResultPath)
        {
            _logFolder = Path.Combine(baseResultPath, "GradingLogs");
            Directory.CreateDirectory(_logFolder);

            // Create text log file
            var textLogPath = Path.Combine(_logFolder, $"GradingMessages_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            _textLogWriter = new StreamWriter(
                new FileStream(textLogPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8)
            {
                AutoFlush = true
            };

            // Write header
            _textLogWriter.WriteLine("=".PadRight(100, '='));
            _textLogWriter.WriteLine($"GRADING SESSION LOG - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _textLogWriter.WriteLine("=".PadRight(100, '='));
            _textLogWriter.WriteLine();
        }

        /// <summary>
        /// Logs a student error (does NOT abort grading flow).
        /// </summary>
        public void LogStudentError(string studentCode, string message, string? testCase = null, int? stage = null, Exception? ex = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode,
                TestCase = testCase,
                Stage = stage,
                Severity = ErrorSeverity.StudentError,
                Message = message,
                Exception = ex,
                StackTrace = ex?.StackTrace
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Logs a grader system error.
        /// </summary>
        public void LogGraderError(string message, string? studentCode = null, Exception? ex = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode ?? "SYSTEM",
                Severity = ErrorSeverity.GraderError,
                Message = message,
                Exception = ex,
                StackTrace = ex?.StackTrace
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Logs a test kit or test case error.
        /// </summary>
        public void LogTestKitError(string message, string? studentCode = null, string? testCase = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode ?? "TESTKIT",
                TestCase = testCase,
                Severity = ErrorSeverity.TestCaseError,
                Message = message
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public void LogWarning(string message, string? studentCode = null, string? testCase = null, int? stage = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode ?? "SYSTEM",
                TestCase = testCase,
                Stage = stage,
                Severity = ErrorSeverity.Warning,
                Message = message
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Logs an informational message.
        /// </summary>
        public void LogInfo(string message, string? studentCode = null, string? testCase = null, int? stage = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode ?? "SYSTEM",
                TestCase = testCase,
                Stage = stage,
                Severity = ErrorSeverity.Info,
                Message = message
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public void LogDebug(string message, string? studentCode = null)
        {
            var msg = new GradingMessage
            {
                StudentCode = studentCode ?? "DEBUG",
                Severity = ErrorSeverity.Info,
                Message = $"DEBUG: {message}"
            };

            LogMessage(msg);
        }

        /// <summary>
        /// Internal method to log a message to all outputs.
        /// </summary>
        private void LogMessage(GradingMessage msg)
        {
            // Add to in-memory collection
            _messages.Add(msg);

            // Write to console (color-coded)
            WriteToConsole(msg);

            // Write to text log file
            lock (_fileLock)
            {
                _textLogWriter?.WriteLine(msg.ToString());
                if (msg.Exception != null)
                {
                    _textLogWriter?.WriteLine($"  Exception: {msg.Exception.Message}");
                    if (!string.IsNullOrEmpty(msg.StackTrace))
                    {
                        _textLogWriter?.WriteLine($"  Stack Trace:");
                        foreach (var line in msg.StackTrace.Split('\n'))
                        {
                            _textLogWriter?.WriteLine($"    {line.TrimEnd()}");
                        }
                    }
                }
                _textLogWriter?.WriteLine();
            }
        }

        /// <summary>
        /// Writes a message to console with color coding.
        /// </summary>
        private void WriteToConsole(GradingMessage msg)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = msg.Severity switch
            {
                ErrorSeverity.Critical => ConsoleColor.DarkRed,
                ErrorSeverity.GraderError => ConsoleColor.Red,
                ErrorSeverity.TestCaseError => ConsoleColor.Magenta,
                ErrorSeverity.StudentError => ConsoleColor.Yellow,
                ErrorSeverity.Warning => ConsoleColor.DarkYellow,
                ErrorSeverity.Info => ConsoleColor.White,
                _ => ConsoleColor.Gray
            };

            Console.WriteLine(msg.ToString());

            if (msg.Exception != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Exception: {msg.Exception.Message}");
            }

            Console.ForegroundColor = originalColor;
        }

        /// <summary>
        /// Exports all messages to a structured Excel file for easy analysis.
        /// Call this at the end of the grading session.
        /// </summary>
        public void ExportToExcel()
        {
            if (_messages.IsEmpty)
                return;

            lock (_fileLock)
            {
                try
                {
                    var excelPath = Path.Combine(_logFolder, $"GradingMessages_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                    using var workbook = new XLWorkbook();

                    // Create summary sheet
                    CreateSummarySheet(workbook);

                    // Create detailed messages sheet
                    CreateMessagesSheet(workbook);

                    // Create student errors sheet
                    CreateStudentErrorsSheet(workbook);

                    // Create grader errors sheet
                    CreateGraderErrorsSheet(workbook);

                    // Create test kit errors sheet
                    CreateTestKitErrorsSheet(workbook);

                    workbook.SaveAs(excelPath);
                    Console.WriteLine($"[GradingMessageLogger] Exported {_messages.Count} messages to {excelPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GradingMessageLogger] Failed to export to Excel: {ex.Message}");
                }
            }
        }

        private void CreateSummarySheet(XLWorkbook workbook)
        {
            var ws = workbook.Worksheets.Add("Summary");

            // Header
            ws.Cell(1, 1).Value = "GRADING SESSION SUMMARY";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Range(1, 1, 1, 2).Merge();

            int row = 3;
            ws.Cell(row, 1).Value = "Total Messages:";
            ws.Cell(row++, 2).Value = _messages.Count;

            ws.Cell(row, 1).Value = "Student Errors:";
            ws.Cell(row++, 2).Value = _messages.Count(m => m.Severity == ErrorSeverity.StudentError);

            ws.Cell(row, 1).Value = "Grader Errors:";
            ws.Cell(row++, 2).Value = _messages.Count(m => m.Severity == ErrorSeverity.GraderError);

            ws.Cell(row, 1).Value = "Test Kit Errors:";
            ws.Cell(row++, 2).Value = _messages.Count(m => m.Severity == ErrorSeverity.TestCaseError);

            ws.Cell(row, 1).Value = "Warnings:";
            ws.Cell(row++, 2).Value = _messages.Count(m => m.Severity == ErrorSeverity.Warning);

            ws.Cell(row, 1).Value = "Info Messages:";
            ws.Cell(row++, 2).Value = _messages.Count(m => m.Severity == ErrorSeverity.Info);

            row += 2;
            ws.Cell(row, 1).Value = "Students with Errors:";
            ws.Cell(row++, 2).Value = _messages
                .Where(m => m.Severity >= ErrorSeverity.StudentError && m.StudentCode != "SYSTEM" && m.StudentCode != "TESTKIT")
                .Select(m => m.StudentCode)
                .Distinct()
                .Count();

            ws.Columns().AdjustToContents();
        }

        private void CreateMessagesSheet(XLWorkbook workbook)
        {
            var ws = workbook.Worksheets.Add("All Messages");

            // Headers
            ws.Cell(1, 1).Value = "Timestamp";
            ws.Cell(1, 2).Value = "Severity";
            ws.Cell(1, 3).Value = "Student";
            ws.Cell(1, 4).Value = "TestCase";
            ws.Cell(1, 5).Value = "Stage";
            ws.Cell(1, 6).Value = "Message";
            ws.Cell(1, 7).Value = "Exception";
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightBlue;

            int row = 2;
            foreach (var msg in _messages.OrderBy(m => m.Timestamp))
            {
                ws.Cell(row, 1).Value = msg.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                ws.Cell(row, 2).Value = msg.Severity.ToString();
                ws.Cell(row, 3).Value = msg.StudentCode;
                ws.Cell(row, 4).Value = msg.TestCase ?? "";
                ws.Cell(row, 5).Value = msg.Stage?.ToString() ?? "";
                ws.Cell(row, 6).Value = msg.Message;
                ws.Cell(row, 7).Value = msg.Exception?.Message ?? "";

                // Color code by severity
                var rowRange = ws.Range(row, 1, row, 7);
                rowRange.Style.Fill.BackgroundColor = msg.Severity switch
                {
                    ErrorSeverity.Critical => XLColor.DarkRed,
                    ErrorSeverity.GraderError => XLColor.LightPink,
                    ErrorSeverity.TestCaseError => XLColor.LightPink,
                    ErrorSeverity.StudentError => XLColor.LightYellow,
                    ErrorSeverity.Warning => XLColor.LightGray,
                    _ => XLColor.White
                };

                row++;
            }

            ws.Columns().AdjustToContents();
        }

        private void CreateStudentErrorsSheet(XLWorkbook workbook)
        {
            var ws = workbook.Worksheets.Add("Student Errors");

            ws.Cell(1, 1).Value = "Student Code";
            ws.Cell(1, 2).Value = "TestCase";
            ws.Cell(1, 3).Value = "Stage";
            ws.Cell(1, 4).Value = "Error Message";
            ws.Cell(1, 5).Value = "Timestamp";
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightYellow;

            int row = 2;
            foreach (var msg in _messages.Where(m => m.Severity == ErrorSeverity.StudentError).OrderBy(m => m.StudentCode).ThenBy(m => m.Timestamp))
            {
                ws.Cell(row, 1).Value = msg.StudentCode;
                ws.Cell(row, 2).Value = msg.TestCase ?? "";
                ws.Cell(row, 3).Value = msg.Stage?.ToString() ?? "";
                ws.Cell(row, 4).Value = msg.Message;
                ws.Cell(row, 5).Value = msg.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        private void CreateGraderErrorsSheet(XLWorkbook workbook)
        {
            var ws = workbook.Worksheets.Add("Grader Errors");

            ws.Cell(1, 1).Value = "Timestamp";
            ws.Cell(1, 2).Value = "Context";
            ws.Cell(1, 3).Value = "Error Message";
            ws.Cell(1, 4).Value = "Exception Details";
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightPink;

            int row = 2;
            foreach (var msg in _messages.Where(m => m.Severity == ErrorSeverity.GraderError).OrderBy(m => m.Timestamp))
            {
                ws.Cell(row, 1).Value = msg.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                ws.Cell(row, 2).Value = msg.StudentCode;
                ws.Cell(row, 3).Value = msg.Message;
                ws.Cell(row, 4).Value = msg.Exception?.ToString() ?? "";
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        private void CreateTestKitErrorsSheet(XLWorkbook workbook)
        {
            var ws = workbook.Worksheets.Add("TestKit Errors");

            ws.Cell(1, 1).Value = "Timestamp";
            ws.Cell(1, 2).Value = "TestCase";
            ws.Cell(1, 3).Value = "Error Message";
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightPink;

            int row = 2;
            foreach (var msg in _messages.Where(m => m.Severity == ErrorSeverity.TestCaseError).OrderBy(m => m.Timestamp))
            {
                ws.Cell(row, 1).Value = msg.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(row, 2).Value = msg.TestCase ?? "";
                ws.Cell(row, 3).Value = msg.Message;
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Gets all messages for a specific student.
        /// </summary>
        public List<GradingMessage> GetMessagesForStudent(string studentCode)
        {
            return _messages.Where(m => m.StudentCode == studentCode).OrderBy(m => m.Timestamp).ToList();
        }

        /// <summary>
        /// Gets all student errors (for UI display).
        /// </summary>
        public List<GradingMessage> GetStudentErrors()
        {
            return _messages.Where(m => m.Severity == ErrorSeverity.StudentError).OrderBy(m => m.Timestamp).ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;

            // Export to Excel before closing
            ExportToExcel();

            // Close text log
            lock (_fileLock)
            {
                _textLogWriter?.WriteLine();
                _textLogWriter?.WriteLine("=".PadRight(100, '='));
                _textLogWriter?.WriteLine($"SESSION ENDED - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _textLogWriter?.WriteLine($"Total messages: {_messages.Count}");
                _textLogWriter?.WriteLine("=".PadRight(100, '='));
                _textLogWriter?.Close();
                _textLogWriter?.Dispose();
            }

            _disposed = true;
        }
    }
}
