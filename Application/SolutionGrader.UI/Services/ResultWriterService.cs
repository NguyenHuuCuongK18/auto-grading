using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for writing grading results to Excel files.
    /// Creates student-specific result files and per-paper summary spreadsheets.
    /// 
    /// Output structure follows the SampleLogging format with paper-based organization:
    /// - {PaperNo}/StudentsSolution.xlsx: Per-paper summary (written by this service)
    /// - {PaperNo}/student/{StudentCode}/
    ///   - OverallSummary.xlsx: Student-level summary 
    ///   - {TestCase}/
    ///     - GradeDetail.xlsx: Detailed test case results
    /// 
    /// IMPORTANT: The global StudentsSolution.xlsx file is NOT written by this service.
    /// It is handled by ExcelLogCoordinator in GradingOrchestrationService to prevent
    /// race conditions that cause grade mismatches between the outer file and inner files.
    /// 
    /// Note: Results are organized by paper number (1/, 2/, etc.) for better navigation.
    /// </summary>
    public class ResultWriterService
    {
        private readonly ILoggingService _logger;
        private readonly string _baseResultPath;
        private System.Threading.Timer? _deferredWriteTimer;
        private bool _hasPendingWrites = false;
        private List<StudentSolution>? _cachedStudents;
        private readonly object _writeLock = new object();

        public ResultWriterService(ILoggingService logger, string baseResultPath)
        {
            _logger = logger;
            _baseResultPath = baseResultPath;
            
            // Ensure base result directory exists
            if (!Directory.Exists(_baseResultPath))
            {
                Directory.CreateDirectory(_baseResultPath);
            }
        }

        /// <summary>
        /// Writes the overall student solution summary spreadsheet.
        /// Creates both a global summary and per-paper summaries.
        /// Format matches SampleLogging/StudentsSolution.xlsx exactly:
        /// No, StudentCode, ExamPaper, Status, FinalResult, StartDate, EndDate
        /// OPTIMIZATION: Defers actual write by 2 seconds to batch multiple updates during parallel grading.
        /// This prevents UI freezing when multiple students complete simultaneously.
        /// </summary>
        public void WriteStudentsSolutionSummary(List<StudentSolution> students)
        {
            lock (_writeLock)
            {
                // Cache the latest student data
                _cachedStudents = students.ToList();
                _hasPendingWrites = true;
                
                // Reset or create timer to write after 2 seconds of inactivity
                // This batches writes when multiple students finish in quick succession
                _deferredWriteTimer?.Dispose();
                _deferredWriteTimer = new System.Threading.Timer(_ => {
                    WritePendingResults();
                }, null, 2000, Timeout.Infinite);
            }
        }
        
        /// <summary>
        /// Forces immediate write of any pending results.
        /// Call this when grading session completes to ensure all results are saved.
        /// </summary>
        public void FlushPendingWrites()
        {
            lock (_writeLock)
            {
                _deferredWriteTimer?.Dispose();
                _deferredWriteTimer = null;
                
                if (_hasPendingWrites && _cachedStudents != null)
                {
                    WritePendingResultsSync();
                }
            }
        }
        
        /// <summary>
        /// Synchronous version for FlushPendingWrites to ensure completion before return.
        /// 
        /// NOTE: Does NOT write the global StudentsSolution.xlsx because ExcelLogCoordinator
        /// (in GradingOrchestrationService) is the authoritative source for that file.
        /// Writing here would cause a race condition that overwrites correct grades.
        /// </summary>
        private void WritePendingResultsSync()
        {
            List<StudentSolution> studentsToWrite;
            
            lock (_writeLock)
            {
                if (!_hasPendingWrites || _cachedStudents == null)
                    return;
                
                // Capture data inside lock, then release lock before heavy I/O
                studentsToWrite = _cachedStudents.ToList();
                _hasPendingWrites = false;
            }
            
            // Synchronous write for flush scenario
            try
            {
                // FIX: Do NOT write global StudentsSolution.xlsx here!
                // ExcelLogCoordinator in GradingOrchestrationService handles the main file.
                // Writing here would overwrite correct grades with potentially stale data,
                // causing the bug where "outer StudentSolution.xlsx does not match inner file and UI".
                // var filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");
                // WriteStudentsSolutionSummaryToFile(filePath, studentsToWrite);

                // Write per-paper summaries (these are separate files, no conflict)
                var paperGroups = studentsToWrite.GroupBy(s => s.PaperNo);
                foreach (var group in paperGroups)
                {
                    var paperDir = Path.Combine(_baseResultPath, group.Key);
                    if (!Directory.Exists(paperDir))
                    {
                        Directory.CreateDirectory(paperDir);
                    }

                    var paperFilePath = Path.Combine(paperDir, "StudentsSolution.xlsx");
                    WriteStudentsSolutionSummaryToFile(paperFilePath, group.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to write pending results during synchronous flush", ex);
            }
        }
        
        /// <summary>
        /// Internal method that performs the actual write operation.
        /// OPTIMIZATION: Runs on background thread pool to prevent blocking UI or worker threads.
        /// 
        /// NOTE: Does NOT write the global StudentsSolution.xlsx because ExcelLogCoordinator
        /// (in GradingOrchestrationService) is the authoritative source for that file.
        /// Writing here would cause a race condition that overwrites correct grades.
        /// </summary>
        private void WritePendingResults()
        {
            List<StudentSolution> studentsToWrite;
            
            lock (_writeLock)
            {
                if (!_hasPendingWrites || _cachedStudents == null)
                    return;
                
                // Capture data inside lock, then release lock before heavy I/O
                studentsToWrite = _cachedStudents.ToList();
                _hasPendingWrites = false;
            }
            
            // Execute heavy Excel write operations on background thread pool
            // This prevents blocking UI thread or worker threads that are grading students
            Task.Run(() =>
            {
                try
                {
                    // FIX: Do NOT write global StudentsSolution.xlsx here!
                    // ExcelLogCoordinator in GradingOrchestrationService handles the main file.
                    // Writing here would overwrite correct grades with potentially stale data,
                    // causing the bug where "outer StudentSolution.xlsx does not match inner file and UI".
                    // var filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");
                    // WriteStudentsSolutionSummaryToFile(filePath, studentsToWrite);

                    // Write per-paper summaries (these are separate files, no conflict)
                    var paperGroups = studentsToWrite.GroupBy(s => s.PaperNo);
                    foreach (var group in paperGroups)
                    {
                        var paperDir = Path.Combine(_baseResultPath, group.Key);
                        if (!Directory.Exists(paperDir))
                        {
                            Directory.CreateDirectory(paperDir);
                        }

                        var paperFilePath = Path.Combine(paperDir, "StudentsSolution.xlsx");
                        WriteStudentsSolutionSummaryToFile(paperFilePath, group.ToList());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to write pending results", ex);
                }
            });
        }

        /// <summary>
        /// Writes the students solution summary to a specific file path.
        /// FIX: Always recreates workbook with ALL students to avoid data loss in parallel grading.
        /// Each call writes the complete current state, not incremental updates.
        /// 
        /// UPDATE: Now includes MaxMark (PossiblePoints) column to properly display
        /// the maximum possible points for each student based on their paper's test kit.
        /// This fixes the issue where max points were not being displayed in the output.
        /// </summary>
        private void WriteStudentsSolutionSummaryToFile(string filePath, List<StudentSolution> students)
        {
            _logger.LogInfo($"Writing students summary to {filePath} ({students.Count} students)");

            try
            {
                // CRITICAL FIX: Always create fresh workbook with ALL students
                // This prevents data loss when multiple threads try to write simultaneously
                // The deferred write mechanism in WriteStudentsSolutionSummary ensures this is called
                // with the complete list of students, not partial updates
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Sheet1");

                // Header row - matching SampleLogging format with MaxMark (PossiblePoints) column
                worksheet.Cell(1, 1).Value = "No";
                worksheet.Cell(1, 2).Value = "StudentCode";
                worksheet.Cell(1, 3).Value = "ExamPaper";
                worksheet.Cell(1, 4).Value = "EarnedPoints";     // FinalResult renamed for clarity
                worksheet.Cell(1, 5).Value = "PossiblePoints";  // MaxMark column
                worksheet.Cell(1, 6).Value = "Status";
                worksheet.Cell(1, 7).Value = "StartDate";
                worksheet.Cell(1, 8).Value = "EndDate";

                // Style header
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Data rows - write ALL students in sorted order
                int row = 2;
                int no = 1;
                foreach (var student in students.OrderBy(s => int.TryParse(s.PaperNo, out var n) ? n : 0).ThenBy(s => s.StudentCode))
                {
                    worksheet.Cell(row, 1).Value = no.ToString();
                    worksheet.Cell(row, 2).Value = student.StudentCode;
                    worksheet.Cell(row, 3).Value = student.PaperNo;
                    worksheet.Cell(row, 4).Value = student.Mark.ToString("0.##");      // EarnedPoints
                    worksheet.Cell(row, 5).Value = student.MaxMark.ToString("0.##");  // MaxMark (PossiblePoints)
                    worksheet.Cell(row, 6).Value = student.StatusDisplay;               // Status (was at col 4)
                    worksheet.Cell(row, 7).Value = student.StartTime?.ToString("dd/MM/yyyy HH:mm:ss") ?? "";
                    worksheet.Cell(row, 8).Value = student.EndTime?.ToString("dd/MM/yyyy HH:mm:ss") ?? "";

                    // Color code rows based on status
                    var rowRange = worksheet.Range(row, 1, row, 8);  // Updated to include new column
                    switch (student.Status)
                    {
                        case GradingStatus.Success:
                            rowRange.Style.Fill.BackgroundColor = XLColor.LightGreen;
                            break;
                        case GradingStatus.Failed:
                            rowRange.Style.Fill.BackgroundColor = XLColor.LightPink;
                            break;
                        case GradingStatus.InProgress:
                            rowRange.Style.Fill.BackgroundColor = XLColor.LightYellow;
                            break;
                    }

                    row++;
                    no++;
                }

                // Auto-fit columns
                worksheet.Columns().AdjustToContents();

                workbook.SaveAs(filePath);
                _logger.LogInfo($"Students summary written successfully with {no - 1} students");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write students summary: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a student-specific result summary.
        /// Creates {PaperNo}/student/{StudentCode}/OverallSummary.xlsx
        /// Format matches SampleLogging/student/student-code-here/OverallSummary.xlsx
        /// </summary>
        public void WriteStudentSummary(StudentSolution student, List<Domain.Models.TestCaseResult> testCases)
        {
            var studentDir = GetStudentResultFolder(student.StudentCode, student.PaperNo);

            var filePath = Path.Combine(studentDir, "OverallSummary.xlsx");
            
            _logger.LogInfo($"Writing student summary for {student.StudentCode} (Paper {student.PaperNo})");

            try
            {
                using var workbook = new XLWorkbook();
                
                // Summary worksheet - matching SampleLogging format
                var summarySheet = workbook.Worksheets.Add("Summary");
                summarySheet.Cell(1, 1).Value = "TestCase";
                summarySheet.Cell(1, 2).Value = "Passed";
                summarySheet.Cell(1, 3).Value = "PointsAwarded";
                summarySheet.Cell(1, 4).Value = "PointsPossible";
                summarySheet.Cell(1, 5).Value = "ErrorNotes";

                var headerRow = summarySheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                int row = 2;
                foreach (var tc in testCases)
                {
                    summarySheet.Cell(row, 1).Value = tc.TestCaseName;
                    summarySheet.Cell(row, 2).Value = tc.Passed ? "PASS" : "FAIL";
                    summarySheet.Cell(row, 3).Value = tc.EarnedMark;
                    summarySheet.Cell(row, 4).Value = tc.MaxMark;
                    summarySheet.Cell(row, 5).Value = tc.ErrorMessage ?? "";

                    var rowRange = summarySheet.Range(row, 1, row, 5);
                    rowRange.Style.Fill.BackgroundColor = tc.Passed ? XLColor.LightGreen : XLColor.LightPink;

                    row++;
                }

                summarySheet.Columns().AdjustToContents();

                workbook.SaveAs(filePath);
                _logger.LogInfo($"Student summary written for {student.StudentCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write student summary: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes detailed test case results for a student.
        /// Creates {PaperNo}/student/{StudentCode}/{TestCase}/GradeDetail.xlsx
        /// </summary>
        public void WriteTestCaseDetail(StudentSolution student, string testCaseName, List<StepResult> steps)
        {
            var testCaseDir = Path.Combine(GetStudentResultFolder(student.StudentCode, student.PaperNo), testCaseName);
            if (!Directory.Exists(testCaseDir))
            {
                Directory.CreateDirectory(testCaseDir);
            }

            var filePath = Path.Combine(testCaseDir, "GradeDetail.xlsx");
            
            _logger.LogDebug($"Writing test case detail for {student.StudentCode}/{testCaseName}");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Detail");

                // Header row
                worksheet.Cell(1, 1).Value = "StepId";
                worksheet.Cell(1, 2).Value = "Stage";
                worksheet.Cell(1, 3).Value = "Action";
                worksheet.Cell(1, 4).Value = "Result";
                worksheet.Cell(1, 5).Value = "Message";
                worksheet.Cell(1, 6).Value = "DurationMs";

                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                int row = 2;
                foreach (var step in steps)
                {
                    worksheet.Cell(row, 1).Value = step.Step?.Id ?? "";
                    worksheet.Cell(row, 2).Value = step.Step?.Stage ?? "";
                    worksheet.Cell(row, 3).Value = step.Step?.Action ?? "";
                    worksheet.Cell(row, 4).Value = step.Passed ? "PASS" : "FAIL";
                    worksheet.Cell(row, 5).Value = step.Message ?? "";
                    worksheet.Cell(row, 6).Value = step.DurationMs;

                    var rowRange = worksheet.Range(row, 1, row, 6);
                    rowRange.Style.Fill.BackgroundColor = step.Passed ? XLColor.LightGreen : XLColor.LightPink;

                    row++;
                }

                worksheet.Columns().AdjustToContents();
                workbook.SaveAs(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write test case detail: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes raw test result data for a test case.
        /// Creates {PaperNo}/student/{StudentCode}/{TestCase}/{TestCase}_Result.xlsx
        /// 
        /// OBSOLETE: This method is no longer used as the {TestCaseName}_Result.xlsx files
        /// are not logging anything useful. They are redundant with GradeDetail.xlsx and 
        /// OverallSummary.xlsx. Kept for backward compatibility but should not be called.
        /// </summary>
        [Obsolete("This method creates {TestCaseName}_Result.xlsx files that are not useful. Use WriteTestCaseDetail instead.")]
        public void WriteTestCaseResult(StudentSolution student, string testCaseName, List<StepResult> steps)
        {
            // NO LONGER CREATES FILES - method kept for backward compatibility only
            // The {TestCaseName}_Result.xlsx files were removed per user requirement:
            // "remove the excessive sheet {testcasename}_Result under each student folder, 
            // it is not logging anything useful anymore"
            
            // All test case information is now available in:
            // 1. GradeDetail.xlsx - detailed step-by-step results
            // 2. OverallSummary.xlsx - summary of all test cases
            
            _logger.LogDebug($"WriteTestCaseResult called for {student.StudentCode}/{testCaseName} but no longer creates files (obsolete)");
        }

        /// <summary>
        /// Gets the student result folder path organized by paper number.
        /// Format: {PaperNo}/student/{StudentCode}
        /// Creates the folder if it doesn't exist.
        /// </summary>
        /// <param name="studentCode">Student code identifier</param>
        /// <param name="paperNo">Paper number for organization (optional)</param>
        public string GetStudentResultFolder(string studentCode, string? paperNo = null)
        {
            string path;
            if (!string.IsNullOrEmpty(paperNo))
            {
                // Organize by paper number: e.g., "1/student/cuongnhhe186494"
                path = Path.Combine(_baseResultPath, paperNo, "student", studentCode);
            }
            else
            {
                // Fallback to non-paper-organized structure
                path = Path.Combine(_baseResultPath, "student", studentCode);
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }

        /// <summary>
        /// Deletes all result files for a student (used when resetting).
        /// Searches in all paper folders.
        /// </summary>
        public void DeleteStudentResults(string studentCode, string? paperNo = null)
        {
            // Try paper-organized path if paperNo provided
            if (!string.IsNullOrEmpty(paperNo))
            {
                var paperPath = Path.Combine(_baseResultPath, paperNo, "student", studentCode);
                if (Directory.Exists(paperPath))
                {
                    try
                    {
                        Directory.Delete(paperPath, true);
                        _logger.LogInfo($"Deleted result folder for {studentCode} (Paper {paperNo})");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to delete result folder for {studentCode}: {ex.Message}");
                    }
                }
            }

            // Also try legacy non-paper-organized path
            var legacyPath = Path.Combine(_baseResultPath, "student", studentCode);
            if (Directory.Exists(legacyPath))
            {
                try
                {
                    Directory.Delete(legacyPath, true);
                    _logger.LogInfo($"Deleted legacy result folder for {studentCode}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete legacy result folder for {studentCode}: {ex.Message}");
                }
            }
        }
    }
}
