using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for writing grading results to Excel files.
    /// Creates student-specific result files and overall summary spreadsheets.
    /// 
    /// Output structure follows the SampleLogging format:
    /// - StudentsSolution.xlsx: Overall summary of all students
    /// - Results/{StudentCode}/
    ///   - OverallSummary.xlsx: Student-level summary
    ///   - {TestCase}/
    ///     - GradeDetail.xlsx: Detailed test case results
    ///     - {TestCase}_Result.xlsx: Raw test data
    /// </summary>
    public class ResultWriterService
    {
        private readonly ILoggingService _logger;
        private readonly string _baseResultPath;

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
        /// Similar to SampleLogging/StudentsSolution.xlsx format.
        /// </summary>
        public void WriteStudentsSolutionSummary(List<StudentSolution> students)
        {
            var filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");
            
            _logger.LogInfo($"Writing students summary to {filePath}");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Students");

                // Header row
                worksheet.Cell(1, 1).Value = "No";
                worksheet.Cell(1, 2).Value = "Student Code";
                worksheet.Cell(1, 3).Value = "Paper No";
                worksheet.Cell(1, 4).Value = "Status";
                worksheet.Cell(1, 5).Value = "Mark";
                worksheet.Cell(1, 6).Value = "Start Time";
                worksheet.Cell(1, 7).Value = "End Time";
                worksheet.Cell(1, 8).Value = "Duration";
                worksheet.Cell(1, 9).Value = "Message";

                // Style header
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Data rows
                int row = 2;
                foreach (var student in students.OrderBy(s => s.PaperNo).ThenBy(s => s.StudentCode))
                {
                    worksheet.Cell(row, 1).Value = row - 1;
                    worksheet.Cell(row, 2).Value = student.StudentCode;
                    worksheet.Cell(row, 3).Value = student.PaperNo;
                    worksheet.Cell(row, 4).Value = student.StatusDisplay;
                    worksheet.Cell(row, 5).Value = student.Mark;
                    worksheet.Cell(row, 6).Value = student.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                    worksheet.Cell(row, 7).Value = student.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                    worksheet.Cell(row, 8).Value = student.Duration;
                    worksheet.Cell(row, 9).Value = student.StatusMessage ?? "";

                    // Color code rows based on status
                    var rowStyle = worksheet.Row(row);
                    switch (student.Status)
                    {
                        case GradingStatus.Success:
                            rowStyle.Style.Fill.BackgroundColor = XLColor.LightGreen;
                            break;
                        case GradingStatus.Failed:
                            rowStyle.Style.Fill.BackgroundColor = XLColor.LightPink;
                            break;
                        case GradingStatus.InProgress:
                            rowStyle.Style.Fill.BackgroundColor = XLColor.LightYellow;
                            break;
                    }

                    row++;
                }

                // Auto-fit columns
                worksheet.Columns().AdjustToContents();

                workbook.SaveAs(filePath);
                _logger.LogInfo($"Students summary written successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write students summary: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a student-specific result summary.
        /// Creates Results/{StudentCode}/OverallSummary.xlsx
        /// </summary>
        public void WriteStudentSummary(StudentSolution student, List<TestCaseResult> testCases)
        {
            var studentDir = Path.Combine(_baseResultPath, student.StudentCode);
            if (!Directory.Exists(studentDir))
            {
                Directory.CreateDirectory(studentDir);
            }

            var filePath = Path.Combine(studentDir, "OverallSummary.xlsx");
            
            _logger.LogInfo($"Writing student summary for {student.StudentCode}");

            try
            {
                using var workbook = new XLWorkbook();
                
                // Summary worksheet
                var summarySheet = workbook.Worksheets.Add("Summary");
                summarySheet.Cell(1, 1).Value = "Student Code";
                summarySheet.Cell(1, 2).Value = student.StudentCode;
                summarySheet.Cell(2, 1).Value = "Paper No";
                summarySheet.Cell(2, 2).Value = student.PaperNo;
                summarySheet.Cell(3, 1).Value = "Total Mark";
                summarySheet.Cell(3, 2).Value = student.Mark;
                summarySheet.Cell(4, 1).Value = "Status";
                summarySheet.Cell(4, 2).Value = student.StatusDisplay;
                summarySheet.Cell(5, 1).Value = "Start Time";
                summarySheet.Cell(5, 2).Value = student.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                summarySheet.Cell(6, 1).Value = "End Time";
                summarySheet.Cell(6, 2).Value = student.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                summarySheet.Cell(7, 1).Value = "Duration";
                summarySheet.Cell(7, 2).Value = student.Duration;

                summarySheet.Column(1).Style.Font.Bold = true;
                summarySheet.Columns().AdjustToContents();

                // Test cases worksheet
                var testCaseSheet = workbook.Worksheets.Add("Test Cases");
                testCaseSheet.Cell(1, 1).Value = "Test Case";
                testCaseSheet.Cell(1, 2).Value = "Status";
                testCaseSheet.Cell(1, 3).Value = "Mark";
                testCaseSheet.Cell(1, 4).Value = "Max Mark";
                testCaseSheet.Cell(1, 5).Value = "Message";

                var headerRow = testCaseSheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                int row = 2;
                foreach (var tc in testCases)
                {
                    testCaseSheet.Cell(row, 1).Value = tc.TestCaseName;
                    testCaseSheet.Cell(row, 2).Value = tc.Passed ? "Pass" : "Fail";
                    testCaseSheet.Cell(row, 3).Value = tc.EarnedMark;
                    testCaseSheet.Cell(row, 4).Value = tc.MaxMark;
                    testCaseSheet.Cell(row, 5).Value = tc.Message ?? "";

                    var rowStyle = testCaseSheet.Row(row);
                    rowStyle.Style.Fill.BackgroundColor = tc.Passed ? XLColor.LightGreen : XLColor.LightPink;

                    row++;
                }

                testCaseSheet.Columns().AdjustToContents();

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
        /// Creates Results/{StudentCode}/{TestCase}/GradeDetail.xlsx
        /// </summary>
        public void WriteTestCaseDetail(StudentSolution student, string testCaseName, List<StepResult> steps)
        {
            var testCaseDir = Path.Combine(_baseResultPath, student.StudentCode, testCaseName);
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
                worksheet.Cell(1, 1).Value = "Step ID";
                worksheet.Cell(1, 2).Value = "Action";
                worksheet.Cell(1, 3).Value = "Stage";
                worksheet.Cell(1, 4).Value = "Result";
                worksheet.Cell(1, 5).Value = "Message";
                worksheet.Cell(1, 6).Value = "Duration (ms)";

                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                int row = 2;
                foreach (var step in steps)
                {
                    worksheet.Cell(row, 1).Value = step.StepId;
                    worksheet.Cell(row, 2).Value = step.Action;
                    worksheet.Cell(row, 3).Value = step.Stage;
                    worksheet.Cell(row, 4).Value = step.Passed ? "Pass" : "Fail";
                    worksheet.Cell(row, 5).Value = step.Message ?? "";
                    worksheet.Cell(row, 6).Value = step.DurationMs;

                    var rowStyle = worksheet.Row(row);
                    rowStyle.Style.Fill.BackgroundColor = step.Passed ? XLColor.LightGreen : XLColor.LightPink;

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
        /// Gets the student result folder path.
        /// </summary>
        public string GetStudentResultFolder(string studentCode)
        {
            var path = Path.Combine(_baseResultPath, studentCode);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }
    }

    /// <summary>
    /// Represents a test case result for a student.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public double EarnedMark { get; set; }
        public double MaxMark { get; set; }
        public string? Message { get; set; }
        public List<StepResult> Steps { get; set; } = new List<StepResult>();
    }

    /// <summary>
    /// Represents a single step result within a test case.
    /// </summary>
    public class StepResult
    {
        public string StepId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public string? Message { get; set; }
        public double DurationMs { get; set; }
    }
}
