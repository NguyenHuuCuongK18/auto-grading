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

        // Sheets expected in Detail.xlsx - support both old (plural) and new (singular) formats
        // Priority: Try new format first (singular), then fall back to old format (plural)
        private const string SheetInput = SuiteKeywords.Sheet_InputClients;        // Old: "InputClients"
        private const string SheetInputAlt = SuiteKeywords.Sheet_InputClient;      // New: "InputClient"
        private const string SheetOutClients = SuiteKeywords.Sheet_OutputClients;  // Old: "OutputClients"
        private const string SheetOutClientsAlt = SuiteKeywords.Sheet_OutputClient;// New: "OutputClient"
        private const string SheetOutServers = SuiteKeywords.Sheet_OutputServers;  // Old: "OutputServers"
        private const string SheetOutServersAlt = SuiteKeywords.Sheet_OutputServer;// New: "OutputServer"
        
        // Maximum length for expected/actual values displayed in ErrorReport sheet
        private const int ErrorReportMaxValueLength = 100;

        // Columns we always ensure exist
        private static readonly string[] BaseColumns =
        {
            GradingKeywords.Col_Stage,
            SuiteKeywords.Col_IC_Input,
            SuiteKeywords.Col_IC_DataType,
            SuiteKeywords.Col_IC_Action
        };

        private static readonly string[] ResultColumns =
        {
            GradingKeywords.Col_Result,
            GradingKeywords.Col_ErrorCode,
            GradingKeywords.Col_ErrorCategory,
            GradingKeywords.Col_PointsAwarded,
            GradingKeywords.Col_PointsPossible,
            GradingKeywords.Col_DurationMs,
            GradingKeywords.Col_DetailPath,
            GradingKeywords.Col_Message,
            GradingKeywords.Col_DiffIndex,
            GradingKeywords.Col_ExpectedOutput,
            GradingKeywords.Col_ActualOutput,
            GradingKeywords.Col_ExpectedExcerpt,
            GradingKeywords.Col_ActualExcerpt
        };

        public ExcelDetailLogService(IFileService files, IRunContext run)
        {
            _files = files;
            _run = run;
        }

        /// <summary>
        /// Helper method to try getting a worksheet by name, supporting both old (plural) and new (singular) formats.
        /// </summary>
        private bool TryGetWorksheetFlexible(string primaryName, string alternateName, out IXLWorksheet? worksheet)
        {
            if (_wb == null)
            {
                worksheet = null;
                return false;
            }
            
            // Try primary name first (new format)
            if (_wb.Worksheets.TryGetWorksheet(primaryName, out worksheet))
                return true;
            
            // Try alternate name (old format)
            if (_wb.Worksheets.TryGetWorksheet(alternateName, out worksheet))
                return true;
            
            worksheet = null;
            return false;
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

            // Try both old (plural) and new (singular) sheet name formats
            var sheetsToCheck = new[]
            {
                (Primary: SheetInputAlt, Alternate: SheetInput),
                (Primary: SheetOutClientsAlt, Alternate: SheetOutClients),
                (Primary: SheetOutServersAlt, Alternate: SheetOutServers)
            };

            foreach (var (primary, alternate) in sheetsToCheck)
            {
                if (!TryGetWorksheetFlexible(primary, alternate, out var ws) || ws == null) continue;

                EnsureColumns(ws, BaseColumns);
                EnsureColumns(ws, ResultColumns);

                // Skip InputClient/InputClients sheet for counting compare steps
                if (!string.Equals(ws.Name, SheetInput, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ws.Name, SheetInputAlt, StringComparison.OrdinalIgnoreCase))
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
            var sheetsToProcess = new[]
            {
                (Primary: SheetInputAlt, Alternate: SheetInput),
                (Primary: SheetOutClientsAlt, Alternate: SheetOutClients),
                (Primary: SheetOutServersAlt, Alternate: SheetOutServers)
            };

            foreach (var (primary, alternate) in sheetsToProcess)
            {
                if (!TryGetWorksheetFlexible(primary, alternate, out var ws) || ws == null) continue;
                var hdr = GetHeaderIndex(ws);
                if (!hdr.TryGetValue(GradingKeywords.Col_PointsAwarded, out var awardedCol) ||
                    !hdr.TryGetValue(GradingKeywords.Col_PointsPossible, out var possibleCol))
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

            var sheetHint = ResolveSheet(step, actualPath);
            
            // Try to get worksheet - support both old (plural) and new (singular) formats
            IXLWorksheet? ws = null;
            if (string.Equals(sheetHint, SheetOutClients, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutClientsAlt, SheetOutClients, out ws))
                    return;
            }
            else if (string.Equals(sheetHint, SheetOutServers, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutServersAlt, SheetOutServers, out ws))
                    return;
            }
            else
            {
                // Fallback to exact match for other sheets
                if (!_wb.Worksheets.TryGetWorksheet(sheetHint, out ws))
                    return;
            }
            
            if (ws == null) return;

            var hdr = GetHeaderIndex(ws);
            var stage = ParseStage(step.Id);
            var rowNum = FindRowByStage(ws, hdr, stage) ?? AppendStageRow(ws, hdr, stage);

            // per-step possible points are equal split of the case's total mark across compare steps
            var perStep = _totalCompareSteps > 0 ? _totalMark / _totalCompareSteps : 0;
            var actualPossible = pointsPossible > 0 ? perStep : 0;

            SetCell(ws, rowNum, hdr, GradingKeywords.Col_Result, passed ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail);
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_ErrorCode, errorCode);
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_ErrorCategory, errorCategory);
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_PointsAwarded, 0);             // awarded later in EndCase
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_PointsPossible, actualPossible);
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_DurationMs, Math.Round(durationMs, 2));
            
            // Only write detailed information when test fails (optimization)
            if (!passed)
            {
                SetCell(ws, rowNum, hdr, GradingKeywords.Col_DetailPath, detailPath ?? string.Empty);
                SetCell(ws, rowNum, hdr, GradingKeywords.Col_Message, message ?? string.Empty);
                
                // Write actual output for failed tests
                TryWriteActualOutput(ws, hdr, rowNum, stage, step.Id, actualPath);
                
                // Write diff columns with colored excerpts
                TryWriteDiffColumns(ws, hdr, rowNum, stage, step.Id, detailPath, message, actualPath);
            }
            else
            {
                // For passing tests, only show brief success message
                SetCell(ws, rowNum, hdr, GradingKeywords.Col_Message, message ?? GradingKeywords.Result_Pass);
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
                Message = message ?? string.Empty,
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
            sb.AppendLine(detail.ExpectedContext ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("Got:");
            sb.AppendLine(detail.ActualContext ?? string.Empty);
            sb.AppendLine();

            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            return outPath;
        }

        public void WriteOverallSummary()
        {
            // still support end-of-suite write, but not required anymore
            if (string.IsNullOrEmpty(_overallSummaryPath) || _caseSummaries.Count == 0) return;

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(GradingKeywords.Sheet_Summary);

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
                ws.Cell(r, 2).Value = s.Passed ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail;
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

        private void TryWriteActualOutput(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string stepId, string? actualPath)
        {
            try
            {
                // Try to get actual output from actualPath (memory:// or file path)
                string? actualOutput = null;
                
                if (!string.IsNullOrEmpty(actualPath))
                {
                    actualOutput = TryReadContext(actualPath, 5000); // Read up to 5000 chars
                }
                
                // If no actualPath provided, try to infer from the sheet, stage, and validation type
                if (string.IsNullOrEmpty(actualOutput) && !string.IsNullOrEmpty(_questionCode))
                {
                    var sheetName = ws.Name;
                    var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutClientsAlt, StringComparison.OrdinalIgnoreCase);
                    var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutServersAlt, StringComparison.OrdinalIgnoreCase);
                    
                    // Determine the correct capture key based on validation type
                    var validationType = GetValidationType(stepId);
                    string? captureKey = null;
                    
                    if (isClientSheet)
                    {
                        captureKey = validationType switch
                        {
                            StepValidationType.DataResponse => _run.GetServerResponseCaptureKey(_questionCode, stage.ToString()),
                            StepValidationType.ConsoleOutput => _run.GetClientCaptureKey(_questionCode, stage.ToString()),
                            _ => _run.GetClientCaptureKey(_questionCode, stage.ToString())
                        };
                    }
                    else if (isServerSheet)
                    {
                        captureKey = validationType switch
                        {
                            StepValidationType.DataRequest => _run.GetServerRequestCaptureKey(_questionCode, stage.ToString()),
                            StepValidationType.ConsoleOutput => _run.GetServerCaptureKey(_questionCode, stage.ToString()),
                            _ => _run.GetServerCaptureKey(_questionCode, stage.ToString())
                        };
                    }
                    
                    if (captureKey != null && _run.TryGetCapturedOutput(captureKey, out var captured))
                    {
                        actualOutput = captured;
                    }
                }
                
                if (!string.IsNullOrEmpty(actualOutput))
                {
                    // Truncate if too long for display
                    if (actualOutput.Length > 5000)
                    {
                        actualOutput = actualOutput.Substring(0, 5000) + "... (truncated)";
                    }
                    SetCell(ws, rowNum, hdr, GradingKeywords.Col_ActualOutput, actualOutput);
                }
            }
            catch { /* best effort */ }
        }

        private void TryWriteDiffColumns(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string stepId, string? detailPath, string? message, string? actualPath)
        {
            try
            {
                // Get expected output from the Detail.xlsx template
                // The column to read from depends on the validation type, which is determined by the StepId:
                // - OC-OUT- = console output validation -> read from Output column
                // - OC-DATA- = data response validation -> read from DataResponse column
                // - OS-OUT- = server console output validation -> read from Output column
                // - OS-REQ- = data request validation -> read from DataRequest column
                string? expectedOutput = null;
                var sheetName = ws.Name;
                var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutClientsAlt, StringComparison.OrdinalIgnoreCase);
                var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutServersAlt, StringComparison.OrdinalIgnoreCase);
                
                // Determine which column to read from based on the validation type
                var validationType = GetValidationType(stepId);
                
                if (isClientSheet)
                {
                    // For OutputClients sheet, determine which column to read based on validation type
                    switch (validationType)
                    {
                        case StepValidationType.DataResponse:
                            if (hdr.TryGetValue(SuiteKeywords.Col_OC_DataResponse, out var dataResponseCol))
                                expectedOutput = ws.Cell(rowNum, dataResponseCol).GetString();
                            break;
                        case StepValidationType.ConsoleOutput:
                            if (hdr.TryGetValue(SuiteKeywords.Col_OC_Output, out var ocOutCol))
                                expectedOutput = ws.Cell(rowNum, ocOutCol).GetString();
                            break;
                        default:
                            // For other validation types (METHOD, STATUS, SIZE), try DataResponse first, then Output
                            if (hdr.TryGetValue(SuiteKeywords.Col_OC_DataResponse, out var dataRespCol))
                                expectedOutput = ws.Cell(rowNum, dataRespCol).GetString();
                            if (string.IsNullOrEmpty(expectedOutput) && hdr.TryGetValue(SuiteKeywords.Col_OC_Output, out var outCol))
                                expectedOutput = ws.Cell(rowNum, outCol).GetString();
                            break;
                    }
                }
                
                if (string.IsNullOrEmpty(expectedOutput) && isServerSheet)
                {
                    // For OutputServers sheet, determine which column to read based on validation type
                    switch (validationType)
                    {
                        case StepValidationType.DataRequest:
                            if (hdr.TryGetValue(SuiteKeywords.Col_OS_DataRequest, out var dataRequestCol))
                                expectedOutput = ws.Cell(rowNum, dataRequestCol).GetString();
                            break;
                        case StepValidationType.ConsoleOutput:
                            if (hdr.TryGetValue(SuiteKeywords.Col_OS_Output, out var osOutCol))
                                expectedOutput = ws.Cell(rowNum, osOutCol).GetString();
                            break;
                        default:
                            // For other validation types (METHOD, SIZE), try DataRequest first, then Output
                            if (hdr.TryGetValue(SuiteKeywords.Col_OS_DataRequest, out var dataReqCol))
                                expectedOutput = ws.Cell(rowNum, dataReqCol).GetString();
                            if (string.IsNullOrEmpty(expectedOutput) && hdr.TryGetValue(SuiteKeywords.Col_OS_Output, out var outCol))
                                expectedOutput = ws.Cell(rowNum, outCol).GetString();
                            break;
                    }
                }
                
                // If no expected output in template, try reading from detailPath (diff file)
                if (string.IsNullOrEmpty(expectedOutput) && !string.IsNullOrEmpty(detailPath) && File.Exists(detailPath))
                {
                    expectedOutput = TryReadContext(detailPath, 5000);
                }

                // Get actual output (already written by TryWriteActualOutput)
                string? actualOutput = null;
                if (hdr.TryGetValue(GradingKeywords.Col_ActualOutput, out var actualOutputCol))
                {
                    actualOutput = ws.Cell(rowNum, actualOutputCol).GetString();
                }
                
                // If ActualOutput column doesn't have data yet, try to get it based on validation type
                if (string.IsNullOrEmpty(actualOutput))
                {
                    actualOutput = TryReadContext(actualPath, 5000);
                    if (string.IsNullOrEmpty(actualOutput) && !string.IsNullOrEmpty(_questionCode))
                    {
                        // Reuse the validationType already determined above
                        string? captureKey = null;
                        
                        if (isClientSheet)
                        {
                            captureKey = validationType switch
                            {
                                StepValidationType.DataResponse => _run.GetServerResponseCaptureKey(_questionCode, stage.ToString()),
                                StepValidationType.ConsoleOutput => _run.GetClientCaptureKey(_questionCode, stage.ToString()),
                                _ => _run.GetClientCaptureKey(_questionCode, stage.ToString())
                            };
                        }
                        else if (isServerSheet)
                        {
                            captureKey = validationType switch
                            {
                                StepValidationType.DataRequest => _run.GetServerRequestCaptureKey(_questionCode, stage.ToString()),
                                StepValidationType.ConsoleOutput => _run.GetServerCaptureKey(_questionCode, stage.ToString()),
                                _ => _run.GetServerCaptureKey(_questionCode, stage.ToString())
                            };
                        }
                        
                        if (captureKey != null && _run.TryGetCapturedOutput(captureKey, out var captured))
                        {
                            actualOutput = captured;
                        }
                    }
                }

                // Write full expected and actual outputs with color coding
                if (!string.IsNullOrEmpty(expectedOutput))
                {
                    var truncatedExp = expectedOutput.Length > 5000 ? expectedOutput.Substring(0, 5000) + "... (truncated)" : expectedOutput;
                    SetCell(ws, rowNum, hdr, GradingKeywords.Col_ExpectedOutput, truncatedExp);
                    // Color expected in green
                    if (hdr.TryGetValue(GradingKeywords.Col_ExpectedOutput, out var expCol))
                    {
                        ws.Cell(rowNum, expCol).Style.Font.FontColor = XLColor.DarkGreen;
                        ws.Cell(rowNum, expCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
                    }
                }
                
                // Color actual output in red (it was already written by TryWriteActualOutput)
                if (!string.IsNullOrEmpty(actualOutput) && hdr.TryGetValue(GradingKeywords.Col_ActualOutput, out var actCol))
                {
                    ws.Cell(rowNum, actCol).Style.Font.FontColor = XLColor.DarkRed;
                    ws.Cell(rowNum, actCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                }

                // Also write excerpts around the difference point for quick comparison
                var idx = FirstDiffIndexFromMessage(message ?? string.Empty);
                if (idx >= 0)
                {
                    SetCell(ws, rowNum, hdr, GradingKeywords.Col_DiffIndex, idx);
                    
                    if (!string.IsNullOrEmpty(expectedOutput) && !string.IsNullOrEmpty(actualOutput))
                    {
                        // Extract context around the mismatch (20 chars on each side for better context)
                        const int contextSize = 20;
                        
                        var expSnippet = ExtractSnippet(expectedOutput, idx, contextSize);
                        var actSnippet = ExtractSnippet(actualOutput, idx, contextSize);
                        
                        if (!string.IsNullOrEmpty(expSnippet))
                        {
                            SetCell(ws, rowNum, hdr, GradingKeywords.Col_ExpectedExcerpt, expSnippet);
                            if (hdr.TryGetValue(GradingKeywords.Col_ExpectedExcerpt, out var expExcerptCol))
                            {
                                ws.Cell(rowNum, expExcerptCol).Style.Font.FontColor = XLColor.DarkGreen;
                                ws.Cell(rowNum, expExcerptCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(actSnippet))
                        {
                            SetCell(ws, rowNum, hdr, GradingKeywords.Col_ActualExcerpt, actSnippet);
                            if (hdr.TryGetValue(GradingKeywords.Col_ActualExcerpt, out var actExcerptCol))
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
            var ws = wb.Worksheets.FirstOrDefault() ?? wb.AddWorksheet(GradingKeywords.Sheet_Summary);

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
            ws.Cell(row, 2).Value = passed ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail;
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
            var ws = wb.AddWorksheet(GradingKeywords.Sheet_Summary);
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
            var action = (step.Action ?? string.Empty).ToUpperInvariant();
            if (action.Contains("SERVER")) return SheetOutServers;
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
            if (!hdr.TryGetValue(GradingKeywords.Col_Stage, out var c)) return null;
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
            if (hdr.TryGetValue(GradingKeywords.Col_Stage, out var c)) ws.Cell(newRow, c).Value = stage;
            return newRow;
        }

        private static void SetCell(IXLWorksheet ws, int row, Dictionary<string, int> hdr, string name, object? value)
        {
            if (!hdr.TryGetValue(name, out var c)) return;
            if (value == null)
            {
                ws.Cell(row, c).Value = string.Empty;
            }
            else
            {
                ws.Cell(row, c).Value = XLCellValue.FromObject(value);
            }
        }

        private static int FirstDiffIndexFromMessage(string message)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message ?? string.Empty, @"(\d+)");
            return m.Success ? int.Parse(m.Value) : -1;
        }

        private void WriteFailedTestDetailIfAny()
        {
            // Only create the file if the test case actually failed
            if (_wb == null || string.IsNullOrEmpty(_failedTestDetailPath) || _allStepsPassed) return;

            var failed = new List<(string Sheet, int Row, string Stage, string Result, string Message, string DetailPath)>();

            var sheetsToCheck = new[]
            {
                (Primary: SheetOutClientsAlt, Alternate: SheetOutClients),
                (Primary: SheetOutServersAlt, Alternate: SheetOutServers)
            };

            foreach (var (primary, alternate) in sheetsToCheck)
            {
                if (!TryGetWorksheetFlexible(primary, alternate, out var worksheet) || worksheet == null) continue;
                var hdr = GetHeaderIndex(worksheet);
                if (!hdr.TryGetValue(GradingKeywords.Col_Result, out var resultCol)) continue;

                var rng = worksheet.RangeUsed();
                if (rng == null) continue;

                foreach (var row in rng.RowsUsed().Skip(1)
                )
                {
                    var result = row.Cell(resultCol).GetString();
                    if (!result.Equals(GradingKeywords.Result_Pass, StringComparison.OrdinalIgnoreCase))
                    {
                        var stageCol = hdr.TryGetValue(GradingKeywords.Col_Stage, out var sc) ? sc : 0;
                        var messageCol = hdr.TryGetValue(GradingKeywords.Col_Message, out var mc) ? mc : 0;
                        var detailCol = hdr.TryGetValue(GradingKeywords.Col_DetailPath, out var dc) ? dc : 0;

                        failed.Add((
                            worksheet.Name,
                            row.RowNumber(),
                            stageCol > 0 ? row.Cell(stageCol).GetString() : string.Empty,
                            result,
                            messageCol > 0 ? row.Cell(messageCol).GetString() : string.Empty,
                            detailCol > 0 ? row.Cell(detailCol).GetString() : string.Empty
                        ));
                    }
                }
            }

            if (failed.Count == 0) return;

            using var workbook = new XLWorkbook();
            var failedSheet = workbook.AddWorksheet(GradingKeywords.Sheet_FailedTests);

            failedSheet.Cell(1, 1).Value = "Sheet";
            failedSheet.Cell(1, 2).Value = "Row";
            failedSheet.Cell(1, 3).Value = GradingKeywords.Col_Stage;
            failedSheet.Cell(1, 4).Value = GradingKeywords.Col_Result;
            failedSheet.Cell(1, 5).Value = GradingKeywords.Col_Message;
            failedSheet.Cell(1, 6).Value = GradingKeywords.Col_DetailPath;
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
            if (_wb.Worksheets.TryGetWorksheet(GradingKeywords.Sheet_TestRunData, out var existingSheet))
            {
                existingSheet.Delete();
            }

            var ws = _wb.AddWorksheet(GradingKeywords.Sheet_TestRunData);
            
            // Create header row
            ws.Cell(1, 1).Value = GradingKeywords.Col_Stage;
            ws.Cell(1, 2).Value = GradingKeywords.Col_StepId;
            ws.Cell(1, 3).Value = GradingKeywords.Col_ValidationType;
            ws.Cell(1, 4).Value = "Action";
            ws.Cell(1, 5).Value = GradingKeywords.Col_Result;
            ws.Cell(1, 6).Value = GradingKeywords.Col_Message;
            ws.Cell(1, 7).Value = GradingKeywords.Col_DurationMs;
            ws.Cell(1, 8).Value = GradingKeywords.Col_ActualOutput;
            ws.Cell(1, 9).Value = GradingKeywords.Col_HttpMethod;
            ws.Cell(1, 10).Value = GradingKeywords.Col_StatusCode;
            ws.Cell(1, 11).Value = GradingKeywords.Col_ByteSize;
            
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightBlue;
            ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            int row = 2;
            foreach (var record in _records)
            {
                ws.Cell(row, 1).Value = record.Stage;
                ws.Cell(row, 2).Value = record.StepId;
                
                // Extract validation type from metadata if available
                var validationType = record.StepId.Contains("-METHOD-") ? GradingKeywords.Validation_HttpMethod :
                                   record.StepId.Contains("-STATUS-") ? GradingKeywords.Validation_StatusCode :
                                   record.StepId.Contains("-SIZE-") ? GradingKeywords.Validation_ByteSize :
                                   record.StepId.Contains("-DATA-") ? (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient) ? GradingKeywords.Validation_DataResponse : GradingKeywords.Validation_DataRequest) :
                                   record.StepId.Contains("-OUT-") ? (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient) ? GradingKeywords.Validation_ClientOutput : GradingKeywords.Validation_ServerOutput) :
                                   record.StepId.Contains("-REQ-") ? GradingKeywords.Validation_DataRequest : GradingKeywords.Validation_Other;
                ws.Cell(row, 3).Value = validationType;
                
                ws.Cell(row, 4).Value = record.Action ?? string.Empty;
                ws.Cell(row, 5).Value = record.Passed ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail;
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
                    if (validationType == GradingKeywords.Validation_ClientOutput)
                    {
                        var key = _run.GetClientCaptureKey(record.QuestionCode, record.Stage.ToString());
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == GradingKeywords.Validation_ServerOutput)
                    {
                        var key = _run.GetServerCaptureKey(record.QuestionCode, record.Stage.ToString());
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == GradingKeywords.Validation_DataResponse)
                    {
                        var key = $"memory://{FileKeywords.Folder_ServersResponse}/{record.QuestionCode}/{record.Stage}";
                        _run.TryGetCapturedOutput(key, out actualOutput);
                    }
                    else if (validationType == GradingKeywords.Validation_DataRequest)
                    {
                        var key = $"memory://{FileKeywords.Folder_ServersRequest}/{record.QuestionCode}/{record.Stage}";
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
                    ws.Cell(row, 9).Value = httpMethod ?? string.Empty;
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
            if (_wb.Worksheets.TryGetWorksheet(GradingKeywords.Sheet_ErrorReport, out var existingSheet))
            {
                existingSheet.Delete();
            }

            var ws = _wb.AddWorksheet(GradingKeywords.Sheet_ErrorReport);
            
            // Create header row
            ws.Cell(1, 1).Value = GradingKeywords.Col_Stage;
            ws.Cell(1, 2).Value = GradingKeywords.Col_StepId;
            ws.Cell(1, 3).Value = GradingKeywords.Col_ValidationType;
            ws.Cell(1, 4).Value = GradingKeywords.Col_ErrorCode;
            ws.Cell(1, 5).Value = GradingKeywords.Col_ErrorCategory;
            ws.Cell(1, 6).Value = GradingKeywords.Col_Message;
            ws.Cell(1, 7).Value = GradingKeywords.Col_Expected;
            ws.Cell(1, 8).Value = GradingKeywords.Col_Actual;
            ws.Cell(1, 9).Value = GradingKeywords.Col_PointsLost;
            
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
                var validationType = record.StepId.Contains("-METHOD-") ? GradingKeywords.Validation_HttpMethod :
                                   record.StepId.Contains("-STATUS-") ? GradingKeywords.Validation_StatusCode :
                                   record.StepId.Contains("-SIZE-") ? GradingKeywords.Validation_ByteSize :
                                   record.StepId.Contains("-DATA-") ? (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient) ? GradingKeywords.Validation_DataResponse : GradingKeywords.Validation_DataRequest) :
                                   record.StepId.Contains("-OUT-") ? (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient) ? GradingKeywords.Validation_ClientOutput : GradingKeywords.Validation_ServerOutput) :
                                   record.StepId.Contains("-REQ-") ? GradingKeywords.Validation_DataRequest : GradingKeywords.Validation_Other;
                ws.Cell(row, 3).Value = validationType;
                
                ws.Cell(row, 4).Value = record.ErrorCode;
                ws.Cell(row, 5).Value = record.ErrorCategory.ToString();
                ws.Cell(row, 6).Value = record.Message;
                
                // Try to extract expected vs actual values
                // First, try to read from the actual test sheets (OutputClients/OutputServers) where they were already populated
                var (expectedValue, actualValue) = TryGetExpectedActualFromSheets(record);
                
                // If not found in sheets, try parsing from the message (for HTTP method, status code, byte size errors)
                if (string.IsNullOrEmpty(expectedValue) && string.IsNullOrEmpty(actualValue))
                {
                    var message = record.Message ?? string.Empty;
                    if (message.Contains("Expected") && message.Contains("got"))
                    {
                        var parts = message.Split(new[] { "Expected", "got" }, StringSplitOptions.TrimEntries);
                        if (parts.Length >= 2)
                        {
                            expectedValue = parts[1].Split(',')[0].Trim().Trim('\'', '"');
                        }
                        if (parts.Length >= 3)
                        {
                            actualValue = parts[2].Split('.')[0].Trim().Trim('\'', '"');
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(expectedValue))
                    ws.Cell(row, 7).Value = expectedValue;
                if (!string.IsNullOrEmpty(actualValue))
                    ws.Cell(row, 8).Value = actualValue;
                
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

        /// <summary>
        /// Attempts to retrieve expected and actual values for a given failed step record.
        /// Expected values come from the Detail.xlsx template (DataResponse, Output, DataRequest columns).
        /// Actual values come from runtime execution captured in memory.
        /// </summary>
        /// <param name="record">The failed step record to get expected/actual values for</param>
        /// <returns>A tuple of (expected, actual) values, or (null, null) if values cannot be retrieved</returns>
        private (string? expected, string? actual) TryGetExpectedActualFromSheets(StepGradeRecord record)
        {
            if (_wb == null) return (null, null);
            
            // Determine which sheet to look in based on the step ID prefix
            // Step IDs follow the convention: "OC-*" for OutputClients, "OS-*" for OutputServers
            // IC- (InputClients) steps don't have expected/actual output, so return null for those
            if (record.StepId.StartsWith(GradingKeywords.StepPrefix_InputClient, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            
            // Try to get the appropriate sheet (support both old and new formats)
            IXLWorksheet? ws = null;
            bool isClientSheet = false;
            bool isServerSheet = false;
            
            if (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutClientsAlt, SheetOutClients, out ws))
                    return (null, null);
                isClientSheet = true;
            }
            else
            {
                if (!TryGetWorksheetFlexible(SheetOutServersAlt, SheetOutServers, out ws))
                    return (null, null);
                isServerSheet = true;
            }
            
            if (ws == null)
                return (null, null);
            
            var hdr = GetHeaderIndex(ws);
            
            // Parse the stage number from the Stage string (should be an integer)
            // If parsing fails, we cannot locate the row in the sheet
            if (!int.TryParse(record.Stage, out var stage))
                return (null, null);
            
            int? rowNum = FindRowByStage(ws, hdr, stage);
            if (!rowNum.HasValue)
                return (null, null);
            
            string? expectedValue = null;
            string? actualValue = null;
            
            // Get expected value from the Detail.xlsx template columns based on validation type
            var validationType = GetValidationType(record.StepId);
            
            if (isClientSheet)
            {
                switch (validationType)
                {
                    case StepValidationType.DataResponse:
                        if (hdr.TryGetValue(SuiteKeywords.Col_OC_DataResponse, out var dataResponseCol))
                            expectedValue = ws.Cell(rowNum.Value, dataResponseCol).GetString();
                        break;
                    case StepValidationType.ConsoleOutput:
                        if (hdr.TryGetValue(SuiteKeywords.Col_OC_Output, out var ocOutCol))
                            expectedValue = ws.Cell(rowNum.Value, ocOutCol).GetString();
                        break;
                    default:
                        // For other validation types (METHOD, STATUS, SIZE), try DataResponse first, then Output
                        if (hdr.TryGetValue(SuiteKeywords.Col_OC_DataResponse, out var dataRespCol))
                            expectedValue = ws.Cell(rowNum.Value, dataRespCol).GetString();
                        if (string.IsNullOrEmpty(expectedValue) && hdr.TryGetValue(SuiteKeywords.Col_OC_Output, out var outCol))
                            expectedValue = ws.Cell(rowNum.Value, outCol).GetString();
                        break;
                }
            }
            else if (isServerSheet)
            {
                switch (validationType)
                {
                    case StepValidationType.DataRequest:
                        if (hdr.TryGetValue(SuiteKeywords.Col_OS_DataRequest, out var dataRequestCol))
                            expectedValue = ws.Cell(rowNum.Value, dataRequestCol).GetString();
                        break;
                    case StepValidationType.ConsoleOutput:
                        if (hdr.TryGetValue(SuiteKeywords.Col_OS_Output, out var osOutCol))
                            expectedValue = ws.Cell(rowNum.Value, osOutCol).GetString();
                        break;
                    default:
                        // For other validation types (METHOD, SIZE), try DataRequest first, then Output
                        if (hdr.TryGetValue(SuiteKeywords.Col_OS_DataRequest, out var dataReqCol))
                            expectedValue = ws.Cell(rowNum.Value, dataReqCol).GetString();
                        if (string.IsNullOrEmpty(expectedValue) && hdr.TryGetValue(SuiteKeywords.Col_OS_Output, out var outCol))
                            expectedValue = ws.Cell(rowNum.Value, outCol).GetString();
                        break;
                }
            }
            
            // Get actual value from runtime execution (captured in memory)
            // The capture key depends on the validation type
            if (!string.IsNullOrEmpty(record.QuestionCode))
            {
                string? captureKey = null;
                
                if (isClientSheet)
                {
                    captureKey = validationType switch
                    {
                        StepValidationType.DataResponse => _run.GetServerResponseCaptureKey(record.QuestionCode, stage.ToString()),
                        StepValidationType.ConsoleOutput => _run.GetClientCaptureKey(record.QuestionCode, stage.ToString()),
                        _ => _run.GetClientCaptureKey(record.QuestionCode, stage.ToString())
                    };
                }
                else if (isServerSheet)
                {
                    captureKey = validationType switch
                    {
                        StepValidationType.DataRequest => _run.GetServerRequestCaptureKey(record.QuestionCode, stage.ToString()),
                        StepValidationType.ConsoleOutput => _run.GetServerCaptureKey(record.QuestionCode, stage.ToString()),
                        _ => _run.GetServerCaptureKey(record.QuestionCode, stage.ToString())
                    };
                }
                
                if (captureKey != null && _run.TryGetCapturedOutput(captureKey, out var captured))
                {
                    actualValue = captured;
                }
                
                // If still no actual value, try reading from ActualPath
                if (string.IsNullOrEmpty(actualValue) && !string.IsNullOrEmpty(record.ActualPath))
                {
                    actualValue = TryReadContext(record.ActualPath, 5000);
                }
            }
            
            // Truncate long values for display in error report (keep it concise)
            if (!string.IsNullOrEmpty(expectedValue) && expectedValue.Length > ErrorReportMaxValueLength)
                expectedValue = expectedValue[..ErrorReportMaxValueLength] + "...";
            if (!string.IsNullOrEmpty(actualValue) && actualValue.Length > ErrorReportMaxValueLength)
                actualValue = actualValue[..ErrorReportMaxValueLength] + "...";
            
            return (expectedValue, actualValue);
        }

        /// <summary>
        /// Enum representing the validation type determined from a step ID.
        /// Used to determine which Excel column to read expected values from and which memory location to retrieve actual values from.
        /// </summary>
        private enum StepValidationType
        {
            /// <summary>Console output validation (OC-OUT or OS-OUT steps)</summary>
            ConsoleOutput,
            
            /// <summary>HTTP data response validation (OC-DATA steps)</summary>
            DataResponse,
            
            /// <summary>HTTP data request validation (OS-REQ steps)</summary>
            DataRequest,
            
            /// <summary>Other validation types (METHOD, STATUS, SIZE, etc.)</summary>
            Other
        }

        /// <summary>
        /// Determines the validation type from a step ID by parsing its structure.
        /// Step IDs follow the format: PREFIX-TYPE-STAGE (e.g., "OC-OUT-2", "OC-DATA-3", "OS-REQ-1").
        /// This method extracts the TYPE part to determine what kind of validation is being performed.
        /// </summary>
        /// <param name="stepId">
        /// The step ID to parse. Expected format: PREFIX-TYPE-STAGE where:
        /// - PREFIX is "OC" (OutputClients), "OS" (OutputServers), or "IC" (InputClients)
        /// - TYPE is "OUT" (output), "DATA" (data response), "REQ" (data request), "METHOD", "STATUS", "SIZE", etc.
        /// - STAGE is a numeric stage identifier
        /// Examples: "OC-OUT-2", "OC-DATA-3", "OS-REQ-1", "OC-METHOD-2"
        /// </param>
        /// <returns>
        /// The validation type:
        /// - ConsoleOutput for OUT steps (console/terminal output validation)
        /// - DataResponse for DATA steps (HTTP response body validation)
        /// - DataRequest for REQ steps (HTTP request body validation)
        /// - Other for all other types (METHOD, STATUS, SIZE, or invalid/null stepIds)
        /// </returns>
        private static StepValidationType GetValidationType(string? stepId)
        {
            // Handle null or empty stepId gracefully
            if (string.IsNullOrWhiteSpace(stepId))
                return StepValidationType.Other;
            
            // Step IDs have format: PREFIX-TYPE-STAGE (e.g., "OC-OUT-2", "OC-DATA-3")
            // We extract the TYPE part (index 1) to determine validation type
            var parts = stepId.Split('-');
            if (parts.Length >= 2)
            {
                var type = parts[1].ToUpperInvariant();
                return type switch
                {
                    "OUT" => StepValidationType.ConsoleOutput,
                    "DATA" => StepValidationType.DataResponse,
                    "REQ" => StepValidationType.DataRequest,
                    _ => StepValidationType.Other
                };
            }
            
            // If the stepId doesn't follow expected format, treat as Other
            return StepValidationType.Other;
        }

        public void Dispose() => _wb?.Dispose();
    }
}
