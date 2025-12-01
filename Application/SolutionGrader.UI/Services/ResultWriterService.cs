using System.IO;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Service for writing grading results to Excel files in the SampleLogging format.
/// </summary>
public class ResultWriterService
{
    private readonly ILoggingService _logger;
    private readonly string _outputRoot;
    
    public ResultWriterService(ILoggingService logger, string outputRoot)
    {
        _logger = logger;
        _outputRoot = outputRoot;
    }
    
    /// <summary>
    /// Writes the StudentsSolution.xlsx summary file for all students.
    /// </summary>
    /// <param name="students">List of all students.</param>
    public void WriteStudentsSolutionSummary(List<StudentSolution> students)
    {
        try
        {
            // Group students by paper number
            var studentsByPaper = students.GroupBy(s => s.PaperNo);
            
            foreach (var paperGroup in studentsByPaper)
            {
                var paperNo = paperGroup.Key;
                var paperStudents = paperGroup.ToList();
                
                // Create paper-specific folder
                var paperFolder = Path.Combine(_outputRoot, paperNo);
                Directory.CreateDirectory(paperFolder);
                
                var summaryPath = Path.Combine(paperFolder, "StudentsSolution.xlsx");
                
                using var workbook = new XLWorkbook();
                var worksheet = workbook.AddWorksheet("Summary");
                
                // Write headers
                worksheet.Cell(1, 1).Value = "No";
                worksheet.Cell(1, 2).Value = "Student Code";
                worksheet.Cell(1, 3).Value = "Status";
                worksheet.Cell(1, 4).Value = "Mark";
                worksheet.Cell(1, 5).Value = "Max Mark";
                worksheet.Cell(1, 6).Value = "Percentage";
                worksheet.Cell(1, 7).Value = "Start Time";
                worksheet.Cell(1, 8).Value = "End Time";
                worksheet.Cell(1, 9).Value = "Duration (s)";
                worksheet.Cell(1, 10).Value = "Status Message";
                
                // Style header
                var headerRange = worksheet.Range(1, 1, 1, 10);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
                
                // Write student data
                int row = 2;
                foreach (var student in paperStudents)
                {
                    worksheet.Cell(row, 1).Value = row - 1;
                    worksheet.Cell(row, 2).Value = student.StudentCode;
                    worksheet.Cell(row, 3).Value = student.Status.ToString();
                    worksheet.Cell(row, 4).Value = student.Mark;
                    worksheet.Cell(row, 5).Value = student.MaxMark;
                    worksheet.Cell(row, 6).Value = student.MaxMark > 0 
                        ? (student.Mark / student.MaxMark * 100).ToString("F1") + "%" 
                        : "N/A";
                    worksheet.Cell(row, 7).Value = student.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                    worksheet.Cell(row, 8).Value = student.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                    
                    if (student.StartTime.HasValue && student.EndTime.HasValue)
                    {
                        var duration = (student.EndTime.Value - student.StartTime.Value).TotalSeconds;
                        worksheet.Cell(row, 9).Value = duration.ToString("F1");
                    }
                    
                    worksheet.Cell(row, 10).Value = student.StatusMessage ?? "";
                    
                    // Color code by status
                    var statusCell = worksheet.Cell(row, 3);
                    switch (student.Status)
                    {
                        case GradingStatus.Success:
                            statusCell.Style.Fill.BackgroundColor = XLColor.LightGreen;
                            break;
                        case GradingStatus.Failed:
                            statusCell.Style.Fill.BackgroundColor = XLColor.LightSalmon;
                            break;
                        case GradingStatus.InProgress:
                            statusCell.Style.Fill.BackgroundColor = XLColor.LightYellow;
                            break;
                    }
                    
                    row++;
                }
                
                // Auto-fit columns
                worksheet.Columns().AdjustToContents();
                
                workbook.SaveAs(summaryPath);
                _logger.LogInfo($"Wrote summary to: {summaryPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to write StudentsSolution.xlsx", ex);
        }
    }
    
    /// <summary>
    /// Writes the OverallSummary.xlsx file for a specific student.
    /// </summary>
    /// <param name="student">The student.</param>
    /// <param name="testCaseResults">Dictionary of test case name to (mark, maxMark, passed).</param>
    public void WriteStudentOverallSummary(
        StudentSolution student,
        Dictionary<string, (double Mark, double MaxMark, bool Passed)> testCaseResults)
    {
        try
        {
            // Create student folder (organized by paper)
            var studentFolder = Path.Combine(_outputRoot, student.PaperNo, "student", student.StudentCode);
            Directory.CreateDirectory(studentFolder);
            
            var summaryPath = Path.Combine(studentFolder, "OverallSummary.xlsx");
            
            using var workbook = new XLWorkbook();
            var worksheet = workbook.AddWorksheet("Overall");
            
            // Student info header
            worksheet.Cell(1, 1).Value = "Student Code";
            worksheet.Cell(1, 2).Value = student.StudentCode;
            worksheet.Cell(2, 1).Value = "Paper No";
            worksheet.Cell(2, 2).Value = student.PaperNo;
            worksheet.Cell(3, 1).Value = "Total Mark";
            worksheet.Cell(3, 2).Value = student.Mark;
            worksheet.Cell(4, 1).Value = "Max Mark";
            worksheet.Cell(4, 2).Value = student.MaxMark;
            worksheet.Cell(5, 1).Value = "Percentage";
            worksheet.Cell(5, 2).Value = student.MaxMark > 0 
                ? (student.Mark / student.MaxMark * 100).ToString("F1") + "%" 
                : "N/A";
            
            // Test case breakdown
            worksheet.Cell(7, 1).Value = "Test Case";
            worksheet.Cell(7, 2).Value = "Mark";
            worksheet.Cell(7, 3).Value = "Max Mark";
            worksheet.Cell(7, 4).Value = "Status";
            
            var headerRange = worksheet.Range(7, 1, 7, 4);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
            
            int row = 8;
            foreach (var tc in testCaseResults)
            {
                worksheet.Cell(row, 1).Value = tc.Key;
                worksheet.Cell(row, 2).Value = tc.Value.Mark;
                worksheet.Cell(row, 3).Value = tc.Value.MaxMark;
                worksheet.Cell(row, 4).Value = tc.Value.Passed ? "PASS" : "FAIL";
                
                var statusCell = worksheet.Cell(row, 4);
                statusCell.Style.Fill.BackgroundColor = tc.Value.Passed ? XLColor.LightGreen : XLColor.LightSalmon;
                
                row++;
            }
            
            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(summaryPath);
            _logger.LogInfo($"Wrote student summary to: {summaryPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to write OverallSummary.xlsx for {student.StudentCode}", ex);
        }
    }
}
