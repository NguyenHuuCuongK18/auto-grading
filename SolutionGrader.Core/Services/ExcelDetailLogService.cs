using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SolutionGrader.Core.Services
{
    public sealed class ExcelDetailLogService : IDetailLogService, IDisposable
    {
        private readonly IFileService _files;
        private XLWorkbook? _wb;
        private string? _outPath;
        private string? _questionCode;
        private double _caseTotalPoints;
        private readonly List<StepGradeRecord> _records = new();

        // Sheets matching Detail.xlsx
        private const string SheetInput = "InputClients";
        private const string SheetOutClients = "OutputClients";
        private const string SheetOutServers = "OutputServers";
        private const string SheetSummary = "Summary";

        // Columns from Detail.xlsx (as per your file)
        private static readonly string[] BaseColumns = new[] { "Stage", "Input", "DataType", "Action" };

        // Appended result columns
        private static readonly string[] ResultColumns = new[]
        {
            "Result", "ErrorCode", "ErrorCategory", "PointsAwarded", "PointsPossible",
            "DurationMs", "DetailPath", "Message", "ActualPath"
        };

        public ExcelDetailLogService(IFileService files) => _files = files;

        public void BeginCase(string outFolder, string questionCode, string detailTemplatePath, double pointsPossible)
        {
            _files.EnsureDirectory(outFolder);
            _questionCode = questionCode;
            _outPath = Path.Combine(outFolder, "GradeDetail.xlsx");
            _caseTotalPoints = pointsPossible;
            _records.Clear();

            // Load template Detail.xlsx if available, then append result columns to each sheet
            _wb = new XLWorkbook(detailTemplatePath);

            foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;

                // Ensure base columns are present; then add result columns if missing
                EnsureColumns(ws, BaseColumns);
                EnsureColumns(ws, ResultColumns);
            }

            EnsureSummarySheet();
        }

        public void EndCase()
        {
            if (_wb != null && _outPath != null)
            {
                foreach (var ws in _wb.Worksheets)
                {
                    ws.Columns().AdjustToContents();
                    ws.Rows().AdjustToContents();
                }

                try { _wb.SaveAs(_outPath); } catch { }
            }
            _wb?.Dispose();
            _wb = null;
            _outPath = null;
            _questionCode = null;
            _records.Clear();
            _caseTotalPoints = 0;
        }

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

            var sheet = ResolveSheet(step, actualPath);
            if (!_wb.Worksheets.TryGetWorksheet(sheet, out var ws))
                return; // not a sheet we track (e.g., SETUP-only steps)

            var stage = ParseStage(step.Id);

            // Find the row by Stage (first match). If not found, append new row with Stage populated.
            var hdr = GetHeaderIndex(ws);
            var row = FindRowByStage(ws, hdr, stage);
            if (row == null)
                row = AppendStageRow(ws, hdr, stage);

            // Write result columns
            SetCell(ws, row.Value, hdr, "Result", passed ? "PASS" : "FAIL");
            SetCell(ws, row.Value, hdr, "ErrorCode", errorCode);
            SetCell(ws, row.Value, hdr, "ErrorCategory", ErrorCodes.CategoryOf(errorCode).ToString());
            SetCell(ws, row.Value, hdr, "PointsAwarded", pointsAwarded);
            SetCell(ws, row.Value, hdr, "PointsPossible", pointsPossible);
            SetCell(ws, row.Value, hdr, "DurationMs", (int)Math.Round(durationMs));
            SetCell(ws, row.Value, hdr, "DetailPath", detailPath ?? "");
            SetCell(ws, row.Value, hdr, "Message", message ?? "");
            if (hdr.TryGetValue("Message", out var msgCol))
            {
                var msgCell = ws.Cell(row.Value, msgCol);
                msgCell.Style.Alignment.WrapText = true;
            }
            SetCell(ws, row.Value, hdr, "ActualPath", actualPath ?? "");

            // Also make sure the Action column is set (if empty), to mirror template
            var actionCell = hdr.TryGetValue("Action", out var aCol) ? ws.Cell(row.Value, aCol) : null;
            if (actionCell != null && string.IsNullOrWhiteSpace(actionCell.GetString()))
                actionCell.Value = step.Action;

            _records.Add(new StepGradeRecord
            {
                QuestionCode = _questionCode ?? step.QuestionCode,
                StepId = step.Id,
                Stage = step.Stage,
                Action = step.Action,
                Passed = passed,
                PointsAwarded = pointsAwarded,
                PointsPossible = pointsPossible,
                ErrorCode = errorCode,
                ErrorCategory = ErrorCodes.CategoryOf(errorCode),
                DetailPath = detailPath,
                ActualPath = actualPath,
                Message = message,
                DurationMs = durationMs
            });
        }

        public void LogCaseSummary(string questionCode, bool passed, double pointsAwarded, double pointsPossible, string message)
        {
            if (_wb == null || _outPath == null) return;

            var ws = EnsureSummarySheet();
            var rowIndex = 2;
            ws.Row(rowIndex).Clear(XLClearOptions.Contents);
            var totalPoints = pointsPossible > 0 ? pointsPossible : (_caseTotalPoints > 0 ? _caseTotalPoints : 0);

            ws.Cell(rowIndex, 1).Value = questionCode;
            ws.Cell(rowIndex, 2).Value = passed ? "PASS" : "FAIL";
            ws.Cell(rowIndex, 3).Value = message;
            ws.Cell(rowIndex, 3).Style.Alignment.WrapText = true;
            ws.Cell(rowIndex, 4).Value = pointsAwarded;
            ws.Cell(rowIndex, 5).Value = totalPoints;

            if (!passed)
            {
                WriteFailedTestDetailWorkbook(questionCode);
            }
        }

        public string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail)
        {
            if (_outPath == null) return string.Empty;
            var folder = Path.Combine(Path.GetDirectoryName(_outPath)!, "mismatches", questionCode);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{stage}.diff.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Step: {questionCode}-{stage}");
            sb.AppendLine($"Expected: {expectedPath}");
            sb.AppendLine($"Actual  : {actualPath}");
            sb.AppendLine($"First diff at index: {detail.FirstDiffIndex}");
            sb.AppendLine();
            sb.AppendLine("[Expected]");
            sb.AppendLine(detail.ExpectedContext);
            sb.AppendLine();
            sb.AppendLine("[Actual]");
            sb.AppendLine(detail.ActualContext);
            sb.AppendLine();
            sb.AppendLine(detail.Message);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        public void LogSkip(Step step, string reason, string errorCode)
        {
            LogStepGrade(step, true, $"SKIP: {reason}", 0, 0, 0, errorCode, null, null);
        }

        private IXLWorksheet EnsureSummarySheet()
        {
            if (_wb == null) throw new InvalidOperationException("Workbook not initialized");
            if (!_wb.Worksheets.TryGetWorksheet(SheetSummary, out var ws))
            {
                ws = _wb.AddWorksheet(SheetSummary);
                ws.Cell(1, 1).Value = "Question";
                ws.Cell(1, 2).Value = "Result";
                ws.Cell(1, 3).Value = "Message";
                ws.Cell(1, 4).Value = "PointsAwarded";
                ws.Cell(1, 5).Value = "PointsPossible";
                ws.Row(1).Style.Font.Bold = true;
            }
            return ws;
        }

        private void WriteFailedTestDetailWorkbook(string questionCode)
        {
            if (_outPath == null) return;

            var failed = _records.Where(r => !r.Passed).ToList();
            if (failed.Count == 0) return;

            var folder = Path.GetDirectoryName(_outPath)!;
            var failPath = Path.Combine(folder, "FailedTestDetail.xlsx");

            XLWorkbook failWb;
            IXLWorksheet ws;
            if (File.Exists(failPath))
            {
                failWb = new XLWorkbook(failPath);
                ws = failWb.Worksheets.FirstOrDefault() ?? failWb.AddWorksheet("FailedTests");
            }
            else
            {
                failWb = new XLWorkbook();
                ws = failWb.AddWorksheet("FailedTests");
            }

            if (ws.Cell(1, 1).IsEmpty())
            {
                ws.Cell(1, 1).Value = "Question";
                ws.Cell(1, 2).Value = "StepId";
                ws.Cell(1, 3).Value = "Stage";
                ws.Cell(1, 4).Value = "Action";
                ws.Cell(1, 5).Value = "Result";
                ws.Cell(1, 6).Value = "ErrorCode";
                ws.Cell(1, 7).Value = "ErrorCategory";
                ws.Cell(1, 8).Value = "DetailPath";
                ws.Cell(1, 9).Value = "ActualPath";
                ws.Cell(1, 10).Value = "Message";
                ws.Cell(1, 11).Value = "DurationMs";
                ws.Row(1).Style.Font.Bold = true;
            }

            var nextRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            foreach (var record in failed)
            {
                nextRow++;
                ws.Cell(nextRow, 1).Value = questionCode;
                ws.Cell(nextRow, 2).Value = record.StepId;
                ws.Cell(nextRow, 3).Value = record.Stage;
                ws.Cell(nextRow, 4).Value = record.Action;
                ws.Cell(nextRow, 5).Value = "FAIL";
                ws.Cell(nextRow, 6).Value = record.ErrorCode;
                ws.Cell(nextRow, 7).Value = record.ErrorCategory.ToString();
                ws.Cell(nextRow, 8).Value = record.DetailPath ?? "";
                ws.Cell(nextRow, 9).Value = record.ActualPath ?? "";
                ws.Cell(nextRow, 10).Value = record.Message;
                ws.Cell(nextRow, 10).Style.Alignment.WrapText = true;
                ws.Cell(nextRow, 11).Value = (int)Math.Round(record.DurationMs);
            }

            ws.Columns().AdjustToContents();
            ws.Rows().AdjustToContents();

            failWb.SaveAs(failPath);
            failWb.Dispose();
        }

        private static int ParseStage(string id)
        {
            var lastDash = id?.LastIndexOf('-') ?? -1;
            if (lastDash >= 0 && lastDash + 1 < id!.Length && int.TryParse(id.Substring(lastDash + 1), out var s)) return s;
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

        private static int AppendStageRow(IXLWorksheet ws, Dictionary<string, int> hdr, int stage)
        {
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            var newRow = lastRow + 1;
            if (hdr.TryGetValue("Stage", out var c))
                ws.Cell(newRow, c).Value = stage;
            return newRow;
        }

        private static int? FindRowByStage(IXLWorksheet ws, Dictionary<string, int> hdr, int stage)
        {
            if (!hdr.TryGetValue("Stage", out var col)) return null;
            var rng = ws.RangeUsed();
            if (rng == null) return null;
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                var v = row.Cell(col).GetString();
                if (int.TryParse(v, out var s) && s == stage) return row.RowNumber();
            }
            return null;
        }

        private static void EnsureColumns(IXLWorksheet ws, IEnumerable<string> columns)
        {
            var hdr = GetHeaderIndex(ws);
            var nextCol = (hdr.Count == 0 ? 1 : hdr.Values.Max() + 1);
            foreach (var name in columns)
            {
                if (!hdr.ContainsKey(name))
                {
                    ws.Cell(1, nextCol).Value = name;
                    hdr[name] = nextCol;
                    nextCol++;
                }
            }
        }

        private static void SetCell(IXLWorksheet ws, int row, Dictionary<string, int> hdr, string colName, object? value)
        {
            if (!hdr.TryGetValue(colName, out var col)) return;
            ws.Cell(row, col).Value = (XLCellValue)(value ?? "");
        }

        private static string ResolveSheet(Step step, string? actualPath)
        {
            // INPUT sheet
            if (string.Equals(step.Stage, "INPUT", StringComparison.OrdinalIgnoreCase))
                return SheetInput;

            // If path indicates clients/servers, choose by path
            if (!string.IsNullOrEmpty(actualPath))
            {
                var lower = actualPath.Replace('\\', '/').ToLowerInvariant();
                if (lower.Contains("/actual/servers/")) return SheetOutServers;
                if (lower.Contains("/actual/clients/")) return SheetOutClients;
            }

            // Heuristics by action
            var action = step.Action?.ToUpperInvariant() ?? "";
            if (action.Contains("HTTP") || action.Contains("CLIENT"))
                return SheetOutClients;

            return SheetOutServers;
        }

        public void Dispose()
        {
            try { _wb?.Dispose(); } catch { }
        }
    }
}
