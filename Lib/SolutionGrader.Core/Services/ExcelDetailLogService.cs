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
        private readonly IRunContext _run;

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
            "DurationMs","DetailPath","Message","DiffIndex","ExpectedOutput","ActualOutput","ExpectedExcerpt","ActualExcerpt"
        };

        public ExcelDetailLogService(IFileService files, IRunContext run)
        {
            _files = files;
            _run = run;
        }

        public void BeginCase(string outFolder, string questionCode, string detailTemplatePath, double pointsPossible)
        {
            _files.EnsureDirectory(outFolder);

            _questionCode = questionCode;
            _outPath = Path.Combine(outFolder, FileKeywords.FileName_GradeDetail);
            _failedTestDetailPath = Path.Combine(outFolder, FileKeywords.FileName_FailedTestDetail);

            // result root is parent of the case folder; that's where OverallSummary.xlsx lives
            var resultRoot = Path.GetDirectoryName(outFolder);
            _overallSummaryPath = string.IsNullOrEmpty(resultRoot) ? null : Path.Combine(resultRoot!, FileKeywords.FileName_OverallSummary);

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

            // NEW: Create separate TestRunData sheet with ONLY actual runtime data (no template duplication)
            CreateTestRunDataSheet();

            // NEW: Create separate ErrorReport sheet with ALL errors (not just first one)
            CreateErrorReportSheet();

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
            
            // Only write detailed information when test fails (optimization)
            if (!passed)
            {
                SetCell(ws, rowNum, hdr, "DetailPath", detailPath ?? "");
                SetCell(ws, rowNum, hdr, "Message", message ?? "");
                
                // Write actual output for failed tests
                TryWriteActualOutput(ws, hdr, rowNum, stage, actualPath);
                
                // Write diff columns with colored excerpts
                TryWriteDiffColumns(ws, hdr, rowNum, stage, detailPath, message, actualPath);
            }
            else
            {
                // For passing tests, only show brief success message
                SetCell(ws, rowNum, hdr, "Message", message ?? "PASS");
            }

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
            var mismRoot = Path.Combine(Path.GetDirectoryName(_outPath!)!, FileKeywords.Folder_Mismatches, questionCode);
            _files.EnsureDirectory(mismRoot);
            var outPath = Path.Combine(mismRoot, string.Format(FileKeywords.Pattern_StageDiff, stage));

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

        private void TryWriteActualOutput(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string? actualPath)
        {
            try
            {
                // Try to get actual output from actualPath (memory:// or file path)
                string? actualOutput = null;
                
                if (!string.IsNullOrEmpty(actualPath))
                {
                    actualOutput = TryReadContext(actualPath, 5000); // Read up to 5000 chars
                }
                
                // If no actualPath provided, try to infer from the sheet and stage
                if (string.IsNullOrEmpty(actualOutput) && !string.IsNullOrEmpty(_questionCode))
                {
                    var sheetName = ws.Name;
                    var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase);
                    var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase);
                    
                    if (isClientSheet)
                    {
                        var captureKey = _run.GetClientCaptureKey(_questionCode, stage.ToString());
                        if (_run.TryGetCapturedOutput(captureKey, out var captured))
                        {
                            actualOutput = captured;
                        }
                    }
                    else if (isServerSheet)
                    {
                        var captureKey = _run.GetServerCaptureKey(_questionCode, stage.ToString());
                        if (_run.TryGetCapturedOutput(captureKey, out var captured))
                        {
                            actualOutput = captured;
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(actualOutput))
                {
                    // Truncate if too long for display
                    if (actualOutput.Length > 5000)
                    {
                        actualOutput = actualOutput.Substring(0, 5000) + "... (truncated)";
                    }
                    SetCell(ws, rowNum, hdr, "ActualOutput", actualOutput);
                }
            }
            catch { /* best effort */ }
        }

        private void TryWriteDiffColumns(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string? detailPath, string? message, string? actualPath)
        {
            try
            {
                // Get expected output from the Detail.xlsx template
                // For OutputClients, check DataResponse column first (for HTTP data validation), then fall back to Output
                // For OutputServers, check DataRequest column first, then fall back to Output
                string? expectedOutput = null;
                var sheetName = ws.Name;
                var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase);
                var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase);
                
                if (isClientSheet && hdr.TryGetValue("DataResponse", out var dataResponseCol))
                {
                    expectedOutput = ws.Cell(rowNum, dataResponseCol).GetString();
                }
                
                if (string.IsNullOrEmpty(expectedOutput) && isServerSheet && hdr.TryGetValue("DataRequest", out var dataRequestCol))
                {
                    expectedOutput = ws.Cell(rowNum, dataRequestCol).GetString();
                }
                
                // Fall back to Output column if no data columns found
                if (string.IsNullOrEmpty(expectedOutput) && hdr.TryGetValue("Output", out var outputCol))
                {
                    expectedOutput = ws.Cell(rowNum, outputCol).GetString();
                }
                
                // If no expected output in template, try reading from detailPath (diff file)
                if (string.IsNullOrEmpty(expectedOutput) && !string.IsNullOrEmpty(detailPath) && File.Exists(detailPath))
                {
                    expectedOutput = TryReadContext(detailPath, 5000);
                }

                // Get actual output (already written by TryWriteActualOutput)
                string? actualOutput = null;
                if (hdr.TryGetValue("ActualOutput", out var actualOutputCol))
                {
                    actualOutput = ws.Cell(rowNum, actualOutputCol).GetString();
                }
                
                // If ActualOutput column doesn't have data yet, try to get it
                if (string.IsNullOrEmpty(actualOutput))
                {
                    actualOutput = TryReadContext(actualPath, 5000);
                    if (string.IsNullOrEmpty(actualOutput) && !string.IsNullOrEmpty(_questionCode))
                    {
                        // Reuse sheetName, isClientSheet, and isServerSheet from above
                        if (isClientSheet)
                        {
                            var captureKey = _run.GetClientCaptureKey(_questionCode, stage.ToString());
                            if (_run.TryGetCapturedOutput(captureKey, out var captured))
                            {
                                actualOutput = captured;
                            }
                        }
                        else if (isServerSheet)
                        {
                            var captureKey = _run.GetServerCaptureKey(_questionCode, stage.ToString());
                            if (_run.TryGetCapturedOutput(captureKey, out var captured))
                            {
                                actualOutput = captured;
                            }
                        }
                    }
                }

                // Write full expected and actual outputs with color coding
                if (!string.IsNullOrEmpty(expectedOutput))
                {
                    var truncatedExp = expectedOutput.Length > 5000 ? expectedOutput.Substring(0, 5000) + "... (truncated)" : expectedOutput;
                    SetCell(ws, rowNum, hdr, "ExpectedOutput", truncatedExp);
                    // Color expected in green
                    if (hdr.TryGetValue("ExpectedOutput", out var expCol))
                    {
                        ws.Cell(rowNum, expCol).Style.Font.FontColor = XLColor.DarkGreen;
                        ws.Cell(rowNum, expCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
                    }
                }
                
                // Color actual output in red (it was already written by TryWriteActualOutput)
                if (!string.IsNullOrEmpty(actualOutput) && hdr.TryGetValue("ActualOutput", out var actCol))
                {
                    ws.Cell(rowNum, actCol).Style.Font.FontColor = XLColor.DarkRed;
                    ws.Cell(rowNum, actCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                }

                // Also write excerpts around the difference point for quick comparison
                var idx = FirstDiffIndexFromMessage(message ?? "");
                if (idx >= 0)
                {
                    SetCell(ws, rowNum, hdr, "DiffIndex", idx);
                    
                    if (!string.IsNullOrEmpty(expectedOutput) && !string.IsNullOrEmpty(actualOutput))
                    {
                        // Extract context around the mismatch (20 chars on each side for better context)
                        const int contextSize = 20;
                        
                        var expSnippet = ExtractSnippet(expectedOutput, idx, contextSize);
                        var actSnippet = ExtractSnippet(actualOutput, idx, contextSize);
                        
                        if (!string.IsNullOrEmpty(expSnippet))
                        {
                            SetCell(ws, rowNum, hdr, "ExpectedExcerpt", expSnippet);
                            if (hdr.TryGetValue("ExpectedExcerpt", out var expExcerptCol))
                            {
                                ws.Cell(rowNum, expExcerptCol).Style.Font.FontColor = XLColor.DarkGreen;
                                ws.Cell(rowNum, expExcerptCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(actSnippet))
                        {
                            SetCell(ws, rowNum, hdr, "ActualExcerpt", actSnippet);
                            if (hdr.TryGetValue("ActualExcerpt", out var actExcerptCol))
                            {
                                ws.Cell(rowNum, actExcerptCol).Style.Font.FontColor = XLColor.DarkRed;
                                ws.Cell(rowNum, actExcerptCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                            }
                        }
                    }
                }
            }
            catch { /* best effort */ }
        }
        
        /// <summary>
        /// Extracts a snippet of text around a difference index for display in Excel.
        /// </summary>
        /// <param name="text">The full text to extract from</param>
        /// <param name="diffIdx">The index where the difference occurred</param>
        /// <param name="contextSize">Number of characters to show before and after the diff</param>
        /// <returns>A snippet with ellipsis markers if truncated</returns>
        private static string ExtractSnippet(string text, int diffIdx, int contextSize)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            
            var start = Math.Max(0, diffIdx - contextSize);
            var end = Math.Min(text.Length, diffIdx + contextSize + 1);
            var length = end - start;
            
            if (length <= 0) return string.Empty;
            
            var snippet = text.Substring(start, length);
            
            // Add ellipsis if truncated
            if (start > 0) snippet = "..." + snippet;
            if (end < text.Length) snippet = snippet + "...";
            
            return snippet;
        }

        private string? TryReadContext(string? path, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            
            // Handle memory:// URIs
            if (path.StartsWith("memory://", StringComparison.OrdinalIgnoreCase))
            {
                if (_run.TryGetCapturedOutput(path, out var captured))
                {
                    var txt = captured ?? string.Empty;
                    if (txt.Length > maxChars) txt = txt.Substring(0, maxChars) + "...";
                    return txt;
                }
                return null;
            }
            
            // Handle file paths
            if (!File.Exists(path)) return null;
            var fileTxt = File.ReadAllText(path);
            if (fileTxt.Length > maxChars) fileTxt = fileTxt.Substring(0, maxChars) + "...";
            return fileTxt;
        }

        private (bool casePassed, double awarded, double possible) ComputeCaseTotals()
        {
            // All-or-nothing policy based on the case's declared mark from Header.xlsx
            var passed = _allStepsPassed;
            var possible = Math.Round(_totalMark, 2);
            var awarded = passed ? possible : 0;
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
            var ms = new MemoryStream();
            sr.CopyTo(ms);
            ms.Position = 0;
            return new XLWorkbook(ms);
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
            if (lower.Contains($"/{FileKeywords.Folder_Actual}/{FileKeywords.Folder_Clients}/")) return SheetOutClients;
            if (lower.Contains($"/{FileKeywords.Folder_Actual}/{FileKeywords.Folder_Servers}/")) return SheetOutServers;

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
            // Only create the file if the test case actually failed
            if (_wb == null || string.IsNullOrEmpty(_failedTestDetailPath) || _allStepsPassed) return;

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

        /// <summary>
        /// Creates a TestRunData sheet with ONLY actual runtime data (no template duplication).
        /// This sheet shows what was actually captured during test execution.
        /// </summary>
        private void CreateTestRunDataSheet()
        {
            if (_wb == null || _records.Count == 0) return;

            // Remove existing TestRunData sheet if it exists
            if (_wb.Worksheets.TryGetWorksheet("TestRunData", out var existingSheet))
            {
                existingSheet.Delete();
            }

            var ws = _wb.AddWorksheet("TestRunData");
            
            // Create header row
            ws.Cell(1, 1).Value = "Stage";
            ws.Cell(1, 2).Value = "StepId";
            ws.Cell(1, 3).Value = "ValidationType";
            ws.Cell(1, 4).Value = "Action";
            ws.Cell(1, 5).Value = "Result";
            ws.Cell(1, 6).Value = "Message";
            ws.Cell(1, 7).Value = "DurationMs";
            ws.Cell(1, 8).Value = "ActualOutput";
            ws.Cell(1, 9).Value = "HttpMethod";
            ws.Cell(1, 10).Value = "StatusCode";
            ws.Cell(1, 11).Value = "ByteSize";
            
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightBlue;
            ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            int row = 2;
            foreach (var record in _records)
            {
                ws.Cell(row, 1).Value = record.Stage;
                ws.Cell(row, 2).Value = record.StepId;
                
                // Extract validation type from metadata if available
                var validationType = record.StepId.Contains("-METHOD-") ? "HTTP_METHOD" :
                                   record.StepId.Contains("-STATUS-") ? "STATUS_CODE" :
                                   record.StepId.Contains("-SIZE-") ? "BYTE_SIZE" :
                                   record.StepId.Contains("-DATA-") ? (record.StepId.StartsWith("OC-") ? "DATA_RESPONSE" : "DATA_REQUEST") :
                                   record.StepId.Contains("-OUT-") ? (record.StepId.StartsWith("OC-") ? "CLIENT_OUTPUT" : "SERVER_OUTPUT") :
                                   record.StepId.Contains("-REQ-") ? "DATA_REQUEST" : "OTHER";
                ws.Cell(row, 3).Value = validationType;
                
                ws.Cell(row, 4).Value = record.Action ?? "";
                ws.Cell(row, 5).Value = record.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 6).Value = record.Message;
                ws.Cell(row, 7).Value = Math.Round(record.DurationMs, 2);
                
                // Get actual output from captured data if available
                string? actualOutput = null;
                if (!string.IsNullOrEmpty(record.ActualPath))
                {
                    _run.TryGetCapturedOutput(record.ActualPath, out actualOutput);
                }
                
                // If ActualPath didn't work, try inferring from validation type and stage
                if (string.IsNullOrEmpty(actualOutput) && !string.IsNullOrEmpty(record.QuestionCode))
                {
                    // Reuse the validationType calculated above
                    if (validationType == "CLIENT_OUTPUT")
                    {
                        var key = _run.GetClientCaptureKey(record.QuestionCode, record.Stage.ToString());
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == "SERVER_OUTPUT")
                    {
                        var key = _run.GetServerCaptureKey(record.QuestionCode, record.Stage.ToString());
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == "DATA_RESPONSE")
                    {
                        var key = $"memory://servers-resp/{record.QuestionCode}/{record.Stage}";
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == "DATA_REQUEST")
                    {
                        var key = $"memory://servers-req/{record.QuestionCode}/{record.Stage}";
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                }
                
                if (!string.IsNullOrEmpty(actualOutput))
                {
                    var truncated = actualOutput.Length > 500 ? actualOutput.Substring(0, 500) + "..." : actualOutput;
                    ws.Cell(row, 8).Value = truncated;
                }
                
                // Get HTTP metadata if available
                if (_run.TryGetHttpMetadata(record.QuestionCode, record.Stage, out var httpMethod, out var statusCode, out var byteSize))
                {
                    ws.Cell(row, 9).Value = httpMethod ?? "";
                    ws.Cell(row, 10).Value = statusCode ?? 0;
                    ws.Cell(row, 11).Value = byteSize ?? 0;
                }
                
                // Color code the result row
                if (!record.Passed)
                {
                    ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightPink;
                }
                
                row++;
            }

            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, 5, 80);
        }

        /// <summary>
        /// Creates an ErrorReport sheet with ALL errors found during test execution.
        /// This sheet consolidates all failures for easy debugging.
        /// </summary>
        private void CreateErrorReportSheet()
        {
            if (_wb == null) return;

            var failedRecords = _records.Where(r => !r.Passed).ToList();
            if (failedRecords.Count == 0) return; // No errors to report

            // Remove existing ErrorReport sheet if it exists
            if (_wb.Worksheets.TryGetWorksheet("ErrorReport", out var existingSheet))
            {
                existingSheet.Delete();
            }

            var ws = _wb.AddWorksheet("ErrorReport");
            
            // Create header row
            ws.Cell(1, 1).Value = "Stage";
            ws.Cell(1, 2).Value = "StepId";
            ws.Cell(1, 3).Value = "ValidationType";
            ws.Cell(1, 4).Value = "ErrorCode";
            ws.Cell(1, 5).Value = "ErrorCategory";
            ws.Cell(1, 6).Value = "Message";
            ws.Cell(1, 7).Value = "ExpectedValue";
            ws.Cell(1, 8).Value = "ActualValue";
            ws.Cell(1, 9).Value = "PointsLost";
            
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.Red;
            ws.Row(1).Style.Font.FontColor = XLColor.White;
            ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            int row = 2;
            foreach (var record in failedRecords)
            {
                ws.Cell(row, 1).Value = record.Stage;
                ws.Cell(row, 2).Value = record.StepId;
                
                // Extract validation type
                var validationType = record.StepId.Contains("-METHOD-") ? "HTTP_METHOD" :
                                   record.StepId.Contains("-STATUS-") ? "STATUS_CODE" :
                                   record.StepId.Contains("-SIZE-") ? "BYTE_SIZE" :
                                   record.StepId.Contains("-DATA-") ? (record.StepId.StartsWith("OC-") ? "DATA_RESPONSE" : "DATA_REQUEST") :
                                   record.StepId.Contains("-OUT-") ? (record.StepId.StartsWith("OC-") ? "CLIENT_OUTPUT" : "SERVER_OUTPUT") :
                                   record.StepId.Contains("-REQ-") ? "DATA_REQUEST" : "OTHER";
                ws.Cell(row, 3).Value = validationType;
                
                ws.Cell(row, 4).Value = record.ErrorCode;
                ws.Cell(row, 5).Value = record.ErrorCategory.ToString();
                ws.Cell(row, 6).Value = record.Message;
                
                // Try to extract expected vs actual from the message
                var message = record.Message ?? "";
                if (message.Contains("Expected") && message.Contains("got"))
                {
                    var parts = message.Split(new[] { "Expected", "got" }, StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var expectedPart = parts[1].Split(',')[0].Trim().Trim('\'', '"');
                        ws.Cell(row, 7).Value = expectedPart;
                    }
                    if (parts.Length >= 3)
                    {
                        var actualPart = parts[2].Split('.')[0].Trim().Trim('\'', '"');
                        ws.Cell(row, 8).Value = actualPart;
                    }
                }
                
                ws.Cell(row, 9).Value = record.PointsPossible;
                
                // Highlight critical errors more prominently
                if (record.ErrorCategory == ErrorCategory.Compare)
                {
                    ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightPink;
                }
                else if (record.ErrorCategory == ErrorCategory.Process || record.ErrorCategory == ErrorCategory.Timeout)
                {
                    ws.Row(row).Style.Fill.BackgroundColor = XLColor.LightYellow;
                }
                
                row++;
            }

            // Add summary at the bottom
            row++;
            ws.Cell(row, 1).Value = "Summary";
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            ws.Cell(row, 1).Value = "Total Errors:";
            ws.Cell(row, 2).Value = failedRecords.Count;
            row++;
            ws.Cell(row, 1).Value = "Total Points Lost:";
            ws.Cell(row, 2).Value = Math.Round(failedRecords.Sum(r => r.PointsPossible), 2);

            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, 5, 80);
        }

        public void Dispose() => _wb?.Dispose();
    }
}
