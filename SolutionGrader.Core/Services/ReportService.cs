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
                r++;
            }
            using var fs = _files.OpenWrite(xlsxPath, overwrite:true);
            wb.SaveAs(fs);
        }

        var csvPath = System.IO.Path.Combine(outFolder, $"{questionCode}_Result.csv");
        using (var sw = new System.IO.StreamWriter(_files.OpenWrite(csvPath, overwrite:true)))
        {
            sw.WriteLine("StepId,Stage,Action,Passed,Message,DurationMs");
            foreach (var s in steps)
            {
                var msg = (s.Message ?? "").Replace("\"","\"\"");
                sw.WriteLine($"{s.Step.Id},{s.Step.Stage},{s.Step.Action},{s.Passed},\"{msg}\",{s.DurationMs:0}");
            }
        }

        return System.Threading.Tasks.Task.FromResult(xlsxPath);
    }
}
