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

        // we keep summaries in-memory too, for end-of-suite WriteOverallSummary if the runner calls it
        private readonly List<TestCaseSummary> _caseSummaries = new();

        // Sheets expected in Detail.xlsx - support OLD and NEW formats
        // OLD format uses: InputClients/InputClient, OutputClients/OutputClient, OutputServers/OutputServer
        // NEW format uses: User, Client, Server, Network (completely different names)
        private const string SheetInput = SuiteKeywords.Sheet_InputClients;        // Old: "InputClients"
        private const string SheetInputAlt = SuiteKeywords.Sheet_InputClient;      // Old singular: "InputClient"
        private const string SheetOutClients = SuiteKeywords.Sheet_OutputClients;  // Old: "OutputClients"
        private const string SheetOutClientsAlt = SuiteKeywords.Sheet_OutputClient;// Old singular: "OutputClient"
        private const string SheetOutClientsNew = SuiteKeywords.Sheet_Client;     // NEW format: "Client"
        private const string SheetOutServers = SuiteKeywords.Sheet_OutputServers;  // Old: "OutputServers"
        private const string SheetOutServersAlt = SuiteKeywords.Sheet_OutputServer;// Old singular: "OutputServer"
        private const string SheetOutServersNew = SuiteKeywords.Sheet_Server;     // NEW format: "Server"
        
        // Maximum length for expected/actual values displayed in ErrorReport sheet
        private const int ErrorReportMaxValueLength = 100;
        
        // Cache for expected values from the template (preserved before any modifications)
        // Key: "SheetName_Stage_ColumnName" -> Value: expected value from template
        private readonly Dictionary<string, string> _expectedValuesCache = new();

        // Columns we always ensure exist
        // Simplified base columns - removed redundant ones per requirements
        private static readonly string[] BaseColumns =
        {
            GradingKeywords.Col_Stage
        };

        // Simplified result columns - removed DataType, Action, ErrorCode, ErrorCategory, 
        // PointsAwarded, PointsPossible, DurationMs, DetailPath per requirements
        // Kept ExpectedExcerpt and ActualExcerpt for mismatch focus
        // Result is written at the end
        private static readonly string[] ResultColumns =
        {
            GradingKeywords.Col_StudentConsole,  // Renamed from ClientStdout/ServerStdout
            GradingKeywords.Col_ExpectedExcerpt,
            GradingKeywords.Col_ActualExcerpt,
            GradingKeywords.Col_Message,
            GradingKeywords.Col_Result  // Moved to end
        };

        public ExcelDetailLogService(IFileService files, IRunContext run)
        {
            _files = files;
            _run = run;
        }

        /// <summary>
        /// Helper method to try getting a worksheet by name, supporting OLD format (plural/singular) and NEW format.
        /// </summary>
        private bool TryGetWorksheetFlexible(string primaryName, string alternateName, string newFormatName, out IXLWorksheet? worksheet)
        {
            if (_wb == null)
            {
                worksheet = null;
                return false;
            }
            
            // Try new format name first (User/Client/Server)
            if (_wb.Worksheets.TryGetWorksheet(newFormatName, out worksheet))
                return true;
            
            // Try primary name (old singular: InputClient/OutputClient/OutputServer)
            if (_wb.Worksheets.TryGetWorksheet(primaryName, out worksheet))
                return true;
            
            // Try alternate name (old plural: InputClients/OutputClients/OutputServers)
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

            // result root is parent of the case folder; that's where OverallSummary.xlsx lives
            var resultRoot = Path.GetDirectoryName(outFolder);
            _overallSummaryPath = string.IsNullOrEmpty(resultRoot) ? null : Path.Combine(resultRoot!, FileKeywords.FileName_OverallSummary);

            _totalMark = 0;
            _totalCompareSteps = 0;
            _allStepsPassed = true;
            _records.Clear();
            _expectedValuesCache.Clear(); // Clear cache for new test case

            _wb = new XLWorkbook(detailTemplatePath);

            // Try OLD format (plural/singular) and NEW format sheet names
            var sheetsToCheck = new[]
            {
                (Primary: SheetInputAlt, Alternate: SheetInput, NewFormat: SuiteKeywords.Sheet_User),
                (Primary: SheetOutClientsAlt, Alternate: SheetOutClients, NewFormat: SheetOutClientsNew),
                (Primary: SheetOutServersAlt, Alternate: SheetOutServers, NewFormat: SheetOutServersNew)
            };

            foreach (var (primary, alternate, newFormat) in sheetsToCheck)
            {
                if (!TryGetWorksheetFlexible(primary, alternate, newFormat, out var ws) || ws == null) continue;

                EnsureColumns(ws, BaseColumns);
                EnsureColumns(ws, ResultColumns);
                
                // Cache expected values from template BEFORE any modifications
                CacheExpectedValues(ws);

                // Skip InputClient/InputClients/User sheet for counting compare steps
                if (!string.Equals(ws.Name, SheetInput, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ws.Name, SheetInputAlt, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ws.Name, SuiteKeywords.Sheet_User, StringComparison.OrdinalIgnoreCase))
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

            // NOTE: PointsAwarded and PointsPossible columns have been removed per requirements.
            // Awarding logic is now only tracked internally for the summary.
            // Sheets now only contain: Stage, Console, StudentConsole, ExpectedExcerpt, ActualExcerpt, Message, Result

            // Add StudentConsole columns to result sheets for captured output during test execution
            // Renamed from STDOUT columns per requirements
            AddStdoutColumnsToSheets();

            // Create separate ErrorReport sheet with ALL errors (not just first one)
            CreateErrorReportSheet();
            
            // Create GradeProcess sheet to log the grading execution process
            CreateGradeProcessSheet();

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

            // 🔁 make sure the overall summary exists/updates after **every** case
            // Now includes error details directly in the summary
            if (!string.IsNullOrEmpty(_overallSummaryPath) && _questionCode != null)
            {
                var errorNotes = CollectErrorNotes();
                UpsertOverallSummaryRow(
                    _overallSummaryPath!,
                    _questionCode,
                    casePassed,
                    Math.Round(totalAwarded, 2),
                    Math.Round(totalPossible, 2),
                    errorNotes);
            }

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
            
            // Network flow validation steps (NETWORK-FLOW-*) return null from ResolveSheet
            // These should NOT be written to Client/Server sheets - they are handled by 
            // PopulateNetworkActualColumns for the Network sheet
            // We still add them to _records for tracking but skip writing to sheets
            if (sheetHint == null)
            {
                // Still record the step for internal tracking (ErrorReport, etc.)
                _records.Add(new StepGradeRecord
                {
                    QuestionCode = step.QuestionCode,
                    StepId = step.Id,
                    Stage = step.Stage,
                    Action = step.Action,
                    Passed = passed,
                    PointsAwarded = 0,
                    PointsPossible = 0, // Network flow steps don't contribute to Client/Server sheet points
                    DurationMs = durationMs,
                    ErrorCode = errorCode,
                    ErrorCategory = ErrorCodes.CategoryOf(errorCode),
                    Message = message ?? string.Empty,
                    DetailPath = detailPath,
                    ActualPath = actualPath
                });
                return;
            }
            
            // Try to get worksheet - support OLD (plural/singular) and NEW formats
            IXLWorksheet? ws = null;
            if (string.Equals(sheetHint, SheetOutClients, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutClientsAlt, SheetOutClients, SheetOutClientsNew, out ws))
                    return;
            }
            else if (string.Equals(sheetHint, SheetOutServers, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutServersAlt, SheetOutServers, SheetOutServersNew, out ws))
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

            // Simplified output per requirements: 
            // Removed ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath
            // Result column is at the end now
            
            // Write StudentConsole (captured output) for all steps
            TryWriteStudentConsole(ws, hdr, rowNum, stage, step.Id, actualPath);
            
            // Only write detailed information when test fails (optimization)
            if (!passed)
            {
                SetCell(ws, rowNum, hdr, GradingKeywords.Col_Message, message ?? string.Empty);
                
                // Write diff columns with colored excerpts for mismatch focus
                TryWriteDiffColumns(ws, hdr, rowNum, stage, step.Id, null, message, actualPath);
            }
            else
            {
                // For passing tests, only show brief success message
                SetCell(ws, rowNum, hdr, GradingKeywords.Col_Message, message ?? GradingKeywords.Result_Pass);
            }
            
            // Result at the end (per requirements: Result should be at the very end to show FAIL/PASS)
            SetCell(ws, rowNum, hdr, GradingKeywords.Col_Result, passed ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail);
            
            // Color code result cell
            if (hdr.TryGetValue(GradingKeywords.Col_Result, out var resultCol))
            {
                ws.Cell(rowNum, resultCol).Style.Fill.BackgroundColor = passed ? XLColor.LightGreen : XLColor.LightPink;
            }

            // Keep internal record tracking for ErrorReport sheet and summary
            var perStep = _totalCompareSteps > 0 ? _totalMark / _totalCompareSteps : 0;
            var actualPossible = pointsPossible > 0 ? perStep : 0;
            
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

        /// <summary>
        /// [DEPRECATED] Writes actual output to the ActualOutput column.
        /// This method is now deprecated as the ActualOutput column has been removed.
        /// Use TryWriteStudentConsole instead which writes to the StudentConsole column.
        /// </summary>
        [Obsolete("ActualOutput column removed. Use TryWriteStudentConsole instead.")]
        private void TryWriteActualOutput(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string stepId, string? actualPath)
        {
            // No-op: ActualOutput column has been removed from the simplified column structure
            // Data is now written to StudentConsole column via TryWriteStudentConsole method
        }

        /// <summary>
        /// Writes the captured student console output (previously ClientStdout/ServerStdout) to the StudentConsole column.
        /// This is written for every stage, not just failed tests, per requirements.
        /// </summary>
        private void TryWriteStudentConsole(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string stepId, string? actualPath)
        {
            try
            {
                string? consoleOutput = null;
                
                // Try to get from actualPath first
                if (!string.IsNullOrEmpty(actualPath))
                {
                    consoleOutput = TryReadContext(actualPath, 5000);
                }
                
                // If no actualPath provided, try to infer from the sheet and stage
                if (string.IsNullOrEmpty(consoleOutput) && !string.IsNullOrEmpty(_questionCode))
                {
                    var sheetName = ws.Name;
                    var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutClientsAlt, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutClientsNew, StringComparison.OrdinalIgnoreCase);
                    var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutServersAlt, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(sheetName, SheetOutServersNew, StringComparison.OrdinalIgnoreCase);
                    
                    string? captureKey = null;
                    
                    if (isClientSheet)
                    {
                        captureKey = _run.GetClientCaptureKey(_questionCode, stage.ToString());
                    }
                    else if (isServerSheet)
                    {
                        captureKey = _run.GetServerCaptureKey(_questionCode, stage.ToString());
                    }
                    
                    if (captureKey != null && _run.TryGetCapturedOutput(captureKey, out var captured))
                    {
                        consoleOutput = captured;
                    }
                }
                
                if (!string.IsNullOrEmpty(consoleOutput))
                {
                    // Truncate if too long for display
                    if (consoleOutput.Length > 5000)
                    {
                        consoleOutput = consoleOutput.Substring(0, 5000) + "... (truncated)";
                    }
                    SetCell(ws, rowNum, hdr, GradingKeywords.Col_StudentConsole, consoleOutput);
                }
            }
            catch { /* best effort */ }
        }

        private void TryWriteDiffColumns(IXLWorksheet ws, Dictionary<string, int> hdr, int rowNum, int stage, string stepId, string? detailPath, string? message, string? actualPath)
        {
            try
            {
                // Get expected output from the cached template values (preserved before any modifications)
                // The column to read from depends on the validation type, which is determined by the StepId:
                // - OC-OUT- = console output validation -> read from Output column
                // - OC-DATA- = data response validation -> read from DataResponse column
                // - OS-OUT- = server console output validation -> read from Output column
                // - OS-REQ- = data request validation -> read from DataRequest column
                string? expectedOutput = null;
                var sheetName = ws.Name;
                var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutClientsAlt, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutClientsNew, StringComparison.OrdinalIgnoreCase);
                var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutServersAlt, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(sheetName, SheetOutServersNew, StringComparison.OrdinalIgnoreCase);
                
                // Determine which column to read from based on the validation type
                var validationType = GetValidationType(stepId);
                
                if (isClientSheet)
                {
                    // For OutputClients/OutputClient/Client sheets, determine which column to read based on validation type
                    switch (validationType)
                    {
                        case StepValidationType.DataResponse:
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_DataResponse);
                            break;
                        case StepValidationType.ConsoleOutput:
                            // NEW format uses "Console", OLD format uses "Output"
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, "Console");
                            if (string.IsNullOrEmpty(expectedOutput))
                                expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_Output);
                            break;
                        default:
                            // For other validation types (METHOD, STATUS, SIZE), try DataResponse first, then Output/Console
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_DataResponse);
                            if (string.IsNullOrEmpty(expectedOutput))
                            {
                                expectedOutput = GetCachedExpectedValue(sheetName, stage, "Console");
                                if (string.IsNullOrEmpty(expectedOutput))
                                    expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_Output);
                            }
                            break;
                    }
                }
                
                if (string.IsNullOrEmpty(expectedOutput) && isServerSheet)
                {
                    // For OutputServers/OutputServer/Server sheets, determine which column to read based on validation type
                    switch (validationType)
                    {
                        case StepValidationType.DataRequest:
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_DataRequest);
                            break;
                        case StepValidationType.ConsoleOutput:
                            // NEW format uses "Console", OLD format uses "Output"
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, "Console");
                            if (string.IsNullOrEmpty(expectedOutput))
                                expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_Output);
                            break;
                        default:
                            // For other validation types (METHOD, SIZE), try DataRequest first, then Output/Console
                            expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_DataRequest);
                            if (string.IsNullOrEmpty(expectedOutput))
                            {
                                expectedOutput = GetCachedExpectedValue(sheetName, stage, "Console");
                                if (string.IsNullOrEmpty(expectedOutput))
                                    expectedOutput = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_Output);
                            }
                            break;
                    }
                }
                
                // If no expected output in template, try reading from detailPath (diff file)
                if (string.IsNullOrEmpty(expectedOutput) && !string.IsNullOrEmpty(detailPath) && File.Exists(detailPath))
                {
                    expectedOutput = TryReadContext(detailPath, 5000);
                }

                // Get actual output from StudentConsole column (or fall back to reading from memory)
                string? actualOutput = null;
                if (hdr.TryGetValue(GradingKeywords.Col_StudentConsole, out var studentConsoleCol))
                {
                    actualOutput = ws.Cell(rowNum, studentConsoleCol).GetString();
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

                // NOTE: ExpectedOutput and ActualOutput columns have been removed from the simplified structure.
                // Expected output is now in the "Console" column (from test kit).
                // Actual output is now in the "StudentConsole" column.
                // We only write color-coded excerpts for mismatch analysis.
                
                // Color StudentConsole (actual output) in red for failed comparisons
                if (!string.IsNullOrEmpty(actualOutput) && hdr.TryGetValue(GradingKeywords.Col_StudentConsole, out var scCol))
                {
                    ws.Cell(rowNum, scCol).Style.Font.FontColor = XLColor.DarkRed;
                    ws.Cell(rowNum, scCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                }

                // Also write excerpts around the difference point for quick comparison
                var idx = FirstDiffIndexFromMessage(message ?? string.Empty);
                if (idx >= 0)
                {
                    // DiffIndex column removed - skip writing
                    
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

        /// <summary>
        /// Collects error notes from all failed test steps to include in the OverallSummary.
        /// This replaces the need for a separate FailedTestDetail.xlsx file.
        /// Network flow validation steps (NETWORK-FLOW-*) are included but labeled as "Network Flow".
        /// </summary>
        /// <returns>A summary string of all errors, or empty string if all tests passed</returns>
        private string CollectErrorNotes()
        {
            if (_allStepsPassed) return string.Empty;

            var failedRecords = _records.Where(r => !r.Passed).ToList();
            if (failedRecords.Count == 0) return string.Empty;

            var notes = new StringBuilder();
            notes.AppendLine($"Failed {failedRecords.Count} step(s):");

            // Group failures by stage for better readability
            var groupedByStage = failedRecords.GroupBy(r => r.Stage).OrderBy(g => g.Key);
            
            foreach (var stageGroup in groupedByStage)
            {
                notes.AppendLine($"  Stage {stageGroup.Key}:");
                foreach (var record in stageGroup)
                {
                    // Use shared method to get human-readable validation type label
                    var validationType = GetValidationTypeLabel(record.StepId);
                    
                    // Create concise error message
                    var message = record.Message ?? "Unknown error";
                    // Truncate long messages for summary
                    if (message.Length > 100)
                        message = message.Substring(0, 97) + "...";
                    
                    notes.AppendLine($"    - {validationType}: {message}");
                }
            }

            return notes.ToString().TrimEnd();
        }

        /// <summary>
        /// Updates or inserts a row in the OverallSummary.xlsx file with test case results and error details.
        /// This method is called after each test case completes, ensuring incremental summary updates.
        /// </summary>
        /// <param name="summaryPath">Path to the OverallSummary.xlsx file</param>
        /// <param name="testCase">Test case identifier (e.g., "TC01", "TC02")</param>
        /// <param name="passed">Whether the test case passed all validations</param>
        /// <param name="pointsAwarded">Points awarded for this test case</param>
        /// <param name="pointsPossible">Maximum points possible for this test case</param>
        /// <param name="errorNotes">Detailed error notes to include for failed tests (empty for passed tests)</param>
        private void UpsertOverallSummaryRow(string summaryPath, string testCase, bool passed, double pointsAwarded, double pointsPossible, string errorNotes)
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
            
            // Add error notes column (column 5) - only for failed tests
            if (!passed && !string.IsNullOrEmpty(errorNotes))
            {
                ws.Cell(row, 5).Value = errorNotes;
                ws.Cell(row, 5).Style.Alignment.WrapText = true;
                ws.Cell(row, 5).Style.Fill.BackgroundColor = XLColor.LightPink;
            }

            // Autofit & wrap for readability
            for (int c = 1; c <= 5; c++)
            {
                ws.Column(c).Style.Alignment.WrapText = true;
                ws.Column(c).AdjustToContents(1, ws.LastRowUsed().RowNumber(), 5, 100);
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

        /// <summary>
        /// Creates a new workbook for OverallSummary.xlsx with appropriate column headers.
        /// Includes an ErrorNotes column to display failure details inline (replacing FailedTestDetail.xlsx).
        /// </summary>
        /// <returns>A new XLWorkbook with the Summary sheet and column headers</returns>
        private static XLWorkbook CreateNewWorkbook()
        {
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(GradingKeywords.Sheet_Summary);
            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";
            ws.Cell(1, 5).Value = GradingKeywords.Col_ErrorNotes;
            ws.Row(1).Style.Font.Bold = true;
            return wb;
        }

        /// <summary>
        /// Determines which sheet to log step results to based on step ID prefix.
        /// Returns null for steps that should not be logged to Client/Server sheets.
        /// 
        /// Network flow validation steps (NETWORK-FLOW-*) return null because:
        /// - They validate TCP handshake (SYN, SYN-ACK, ACK, FIN-ACK) and connection lifecycle
        /// - Their results are recorded in the Network sheet via PopulateNetworkActualColumns
        /// - They should NOT appear in Client/Server sheets as they don't represent
        ///   client/server console output or application data
        /// </summary>
        /// <param name="step">The step being processed</param>
        /// <param name="actualPath">Optional path hint for sheet resolution</param>
        /// <returns>Sheet name to log to, or null if step should not be logged to Client/Server sheets</returns>
        private static string? ResolveSheet(Step step, string? actualPath)
        {
            // PRIMARY: Use Step ID prefix to determine sheet (most reliable)
            // OLD format: OC- = OutputClient, OS- = OutputServer, IC- = InputClient
            // NEW format: SERVER- = Server sheet, CLIENT- = Client sheet, USER- = User sheet
            
            var stepId = step.Id.ToUpperInvariant();
            
            // Check OLD format prefixes (OS-, OC-, IC-)
            if (stepId.StartsWith(GradingKeywords.StepPrefix_OutputServer.ToUpperInvariant()))
                return SheetOutServers;
            
            if (stepId.StartsWith(GradingKeywords.StepPrefix_OutputClient.ToUpperInvariant()))
                return SheetOutClients;
            
            // Check NEW format prefixes (SERVER-, CLIENT-, NETWORK-)
            // SERVER-CONSOLE, SERVER-REQUEST, etc. should go to Server sheet
            if (stepId.StartsWith("SERVER-"))
                return SheetOutServers;
            
            // CLIENT-CONSOLE, CLIENT-DATA, etc. should go to Client sheet
            if (stepId.StartsWith("CLIENT-"))
                return SheetOutClients;
            
            // NETWORK-FLOW-* steps (TCP handshake validation) should NOT be logged to Client/Server sheets
            // They are handled separately in PopulateNetworkActualColumns for the Network sheet
            // Format: NETWORK-FLOW-{stage}-{rowIndex} e.g., "NETWORK-FLOW-3-1"
            if (stepId.StartsWith("NETWORK-FLOW-"))
                return null;
            
            // Other NETWORK- steps (HTTP method, status code, payload) go to Client sheet
            // These represent HTTP-level validation which is client-facing
            if (stepId.StartsWith("NETWORK-"))
                return SheetOutClients;
            
            // SECONDARY: Check actual path hint (for backward compatibility)
            var lower = (actualPath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (lower.Contains($"/{FileKeywords.Folder_Actual}/{FileKeywords.Folder_Servers}/"))
                return SheetOutServers;
            if (lower.Contains($"/{FileKeywords.Folder_Actual}/{FileKeywords.Folder_Clients}/"))
                return SheetOutClients;

            // TERTIARY: Fallback by action (least reliable)
            var action = (step.Action ?? string.Empty).ToUpperInvariant();
            if (action.Contains("SERVER"))
                return SheetOutServers;
            
            // DEFAULT: Client sheet (for input steps and others like USER-)
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

        /// <summary>
        /// Adds STDOUT columns to the appropriate result sheets.
        /// Each sheet only gets its relevant STDOUT column:
        /// - Client sheet: ClientStdout only
        /// - Server sheet: ServerStdout only  
        /// - Network sheet: NetworkStdout only
        /// This prevents duplicate/irrelevant data from being shown on each sheet.
        /// 
        /// If there is captured network data but no Network sheet in the template,
        /// this method will create one with the captured data for easy analysis.
        /// 
        /// UPDATE: Renamed ClientStdout/ServerStdout to StudentConsole per requirements.
        /// </summary>
        private void AddStdoutColumnsToSheets()
        {
            if (_wb == null || _records.Count == 0) return;

            // Add StudentConsole column to Client sheet (renamed from ClientStdout)
            if (TryGetWorksheetFlexible(SheetOutClientsAlt, SheetOutClients, SheetOutClientsNew, out var clientWs) && clientWs != null)
            {
                EnsureColumns(clientWs, new[] { GradingKeywords.Col_StudentConsole });
                var hdr = GetHeaderIndex(clientWs);
                PopulateStudentConsoleColumn(clientWs, hdr, isClientSheet: true);
            }

            // Add StudentConsole column to Server sheet (renamed from ServerStdout)
            if (TryGetWorksheetFlexible(SheetOutServersAlt, SheetOutServers, SheetOutServersNew, out var serverWs) && serverWs != null)
            {
                EnsureColumns(serverWs, new[] { GradingKeywords.Col_StudentConsole });
                var hdr = GetHeaderIndex(serverWs);
                PopulateStudentConsoleColumn(serverWs, hdr, isClientSheet: false);
            }
            
            // Handle Network sheet - create it if there's captured network data
            IXLWorksheet? networkWs;
            if (_wb.Worksheets.TryGetWorksheet(SuiteKeywords.Sheet_Network, out networkWs))
            {
                // Network sheet exists in template - preserve expected Data column and add actual data columns
                // CRITICAL FIX: The template's Network sheet Data column contains expected values (e.g., "S123", JSON responses)
                // These values must be preserved when creating the graded output for comparison
                // ClosedXML sometimes treats empty/populated cells inconsistently, so we explicitly preserve Data values
                
                EnsureColumns(networkWs, new[] 
                { 
                    GradingKeywords.Col_ActualFlags,
                    GradingKeywords.Col_ActualState,
                    GradingKeywords.Col_ActualSourceRole,
                    GradingKeywords.Col_ActualDestRole,
                    GradingKeywords.Col_ActualData,
                    GradingKeywords.Col_ActualSourcePort,
                    GradingKeywords.Col_ActualDestPort,
                    GradingKeywords.Col_Result  // Changed from Col_NetworkResult to Col_Result per requirements
                });
                var hdr = GetHeaderIndex(networkWs);
                
                // IMPORTANT: Preserve expected Data column values from template
                // The template Detail.xlsx contains expected Data values that must be shown for comparison
                // This ensures instructors can see both expected and actual data side-by-side
                PreserveNetworkExpectedData(networkWs, hdr);
                
                PopulateNetworkActualColumns(networkWs, hdr);
            }
            else
            {
                // Network sheet doesn't exist - check if we have captured network data
                // If so, create a new Network sheet with the captured data
                var questionCode = _questionCode ?? "";
                bool hasNetworkData = false;
                
                // Check if any stage has captured network packets
                for (int stage = 0; stage <= GradingKeywords.MaxStagesToCheck; stage++)
                {
                    var packets = _run.GetCapturedNetworkPackets(questionCode, stage.ToString());
                    if (packets.Count > 0)
                    {
                        hasNetworkData = true;
                        break;
                    }
                }
                
                if (hasNetworkData)
                {
                    networkWs = CreateNetworkSheetFromCapturedData(questionCode);
                }
            }
        }
        
        /// <summary>
        /// Creates a Network sheet from captured network data when no template exists.
        /// This ensures network traffic is always visible in the output even if the
        /// test kit didn't define expected network flow.
        /// 
        /// The whole point of network monitoring is to boot up and capture the network
        /// flow on the exposed port. This method ensures that captured data is shown
        /// regardless of whether the test kit had a Network sheet.
        /// </summary>
        private IXLWorksheet CreateNetworkSheetFromCapturedData(string questionCode)
        {
            if (_wb == null)
            {
                throw new InvalidOperationException("Workbook is not initialized");
            }
            
            var networkWs = _wb.AddWorksheet(SuiteKeywords.Sheet_Network);
            
            // Create header row with columns for captured network data
            networkWs.Cell(1, 1).Value = NetworkKeywords.Col_Stage;
            networkWs.Cell(1, 2).Value = NetworkKeywords.Col_Time;
            networkWs.Cell(1, 3).Value = NetworkKeywords.Col_Flags;
            networkWs.Cell(1, 4).Value = NetworkKeywords.Col_State;
            networkWs.Cell(1, 5).Value = NetworkKeywords.Col_SourceRole;
            networkWs.Cell(1, 6).Value = NetworkKeywords.Col_DestinationRole;
            networkWs.Cell(1, 7).Value = NetworkKeywords.Col_Data;
            networkWs.Cell(1, 8).Value = GradingKeywords.Col_Message;
            
            networkWs.Row(1).Style.Font.Bold = true;
            networkWs.Row(1).Style.Fill.BackgroundColor = XLColor.LightBlue;
            
            int row = 2;
            
            // Iterate through stages and add captured packets
            for (int stage = 0; stage <= GradingKeywords.MaxStagesToCheck; stage++)
            {
                var packets = _run.GetCapturedNetworkPackets(questionCode, stage.ToString());
                if (packets.Count == 0) continue;
                
                foreach (var packet in packets)
                {
                    networkWs.Cell(row, 1).Value = stage;
                    networkWs.Cell(row, 2).Value = packet.Timestamp.ToString("HH:mm:ss.fff");
                    networkWs.Cell(row, 3).Value = packet.Flags ?? "";
                    networkWs.Cell(row, 4).Value = packet.State ?? "";
                    networkWs.Cell(row, 5).Value = packet.SourceRole ?? "";
                    networkWs.Cell(row, 6).Value = packet.DestinationRole ?? "";
                    
                    // Truncate data if too long
                    if (!string.IsNullOrEmpty(packet.Data))
                    {
                        var dataPreview = packet.Data.Length > PortKeywords.ACTUAL_DATA_COLUMN_MAX_CHARS 
                            ? packet.Data.Substring(0, PortKeywords.ACTUAL_DATA_COLUMN_MAX_CHARS) + "..." 
                            : packet.Data;
                        networkWs.Cell(row, 7).Value = dataPreview;
                    }
                    
                    networkWs.Cell(row, 8).Value = "Captured network traffic";
                    
                    row++;
                }
            }
            
            // Add summary at the bottom
            if (row > 2)
            {
                row++;
                networkWs.Cell(row, 1).Value = "Summary";
                networkWs.Cell(row, 1).Style.Font.Bold = true;
                row++;
                networkWs.Cell(row, 1).Value = $"Total packets captured: {row - 3}";
                networkWs.Cell(row, 8).Value = "This sheet was auto-generated from captured network traffic";
                networkWs.Cell(row, 8).Style.Font.Italic = true;
            }
            else
            {
                // No packets captured - add a note
                networkWs.Cell(2, 1).Value = "-";
                networkWs.Cell(2, 8).Value = "No network traffic was captured. Ensure NPcap/libpcap is installed and the server is running on the monitored port.";
                networkWs.Cell(2, 8).Style.Font.FontColor = XLColor.DarkRed;
            }
            
            // Adjust column widths
            networkWs.Style.Alignment.WrapText = true;
            networkWs.Columns().AdjustToContents(1, networkWs.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
            
            return networkWs;
        }
        
        /// <summary>
        /// Tries to get a column index from the header dictionary, supporting multiple naming conventions.
        /// This method checks for both the primary column name and alternative names (e.g., with underscores).
        /// Returns the column index (1-based) if found, or 0 if not found.
        /// </summary>
        /// <param name="hdr">Header dictionary mapping column names to indices</param>
        /// <param name="names">Column names to try (primary first, then alternates)</param>
        /// <returns>Column index (1-based) if found, 0 if not found</returns>
        private int TryGetColumnIndex(Dictionary<string, int> hdr, params string[] names)
        {
            foreach (var name in names.Where(n => !string.IsNullOrEmpty(n)))
            {
                if (hdr.TryGetValue(name, out int colIndex))
                {
                    return colIndex;
                }
            }
            return 0;
        }
        
        
        /// <summary>
        /// Preserves expected Data column values from the Network sheet template.
        /// The template Detail.xlsx contains expected payload values (e.g., "S123", "None", JSON responses)
        /// that must be retained in the graded output for side-by-side comparison with actual captured data.
        /// 
        /// This method explicitly reads and re-writes ALL Data column values to ensure they are preserved.
        /// ClosedXML sometimes treats cells inconsistently, so we force preservation of all values.
        /// </summary>
        private void PreserveNetworkExpectedData(IXLWorksheet ws, Dictionary<string, int> hdr)
        {
            if (!hdr.TryGetValue(NetworkKeywords.Col_Data, out var dataCol)) return;
            
            var rng = ws.RangeUsed();
            if (rng == null) return;
            
            // Read all Data column values and re-write them to ensure they're preserved
            // This forces ClosedXML to recognize the values and keep them when saving
            // CRITICAL: Preserve ALL values including "None", empty strings, and null
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                var dataCell = ws.Cell(row.RowNumber(), dataCol);
                
                // CRITICAL FIX: Get the actual string value directly
                // Don't use cellValue.IsBlank or cellValue.IsText as they may not work correctly
                // for all cell types. Just get the string representation and re-assign it.
                try
                {
                    var dataValue = dataCell.GetString();
                    // Re-assign to force ClosedXML to preserve it
                    // Even if empty or "None", we want to keep it
                    dataCell.Value = dataValue;
                }
                catch
                {
                    // If GetString() fails, try getting as Value
                    var cellValue = dataCell.Value;
                    if (!cellValue.IsBlank)
                    {
                        dataCell.Value = cellValue.ToString() ?? "";
                    }
                }
            }
        }
        
        /// <summary>
        /// Populates the Network sheet with actual captured data for side-by-side comparison.
        /// Each row in the Network sheet represents an expected packet from the test kit.
        /// This method adds actual captured data columns to enable easy verification of:
        /// - TCP flags (SYN, SYN-ACK, ACK, PSH-ACK, FIN-ACK, RST)
        /// - Connection state descriptions
        /// - Source/Destination roles (Client/Server)
        /// - Payload data (for PSH packets)
        /// 
        /// Supports two naming conventions:
        /// 1. New format: ActualFlags, ActualState, NetworkResult (no underscores)
        /// 2. Legacy format: Actual_Flags, Actual_State, Result (with underscores)
        /// </summary>
        private void PopulateNetworkActualColumns(IXLWorksheet ws, Dictionary<string, int> hdr)
        {
            if (!hdr.TryGetValue(NetworkKeywords.Col_Stage, out var stageCol)) return;
            
            var rng = ws.RangeUsed();
            if (rng == null) return;
            
            // Get actual data column indices - support BOTH naming conventions:
            // 1. "ActualFlags" (no underscore) - current/new format
            // 2. "Actual_Flags" (with underscore) - legacy format
            int actualFlagsCol = TryGetColumnIndex(hdr, "ActualFlags", "Actual_Flags");
            int actualStateCol = TryGetColumnIndex(hdr, "ActualState", "Actual_State");
            int actualSrcRoleCol = TryGetColumnIndex(hdr, "ActualSourceRole", "Actual_SourceRole");
            int actualDstRoleCol = TryGetColumnIndex(hdr, "ActualDestRole", "Actual_DestinationRole");
            int actualDataCol = TryGetColumnIndex(hdr, "ActualData", "Actual_Data");
            int actualSrcPortCol = TryGetColumnIndex(hdr, "ActualSourcePort", "Actual_SourcePort");
            int actualDstPortCol = TryGetColumnIndex(hdr, "ActualDestPort", "Actual_DestinationPort");
            int resultCol = TryGetColumnIndex(hdr, "NetworkResult", "Result");
            
            // Get expected data column indices for comparison - also support both conventions
            int expFlagsCol = TryGetColumnIndex(hdr, NetworkKeywords.Col_Flags, "Expected_Flags");
            int expSrcRoleCol = TryGetColumnIndex(hdr, NetworkKeywords.Col_SourceRole, "Expected_SourceRole");
            int expDstRoleCol = TryGetColumnIndex(hdr, NetworkKeywords.Col_DestinationRole, "Expected_DestinationRole");
            int expDataCol = TryGetColumnIndex(hdr, NetworkKeywords.Col_Data, "Expected_Data");
            
            // Track per-stage packet indices for matching with captured data
            var stagePacketIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var questionCode = _questionCode ?? "";
            
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                var stageStr = row.Cell(stageCol).GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(stageStr)) continue;
                
                // Get or initialize the per-stage packet index
                if (!stagePacketIndices.TryGetValue(stageStr, out var packetIndex))
                {
                    packetIndex = 0;
                }
                
                // Get captured packets for this stage
                var capturedPackets = _run.GetCapturedNetworkPackets(questionCode, stageStr);
                
                // Check if we have a packet at this index
                if (capturedPackets != null && packetIndex < capturedPackets.Count)
                {
                    var actualPacket = capturedPackets[packetIndex];
                    
                    // Populate actual data columns
                    if (actualFlagsCol > 0)
                        ws.Cell(row.RowNumber(), actualFlagsCol).Value = actualPacket.Flags ?? "";
                    if (actualStateCol > 0)
                        ws.Cell(row.RowNumber(), actualStateCol).Value = actualPacket.State ?? "";
                    if (actualSrcRoleCol > 0)
                        ws.Cell(row.RowNumber(), actualSrcRoleCol).Value = actualPacket.SourceRole ?? "";
                    if (actualDstRoleCol > 0)
                        ws.Cell(row.RowNumber(), actualDstRoleCol).Value = actualPacket.DestinationRole ?? "";
                    if (actualSrcPortCol > 0)
                        ws.Cell(row.RowNumber(), actualSrcPortCol).Value = actualPacket.SourcePort;
                    if (actualDstPortCol > 0)
                        ws.Cell(row.RowNumber(), actualDstPortCol).Value = actualPacket.DestinationPort;
                    if (actualDataCol > 0 && !string.IsNullOrEmpty(actualPacket.Data))
                    {
                        var dataPreview = actualPacket.Data.Length > PortKeywords.ACTUAL_DATA_COLUMN_MAX_CHARS 
                            ? actualPacket.Data.Substring(0, PortKeywords.ACTUAL_DATA_COLUMN_MAX_CHARS) + "..." 
                            : actualPacket.Data;
                        ws.Cell(row.RowNumber(), actualDataCol).Value = dataPreview;
                    }
                    
                    // Determine if this packet matches expected values
                    bool matched = true;
                    
                    // Compare flags (normalize for comparison)
                    if (expFlagsCol > 0)
                    {
                        var expectedFlags = row.Cell(expFlagsCol).GetString()?.Trim() ?? "";
                        var actualFlags = actualPacket.Flags ?? "";
                        if (!FlagsMatch(expectedFlags, actualFlags))
                        {
                            matched = false;
                        }
                    }
                    
                    // Compare source role
                    if (matched && expSrcRoleCol > 0)
                    {
                        var expectedSrcRole = row.Cell(expSrcRoleCol).GetString()?.Trim() ?? "";
                        var actualSrcRole = actualPacket.SourceRole ?? "";
                        if (!string.IsNullOrEmpty(expectedSrcRole) && 
                            !string.Equals(expectedSrcRole, actualSrcRole, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = false;
                        }
                    }
                    
                    // Compare destination role
                    if (matched && expDstRoleCol > 0)
                    {
                        var expectedDstRole = row.Cell(expDstRoleCol).GetString()?.Trim() ?? "";
                        var actualDstRole = actualPacket.DestinationRole ?? "";
                        if (!string.IsNullOrEmpty(expectedDstRole) && 
                            !string.Equals(expectedDstRole, actualDstRole, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = false;
                        }
                    }
                    
                    // Compare Data payload if expected data is provided
                    // Note: Excel uses null, empty string, or "None" to indicate "no data expected"
                    // We only validate data if the expected value is non-empty and not "None"
                    if (matched && expDataCol > 0)
                    {
                        var expectedData = row.Cell(expDataCol).GetString()?.Trim() ?? "";
                        var actualData = actualPacket.Data ?? "";
                        
                        // Only compare if expected data is specified and not "None"
                        if (!string.IsNullOrEmpty(expectedData) && 
                            !expectedData.Equals("None", StringComparison.OrdinalIgnoreCase))
                        {
                            // Trim and compare STRICTLY (case-sensitive, no normalization)
                            // Network data must match exactly to catch encoding/casing bugs
                            if (!actualData.Trim().Equals(expectedData.Trim(), StringComparison.Ordinal))
                            {
                                matched = false;
                            }
                        }
                    }
                    
                    // Set result
                    if (resultCol > 0)
                    {
                        ws.Cell(row.RowNumber(), resultCol).Value = matched ? GradingKeywords.Result_Pass : GradingKeywords.Result_Fail;
                        if (matched)
                        {
                            ws.Cell(row.RowNumber(), resultCol).Style.Font.FontColor = XLColor.DarkGreen;
                            ws.Cell(row.RowNumber(), resultCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
                        }
                        else
                        {
                            ws.Cell(row.RowNumber(), resultCol).Style.Font.FontColor = XLColor.DarkRed;
                            ws.Cell(row.RowNumber(), resultCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                        }
                    }
                }
                else
                {
                    // No packet captured at this index - this is a failure (missing network traffic)
                    if (resultCol > 0)
                    {
                        ws.Cell(row.RowNumber(), resultCol).Value = GradingKeywords.Result_Fail;
                        ws.Cell(row.RowNumber(), resultCol).Style.Font.FontColor = XLColor.DarkRed;
                        ws.Cell(row.RowNumber(), resultCol).Style.Fill.BackgroundColor = XLColor.LightPink;
                    }
                    
                    // Add note about missing packet
                    if (actualFlagsCol > 0)
                    {
                        ws.Cell(row.RowNumber(), actualFlagsCol).Value = "(Missing)";
                        ws.Cell(row.RowNumber(), actualFlagsCol).Style.Font.FontColor = XLColor.DarkRed;
                    }
                }
                
                // Increment per-stage packet index for next row
                stagePacketIndices[stageStr] = packetIndex + 1;
            }
            
            // Adjust column widths
            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
        }
        
        /// <summary>
        /// Compares TCP flags for equality, ignoring order.
        /// e.g., "SYN, ACK" should match "ACK, SYN"
        /// </summary>
        private static bool FlagsMatch(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) && string.IsNullOrWhiteSpace(actual))
                return true;
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
                return false;
            
            // Use regex to extract flag names, ignoring all delimiters (comma, period, hyphen, space, pipe, underscore, etc.)
            // This is more robust than trying to list every possible separator
            // Pattern [a-zA-Z]+ matches one or more letters (e.g., "SYN", "ACK", "PSH", "FIN", "RST")
            // Handles formats: "SYN, ACK", "SYN.ACK", "SYN-ACK", "SYN ACK", "SYN|ACK", "SYN_ACK"
            var expectedFlags = System.Text.RegularExpressions.Regex.Matches(expected, @"[a-zA-Z]+")
                .Select(m => m.Value.ToUpperInvariant())
                .ToHashSet();
            
            var actualFlags = System.Text.RegularExpressions.Regex.Matches(actual, @"[a-zA-Z]+")
                .Select(m => m.Value.ToUpperInvariant())
                .ToHashSet();
            
            // Use set equality - order doesn't matter, just the flags present
            return expectedFlags.SetEquals(actualFlags);
        }
        
        /// <summary>
        /// Populates StudentConsole column with captured output data for each stage in the worksheet.
        /// Renamed from PopulateStdoutColumns per requirements to use unified StudentConsole column.
        /// </summary>
        private void PopulateStudentConsoleColumn(IXLWorksheet ws, Dictionary<string, int> hdr, bool isClientSheet)
        {
            if (!hdr.TryGetValue(GradingKeywords.Col_Stage, out var stageCol)) return;
            
            var rng = ws.RangeUsed();
            if (rng == null) return;
            
            // Get StudentConsole column index
            if (!hdr.TryGetValue(GradingKeywords.Col_StudentConsole, out var consoleCol) || consoleCol <= 0) return;
            
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                if (!int.TryParse(row.Cell(stageCol).GetString(), out var stage)) continue;
                
                var stageStr = stage.ToString();
                var questionCode = _questionCode ?? "";
                
                // Get capture key based on sheet type
                var captureKey = isClientSheet 
                    ? _run.GetClientCaptureKey(questionCode, stageStr)
                    : _run.GetServerCaptureKey(questionCode, stageStr);
                    
                if (_run.TryGetCapturedOutput(captureKey, out var output) && !string.IsNullOrEmpty(output))
                {
                    var truncated = output.Length > PortKeywords.OUTPUT_PREVIEW_MAX_CHARS 
                        ? output[..PortKeywords.OUTPUT_PREVIEW_MAX_CHARS] + "..." 
                        : output;
                    ws.Cell(row.RowNumber(), consoleCol).Value = truncated;
                }
            }
            
            // Adjust column widths and wrap text
            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
        }
        
        /// <summary>
        /// [DEPRECATED] Populates STDOUT columns with captured output data for each stage in the worksheet.
        /// Use PopulateStudentConsoleColumn instead which uses the unified StudentConsole column name.
        /// </summary>
        [Obsolete("Use PopulateStudentConsoleColumn instead")]
        private void PopulateStdoutColumns(IXLWorksheet ws, Dictionary<string, int> hdr, bool isClientSheet, bool isServerSheet)
        {
            if (!hdr.TryGetValue(GradingKeywords.Col_Stage, out var stageCol)) return;
            
            var rng = ws.RangeUsed();
            if (rng == null) return;
            
            // Get STDOUT column indices - only get the relevant column for this sheet type
            int clientStdoutCol = 0, serverStdoutCol = 0, networkStdoutCol = 0;
            
            if (isClientSheet)
            {
                #pragma warning disable CS0612 // Type or member is obsolete
                hdr.TryGetValue(GradingKeywords.Col_ClientStdout, out clientStdoutCol);
                #pragma warning restore CS0612
            }
            else if (isServerSheet)
            {
                #pragma warning disable CS0612 // Type or member is obsolete
                hdr.TryGetValue(GradingKeywords.Col_ServerStdout, out serverStdoutCol);
                #pragma warning restore CS0612
            }
            else
                hdr.TryGetValue(GradingKeywords.Col_NetworkStdout, out networkStdoutCol);
            
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                if (!int.TryParse(row.Cell(stageCol).GetString(), out var stage)) continue;
                
                var stageStr = stage.ToString();
                var questionCode = _questionCode ?? "";
                
                // Populate ClientStdout column (only on Client sheet)
                if (isClientSheet && clientStdoutCol > 0)
                {
                    var clientKey = _run.GetClientCaptureKey(questionCode, stageStr);
                    if (_run.TryGetCapturedOutput(clientKey, out var clientOutput) && !string.IsNullOrEmpty(clientOutput))
                    {
                        var truncated = clientOutput.Length > PortKeywords.OUTPUT_PREVIEW_MAX_CHARS 
                            ? clientOutput[..PortKeywords.OUTPUT_PREVIEW_MAX_CHARS] + "..." 
                            : clientOutput;
                        ws.Cell(row.RowNumber(), clientStdoutCol).Value = truncated;
                    }
                }
                
                // Populate ServerStdout column (only on Server sheet)
                if (isServerSheet && serverStdoutCol > 0)
                {
                    var serverKey = _run.GetServerCaptureKey(questionCode, stageStr);
                    if (_run.TryGetCapturedOutput(serverKey, out var serverOutput) && !string.IsNullOrEmpty(serverOutput))
                    {
                        var truncated = serverOutput.Length > PortKeywords.OUTPUT_PREVIEW_MAX_CHARS 
                            ? serverOutput[..PortKeywords.OUTPUT_PREVIEW_MAX_CHARS] + "..." 
                            : serverOutput;
                        ws.Cell(row.RowNumber(), serverStdoutCol).Value = truncated;
                    }
                }
                
                // Populate NetworkStdout column (only on Network sheet) with full TCP flow data
                if (!isClientSheet && !isServerSheet && networkStdoutCol > 0)
                {
                    // Get the full captured network flow for this stage using the constant key pattern
                    var networkFlowKey = string.Format(PortKeywords.NETWORK_FLOW_KEY_PATTERN, stageStr);
                    if (_run.TryGetCapturedOutput(networkFlowKey, out var networkFlow) && !string.IsNullOrEmpty(networkFlow))
                    {
                        ws.Cell(row.RowNumber(), networkStdoutCol).Value = networkFlow;
                    }
                    else
                    {
                        // Fall back to individual request/response data if full flow is not available
                        var responseKey = _run.GetServerResponseCaptureKey(questionCode, stageStr);
                        var requestKey = _run.GetServerRequestCaptureKey(questionCode, stageStr);
                        
                        var networkData = new StringBuilder();
                        
                        if (_run.TryGetCapturedOutput(requestKey, out var requestData) && !string.IsNullOrEmpty(requestData))
                        {
                            var truncatedReq = requestData.Length > PortKeywords.NETWORK_PREVIEW_MAX_CHARS 
                                ? requestData[..PortKeywords.NETWORK_PREVIEW_MAX_CHARS] + "..." 
                                : requestData;
                            networkData.AppendLine("[REQUEST]");
                            networkData.AppendLine(truncatedReq);
                        }
                        
                        if (_run.TryGetCapturedOutput(responseKey, out var responseData) && !string.IsNullOrEmpty(responseData))
                        {
                            var truncatedRes = responseData.Length > PortKeywords.NETWORK_PREVIEW_MAX_CHARS 
                                ? responseData[..PortKeywords.NETWORK_PREVIEW_MAX_CHARS] + "..." 
                                : responseData;
                            if (networkData.Length > 0) networkData.AppendLine();
                            networkData.AppendLine("[RESPONSE]");
                            networkData.AppendLine(truncatedRes);
                        }
                        
                        if (networkData.Length > 0)
                        {
                            ws.Cell(row.RowNumber(), networkStdoutCol).Value = networkData.ToString().Trim();
                        }
                    }
                }
            }
            
            // Adjust column widths and wrap text
            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
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
                
                // Use shared method to get validation type constant (non-obsolete)
                var validationType = GetValidationTypeConstant(record.StepId);
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
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
        }
        
        /// <summary>
        /// Creates the GradeProcess sheet that logs the grading execution process.
        /// This provides visibility into where grading may have failed or been skipped.
        /// Columns: Stage, Action, GradeAction, Message
        /// Per requirements: "make another sheet called GradeProcess inside this GradeResult file, 
        /// which log details the execution process"
        /// </summary>
        private void CreateGradeProcessSheet()
        {
            if (_wb == null) return;
            
            // Remove existing GradeProcess sheet if it exists
            if (_wb.Worksheets.TryGetWorksheet(GradingKeywords.Sheet_GradeProcess, out var existingSheet))
            {
                existingSheet.Delete();
            }
            
            var ws = _wb.AddWorksheet(GradingKeywords.Sheet_GradeProcess);
            
            // Create header row: Stage, Action, GradeAction, Message
            ws.Cell(1, 1).Value = GradingKeywords.Col_Stage;
            ws.Cell(1, 2).Value = GradingKeywords.Col_UserAction;
            ws.Cell(1, 3).Value = GradingKeywords.Col_GradeAction;
            ws.Cell(1, 4).Value = GradingKeywords.Col_Message;
            
            ws.Row(1).Style.Font.Bold = true;
            ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;
            
            int row = 2;
            
            // Log all steps from _records with their grading actions
            foreach (var record in _records)
            {
                ws.Cell(row, 1).Value = record.Stage;
                ws.Cell(row, 2).Value = record.Action ?? "";
                
                // Determine grade action based on step outcome
                string gradeAction;
                if (record.Passed)
                {
                    gradeAction = "COMPARE_PASS";
                }
                else if (record.ErrorCode.Contains("SKIP"))
                {
                    gradeAction = "SKIPPED";
                }
                else if (record.ErrorCode.Contains("TIMEOUT"))
                {
                    gradeAction = "TIMEOUT";
                }
                else if (record.ErrorCode.Contains("CRASH"))
                {
                    gradeAction = "CRASHED";
                }
                else
                {
                    gradeAction = "COMPARE_FAIL";
                }
                
                ws.Cell(row, 3).Value = gradeAction;
                
                // Build message with context
                string message = record.Message ?? "";
                if (!record.Passed && string.IsNullOrEmpty(message))
                {
                    message = $"Grading failed for {record.StepId}";
                }
                else if (record.Passed && string.IsNullOrEmpty(message))
                {
                    message = "Comparison passed";
                }
                
                ws.Cell(row, 4).Value = message;
                
                // Color code based on outcome
                if (!record.Passed)
                {
                    ws.Cell(row, 3).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(row, 3).Style.Fill.BackgroundColor = XLColor.LightPink;
                }
                else
                {
                    ws.Cell(row, 3).Style.Font.FontColor = XLColor.DarkGreen;
                    ws.Cell(row, 3).Style.Fill.BackgroundColor = XLColor.LightGreen;
                }
                
                row++;
            }
            
            // Add summary at the bottom
            row++;
            ws.Cell(row, 1).Value = "Summary";
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            
            int passedCount = _records.Count(r => r.Passed);
            int failedCount = _records.Count(r => !r.Passed);
            
            ws.Cell(row, 1).Value = "Total Steps:";
            ws.Cell(row, 2).Value = _records.Count;
            row++;
            ws.Cell(row, 1).Value = "Passed:";
            ws.Cell(row, 2).Value = passedCount;
            ws.Cell(row, 2).Style.Font.FontColor = XLColor.DarkGreen;
            row++;
            ws.Cell(row, 1).Value = "Failed:";
            ws.Cell(row, 2).Value = failedCount;
            if (failedCount > 0)
            {
                ws.Cell(row, 2).Style.Font.FontColor = XLColor.Red;
            }
            row++;
            ws.Cell(row, 1).Value = "Overall:";
            ws.Cell(row, 2).Value = _allStepsPassed ? "PASS" : "FAIL";
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 2).Style.Fill.BackgroundColor = _allStepsPassed ? XLColor.LightGreen : XLColor.LightPink;
            
            ws.Style.Alignment.WrapText = true;
            ws.Columns().AdjustToContents(1, ws.LastRowUsed()?.RowNumber() ?? 1, PortKeywords.EXCEL_COLUMN_MIN_WIDTH, PortKeywords.EXCEL_COLUMN_MAX_WIDTH);
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
            
            // Try to get the appropriate sheet (support OLD and NEW formats)
            IXLWorksheet? ws = null;
            bool isClientSheet = false;
            bool isServerSheet = false;
            
            if (record.StepId.StartsWith(GradingKeywords.StepPrefix_OutputClient, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetWorksheetFlexible(SheetOutClientsAlt, SheetOutClients, SheetOutClientsNew, out ws))
                    return (null, null);
                isClientSheet = true;
            }
            else
            {
                if (!TryGetWorksheetFlexible(SheetOutServersAlt, SheetOutServers, SheetOutServersNew, out ws))
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
            
            // Get expected value from the cached template values (preserved before any modifications)
            // This ensures we get the original expected values even if the worksheet was modified during grading
            var validationType = GetValidationType(record.StepId);
            var sheetName = ws.Name;
            
            if (isClientSheet)
            {
                switch (validationType)
                {
                    case StepValidationType.DataResponse:
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_DataResponse);
                        break;
                    case StepValidationType.ConsoleOutput:
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_Output);
                        // Try NEW format Console column if Output not found
                        if (string.IsNullOrEmpty(expectedValue))
                            expectedValue = GetCachedExpectedValue(sheetName, stage, "Console");
                        break;
                    default:
                        // For other validation types (METHOD, STATUS, SIZE), try DataResponse first, then Output
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_DataResponse);
                        if (string.IsNullOrEmpty(expectedValue))
                        {
                            expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OC_Output);
                            if (string.IsNullOrEmpty(expectedValue))
                                expectedValue = GetCachedExpectedValue(sheetName, stage, "Console");
                        }
                        break;
                }
            }
            else if (isServerSheet)
            {
                switch (validationType)
                {
                    case StepValidationType.DataRequest:
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_DataRequest);
                        break;
                    case StepValidationType.ConsoleOutput:
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_Output);
                        // Try NEW format Console column if Output not found
                        if (string.IsNullOrEmpty(expectedValue))
                            expectedValue = GetCachedExpectedValue(sheetName, stage, "Console");
                        break;
                    default:
                        // For other validation types (METHOD, SIZE), try DataRequest first, then Output
                        expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_DataRequest);
                        if (string.IsNullOrEmpty(expectedValue))
                        {
                            expectedValue = GetCachedExpectedValue(sheetName, stage, SuiteKeywords.Col_OS_Output);
                            if (string.IsNullOrEmpty(expectedValue))
                                expectedValue = GetCachedExpectedValue(sheetName, stage, "Console");
                        }
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
        
        /// <summary>
        /// Retrieves a cached expected value from the template.
        /// </summary>
        /// <param name="sheetName">Name of the sheet</param>
        /// <param name="stage">Stage number</param>
        /// <param name="columnName">Column name (e.g., DataResponse, Output, DataRequest)</param>
        /// <returns>Cached expected value or null if not found</returns>
        private string? GetCachedExpectedValue(string sheetName, int stage, string columnName)
        {
            var cacheKey = $"{sheetName}_{stage}_{columnName}";
            return _expectedValuesCache.TryGetValue(cacheKey, out var value) ? value : null;
        }
        
        /// <summary>
        /// Gets a human-readable validation type label from a step ID for display purposes.
        /// This is used in error reports and summaries to describe what type of validation failed.
        /// </summary>
        /// <param name="stepId">The step ID (e.g., "NETWORK-FLOW-3-1", "CLIENT-CONSOLE-2")</param>
        /// <returns>Human-readable validation type label</returns>
        private static string GetValidationTypeLabel(string? stepId)
        {
            if (string.IsNullOrWhiteSpace(stepId))
                return "Unknown";
            
            // Check for specific validation types in the step ID
            // Order matters - more specific patterns should be checked first
            if (stepId.Contains("-FLOW-", StringComparison.OrdinalIgnoreCase))
                return "Network Flow (TCP)";
            if (stepId.Contains("-REQPAYLOAD-", StringComparison.OrdinalIgnoreCase))
                return "Request Payload";
            if (stepId.Contains("-RESPAYLOAD-", StringComparison.OrdinalIgnoreCase))
                return "Response Payload";
            if (stepId.Contains("-CONSOLE-", StringComparison.OrdinalIgnoreCase))
                return "Console Output";
            if (stepId.Contains("-METHOD-", StringComparison.OrdinalIgnoreCase))
                return "HTTP Method";
            if (stepId.Contains("-STATUS-", StringComparison.OrdinalIgnoreCase))
                return "Status Code";
            if (stepId.Contains("-SIZE-", StringComparison.OrdinalIgnoreCase))
                return "Byte Size";
            if (stepId.Contains("-DATA-", StringComparison.OrdinalIgnoreCase))
                return "Data";
            if (stepId.Contains("-OUT-", StringComparison.OrdinalIgnoreCase))
                return "Output";
            if (stepId.Contains("-REQ-", StringComparison.OrdinalIgnoreCase))
                return "Request";
            
            return "Unknown";
        }
        
        /// <summary>
        /// Gets the validation type constant from a step ID for use in grading reports.
        /// Uses the new format validation type constants (non-obsolete).
        /// </summary>
        /// <param name="stepId">The step ID</param>
        /// <returns>Validation type constant value</returns>
        private static string GetValidationTypeConstant(string? stepId)
        {
            if (string.IsNullOrWhiteSpace(stepId))
                return "OTHER";
            
            // Check for specific validation types in the step ID
            if (stepId.Contains("-FLOW-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_NetworkFlow;
            if (stepId.Contains("-REQPAYLOAD-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_ReqPayload;
            if (stepId.Contains("-RESPAYLOAD-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_ResPayload;
            if (stepId.Contains("-CONSOLE-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_Console;
            if (stepId.Contains("-METHOD-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_Method;
            if (stepId.Contains("-STATUS-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_Status;
            if (stepId.Contains("-DATA-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_Data;
            if (stepId.Contains("-OUT-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_Console; // OUT is also console output
            if (stepId.Contains("-REQ-", StringComparison.OrdinalIgnoreCase))
                return GradingKeywords.Validation_ReqPayload;
            
            return "OTHER";
        }
        
        /// <summary>
        /// Caches expected values from the Detail.xlsx template before any modifications.
        /// This ensures we preserve the original expected values even if the worksheet is modified during grading.
        /// </summary>
        /// <param name="ws">The worksheet to cache expected values from</param>
        private void CacheExpectedValues(IXLWorksheet ws)
        {
            if (ws == null) return;
            
            var hdr = GetHeaderIndex(ws);
            if (!hdr.TryGetValue(GradingKeywords.Col_Stage, out var stageCol)) return;
            
            var sheetName = ws.Name;
            var isClientSheet = string.Equals(sheetName, SheetOutClients, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(sheetName, SheetOutClientsAlt, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(sheetName, SheetOutClientsNew, StringComparison.OrdinalIgnoreCase);
            var isServerSheet = string.Equals(sheetName, SheetOutServers, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(sheetName, SheetOutServersAlt, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(sheetName, SheetOutServersNew, StringComparison.OrdinalIgnoreCase);
            
            // Only cache for output sheets (Client and Server)
            if (!isClientSheet && !isServerSheet) return;
            
            var rng = ws.RangeUsed();
            if (rng == null) return;
            
            // Determine which columns to cache based on sheet type
            var columnsToCacheClient = new[] 
            { 
                SuiteKeywords.Col_OC_DataResponse,
                SuiteKeywords.Col_OC_Output,
                "Console" // NEW format
            };
            
            var columnsToCacheServer = new[] 
            { 
                SuiteKeywords.Col_OS_DataRequest,
                SuiteKeywords.Col_OS_Output,
                "Console" // NEW format
            };
            
            var columnsToCache = isClientSheet ? columnsToCacheClient : columnsToCacheServer;
            
            // Iterate through all data rows (skip header)
            foreach (var row in rng.RowsUsed().Skip(1))
            {
                var rowNum = row.RowNumber();
                
                // Get stage number for this row
                if (!int.TryParse(ws.Cell(rowNum, stageCol).GetString(), out var stage))
                    continue;
                
                // Cache each expected value column
                foreach (var columnName in columnsToCache)
                {
                    if (hdr.TryGetValue(columnName, out var colIndex))
                    {
                        var value = ws.Cell(rowNum, colIndex).GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            var cacheKey = $"{sheetName}_{stage}_{columnName}";
                            _expectedValuesCache[cacheKey] = value;
                        }
                    }
                }
            }
        }

        public void Dispose() => _wb?.Dispose();
    }
}
