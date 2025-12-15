using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Domain.Entities.Docker.DockerSupporter.Entity;
using Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Handles writing grading results to Excel files in simplified SampleLogging format.
    /// Extracted from DockerGradingService to reduce file size and improve maintainability.
    /// 
    /// Simplified columns (per user requirement):
    /// - User sheet: Stage, Input, Action, Message
    /// - Client sheet: Stage, Console, StudentConsole, ExpectedExcerpt, ActualExcerpt, Message, Result
    /// - Server sheet: Stage, Console, StudentConsole, ExpectedExcerpt, ActualExcerpt, Message, Result
    /// - GradeProcess sheet: Stage, Action, GradeAction, Message (logs grading execution process)
    /// </summary>
    public class ExcelResultWriter
    {
        private readonly Action<string>? _progressCallback;

        public ExcelResultWriter(Action<string>? progressCallback = null)
        {
            _progressCallback = progressCallback;
        }

        /// <summary>
        /// Writes test case result to GradeDetail.xlsx in the EXACT SampleLogging format:
        /// - User sheet: Stage, Input, Action, DataType, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt
        /// - Client sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ClientStdout
        /// - Server sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ServerStdout
        /// - Network sheet: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// - Database sheet: (empty)
        /// </summary>
        public async Task WriteTestCaseResultAsync(string tcResultPath, string tcName, string testCasePath, TestCaseResult result, Func<string, List<ExpectedNetworkFlow>> readExpectedNetwork, Func<string, string> normalizeFlags)
        {
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();
            
            // === User Sheet ===
            var userWs = wb.Worksheets.Add("User");
            SetUserSheetHeaders(userWs);
            int userRow = 2;
            foreach (var action in result.Actions)
            {
                userWs.Cell(userRow, 1).Value = action.Stage;        // Stage
                userWs.Cell(userRow, 2).Value = action.Input ?? "";  // Input
                userWs.Cell(userRow, 3).Value = action.ActionType ?? "";  // Action
                // Message (col 4) left empty for actions
                userRow++;
            }
            userWs.Columns().AdjustToContents();
            
            // === Client Sheet ===
            // Columns: Stage, Console, StudentConsole, ExpectedExcerpt, ActualExcerpt, Message, Result
            var clientWs = wb.Worksheets.Add("Client");
            SetClientSheetHeaders(clientWs);
            int clientRow = 2;
            foreach (var comp in result.ClientComparisons)
            {
                clientWs.Cell(clientRow, 1).Value = comp.Stage;           // Stage
                clientWs.Cell(clientRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                clientWs.Cell(clientRow, 3).Value = comp.Actual ?? "";    // StudentConsole (actual)
                // ExpectedExcerpt (4) and ActualExcerpt (5) - extract mismatch excerpts if failed
                if (!comp.Passed && !string.IsNullOrEmpty(comp.Expected) && !string.IsNullOrEmpty(comp.Actual))
                {
                    var expExcerpt = ExtractMismatchExcerpt(comp.Expected, comp.Actual, true);
                    var actExcerpt = ExtractMismatchExcerpt(comp.Expected, comp.Actual, false);
                    clientWs.Cell(clientRow, 4).Value = expExcerpt;  // ExpectedExcerpt
                    clientWs.Cell(clientRow, 5).Value = actExcerpt;  // ActualExcerpt
                }
                clientWs.Cell(clientRow, 6).Value = comp.Passed 
                    ? "Text comparison passed: client output matches"
                    : "Text comparison failed: client output mismatch";  // Message
                clientWs.Cell(clientRow, 7).Value = comp.Passed ? "PASS" : "FAIL";  // Result (at end)
                clientRow++;
            }
            clientWs.Columns().AdjustToContents();
            
            // === Server Sheet ===
            // Columns: Stage, Console, StudentConsole, ExpectedExcerpt, ActualExcerpt, Message, Result
            var serverWs = wb.Worksheets.Add("Server");
            SetServerSheetHeaders(serverWs);
            int serverRow = 2;
            foreach (var comp in result.ServerComparisons)
            {
                serverWs.Cell(serverRow, 1).Value = comp.Stage;           // Stage
                serverWs.Cell(serverRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                serverWs.Cell(serverRow, 3).Value = comp.Actual ?? "";    // StudentConsole (actual)
                // ExpectedExcerpt (4) and ActualExcerpt (5) - extract mismatch excerpts if failed
                if (!comp.Passed && !string.IsNullOrEmpty(comp.Expected) && !string.IsNullOrEmpty(comp.Actual))
                {
                    var expExcerpt = ExtractMismatchExcerpt(comp.Expected, comp.Actual, true);
                    var actExcerpt = ExtractMismatchExcerpt(comp.Expected, comp.Actual, false);
                    serverWs.Cell(serverRow, 4).Value = expExcerpt;  // ExpectedExcerpt
                    serverWs.Cell(serverRow, 5).Value = actExcerpt;  // ActualExcerpt
                }
                serverWs.Cell(serverRow, 6).Value = comp.Passed 
                    ? "Text comparison passed: server output matches"
                    : "Text comparison failed: server output mismatch";  // Message
                serverWs.Cell(serverRow, 7).Value = comp.Passed ? "PASS" : "FAIL";  // Result (at end)
                serverRow++;
            }
            serverWs.Columns().AdjustToContents();
            
            // === Database Sheet ===
            wb.Worksheets.Add("Database");
            
            // === Network Sheet ===
            var netWs = wb.Worksheets.Add("Network");
            SetNetworkSheetHeaders(netWs);
            int netRow = 2;
            
            var detailPath_forNetwork = Path.Combine(testCasePath, "Detail.xlsx");
            var expectedNetworkFlows = readExpectedNetwork(detailPath_forNetwork);
            
            var capturesByStage = result.NetworkCaptures
                .GroupBy(p => p.Stage)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).ToList());
            
            // CRITICAL FIX: Group expected flows by stage for POSITIONAL matching
            // This matches the CompareNetwork algorithm which uses position within stage
            var expectedByStage = expectedNetworkFlows
                .GroupBy(e => e.Stage)
                .ToDictionary(g => g.Key, g => g.ToList());
            
            // === SECTION 1: EXPECTED Network Flows ===
            // CRITICAL: Use POSITIONAL matching to align with CompareNetwork algorithm
            if (expectedNetworkFlows.Count > 0)
            {
                OnProgress($"[Network Sheet] Writing {expectedNetworkFlows.Count} expected network flows...");
                
                foreach (var expectedFlow in expectedNetworkFlows.OrderBy(f => f.Stage))
                {
                    netWs.Cell(netRow, 1).Value = expectedFlow.Stage;
                    netWs.Cell(netRow, 2).Value = "";
                    netWs.Cell(netRow, 3).Value = "TCP";
                    netWs.Cell(netRow, 4).Value = "";
                    netWs.Cell(netRow, 5).Value = "";
                    netWs.Cell(netRow, 6).Value = expectedFlow.Flags ?? "";
                    netWs.Cell(netRow, 7).Value = expectedFlow.State ?? "";
                    netWs.Cell(netRow, 8).Value = expectedFlow.Data ?? "";
                    netWs.Cell(netRow, 9).Value = expectedFlow.SourceRole ?? "";
                    netWs.Cell(netRow, 10).Value = expectedFlow.DestinationRole ?? "";
                    
                    // CRITICAL FIX: Use POSITIONAL matching - same algorithm as CompareNetwork
                    // The expected flow at position N within its stage should match captured packet at position N
                    
                    // Get expected flows for this stage to determine position
                    var expectedFlowsForStage = expectedByStage.TryGetValue(expectedFlow.Stage, out var expFlows) 
                        ? expFlows 
                        : new List<ExpectedNetworkFlow>();
                    
                    // Get captured packets for this stage
                    var actualPacketsForStage = capturesByStage.TryGetValue(expectedFlow.Stage, out var packets) 
                        ? packets 
                        : new List<CapturedNetworkPacket>();
                    
                    // Find position of this expected flow within its stage
                    int positionInStage = expectedFlowsForStage.IndexOf(expectedFlow);
                    
                    // POSITIONAL MATCHING: Get the packet at the same position
                    CapturedNetworkPacket? matchingPacket = null;
                    if (positionInStage >= 0 && positionInStage < actualPacketsForStage.Count)
                    {
                        matchingPacket = actualPacketsForStage[positionInStage];
                    }
                    
                    if (matchingPacket != null)
                    {
                        netWs.Cell(netRow, 11).Value = matchingPacket.Flags;
                        netWs.Cell(netRow, 12).Value = matchingPacket.State;
                        netWs.Cell(netRow, 13).Value = matchingPacket.SourceRole;
                        netWs.Cell(netRow, 14).Value = matchingPacket.DestinationRole;
                        netWs.Cell(netRow, 15).Value = matchingPacket.Data ?? "";
                        netWs.Cell(netRow, 16).Value = matchingPacket.SourcePort;
                        netWs.Cell(netRow, 17).Value = matchingPacket.DestinationPort;
                        
                        bool exactMatch = true;
                        if (!string.IsNullOrEmpty(expectedFlow.Flags) && normalizeFlags(expectedFlow.Flags) != normalizeFlags(matchingPacket.Flags))
                            exactMatch = false;
                        if (!string.IsNullOrEmpty(expectedFlow.SourceRole) && matchingPacket.SourceRole != expectedFlow.SourceRole)
                            exactMatch = false;
                        if (!string.IsNullOrEmpty(expectedFlow.DestinationRole) && matchingPacket.DestinationRole != expectedFlow.DestinationRole)
                            exactMatch = false;
                        // STRICT DATA COMPARISON: Data must match exactly (case-sensitive)
                        // Per user requirement: "data comparison is strict. if they do not match 100% including case -> FAIL"
                        if (!string.IsNullOrEmpty(expectedFlow.Data) && 
                            !expectedFlow.Data.Equals(NetworkKeywords.Data_None, StringComparison.OrdinalIgnoreCase))
                        {
                            var actualData = matchingPacket.Data ?? "";
                            if (!actualData.Trim().Equals(expectedFlow.Data.Trim(), StringComparison.Ordinal))
                                exactMatch = false;
                        }
                        
                        // STRICT GRADING: No PARTIAL status - only PASS or FAIL
                        // If any field doesn't match exactly, mark as FAIL
                        netWs.Cell(netRow, 18).Value = exactMatch ? "PASS" : "FAIL";
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = exactMatch ? XLColor.LightGreen : XLColor.LightPink;
                    }
                    else
                    {
                        netWs.Cell(netRow, 11).Value = "(MISSING - not captured)";
                        netWs.Cell(netRow, 12).Value = "";
                        netWs.Cell(netRow, 13).Value = "";
                        netWs.Cell(netRow, 14).Value = "";
                        netWs.Cell(netRow, 15).Value = "";
                        netWs.Cell(netRow, 16).Value = "";
                        netWs.Cell(netRow, 17).Value = "";
                        netWs.Cell(netRow, 18).Value = "FAIL";
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = XLColor.LightPink;
                        
                        OnProgress($"[Network Sheet] Expected flow MISSING at stage {expectedFlow.Stage} position {positionInStage}: Flags={expectedFlow.Flags}, SourceRole={expectedFlow.SourceRole}, DestRole={expectedFlow.DestinationRole}");
                    }
                    
                    netRow++;
                }
            }
            
            // === SECTION 2: Additional Captured Packets ===
            // With POSITIONAL matching, "additional" packets are those beyond the expected count
            foreach (var stage in capturesByStage.Keys.OrderBy(k => k))
            {
                var allPacketsForStage = capturesByStage[stage];
                var expectedCountForStage = expectedByStage.TryGetValue(stage, out var expList) 
                    ? expList.Count 
                    : 0;
                
                // Get packets beyond the expected count (these are "additional" not validated)
                if (allPacketsForStage.Count > expectedCountForStage)
                {
                    var additionalPackets = allPacketsForStage.Skip(expectedCountForStage).ToList();
                    OnProgress($"[Network Sheet] Found {additionalPackets.Count} additional (not validated) packets at stage {stage}");
                    
                    foreach (var packet in additionalPackets)
                    {
                        netWs.Cell(netRow, 1).Value = packet.Stage;
                        netWs.Cell(netRow, 2).Value = "(Not validated by this test case)";
                        for (int i = 3; i <= 10; i++) 
                            netWs.Cell(netRow, i).Value = "";
                        
                        netWs.Cell(netRow, 11).Value = packet.Flags;
                        netWs.Cell(netRow, 12).Value = packet.State;
                        netWs.Cell(netRow, 13).Value = packet.SourceRole;
                        netWs.Cell(netRow, 14).Value = packet.DestinationRole;
                        netWs.Cell(netRow, 15).Value = packet.Data ?? "";
                        netWs.Cell(netRow, 16).Value = packet.SourcePort;
                        netWs.Cell(netRow, 17).Value = packet.DestinationPort;
                        
                        netWs.Cell(netRow, 18).Value = "INFO";
                        netWs.Cell(netRow, 18).Style.Fill.BackgroundColor = XLColor.LightGray;
                        
                        netRow++;
                    }
                }
            }
            
            // === SECTION 3: No network data case ===
            if (expectedNetworkFlows.Count == 0 && result.NetworkCaptures.Count == 0)
            {
                netWs.Cell(netRow, 1).Value = "N/A";
                netWs.Cell(netRow, 2).Value = "No network flows expected or captured for this test case";
            }
            
            netWs.Columns().AdjustToContents();

            // === GradeProcess Sheet ===
            // Logs the grading execution process with columns: Stage, Action, GradeAction, Message
            var processWs = wb.Worksheets.Add("GradeProcess");
            SetGradeProcessSheetHeaders(processWs);
            int processRow = 2;

            // Log actions from the test case
            foreach (var action in result.Actions)
            {
                processWs.Cell(processRow, 1).Value = action.Stage;
                processWs.Cell(processRow, 2).Value = action.ActionType ?? "";
                processWs.Cell(processRow, 3).Value = "EXECUTE";
                processWs.Cell(processRow, 4).Value = $"Executed: {action.ActionType ?? ""} {action.Input ?? ""}".Trim();
                processRow++;
            }

            // Log client comparisons
            foreach (var comp in result.ClientComparisons)
            {
                processWs.Cell(processRow, 1).Value = comp.Stage;
                processWs.Cell(processRow, 2).Value = "CompareClientConsole";
                processWs.Cell(processRow, 3).Value = comp.Passed ? "PASS" : "FAIL";
                processWs.Cell(processRow, 4).Value = comp.Passed ? "Client console output matches" : "Client console output mismatch";
                processRow++;
            }

            // Log server comparisons
            foreach (var comp in result.ServerComparisons)
            {
                processWs.Cell(processRow, 1).Value = comp.Stage;
                processWs.Cell(processRow, 2).Value = "CompareServerConsole";
                processWs.Cell(processRow, 3).Value = comp.Passed ? "PASS" : "FAIL";
                processWs.Cell(processRow, 4).Value = comp.Passed ? "Server console output matches" : "Server console output mismatch";
                processRow++;
            }

            // Log network summary
            if (expectedNetworkFlows.Count > 0)
            {
                int networkPassed = 0;
                foreach (var expectedFlow in expectedNetworkFlows)
                {
                    var expFlowsForStage = expectedByStage.TryGetValue(expectedFlow.Stage, out var expFlows) ? expFlows : new List<ExpectedNetworkFlow>();
                    var actualPacketsForStage = capturesByStage.TryGetValue(expectedFlow.Stage, out var packets) ? packets : new List<CapturedNetworkPacket>();
                    int positionInStage = expFlowsForStage.IndexOf(expectedFlow);
                    if (positionInStage >= 0 && positionInStage < actualPacketsForStage.Count)
                        networkPassed++;
                }
                processWs.Cell(processRow, 1).Value = "All";
                processWs.Cell(processRow, 2).Value = "CompareNetwork";
                processWs.Cell(processRow, 3).Value = networkPassed == expectedNetworkFlows.Count ? "PASS" : "FAIL";
                processWs.Cell(processRow, 4).Value = $"Network: {networkPassed}/{expectedNetworkFlows.Count} flows matched";
                processRow++;
            }

            // Log error if any
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                processWs.Cell(processRow, 1).Value = "N/A";
                processWs.Cell(processRow, 2).Value = "TestCaseError";
                processWs.Cell(processRow, 3).Value = "ERROR";
                processWs.Cell(processRow, 4).Value = result.ErrorMessage;
                processRow++;
            }

            // Summary
            processWs.Cell(processRow, 1).Value = "Summary";
            processWs.Cell(processRow, 2).Value = "";
            processWs.Cell(processRow, 3).Value = result.Passed ? "PASS" : "FAIL";
            var passFailText = result.Passed ? "PASSED" : "FAILED";
            processWs.Cell(processRow, 4).Value = $"Test case {passFailText}: {result.EarnedMark:F2}/{result.MaxMark:F2} points";

            processWs.Columns().AdjustToContents();

            wb.SaveAs(detailPath);
            
            await Task.CompletedTask;
        }

        public async Task WriteOverallSummaryAsync(string studentResultPath, List<TestCaseResult> results)
        {
            var summaryPath = Path.Combine(studentResultPath, "OverallSummary.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Summary");
            
            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "EarnedMark";
            ws.Cell(1, 4).Value = "MaxMark";
            ws.Cell(1, 5).Value = "Error";
            ws.Row(1).Style.Font.Bold = true;
            
            int row = 2;
            foreach (var result in results)
            {
                ws.Cell(row, 1).Value = result.TestCaseName;
                ws.Cell(row, 2).Value = result.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 3).Value = result.EarnedMark;
                ws.Cell(row, 4).Value = result.MaxMark;
                ws.Cell(row, 5).Value = result.ErrorMessage ?? "";
                row++;
            }
            
            ws.Columns().AdjustToContents();
            wb.SaveAs(summaryPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Set User sheet headers - simplified to only include test kit action data.
        /// </summary>
        private static void SetUserSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Input", "Action", "Message" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        /// <summary>
        /// Set Client sheet headers - simplified per user requirement.
        /// Columns: Stage, Console (expected), StudentConsole (actual), ExpectedExcerpt, ActualExcerpt, Message, Result
        /// </summary>
        private static void SetClientSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "StudentConsole", "ExpectedExcerpt", "ActualExcerpt", "Message", "Result" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        /// <summary>
        /// Set Server sheet headers - simplified per user requirement.
        /// Columns: Stage, Console (expected), StudentConsole (actual), ExpectedExcerpt, ActualExcerpt, Message, Result
        /// </summary>
        private static void SetServerSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "StudentConsole", "ExpectedExcerpt", "ActualExcerpt", "Message", "Result" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }
        
        private static void SetNetworkSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { 
                "Stage", "Time", "Info", "Source", "Destination", 
                "Flags", "State", "Data", "SourceRole", "DestinationRole",
                "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData",
                "ActualSourcePort", "ActualDestPort",
                "NetworkResult"
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }

        /// <summary>
        /// Set GradeProcess sheet headers.
        /// Columns: Stage, Action (from User sheet), GradeAction (grading process), Message
        /// </summary>
        private static void SetGradeProcessSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Action", "GradeAction", "Message" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }

        /// <summary>
        /// Extracts an excerpt around the first mismatch point between expected and actual text.
        /// </summary>
        private static string ExtractMismatchExcerpt(string expected, string actual, bool getExpected, int contextChars = 30)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
                return getExpected ? (expected ?? "") : (actual ?? "");

            var normExp = NormalizeConsoleOutput(expected);
            var normAct = NormalizeConsoleOutput(actual);

            int diffIdx = 0;
            var minLen = Math.Min(normExp.Length, normAct.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (normExp[i] != normAct[i])
                {
                    diffIdx = i;
                    break;
                }
                diffIdx = i + 1;
            }

            if (diffIdx == minLen && normExp.Length != normAct.Length)
                diffIdx = minLen;

            var source = getExpected ? expected : actual;
            var start = Math.Max(0, diffIdx - contextChars);
            var end = Math.Min(source.Length, diffIdx + contextChars);
            var excerpt = source.Substring(start, end - start);

            if (start > 0) excerpt = "..." + excerpt;
            if (end < source.Length) excerpt = excerpt + "...";

            return excerpt;
        }

        /// <summary>
        /// Normalizes console output for comparison, handling newline differences.
        /// </summary>
        private static string NormalizeConsoleOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";

            output = output.Replace("\r\n", "\n").Replace("\r", "\n");

            var lines = output.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return string.Join(" ", lines);
        }

        private void OnProgress(string message)
        {
            _progressCallback?.Invoke(message);
        }
    }
}
