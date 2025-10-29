using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
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
        private string? _overallSummaryPath;
        private double _totalMark;
        private int _totalCompareSteps;
        private double _caseTotalPoints;
        private readonly List<StepGradeRecord> _records = new();
        private bool _allStepsPassed = true; // Track if all steps passed
        private string? _failedTestDetailPath; // Path to FailedTestDetail.xlsx

        // Summary data for overall report
        private readonly List<TestCaseSummary> _caseSummaries = new();

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
            _allStepsPassed = true; // Reset for new test case
            _failedTestDetailPath = Path.Combine(outFolder, "FailedTestDetail.xlsx");

            // Set overall summary path (one level up from test case folder)
            var resultRoot = Path.GetDirectoryName(outFolder);
            if (!string.IsNullOrEmpty(resultRoot))
            {
                _overallSummaryPath = Path.Combine(resultRoot, "OverallSummary.xlsx");
            }

            // Load template Detail.xlsx if available, then append result columns to each sheet
            _wb = new XLWorkbook(detailTemplatePath);

            // Count total compare steps (rows with Output filled) and read mark
            _totalCompareSteps = 0;
            _totalMark = 0;

            foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;

                // Ensure base columns are present; then add result columns if missing
                EnsureColumns(ws, BaseColumns);
                EnsureColumns(ws, ResultColumns);

                // Count compare steps (rows with data, excluding INPUT sheet)
                if (!sheetName.Equals(SheetInput, StringComparison.OrdinalIgnoreCase))
                {
                    var rng = ws.RangeUsed();
                    if (rng != null)
                    {
                        _totalCompareSteps += rng.RowsUsed().Skip(1).Count();
                    }
                }
            }

            // Read Mark from Header sheet in the template if it exists
            if (_wb.Worksheets.TryGetWorksheet(SuiteKeywords.Sheet_Header, out var headerSheet))
            {
                var hdr = GetHeaderIndex(headerSheet);
                if (hdr.TryGetValue("TestCase", out var tcCol) && hdr.TryGetValue("Mark", out var markCol))
                {
                    var rng = headerSheet.RangeUsed();
                    if (rng != null)
                    {
                        foreach (var row in rng.RowsUsed().Skip(1))
                        {
                            var tc = row.Cell(tcCol).GetString().Trim();
                            if (string.Equals(tc, questionCode, StringComparison.OrdinalIgnoreCase))
                            {
                                var markStr = row.Cell(markCol).GetString().Trim();
                                double.TryParse(markStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _totalMark);
                                break;
                            }
                        }
                    }
                }
            }

            // Fallback: if mark not found in Detail template, we'll use the total from the suite
            if (_totalMark == 0) _totalMark = 1; // Default to 1 if no mark specified

            // Don't save yet - accumulate changes in memory
        }

        public void EndCase()
        {
            try 
            { 
                if (_wb != null && _outPath != null)
                {
                    // Update points awarded based on whether ALL steps passed
                    // Only award points if _allStepsPassed is true
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
                            var possibleStr = row.Cell(possibleCol).GetString();
                            if (double.TryParse(possibleStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0)
                            {
                                // Award points only if all steps passed
                                double awarded = _allStepsPassed ? p : 0;
                                SetCell(ws, row.RowNumber(), hdr, "PointsAwarded", Math.Round(awarded, 2));
                            }
                        }
                    }

                    // Calculate summary for this case
                    double totalPoints = 0, totalPossible = 0;
                    bool allPassed = _allStepsPassed;
                    var failedSteps = new List<(string Sheet, int Row, string Stage, string Result, string Message, string DetailPath)>();

                    foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
                    {
                        if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;
                        var hdr = GetHeaderIndex(ws);
                        
                        if (!hdr.TryGetValue("PointsAwarded", out var awardedCol) || 
                            !hdr.TryGetValue("PointsPossible", out var possibleCol) ||
                            !hdr.TryGetValue("Result", out var resultCol)) 
                            continue;

                        var rng = ws.RangeUsed();
                        if (rng == null) continue;

                        foreach (var row in rng.RowsUsed().Skip(1))
                        {
                            var awarded = row.Cell(awardedCol).GetString();
                            var possible = row.Cell(possibleCol).GetString();
                            var result = row.Cell(resultCol).GetString();

                            if (double.TryParse(awarded, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a)) 
                                totalPoints += a;
                            if (double.TryParse(possible, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p)) 
                                totalPossible += p;
                            
                            // Track failed steps for FailedTestDetail.xlsx
                            if (!string.IsNullOrEmpty(result) && !result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                            {
                                var stageCol = hdr.TryGetValue("Stage", out var sc) ? sc : 0;
                                var messageCol = hdr.TryGetValue("Message", out var mc) ? mc : 0;
                                var detailCol = hdr.TryGetValue("DetailPath", out var dc) ? dc : 0;
                                
                                failedSteps.Add((
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

                    // Create FailedTestDetail.xlsx if there are failures
                    if (failedSteps.Count > 0 && _failedTestDetailPath != null)
                    {
                        CreateFailedTestDetailReport(failedSteps);
                    }

                    // Format all worksheets
                    FormatWorksheets();

                    // Add to summary list
                    if (_questionCode != null)
                    {
                        _caseSummaries.Add(new TestCaseSummary
                        {
                            TestCase = _questionCode,
                            Passed = allPassed,
                            PointsAwarded = Math.Round(totalPoints, 2),
                            PointsPossible = Math.Round(totalPossible, 2)
                        });
                    }

                    // Save the detailed grade file for this case
                    _wb.SaveAs(_outPath);
                }
            } 
            catch { }

            _wb?.Dispose();
            _wb = null;
            _outPath = null;
            _questionCode = null;
            _totalMark = 0;
            _totalCompareSteps = 0;
            _allStepsPassed = true;
            _failedTestDetailPath = null;
        }

        public void SetTestCaseMark(double mark)
        {
            _totalMark = mark > 0 ? mark : 1;
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

            // Track if any step failed
            if (!passed && pointsPossible > 0)
            {
                _allStepsPassed = false;
            }

            var sheet = ResolveSheet(step, actualPath);
            if (!_wb.Worksheets.TryGetWorksheet(sheet, out var ws))
                return; // not a sheet we track (e.g., SETUP-only steps)

            var stage = ParseStage(step.Id);

            // Find the row by Stage (first match). If not found, append new row with Stage populated.
            var hdr = GetHeaderIndex(ws);
            var row = FindRowByStage(ws, hdr, stage);
            if (row == null)
                row = AppendStageRow(ws, hdr, stage);

            // Calculate points per step based on total mark
            // IMPORTANT: We'll update awarded points in EndCase based on _allStepsPassed
            double pointsPerStep = _totalCompareSteps > 0 ? _totalMark / _totalCompareSteps : 0;
            double actualPointsPossible = pointsPossible > 0 ? pointsPerStep : 0;
            // For now, store 0 for awarded - will be updated in EndCase if all pass
            double actualPointsAwarded = 0;

            // Write result columns
            SetCell(ws, row.Value, hdr, "Result", passed ? "PASS" : "FAIL");
            SetCell(ws, row.Value, hdr, "ErrorCode", errorCode);
            SetCell(ws, row.Value, hdr, "ErrorCategory", ErrorCodes.CategoryOf(errorCode).ToString());
            SetCell(ws, row.Value, hdr, "PointsAwarded", Math.Round(actualPointsAwarded, 2));
            SetCell(ws, row.Value, hdr, "PointsPossible", Math.Round(actualPointsPossible, 2));
            SetCell(ws, row.Value, hdr, "DurationMs", (int)Math.Round(durationMs));
            SetCell(ws, row.Value, hdr, "DetailPath", detailPath ?? "");
            SetCell(ws, row.Value, hdr, "Message", message ?? "");
            SetCell(ws, row.Value, hdr, "ActualPath", actualPath ?? "");

            // Also make sure the Action column is set (if empty), to mirror template
            var actionCell = hdr.TryGetValue("Action", out var aCol) ? ws.Cell(row.Value, aCol) : null;
            if (actionCell != null && string.IsNullOrWhiteSpace(actionCell.GetString()))
                actionCell.Value = XLCellValue.FromObject(step.Action ?? string.Empty);

            // DON'T save here - accumulate changes in memory
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

        public void LogCaseSummary(string questionCode, bool passed, double pointsAwarded, double pointsPossible, string message)
        {
            // This is handled in EndCase for ExcelDetailLogService
            // No need to do anything here as summary is calculated from all steps
        }

        public void WriteOverallSummary()
        {
            if (string.IsNullOrEmpty(_overallSummaryPath) || _caseSummaries.Count == 0) return;

            try
            {
                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Summary");

                // Headers
                ws.Cell(1, 1).Value = XLCellValue.FromObject("TestCase");
                ws.Cell(1, 2).Value = XLCellValue.FromObject("Pass/Fail");
                ws.Cell(1, 3).Value = XLCellValue.FromObject("PointsAwarded");
                ws.Cell(1, 4).Value = XLCellValue.FromObject("PointsPossible");

                // Format header row
                ws.Row(1).Style.Font.Bold = true;
                ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightBlue;
                ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                // Data rows
                int row = 2;
                foreach (var summary in _caseSummaries)
                {
                    ws.Cell(row, 1).Value = XLCellValue.FromObject(summary.TestCase);
                    ws.Cell(row, 2).Value = XLCellValue.FromObject(summary.Passed ? "PASS" : "FAIL");
                    ws.Cell(row, 3).Value = XLCellValue.FromObject(summary.PointsAwarded);
                    ws.Cell(row, 4).Value = XLCellValue.FromObject(summary.PointsPossible);
                    
                    // Color code Pass/Fail
                    if (summary.Passed)
                        ws.Cell(row, 2).Style.Font.FontColor = XLColor.Green;
                    else
                        ws.Cell(row, 2).Style.Font.FontColor = XLColor.Red;
                    
                    row++;
                }

                // Totals row
                ws.Cell(row, 1).Value = XLCellValue.FromObject("TOTAL");
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 3).Value = XLCellValue.FromObject(Math.Round(_caseSummaries.Sum(s => s.PointsAwarded), 2));
                ws.Cell(row, 3).Style.Font.Bold = true;
                ws.Cell(row, 4).Value = XLCellValue.FromObject(Math.Round(_caseSummaries.Sum(s => s.PointsPossible), 2));
                ws.Cell(row, 4).Style.Font.Bold = true;

                // Auto-fit columns
                ws.Columns().AdjustToContents();

                wb.SaveAs(_overallSummaryPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to write overall summary: {ex.Message}");
            }
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
                ws.Cell(newRow, c).Value = XLCellValue.FromObject(stage);
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
                    ws.Cell(1, nextCol).Value = XLCellValue.FromObject(name);
                    hdr[name] = nextCol;
                    nextCol++;
                }
            }
        }

        private static void SetCell(IXLWorksheet ws, int row, Dictionary<string, int> hdr, string colName, object? value)
        {
            if (!hdr.TryGetValue(colName, out var col)) return;

            var cell = ws.Cell(row, col);
            if (value is null)
            {
                cell.Value = XLCellValue.FromObject(string.Empty);
                return;
            }

            // Handle different types explicitly
            switch (value)
            {
                case string s:
                    cell.Value = XLCellValue.FromObject(s);
                    break;
                case int i:
                    cell.Value = XLCellValue.FromObject(i);
                    break;
                case double d:
                    cell.Value = XLCellValue.FromObject(d);
                    // Format as number with 2 decimal places
                    cell.Style.NumberFormat.Format = "0.00";
                    break;
                case bool b:
                    cell.Value = XLCellValue.FromObject(b);
                    break;
                default:
                    cell.Value = XLCellValue.FromObject(value.ToString() ?? string.Empty);
                    break;
            }
        }

        private void CreateFailedTestDetailReport(List<(string Sheet, int Row, string Stage, string Result, string Message, string DetailPath)> failedSteps)
        {
            if (_failedTestDetailPath == null || _wb == null) return;

            try
            {
                using var failedWb = new XLWorkbook();
                var ws = failedWb.AddWorksheet("FailedTests");

                // Headers
                ws.Cell(1, 1).Value = XLCellValue.FromObject("Sheet");
                ws.Cell(1, 2).Value = XLCellValue.FromObject("Stage");
                ws.Cell(1, 3).Value = XLCellValue.FromObject("Result");
                ws.Cell(1, 4).Value = XLCellValue.FromObject("Message");
                ws.Cell(1, 5).Value = XLCellValue.FromObject("DetailPath");

                // Format header
                ws.Row(1).Style.Font.Bold = true;
                ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightCoral;
                ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                // Data rows
                int row = 2;
                foreach (var failed in failedSteps)
                {
                    ws.Cell(row, 1).Value = XLCellValue.FromObject(failed.Sheet);
                    ws.Cell(row, 2).Value = XLCellValue.FromObject(failed.Stage);
                    ws.Cell(row, 3).Value = XLCellValue.FromObject(failed.Result);
                    ws.Cell(row, 4).Value = XLCellValue.FromObject(failed.Message);
                    ws.Cell(row, 5).Value = XLCellValue.FromObject(failed.DetailPath);

                    // Color code result column
                    ws.Cell(row, 3).Style.Font.FontColor = XLColor.Red;

                    row++;
                }

                // Auto-fit columns and enable wrap text
                ws.Columns().AdjustToContents();
                ws.Column(4).Style.Alignment.WrapText = true;
                ws.Column(4).Width = 60; // Message column
                ws.Column(5).Style.Alignment.WrapText = true;
                ws.Column(5).Width = 50; // DetailPath column

                failedWb.SaveAs(_failedTestDetailPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to create FailedTestDetail.xlsx: {ex.Message}");
            }
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

        private void FormatWorksheets()
        {
            if (_wb == null) return;

            foreach (var sheetName in new[] { SheetInput, SheetOutClients, SheetOutServers })
            {
                if (!_wb.Worksheets.TryGetWorksheet(sheetName, out var ws)) continue;

                // Get header index
                var hdr = GetHeaderIndex(ws);

                // Enable wrap text for columns that might have long content
                if (hdr.TryGetValue("Message", out var msgCol))
                {
                    ws.Column(msgCol).Style.Alignment.WrapText = true;
                    ws.Column(msgCol).Width = 60; // Set a reasonable width
                }

                if (hdr.TryGetValue("DetailPath", out var detailCol))
                {
                    ws.Column(detailCol).Style.Alignment.WrapText = true;
                    ws.Column(detailCol).Width = 50;
                }

                if (hdr.TryGetValue("ActualPath", out var actualCol))
                {
                    ws.Column(actualCol).Style.Alignment.WrapText = true;
                    ws.Column(actualCol).Width = 50;
                }

                if (hdr.TryGetValue("Output", out var outputCol))
                {
                    ws.Column(outputCol).Style.Alignment.WrapText = true;
                    ws.Column(outputCol).Width = 60;
                }

                if (hdr.TryGetValue("DataResponse", out var dataResponseCol))
                {
                    ws.Column(dataResponseCol).Style.Alignment.WrapText = true;
                    ws.Column(dataResponseCol).Width = 60;
                }

                // Auto-fit other columns
                foreach (var col in hdr.Keys)
                {
                    if (col != "Message" && col != "DetailPath" && col != "ActualPath" && col != "Output" && col != "DataResponse")
                    {
                        if (hdr.TryGetValue(col, out var colIdx))
                        {
                            ws.Column(colIdx).AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1);
                        }
                    }
                }

                // Format header row
                var headerRow = ws.Row(1);
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRow.Style.Alignment.WrapText = true;
                
                // Set minimum height for rows with wrapped text
                for (int r = 2; r <= (ws.LastRowUsed()?.RowNumber() ?? 1); r++)
                {
                    ws.Row(r).Height = 15; // Minimum height, will expand with content
                }
            }
        }

        public void Dispose()
        {
            try { _wb?.Dispose(); } catch { }
        }
    }
}
