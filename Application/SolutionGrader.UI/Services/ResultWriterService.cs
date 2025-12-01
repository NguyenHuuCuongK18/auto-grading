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
    /// Output structure follows the SampleLogging format (paper-organized):
    /// - {PaperNo}/StudentsSolution.xlsx: Summary of all students in this paper
    /// - {PaperNo}/student/{StudentCode}/
    ///   - OverallSummary.xlsx: Student-level summary
    ///   - {TestCase}/
    ///     - GradeDetail.xlsx: Detailed grading information
    ///     - {TestCase}_Result.xlsx: Raw test results
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
        /// Writes the overall student solution summary spreadsheet for a specific paper.
        /// Similar to SampleLogging/{PaperNo}/StudentsSolution.xlsx format.
        /// </summary>
        public void WriteStudentsSolutionSummary(List<StudentSolution> students, string? paperNo = null)
        {
            // If paperNo is provided, write to paper-specific folder
            string filePath;
            List<StudentSolution> studentsToWrite;
            
            if (!string.IsNullOrEmpty(paperNo))
            {
                var paperDir = Path.Combine(_baseResultPath, paperNo);
                if (!Directory.Exists(paperDir))
                {
                    Directory.CreateDirectory(paperDir);
                }
                filePath = Path.Combine(paperDir, "StudentsSolution.xlsx");
                studentsToWrite = students.Where(s => s.PaperNo == paperNo).ToList();
            }
            else
            {
                // Write all students to root folder
                filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");
                studentsToWrite = students;
            }
            
            _logger.LogInfo($"Writing students summary to {filePath}");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Sheet1");

                // Header row matching SampleLogging format
                worksheet.Cell(1, 1).Value = "No";
                worksheet.Cell(1, 2).Value = "StudentCode";
                worksheet.Cell(1, 3).Value = "ExamPaper";
                worksheet.Cell(1, 4).Value = "Status";
                worksheet.Cell(1, 5).Value = "FinalResult";
                worksheet.Cell(1, 6).Value = "StartDate";
                worksheet.Cell(1, 7).Value = "EndDate";

                // Style header
                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Data rows
                int row = 2;
                foreach (var student in studentsToWrite.OrderBy(s => s.StudentCode))
                {
                    worksheet.Cell(row, 1).Value = row - 1;
                    worksheet.Cell(row, 2).Value = student.StudentCode;
                    worksheet.Cell(row, 3).Value = student.PaperNo;
                    worksheet.Cell(row, 4).Value = student.Status.ToString().Replace("_", " ");
                    worksheet.Cell(row, 5).Value = student.Mark;
                    worksheet.Cell(row, 6).Value = student.StartTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";
                    worksheet.Cell(row, 7).Value = student.EndTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";

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
        /// Creates {PaperNo}/student/{StudentCode}/OverallSummary.xlsx matching SampleLogging format
        /// </summary>
        public void WriteStudentSummary(StudentSolution student, List<TestCaseResult> testCases)
        {
            // Create paper-organized folder structure
            var studentDir = Path.Combine(_baseResultPath, student.PaperNo, "student", student.StudentCode);
            if (!Directory.Exists(studentDir))
            {
                Directory.CreateDirectory(studentDir);
            }

            var filePath = Path.Combine(studentDir, "OverallSummary.xlsx");
            
            _logger.LogInfo($"Writing student summary for {student.StudentCode}");

            try
            {
                using var workbook = new XLWorkbook();
                
                // Summary worksheet matching SampleLogging format
                var summarySheet = workbook.Worksheets.Add("Summary");
                
                // Header row
                summarySheet.Cell(1, 1).Value = "TestCase";
                summarySheet.Cell(1, 2).Value = "Passed";
                summarySheet.Cell(1, 3).Value = "PointsAwarded";
                summarySheet.Cell(1, 4).Value = "PointsPossible";
                summarySheet.Cell(1, 5).Value = "ErrorNotes";

                var headerRow = summarySheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                // Data rows
                int row = 2;
                foreach (var tc in testCases)
                {
                    summarySheet.Cell(row, 1).Value = tc.TestCaseName;
                    summarySheet.Cell(row, 2).Value = tc.Passed ? "PASS" : "FAIL";
                    summarySheet.Cell(row, 3).Value = tc.EarnedMark;
                    summarySheet.Cell(row, 4).Value = tc.MaxMark;
                    summarySheet.Cell(row, 5).Value = tc.Message ?? "";

                    var rowStyle = summarySheet.Row(row);
                    rowStyle.Style.Fill.BackgroundColor = tc.Passed ? XLColor.LightGreen : XLColor.LightPink;

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
        public void WriteTestCaseDetail(StudentSolution student, string testCaseName, List<StepResult> steps, List<NetworkFlowResult>? networkFlows = null)
        {
            // Create paper-organized folder structure
            var testCaseDir = Path.Combine(_baseResultPath, student.PaperNo, "student", student.StudentCode, testCaseName);
            if (!Directory.Exists(testCaseDir))
            {
                Directory.CreateDirectory(testCaseDir);
            }

            var filePath = Path.Combine(testCaseDir, "GradeDetail.xlsx");
            
            _logger.LogDebug($"Writing test case detail for {student.StudentCode}/{testCaseName}");

            try
            {
                using var workbook = new XLWorkbook();
                
                // Create separate sheets for different components (matching SampleLogging)
                CreateNetworkSheet(workbook, networkFlows);
                CreateUserSheet(workbook, steps.Where(s => s.Stage.StartsWith("USER", StringComparison.OrdinalIgnoreCase)).ToList());
                CreateClientSheet(workbook, steps.Where(s => s.Stage.StartsWith("CLIENT", StringComparison.OrdinalIgnoreCase)).ToList());
                CreateServerSheet(workbook, steps.Where(s => s.Stage.StartsWith("SERVER", StringComparison.OrdinalIgnoreCase)).ToList());
                CreateDatabaseSheet(workbook, steps.Where(s => s.Stage.StartsWith("DATABASE", StringComparison.OrdinalIgnoreCase)).ToList());

                workbook.SaveAs(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write test case detail: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes test case result file.
        /// Creates {PaperNo}/student/{StudentCode}/{TestCase}/{TestCase}_Result.xlsx
        /// </summary>
        public void WriteTestCaseResult(StudentSolution student, string testCaseName, List<StepResult> steps)
        {
            // Create paper-organized folder structure
            var testCaseDir = Path.Combine(_baseResultPath, student.PaperNo, "student", student.StudentCode, testCaseName);
            if (!Directory.Exists(testCaseDir))
            {
                Directory.CreateDirectory(testCaseDir);
            }

            var filePath = Path.Combine(testCaseDir, $"{testCaseName}_Result.xlsx");
            
            _logger.LogDebug($"Writing test case result for {student.StudentCode}/{testCaseName}");

            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Result");

                // Header row matching SampleLogging format
                worksheet.Cell(1, 1).Value = "StepId";
                worksheet.Cell(1, 2).Value = "Stage";
                worksheet.Cell(1, 3).Value = "Action";
                worksheet.Cell(1, 4).Value = "Passed";
                worksheet.Cell(1, 5).Value = "Message";
                worksheet.Cell(1, 6).Value = "DurationMs";

                var headerRow = worksheet.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                int row = 2;
                foreach (var step in steps)
                {
                    worksheet.Cell(row, 1).Value = step.StepId;
                    worksheet.Cell(row, 2).Value = step.Stage;
                    worksheet.Cell(row, 3).Value = step.Action;
                    worksheet.Cell(row, 4).Value = step.Passed;
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
                _logger.LogError($"Failed to write test case result: {ex.Message}");
            }
        }

        private void CreateNetworkSheet(XLWorkbook workbook, List<NetworkFlowResult>? networkFlows)
        {
            var worksheet = workbook.Worksheets.Add("Network");
            
            // Header matching SampleLogging
            var headers = new[] { "Stage", "Time", "Info", "Source", "Destination", "Flags", "State", "Data",
                "SourceRole", "DestinationRole", "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", 
                "ActualData", "NetworkResult" };
            
            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cell(1, i + 1).Value = headers[i];
            }

            var headerRow = worksheet.Row(1);
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

            if (networkFlows != null)
            {
                int row = 2;
                foreach (var flow in networkFlows)
                {
                    worksheet.Cell(row, 1).Value = flow.Stage;
                    worksheet.Cell(row, 2).Value = flow.Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                    worksheet.Cell(row, 3).Value = flow.Info;
                    worksheet.Cell(row, 4).Value = flow.Source;
                    worksheet.Cell(row, 5).Value = flow.Destination;
                    worksheet.Cell(row, 6).Value = flow.ExpectedFlags;
                    worksheet.Cell(row, 7).Value = flow.ExpectedState;
                    worksheet.Cell(row, 8).Value = flow.ExpectedData;
                    worksheet.Cell(row, 9).Value = flow.SourceRole;
                    worksheet.Cell(row, 10).Value = flow.DestinationRole;
                    worksheet.Cell(row, 11).Value = flow.ActualFlags;
                    worksheet.Cell(row, 12).Value = flow.ActualState;
                    worksheet.Cell(row, 13).Value = flow.ActualSourceRole;
                    worksheet.Cell(row, 14).Value = flow.ActualDestRole;
                    worksheet.Cell(row, 15).Value = flow.ActualData;
                    worksheet.Cell(row, 16).Value = flow.Passed ? "PASS" : "FAIL";
                    row++;
                }
            }

            worksheet.Columns().AdjustToContents();
        }

        private void CreateUserSheet(XLWorkbook workbook, List<StepResult> steps)
        {
            var worksheet = workbook.Worksheets.Add("User");
            CreateStepSheet(worksheet, steps);
        }

        private void CreateClientSheet(XLWorkbook workbook, List<StepResult> steps)
        {
            var worksheet = workbook.Worksheets.Add("Client");
            CreateStepSheet(worksheet, steps);
        }

        private void CreateServerSheet(XLWorkbook workbook, List<StepResult> steps)
        {
            var worksheet = workbook.Worksheets.Add("Server");
            CreateStepSheet(worksheet, steps);
        }

        private void CreateDatabaseSheet(XLWorkbook workbook, List<StepResult> steps)
        {
            var worksheet = workbook.Worksheets.Add("Database");
            CreateStepSheet(worksheet, steps);
        }

        private void CreateStepSheet(IXLWorksheet worksheet, List<StepResult> steps)
        {
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
        }

        /// <summary>
        /// Gets the student result folder path (paper-organized).
        /// </summary>
        public string GetStudentResultFolder(string studentCode, string paperNo)
        {
            var path = Path.Combine(_baseResultPath, paperNo, "student", studentCode);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }

        /// <summary>
        /// Gets the student result folder path (legacy format).
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

    /// <summary>
    /// Represents a network flow result for grading.
    /// </summary>
    public class NetworkFlowResult
    {
        public int Stage { get; set; }
        public DateTime? Time { get; set; }
        public string Info { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string ExpectedFlags { get; set; } = string.Empty;
        public string ExpectedState { get; set; } = string.Empty;
        public string? ExpectedData { get; set; }
        public string SourceRole { get; set; } = string.Empty;
        public string DestinationRole { get; set; } = string.Empty;
        public string ActualFlags { get; set; } = string.Empty;
        public string ActualState { get; set; } = string.Empty;
        public string ActualSourceRole { get; set; } = string.Empty;
        public string ActualDestRole { get; set; } = string.Empty;
        public string? ActualData { get; set; }
        public bool Passed { get; set; }
    }
}
