using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for writing grading results in the SampleLogging format.
    /// 
    /// Output structure:
    /// {SaveResultPath}/
    ///   {PaperNo}/
    ///     StudentsSolution.xlsx        - Summary of all students for this paper
    ///     student/
    ///       {StudentCode}/
    ///         OverallSummary.xlsx      - Summary of all test cases for this student
    ///         TC1/
    ///           GradeDetail.xlsx       - Detailed step results copied from test kit
    ///           TC1_Result.xlsx        - Raw test results
    ///         TC2/
    ///           ...
    /// 
    /// This format matches the SampleLogging structure provided in the repository.
    /// Uses ClosedXML for Excel file generation.
    /// </summary>
    public class ResultWriterService
    {
        private readonly ILoggingService _logger;
        private readonly string _resultPath;

        public ResultWriterService(ILoggingService logger, string resultPath)
        {
            _logger = logger;
            _resultPath = resultPath;
        }

        /// <summary>
        /// Writes the StudentsSolution.xlsx summary for all students.
        /// This is the main summary file grouping all results.
        /// </summary>
        /// <param name="students">List of all graded students.</param>
        public void WriteStudentsSolutionSummary(List<StudentSolution> students)
        {
            if (students.Count == 0) return;

            // Group students by paper
            var groupedByPaper = students.GroupBy(s => s.PaperNo);

            foreach (var paperGroup in groupedByPaper)
            {
                var paperNo = paperGroup.Key;
                var paperStudents = paperGroup.ToList();

                // Create paper folder
                var paperFolder = Path.Combine(_resultPath, paperNo);
                Directory.CreateDirectory(paperFolder);

                var summaryPath = Path.Combine(paperFolder, "StudentsSolution.xlsx");

                try
                {
                    using var workbook = new XLWorkbook();
                    var worksheet = workbook.AddWorksheet("Summary");

                    // Write header row
                    worksheet.Cell(1, 1).Value = "StudentCode";
                    worksheet.Cell(1, 2).Value = "Status";
                    worksheet.Cell(1, 3).Value = "Mark";
                    worksheet.Cell(1, 4).Value = "MaxMark";
                    worksheet.Cell(1, 5).Value = "Percentage";
                    worksheet.Cell(1, 6).Value = "Duration";
                    worksheet.Cell(1, 7).Value = "Message";

                    // Style header
                    var headerRange = worksheet.Range(1, 1, 1, 7);
                    headerRange.Style.Font.Bold = true;
                    headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;
                    headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Write student data
                    int row = 2;
                    foreach (var student in paperStudents.OrderBy(s => s.StudentCode))
                    {
                        worksheet.Cell(row, 1).Value = student.StudentCode;
                        worksheet.Cell(row, 2).Value = student.StatusDisplay;
                        worksheet.Cell(row, 3).Value = student.Mark;
                        worksheet.Cell(row, 4).Value = student.MaxMark;
                        worksheet.Cell(row, 5).Value = student.MaxMark > 0 
                            ? (student.Mark / student.MaxMark * 100).ToString("F1") + "%"
                            : "N/A";
                        worksheet.Cell(row, 6).Value = student.Duration;
                        worksheet.Cell(row, 7).Value = student.StatusMessage ?? string.Empty;

                        // Color code status
                        var statusCell = worksheet.Cell(row, 2);
                        switch (student.Status)
                        {
                            case GradingStatus.Success:
                                statusCell.Style.Fill.BackgroundColor = XLColor.LightGreen;
                                break;
                            case GradingStatus.Failed:
                                statusCell.Style.Fill.BackgroundColor = XLColor.LightPink;
                                break;
                            case GradingStatus.Not_Run:
                                statusCell.Style.Fill.BackgroundColor = XLColor.LightGray;
                                break;
                        }

                        row++;
                    }

                    // Add summary row
                    row++;
                    worksheet.Cell(row, 1).Value = "TOTAL";
                    worksheet.Cell(row, 1).Style.Font.Bold = true;
                    worksheet.Cell(row, 3).Value = paperStudents.Sum(s => s.Mark);
                    worksheet.Cell(row, 3).Style.Font.Bold = true;
                    worksheet.Cell(row, 4).Value = paperStudents.Sum(s => s.MaxMark);
                    worksheet.Cell(row, 4).Style.Font.Bold = true;

                    // Add statistics
                    row += 2;
                    worksheet.Cell(row, 1).Value = "Statistics";
                    worksheet.Cell(row, 1).Style.Font.Bold = true;
                    row++;
                    worksheet.Cell(row, 1).Value = "Total Students:";
                    worksheet.Cell(row, 2).Value = paperStudents.Count;
                    row++;
                    worksheet.Cell(row, 1).Value = "Passed:";
                    worksheet.Cell(row, 2).Value = paperStudents.Count(s => s.Status == GradingStatus.Success);
                    row++;
                    worksheet.Cell(row, 1).Value = "Failed:";
                    worksheet.Cell(row, 2).Value = paperStudents.Count(s => s.Status == GradingStatus.Failed);
                    row++;
                    worksheet.Cell(row, 1).Value = "Not Run:";
                    worksheet.Cell(row, 2).Value = paperStudents.Count(s => s.Status == GradingStatus.Not_Run);

                    // Auto-fit columns
                    worksheet.Columns().AdjustToContents();

                    // Save
                    workbook.SaveAs(summaryPath);
                    _logger.LogInfo($"Wrote StudentsSolution.xlsx for Paper {paperNo}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to write StudentsSolution.xlsx for Paper {paperNo}", ex);
                }
            }
        }

        /// <summary>
        /// Writes the OverallSummary.xlsx for a specific student.
        /// Contains summary of all test cases for that student.
        /// </summary>
        /// <param name="student">Student to write summary for.</param>
        /// <param name="testCaseResults">Dictionary of test case name to (passed, mark, maxMark).</param>
        public void WriteStudentOverallSummary(
            StudentSolution student,
            Dictionary<string, (bool Passed, double Mark, double MaxMark, string? ErrorNotes)> testCaseResults)
        {
            var studentFolder = GetStudentResultFolder(student);
            Directory.CreateDirectory(studentFolder);

            var summaryPath = Path.Combine(studentFolder, "OverallSummary.xlsx");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.AddWorksheet("Summary");

                // Write header
                worksheet.Cell(1, 1).Value = "TestCase";
                worksheet.Cell(1, 2).Value = "Pass/Fail";
                worksheet.Cell(1, 3).Value = "PointsAwarded";
                worksheet.Cell(1, 4).Value = "PointsPossible";
                worksheet.Cell(1, 5).Value = "ErrorNotes";

                var headerRange = worksheet.Range(1, 1, 1, 5);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Write test case results
                int row = 2;
                foreach (var tc in testCaseResults.OrderBy(t => ExtractNumber(t.Key)))
                {
                    worksheet.Cell(row, 1).Value = tc.Key;
                    worksheet.Cell(row, 2).Value = tc.Value.Passed ? "Pass" : "Fail";
                    worksheet.Cell(row, 3).Value = tc.Value.Mark;
                    worksheet.Cell(row, 4).Value = tc.Value.MaxMark;
                    worksheet.Cell(row, 5).Value = tc.Value.ErrorNotes ?? string.Empty;

                    // Color code
                    var passFailCell = worksheet.Cell(row, 2);
                    passFailCell.Style.Fill.BackgroundColor = tc.Value.Passed 
                        ? XLColor.LightGreen 
                        : XLColor.LightPink;

                    row++;
                }

                // Total row
                row++;
                worksheet.Cell(row, 1).Value = "TOTAL";
                worksheet.Cell(row, 1).Style.Font.Bold = true;
                worksheet.Cell(row, 3).Value = testCaseResults.Sum(t => t.Value.Mark);
                worksheet.Cell(row, 3).Style.Font.Bold = true;
                worksheet.Cell(row, 4).Value = testCaseResults.Sum(t => t.Value.MaxMark);
                worksheet.Cell(row, 4).Style.Font.Bold = true;

                worksheet.Columns().AdjustToContents();
                workbook.SaveAs(summaryPath);

                _logger.LogDebug($"Wrote OverallSummary.xlsx for {student.StudentCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write OverallSummary.xlsx for {student.StudentCode}", ex);
            }
        }

        /// <summary>
        /// Creates the test case result folder structure and copies GradeDetail.xlsx.
        /// </summary>
        /// <param name="student">Student being graded.</param>
        /// <param name="testCaseName">Name of the test case (e.g., "TC1").</param>
        /// <param name="detailSourcePath">Path to the Detail.xlsx template from test kit.</param>
        /// <returns>Path to the GradeDetail.xlsx in the result folder.</returns>
        public string PrepareTestCaseFolder(StudentSolution student, string testCaseName, string detailSourcePath)
        {
            var tcFolder = Path.Combine(GetStudentResultFolder(student), testCaseName);
            Directory.CreateDirectory(tcFolder);

            var gradeDetailPath = Path.Combine(tcFolder, "GradeDetail.xlsx");

            // Copy Detail.xlsx as template for GradeDetail.xlsx
            if (File.Exists(detailSourcePath))
            {
                File.Copy(detailSourcePath, gradeDetailPath, overwrite: true);
            }

            return gradeDetailPath;
        }

        /// <summary>
        /// Writes the TC_Result.xlsx file with raw test results.
        /// </summary>
        /// <param name="student">Student being graded.</param>
        /// <param name="testCaseName">Name of the test case.</param>
        /// <param name="results">List of step results (StepId, Passed, Expected, Actual, Message).</param>
        public void WriteTestCaseResult(
            StudentSolution student,
            string testCaseName,
            List<(string StepId, bool Passed, string? Expected, string? Actual, string? Message)> results)
        {
            var tcFolder = Path.Combine(GetStudentResultFolder(student), testCaseName);
            Directory.CreateDirectory(tcFolder);

            var resultPath = Path.Combine(tcFolder, $"{testCaseName}_Result.xlsx");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.AddWorksheet("Results");

                // Header
                worksheet.Cell(1, 1).Value = "StepId";
                worksheet.Cell(1, 2).Value = "Result";
                worksheet.Cell(1, 3).Value = "Expected";
                worksheet.Cell(1, 4).Value = "Actual";
                worksheet.Cell(1, 5).Value = "Message";

                var headerRange = worksheet.Range(1, 1, 1, 5);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Data
                int row = 2;
                foreach (var result in results)
                {
                    worksheet.Cell(row, 1).Value = result.StepId;
                    worksheet.Cell(row, 2).Value = result.Passed ? "Pass" : "Fail";
                    worksheet.Cell(row, 3).Value = result.Expected ?? string.Empty;
                    worksheet.Cell(row, 4).Value = result.Actual ?? string.Empty;
                    worksheet.Cell(row, 5).Value = result.Message ?? string.Empty;

                    worksheet.Cell(row, 2).Style.Fill.BackgroundColor = result.Passed
                        ? XLColor.LightGreen
                        : XLColor.LightPink;

                    row++;
                }

                worksheet.Style.Alignment.WrapText = true;
                worksheet.Columns().AdjustToContents(1, row, 5, 100);
                workbook.SaveAs(resultPath);

                _logger.LogDebug($"Wrote {testCaseName}_Result.xlsx for {student.StudentCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write {testCaseName}_Result.xlsx for {student.StudentCode}", ex);
            }
        }

        /// <summary>
        /// Gets the result folder path for a student, organized by paper.
        /// </summary>
        private string GetStudentResultFolder(StudentSolution student)
        {
            return Path.Combine(_resultPath, student.PaperNo, "student", student.StudentCode);
        }

        /// <summary>
        /// Extracts number from string for sorting.
        /// </summary>
        private int ExtractNumber(string s)
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var num) ? num : 999;
        }
    }
}
