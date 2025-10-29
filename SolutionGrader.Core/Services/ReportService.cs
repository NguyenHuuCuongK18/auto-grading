namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;

public sealed class ReportService : IReportService
{
    private readonly IFileService _files;
    public ReportService(IFileService files) => _files = files;

    public System.Threading.Tasks.Task<string> WriteQuestionResultAsync(string outFolder, string questionCode, System.Collections.Generic.IReadOnlyList<StepResult> steps, System.Threading.CancellationToken ct)
    {
        _files.EnsureDirectory(outFolder);
        var xlsxPath = System.IO.Path.Combine(outFolder, $"{questionCode}_Result.xlsx");

        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Result");
            ws.Cell(1,1).Value = "StepId";
            ws.Cell(1,2).Value = "Stage";
            ws.Cell(1,3).Value = "Action";
            ws.Cell(1,4).Value = "Passed";
            ws.Cell(1,5).Value = "Message";
            ws.Cell(1,6).Value = "DurationMs";

            // Format header
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var r = 2;
            foreach (var s in steps)
            {
                ct.ThrowIfCancellationRequested();
                ws.Cell(r,1).Value = s.Step.Id;
                ws.Cell(r,2).Value = s.Step.Stage;
                ws.Cell(r,3).Value = s.Step.Action;
                ws.Cell(r,4).Value = s.Passed;
                ws.Cell(r,5).Value = s.Message;
                ws.Cell(r,6).Value = s.DurationMs;

                // Color code Passed column
                if (s.Passed)
                    ws.Cell(r, 4).Style.Font.FontColor = XLColor.Green;
                else
                    ws.Cell(r, 4).Style.Font.FontColor = XLColor.Red;

                r++;
            }

            // Enable wrap text for message column and auto-fit
            ws.Column(5).Style.Alignment.WrapText = true;
            ws.Column(5).Width = 60;
            
            // Auto-fit other columns
            for (int c = 1; c <= 6; c++)
            {
                if (c != 5) // Skip message column
                    ws.Column(c).AdjustToContents();
            }

            using var fs = _files.OpenWrite(xlsxPath, overwrite:true);
            wb.SaveAs(fs);
        }

        return System.Threading.Tasks.Task.FromResult(xlsxPath);
    }
}
