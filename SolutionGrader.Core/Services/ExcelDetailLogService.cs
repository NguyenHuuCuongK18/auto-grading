using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Excel detail logger:
    /// - Writes step rows into the case's GradeDetail workbook (with text wrap + adjusted widths).
    /// - Awards points all-or-nothing for the case (only if ALL compare steps passed).
    /// - Logs first diff index and context excerpts (expected vs got).
    /// - NO "ActualPath" column clutter.
    /// - Incrementally upserts OverallSummary.xlsx at the result root **after each case**.
    /// </summary>
    public sealed class ExcelDetailLogService : IDetailLogService, IDisposable
    {
        private readonly IFileService _files;

        private XLWorkbook? _wb;
        private string? _outPath;
        private string? _questionCode;
        private string? _overallSummaryPath;   // <— resultRoot/OverallSummary.xlsx

        private double _totalMark;
        private int _totalCompareSteps;
        private readonly List<StepGradeRecord> _records = new();
        private bool _allStepsPassed = true;
        private string? _failedTestDetailPath;

        // we keep summaries in-memory too, for end-of-suite WriteOverallSummary if the runner calls it
        private readonly List<TestCaseSummary> _caseSummaries = new();

        // Sheets expected in Detail.xlsx
        private const string SheetInput = "InputClients";
        private const string SheetOutClients = "OutputClients";
        private const string SheetOutServers = "OutputServers";

        // Columns we always ensure exist
        private static readonly string[] BaseColumns = { "Stage", "Input", "DataType", "Action" };
        private static readonly string[] ResultColumns = {
            "Result","ErrorCode","ErrorCategory","PointsAwarded","PointsPossible",
            "DurationMs","DetailPath","Message","DiffIndex","ExpectedExcerpt","ActualExcerpt"
        };

        public ExcelDetailLogService(IFileService files) => _files = files;

        public void BeginCase(string outFolder, string questionCode, string detailTemplatePath, double pointsPossible)
        {
            _files.EnsureDirectory(outFolder);

            _questionCode = questionCode;
            _outPath = Path.Combine(outFolder, "GradeDetail.xlsx");
            _failedTestDetailPath = Path.Combine(outFolder, "FailedTestDetail.xlsx");

            // result root is parent of the case folder; that’s where OverallSummary.xlsx lives
            var resultRoot = Path.GetDirectoryName(outFolder);
            _overallSummaryPath = string.IsNullOrEmpty(resultRoot) ? null : Path.Combine(resultRoot!, "OverallSummary.xlsx");

            _totalMark = 0;
            _totalCompareSteps = 0;
            _allStepsPassed = true;
            _records.Clear();

            _wb = new XLWorkbook(detailTemplatePath);

            foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;

                EnsureColumns(ws, BaseColumns);
                EnsureColumns(ws, ResultColumns);

                if (!sheetName.Equals(SheetInput, StringComparison.OrdinalIgnoreCase))
                {
                    var rng = ws.RangeUsed();
                    if (rng != null)
                    {
                        // compare step = row has any content in non-header rows
                        _totalCompareSteps += rng.RowsUsed().Skip(1).Count();
                    }
                }
            }

            // If template has 0 compare rows, we still allow compare steps coming from execution plan.
            _totalMark = pointsPossible;
        }

        public void EndCase()
        {
            if (_wb == null || _outPath == null) return;

            // Award all-or-nothing on each row that has points
            foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;
                var hdr = GetHeaderIndex(ws);
                if (!hdr.TryGetValue("PointsAwarded", out var awardedCol) ||
                    !hdr.TryGetValue("PointsPossible", out var possibleCol))
                    continue;

                var rng = ws.RangeUsed();
                if (rng == null) continue;

                foreach (var row in rng.RowsUsed().Skip(1))
                {
                    if (row.Cell(possibleCol).TryGetValue<double>(out var p) && p > 0)
                    {
                        row.Cell(awardedCol).Value = _allStepsPassed ? p : 0;
                    }
                }

                // Wrap and adjust for readability
                ws.Style.Alignment.WrapText = true;
                ws.Columns().AdjustToContents(1, ws.LastRowUsed().RowNumber(), 5, 80);
            }

            // Totals for this case → feed in-memory and incremental summary
            var (casePassed, totalAwarded, totalPossible) = ComputeCaseTotals();
            if (_questionCode != null)
            {
                _caseSummaries.Add(new TestCaseSummary
                {
                    TestCase = _questionCode,
                    Passed = casePassed,
                    PointsAwarded = Math.Round(totalAwarded, 2),
                    PointsPossible = Math.Round(totalPossible, 2)
                });
            }

            // Save case workbook
            using (var s = _files.OpenWrite(_outPath))
                _wb.SaveAs(s);

            // 🔁 NEW: make sure the overall summary exists/updates after **every** case
            if (!string.IsNullOrEmpty(_overallSummaryPath) && _questionCode != null)
            {
                UpsertOverallSummaryRow(
                    _overallSummaryPath!,
                    _questionCode,
                    casePassed,
                    Math.Round(totalAwarded, 2),
                    Math.Round(totalPossible, 2));
            }

            // Optionally create a compact FailedTestDetail.xlsx (only when there are failures)
            WriteFailedTestDetailIfAny();

            // dispose workbook to avoid file locks between cases
            _wb.Dispose();
            _wb = null;
        }

        public void SetTestCaseMark(double mark) => _totalMark = mark;
        public void SetTotalCompareSteps(int count) => _totalCompareSteps = count;

        public void LogStepGrade(
            Step step,
            bool passed,
            string message,
            double pointsAwarded,
            double pointsPossible,
            double durationMs,
            string errorCode,
            string? detailPath = null,
            string? actualPath = null)
        {
            if (_wb == null || _outPath == null) return;

            var errorCategory = ErrorCodes.CategoryOf(errorCode).ToString();

            if (!passed && pointsPossible > 0) _allStepsPassed = false;

            var sheet = ResolveSheet(step, actualPath);
            if (!_wb.Worksheets.TryGetWorksheet(sheet, out var ws)) return;

            var hdr = GetHeaderIndex(ws);
            var stage = ParseStage(step.Id);
            var rowNum = FindRowByStage(ws, hdr, stage) ?? AppendStageRow(ws, hdr, stage);

            // per-step possible points are equal split of the case's total mark across compare steps
            var perStep = _totalCompareSteps > 0 ? _totalMark / _totalCompareSteps : 0;
            var actualPossible = pointsPossible > 0 ? perStep : 0;

            SetCell(ws, rowNum, hdr, "Result", passed ? "PASS" : "FAIL");
            SetCell(ws, rowNum, hdr, "ErrorCode", errorCode);
            SetCell(ws, rowNum, hdr, "ErrorCategory", errorCategory);
            SetCell(ws, rowNum, hdr, "PointsAwarded", 0);             // awarded later in EndCase
            SetCell(ws, rowNum, hdr, "PointsPossible", actualPossible);
            SetCell(ws, rowNum, hdr, "DurationMs", Math.Round(durationMs, 2));
            SetCell(ws, rowNum, hdr, "DetailPath", detailPath ?? "");
            SetCell(ws, rowNum, hdr, "Message", message ?? "");

            // If we have a text diff, write index + short excerpts
            TryWriteDiffColumns(ws, hdr, rowNum, stage, detailPath, message, actualPath);

            _records.Add(new StepGradeRecord
            {
                QuestionCode = step.QuestionCode,
                StepId = step.Id,
                Stage = step.Stage,
                Action = step.Action,
                Passed = passed,
                PointsAwarded = 0,
                PointsPossible = actualPossible,
                DurationMs = durationMs,
                ErrorCode = errorCode,
                ErrorCategory = ErrorCodes.CategoryOf(errorCode),
                Message = message ?? "",
                DetailPath = detailPath,
                ActualPath = actualPath
            });
        }

        public void LogCaseSummary(string questionCode, bool passed, double pointsAwarded, double pointsPossible, string message)
        {
            // no-op: this service derives summary from rows (EndCase)
        }

        public string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail)
        {
            var mismRoot = Path.Combine(Path.GetDirectoryName(_outPath!)!, "mismatches", questionCode);
            _files.EnsureDirectory(mismRoot);
            var outPath = Path.Combine(mismRoot, $"stage_{stage}.diff.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Question: {questionCode} | Stage: {stage}");
            sb.AppendLine($"FirstDiffIndex: {detail.FirstDiffIndex}");
            sb.AppendLine();
            sb.AppendLine("From test case (expected):");
            sb.AppendLine(detail.ExpectedContext ?? "");
            sb.AppendLine();
            sb.AppendLine("Got:");
            sb.AppendLine(detail.ActualContext ?? "");
            sb.AppendLine();

            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            return outPath;
        }

        public void WriteOverallSummary()
        {
            // still support end-of-suite write, but not required anymore
            if (string.IsNullOrEmpty(_overallSummaryPath) || _caseSummaries.Count == 0) return;

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Summary");

            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Pass/Fail";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";

            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            int r = 2;
            foreach (var s in _caseSummaries)
            {
                ws.Cell(r, 1).Value = s.TestCase;
                ws.Cell(r, 2).Value = s.Passed ? "PASS" : "FAIL";
                ws.Cell(r, 3).Value = s.PointsAwarded;
                ws.Cell(r, 4).Value = s.PointsPossible;
                r++;
            }

            ws.Cell(r, 1).Value = "TOTAL";
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 3).Value = Math.Round(_caseSummaries.Sum(x => x.PointsAwarded), 2);
            ws.Cell(r, 3).Style.Font.Bold = true;
            ws.Cell(r, 4).Value = Math.Round(_caseSummaries.Sum(x => x.PointsPossible), 2);
            ws.Cell(r, 4).Style.Font.Bold = true;

            ws.Columns().AdjustToContents(1, ws.LastRowUsed().RowNumber(), 5, 60);

            wb.SaveAs(_overallSummaryPath);
        }

        public void LogSkip(Step step, string reason, string errorCode)
        {
            // Keep the row shape consistent; skipped steps never contribute points.
            LogStepGrade(step, false, reason, 0, 0, 0, errorCode, null, null);
        }

        // ---------- helpers ----------

        private void TryWriteDiffColumns(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string? detailPath, string? message, string? actualPath)
        {
            try
            {
                if (string.IsNullOrEmpty(detailPath) || !File.Exists(detailPath)) return;

                // Diff index if it’s in the message
                var idx = FirstDiffIndexFromMessage(message ?? "");
                if (idx >= 0) SetCell(ws, rowNum, hdr, "DiffIndex", idx);

                // Heuristics: if we know expected/actual paths, put short excerpts in the sheet
                // (Callers generate .diff.txt as well; we only store short snippets here.)
                var exp = TryReadContext(detailPath, 2000);
                var act = TryReadContext(actualPath, 2000);
                if (!string.IsNullOrEmpty(exp)) SetCell(ws, rowNum, hdr, "ExpectedExcerpt", exp);
                if (!string.IsNullOrEmpty(act)) SetCell(ws, rowNum, hdr, "ActualExcerpt", act);
            }
            catch { /* best effort */ }
        }

        private static string? TryReadContext(string? path, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var txt = File.ReadAllText(path);
            if (txt.Length > maxChars) txt = txt.Substring(0, maxChars) + "...";
            return txt;
        }

        private (bool casePassed, double awarded, double possible) ComputeCaseTotals()
        {
            bool anyFail = _records.Any(r => r.PointsPossible > 0 && !r.Passed);
            bool passed = !anyFail;
            double possible = Math.Round(_records.Sum(r => r.PointsPossible), 2);
            double awarded = passed ? possible : 0;
            return (passed, awarded, possible);
        }

        private void UpsertOverallSummaryRow(string summaryPath, string testCase, bool passed, double pointsAwarded, double pointsPossible)
        {
            using XLWorkbook wb = File.Exists(summaryPath) ? LoadExistingWorkbook(summaryPath) : CreateNewWorkbook();
            var ws = wb.Worksheets.FirstOrDefault() ?? wb.AddWorksheet("Summary");

            // Find existing row by TestCase
            var last = ws.LastRowUsed()?.RowNumber() ?? 1;
            int? found = null;
            for (int r = 2; r <= last; r++)
            {
                if (string.Equals(ws.Cell(r, 1).GetString(), testCase, StringComparison.OrdinalIgnoreCase))
                {
                    found = r; break;
                }
            }

            var row = found ?? (last + 1);
            ws.Cell(row, 1).Value = testCase;
            ws.Cell(row, 2).Value = passed ? "PASS" : "FAIL";
            ws.Cell(row, 3).Value = pointsAwarded;
            ws.Cell(row, 4).Value = pointsPossible;

            // Autofit & wrap for readability
            for (int c = 1; c <= 4; c++)
            {
                ws.Column(c).Style.Alignment.WrapText = true;
                ws.Column(c).AdjustToContents(1, ws.LastRowUsed().RowNumber(), 5, 60);
            }

            using (var sw = _files.OpenWrite(summaryPath))
            {
                wb.SaveAs(sw);
            }
        }

        private XLWorkbook LoadExistingWorkbook(string path)
        {
            using var sr = _files.OpenRead(path);
            return new XLWorkbook(sr);
        }

        private static XLWorkbook CreateNewWorkbook()
        {
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Summary");
            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";
            ws.Row(1).Style.Font.Bold = true;
            return wb;
        }

        private static string ResolveSheet(Step step, string? actualPath)
        {
            // prefer actual path hint to decide client/server
            var lower = (actualPath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (lower.Contains("/actual/clients/")) return SheetOutClients;
            if (lower.Contains("/actual/servers/")) return SheetOutServers;

            // fallback by action
            var action = (step.Action ?? "").ToLowerInvariant();
            if (action.Contains("server")) return SheetOutServers;
            return SheetOutClients;
        }

        private static int ParseStage(string id)
        {
            var lastDash = id?.LastIndexOf('-') ?? -1;
            if (lastDash >= 0 && int.TryParse(id.Substring(lastDash + 1), out var s)) return s;
            return 0;
        }

        private static Dictionary<string, int> GetHeaderIndex(IXLWorksheet ws)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var row1 = ws.Row(1);
            int col = 1;
            while (!row1.Cell(col).IsEmpty())
            {
                var name = row1.Cell(col).GetString().Trim();
                if (!string.IsNullOrEmpty(name)) map[name] = col;
                col++;
            }
            return map;
        }

        private static void EnsureColumns(IXLWorksheet ws, IEnumerable<string> names)
        {
            var hdr = GetHeaderIndex(ws);
            int col = hdr.Count + 1;
            foreach (var n in names)
            {
                if (!hdr.ContainsKey(n))
                {
                    ws.Cell(1, col).Value = n;
                    col++;
                }
            }
            ws.Row(1).Style.Font.Bold = true;
        }

        private static int? FindRowByStage(IXLWorksheet ws, Dictionary<string, int> hdr, int stage)
        {
            if (!hdr.TryGetValue("Stage", out var c)) return null;
            var rng = ws.RangeUsed();
            if (rng == null) return null;
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                if (int.TryParse(row.Cell(c).GetString(), out var s) && s == stage)
                    return row.RowNumber();
            }
            return null;
        }

        private static int AppendStageRow(IXLWorksheet ws, Dictionary<string, int> hdr, int stage)
        {
            var newRow = (ws.LastRowUsed()?.RowNumber() ?? 1) + 1;
            if (hdr.TryGetValue("Stage", out var c)) ws.Cell(newRow, c).Value = stage;
            return newRow;
        }

        private static void SetCell(IXLWorksheet ws, int row, Dictionary<string, int> hdr, string name, object? value)
        {
            if (!hdr.TryGetValue(name, out var c)) return;
            if (value == null)
            {
                ws.Cell(row, c).Value = "";
            }
            else
            {
                ws.Cell(row, c).Value = XLCellValue.FromObject(value);
            }
        }

        private static int FirstDiffIndexFromMessage(string message)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message ?? "", @"(\d+)");
            return m.Success ? int.Parse(m.Value) : -1;
        }

        private void WriteFailedTestDetailIfAny()
        {
            if (_wb == null || string.IsNullOrEmpty(_failedTestDetailPath)) return;

            var failed = new List<(string Sheet, int Row, string Stage, string Result, string Message, string DetailPath)>();

            foreach (var sheetName in new[] { SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var worksheet)) continue;
                var hdr = GetHeaderIndex(worksheet);
                if (!hdr.TryGetValue("Result", out var resultCol)) continue;

                var rng = worksheet.RangeUsed();
                if (rng == null) continue;

                foreach (var row in rng.RowsUsed().Skip(1))
                {
                    var result = row.Cell(resultCol).GetString();
                    if (!result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        var stageCol = hdr.TryGetValue("Stage", out var sc) ? sc : 0;
                        var messageCol = hdr.TryGetValue("Message", out var mc) ? mc : 0;
                        var detailCol = hdr.TryGetValue("DetailPath", out var dc) ? dc : 0;

                        failed.Add((
                            sheetName,
                            row.RowNumber(),
                            stageCol > 0 ? row.Cell(stageCol).GetString() : "",
                            result,
                            messageCol > 0 ? row.Cell(messageCol).GetString() : "",
                            detailCol > 0 ? row.Cell(detailCol).GetString() : ""
                        ));
                    }
                }
            }

            if (failed.Count == 0) return;

            using var workbook = new XLWorkbook();
            var failedSheet = workbook.AddWorksheet("FailedTests");

            failedSheet.Cell(1, 1).Value = "Sheet";
            failedSheet.Cell(1, 2).Value = "Row";
            failedSheet.Cell(1, 3).Value = "Stage";
            failedSheet.Cell(1, 4).Value = "Result";
            failedSheet.Cell(1, 5).Value = "Message";
            failedSheet.Cell(1, 6).Value = "DetailPath";
            failedSheet.Row(1).Style.Font.Bold = true;

            int r = 2;
            foreach (var f in failed)
            {
                failedSheet.Cell(r, 1).Value = f.Sheet;
                failedSheet.Cell(r, 2).Value = f.Row;
                failedSheet.Cell(r, 3).Value = f.Stage;
                failedSheet.Cell(r, 4).Value = f.Result;
                failedSheet.Cell(r, 5).Value = f.Message;
                failedSheet.Cell(r, 6).Value = f.DetailPath;
                r++;
            }

            failedSheet.Columns().AdjustToContents(1, failedSheet.LastRowUsed().RowNumber(), 5, 80);

            using var s = _files.OpenWrite(_failedTestDetailPath!);
            workbook.SaveAs(s);
        }

        public void Dispose() => _wb?.Dispose();
    }
}
