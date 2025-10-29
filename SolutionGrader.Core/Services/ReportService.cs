namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;

public sealed class ReportService : IReportService
{
    private readonly IFileService _files;
    public ReportService(IFileService files) => _files = files;

    public async System.Threading.Tasks.Task<string> WriteQuestionResultAsync(string outFolder, string questionCode, System.Collections.Generic.IReadOnlyList<StepResult> steps, System.Threading.CancellationToken ct)
    {
        _files.EnsureDirectory(outFolder);
        var xlsxPath = System.IO.Path.Combine(outFolder, $"{questionCode}_Result.xlsx");

        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Result");

            ws.Cell(1, 1).Value = "StepId";
            ws.Cell(1, 2).Value = "Stage";
            ws.Cell(1, 3).Value = "Action";
            ws.Cell(1, 4).Value = "Passed";
            ws.Cell(1, 5).Value = "Message";
            ws.Cell(1, 6).Value = "DurationMs";

            // Header style
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Row(1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            var r = 2;
            foreach (var s in steps)
            {
                ws.Cell(r, 1).Value = s.Step.Id;
                ws.Cell(r, 2).Value = s.Step.Stage;
                ws.Cell(r, 3).Value = s.Step.Action;
                ws.Cell(r, 4).Value = s.Passed;
                ws.Cell(r, 5).Value = s.Message;
                ws.Cell(r, 6).Value = s.DurationMs;
                r++;
            }

            // Make message readable
            ws.Column(5).Style.Alignment.WrapText = true;

            // Auto-fit but keep columns reasonably wide
            for (int c = 1; c <= 6; c++)
            {
                ws.Column(c).AdjustToContents(1, ws.LastRowUsed().RowNumber(), 5, 60);
            }

            using var stream = _files.OpenWrite(xlsxPath);
            wb.SaveAs(stream);
        }

        return await System.Threading.Tasks.Task.FromResult(xlsxPath);
    }
}
