// This file contains the Result Writing region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Result Writing

        /// <summary>
        /// Writes test case result to GradeDetail.xlsx in the EXACT SampleLogging format:
        /// - User sheet: Stage, Input, Action, DataType, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt
        /// - Client sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ClientStdout
        /// - Server sheet: Stage, Console, Input, DataType, Action, Result, ErrorCode, ErrorCategory, PointsAwarded, PointsPossible, DurationMs, DetailPath, Message, DiffIndex, ExpectedOutput, ActualOutput, ExpectedExcerpt, ActualExcerpt, ServerStdout
        /// - Network sheet: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData, NetworkResult
        /// - Database sheet: (empty)
        /// </summary>
        private async Task WriteTestCaseResultAsync(string tcResultPath, string tcName, string testCasePath, TestCaseResult result)
        {
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();

            // === User Sheet ===
            // Contains the action steps (StartClient, StartServer, Input, etc.)
            var userWs = wb.Worksheets.Add("User");
            SetUserSheetHeaders(userWs);
            int userRow = 2;
            foreach (var action in result.Actions)
            {
                userWs.Cell(userRow, 1).Value = action.Stage;
                userWs.Cell(userRow, 2).Value = action.Input ?? "";
                userWs.Cell(userRow, 3).Value = action.ActionType ?? "";
                // DataType, Result, etc. are optional for action rows
                userRow++;
            }
            userWs.Columns().AdjustToContents();

            // === Client Sheet ===
            // Contains client console output comparisons
            var clientWs = wb.Worksheets.Add("Client");
            SetClientSheetHeaders(clientWs);
            int clientRow = 2;
            foreach (var comp in result.ClientComparisons)
            {
                clientWs.Cell(clientRow, 1).Value = comp.Stage;  // Stage
                clientWs.Cell(clientRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                // Skip Input, DataType, Action
                clientWs.Cell(clientRow, 6).Value = comp.Passed ? "PASS" : "FAIL";  // Result
                clientWs.Cell(clientRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";  // ErrorCode
                clientWs.Cell(clientRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";  // ErrorCategory
                clientWs.Cell(clientRow, 9).Value = comp.PointsAwarded;  // PointsAwarded
                clientWs.Cell(clientRow, 10).Value = comp.PointsPossible;  // PointsPossible
                clientWs.Cell(clientRow, 11).Value = comp.DurationMs;  // DurationMs
                clientWs.Cell(clientRow, 13).Value = comp.Passed ? "Text comparison passed: client output matches exactly" : "Text comparison failed: client output mismatch";  // Message
                clientWs.Cell(clientRow, 19).Value = comp.Actual ?? "";  // ClientStdout
                clientRow++;
            }
            clientWs.Columns().AdjustToContents();

            // === Server Sheet ===
            // Contains server console output comparisons
            var serverWs = wb.Worksheets.Add("Server");
            SetServerSheetHeaders(serverWs);
            int serverRow = 2;
            foreach (var comp in result.ServerComparisons)
            {
                serverWs.Cell(serverRow, 1).Value = comp.Stage;  // Stage
                serverWs.Cell(serverRow, 2).Value = comp.Expected ?? "";  // Console (expected)
                // Skip Input, DataType, Action
                serverWs.Cell(serverRow, 6).Value = comp.Passed ? "PASS" : "FAIL";  // Result
                serverWs.Cell(serverRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";  // ErrorCode
                serverWs.Cell(serverRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";  // ErrorCategory
                serverWs.Cell(serverRow, 9).Value = comp.PointsAwarded;  // PointsAwarded
                serverWs.Cell(serverRow, 10).Value = comp.PointsPossible;  // PointsPossible
                serverWs.Cell(serverRow, 11).Value = comp.DurationMs;  // DurationMs
                serverWs.Cell(serverRow, 13).Value = comp.Passed ? "Text comparison passed: server output matches exactly" : "Text comparison failed: server output mismatch";  // Message
                serverWs.Cell(serverRow, 19).Value = comp.Actual ?? "";  // ServerStdout
                serverRow++;
            }
            serverWs.Columns().AdjustToContents();

            // === Database Sheet ===
            // Empty placeholder for database operations
            wb.Worksheets.Add("Database");

            // === Network Sheet ===
            // IMPROVED FORMAT: Show ALL expected network flows and ALL actual captured packets
            // This provides a comprehensive comparison that makes it easy to identify:
            // - Missing packets (expected but not captured)
            // - Extra packets (captured but not expected)
            // - Mismatched packets (flags, roles differ from expected)
            //
            // The format shows expected flows on the left (columns 1-10) and actual captures 
            // on the right (columns 11-15), with a Match column (16) showing comparison result.
            // This allows reviewers to quickly scan for red (FAIL/MISSING) rows.
            var netWs = wb.Worksheets.Add("Network");
            SetNetworkSheetHeaders(netWs);
            int netRow = 2;

            // Read expected network flows from testkit Detail.xlsx to get COMPLETE data
            // This ensures we show ALL expected flows, not just the ones used in comparison
            var detailPath_forNetwork = Path.Combine(testCasePath, "Detail.xlsx");
            var expectedNetworkFlows = ReadExpectedNetwork(detailPath_forNetwork);

            // CRITICAL FIX: Apply the same 3-way to 4-way TCP close normalization as CompareNetwork
            // This ensures the Excel writer uses the same normalized packets that the grading logic used.
            // Without this, the Excel shows FAIL for tests that actually PASSED during grading because:
            // - Grading uses Normalize3WayTo4WayClose to inject synthetic ACK packets
            // - Excel was showing raw packets without the synthetic ACKs, causing positional mismatch
            var normalizedCaptures = Normalize3WayTo4WayClose(result.NetworkCaptures.ToList());
            
            // Group actual captures by stage for easier lookup
            var capturesByStage = normalizedCaptures
                .GroupBy(p => p.Stage)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).ToList());

            // CRITICAL FIX: Group expected flows by stage for POSITIONAL matching
            // This matches the CompareNetwork algorithm which uses position within stage
            // Expected flow at position N in stage S should match captured packet at position N in stage S
            var expectedByStage = expectedNetworkFlows
                .GroupBy(e => e.Stage)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Determine protocol type once for the entire Network sheet
            var protocol = _currentTestKitProtocol ?? "TCP";
            bool isHttpProtocol = protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase);

            // === SECTION 1: EXPECTED Network Flows (from TestKit) ===
            // Show ALL expected flows with their matching actual captures
            // CRITICAL: Use POSITIONAL matching to align with CompareNetwork algorithm
            if (expectedNetworkFlows.Count > 0)
            {
                OnProgress($"[Network Sheet] Writing {expectedNetworkFlows.Count} expected network flows...");

                foreach (var expectedFlow in expectedNetworkFlows.OrderBy(f => f.Stage))
                {
                    int col = 1;

                    // Common columns for both TCP and HTTP
                    netWs.Cell(netRow, col++).Value = expectedFlow.Stage;  // Stage
                    netWs.Cell(netRow, col++).Value = "";  // Time (from testkit - not always available)
                    netWs.Cell(netRow, col++).Value = protocol;  // Info (TCP or HTTP)
                    netWs.Cell(netRow, col++).Value = "";  // Source (IP from testkit if available)
                    netWs.Cell(netRow, col++).Value = "";  // Destination (IP from testkit if available)
                    netWs.Cell(netRow, col++).Value = expectedFlow.Flags ?? "";  // Flags
                    netWs.Cell(netRow, col++).Value = expectedFlow.State ?? "";  // State

                    if (isHttpProtocol)
                    {
                        // HTTP-specific expected columns
                        netWs.Cell(netRow, col++).Value = expectedFlow.URI ?? "";  // URI
                        netWs.Cell(netRow, col++).Value = expectedFlow.Method ?? "";  // Method
                        netWs.Cell(netRow, col++).Value = expectedFlow.Status ?? "";  // Status
                        netWs.Cell(netRow, col++).Value = expectedFlow.HttpVersion ?? "";  // HttpVersion
                        netWs.Cell(netRow, col++).Value = expectedFlow.HttpBody ?? "";  // HttpBody
                        netWs.Cell(netRow, col++).Value = expectedFlow.SourceRole ?? "";  // SourceRole
                        netWs.Cell(netRow, col++).Value = expectedFlow.DestinationRole ?? "";  // DestinationRole
                    }
                    else
                    {
                        // TCP-specific expected columns
                        netWs.Cell(netRow, col++).Value = expectedFlow.Data ?? "";  // Data
                        netWs.Cell(netRow, col++).Value = expectedFlow.SourceRole ?? "";  // SourceRole
                        netWs.Cell(netRow, col++).Value = expectedFlow.DestinationRole ?? "";  // DestinationRole
                    }

                    // CRITICAL FIX: Use POSITIONAL matching - same algorithm as CompareNetwork
                    // The expected flow at position N within its stage should match captured packet at position N
                    // This ensures:
                    // - ACK at position 2 matches captured packet 2 (not just "any ACK")
                    // - FIN-ACK at position 7 matches captured packet 7 (not first FIN-ACK found)
                    // - Proper 4-way handshake validation works correctly

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
                        // Found matching packet - write actual data
                        // Column index continues from where expected columns ended
                        netWs.Cell(netRow, col++).Value = matchingPacket.Flags;  // ActualFlags
                        netWs.Cell(netRow, col++).Value = matchingPacket.State;  // ActualState

                        if (isHttpProtocol)
                        {
                            // HTTP-specific actual columns
                            netWs.Cell(netRow, col++).Value = matchingPacket.URI ?? "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = matchingPacket.Method ?? "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = matchingPacket.Status ?? "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = matchingPacket.HttpVersion ?? "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = matchingPacket.HttpBody ?? "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = matchingPacket.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.DestinationRole;  // ActualDestRole
                        }
                        else
                        {
                            // TCP-specific actual columns
                            netWs.Cell(netRow, col++).Value = matchingPacket.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.DestinationRole;  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = matchingPacket.Data ?? "";  // ActualData
                        }

                        // Port columns (common for both protocols)
                        netWs.Cell(netRow, col++).Value = matchingPacket.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = matchingPacket.DestinationPort;  // ActualDestPort

                        // Check if it's an exact match or just partial
                        // Flags must match exactly (but order doesn't matter - normalize both)
                        bool exactMatch = true;

                        // Compare flags - exact match required (but order-normalized)
                        if (!string.IsNullOrEmpty(expectedFlow.Flags) && NormalizeFlags(expectedFlow.Flags) != NormalizeFlags(matchingPacket.Flags))
                            exactMatch = false;

                        // Compare roles exactly
                        if (!string.IsNullOrEmpty(expectedFlow.SourceRole) && matchingPacket.SourceRole != expectedFlow.SourceRole)
                            exactMatch = false;
                        if (!string.IsNullOrEmpty(expectedFlow.DestinationRole) && matchingPacket.DestinationRole != expectedFlow.DestinationRole)
                            exactMatch = false;

                        // Compare protocol-specific fields
                        if (isHttpProtocol)
                        {
                            if (!string.IsNullOrEmpty(expectedFlow.URI) && matchingPacket.URI != expectedFlow.URI)
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.Method) && matchingPacket.Method != expectedFlow.Method)
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.Status) && !(matchingPacket.Status ?? "").StartsWith(expectedFlow.Status, StringComparison.OrdinalIgnoreCase))
                                exactMatch = false;
                            if (!string.IsNullOrEmpty(expectedFlow.HttpBody) && matchingPacket.HttpBody != expectedFlow.HttpBody)
                                exactMatch = false;
                        }
                        else
                        {
                            // TCP: Compare Data field STRICTLY (case-sensitive, trimmed)
                            // Per user requirement: "data comparison is strict. if they do not match 100% including case -> FAIL"
                            if (!string.IsNullOrEmpty(expectedFlow.Data) &&
                                !expectedFlow.Data.Equals(NetworkKeywords.Data_None, StringComparison.OrdinalIgnoreCase))
                            {
                                var actualData = matchingPacket.Data ?? "";
                                if (!actualData.Trim().Equals(expectedFlow.Data.Trim(), StringComparison.Ordinal))
                                    exactMatch = false;
                            }
                        }

                        // STRICT GRADING: No PARTIAL status - only PASS or FAIL
                        // If any field doesn't match exactly, mark as FAIL
                        netWs.Cell(netRow, col).Value = exactMatch ? "PASS" : "FAIL";
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = exactMatch ? XLColor.LightGreen : XLColor.LightPink;

                        // NOTE: With positional matching, we don't remove packets from list
                        // The "Additional Captured Packets" section now shows packets beyond expected count
                    }
                    else
                    {
                        // No matching packet found - expected flow is MISSING
                        // Fill in empty actual columns
                        netWs.Cell(netRow, col++).Value = "(missing)";  // ActualFlags
                        netWs.Cell(netRow, col++).Value = "";  // ActualState

                        if (isHttpProtocol)
                        {
                            // HTTP: Empty actual columns
                            netWs.Cell(netRow, col++).Value = "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = "";  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualDestRole
                        }
                        else
                        {
                            // TCP: Empty actual columns
                            netWs.Cell(netRow, col++).Value = "";  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = "";  // ActualData
                        }

                        netWs.Cell(netRow, col++).Value = "";  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = "";  // ActualDestPort
                        netWs.Cell(netRow, col).Value = "FAIL";
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = XLColor.LightPink;

                        OnProgress($"[Network Sheet] Expected flow MISSING at stage {expectedFlow.Stage}: Flags={expectedFlow.Flags}, SourceRole={expectedFlow.SourceRole}, DestRole={expectedFlow.DestinationRole}");
                    }

                    netRow++;
                }
            }

            // === SECTION 2: Additional Captured Packets (not validated by this test case) ===
            // With POSITIONAL matching, "additional" packets are those beyond the expected count.
            // For example, if stage 3 expects 11 packets but captured 13, packets 12 and 13 are shown here.
            // This is NORMAL - test cases intentionally validate only specific aspects:
            //   - TC1 may only validate sending
            //   - TC2 may validate send + server confirm
            //   - TC3 may validate all communication + console output
            //   - TC4 may validate disconnect behavior
            // Extra packets are shown for information but DO NOT cause test failure.
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
                    OnProgress($"[Network Sheet] Found {additionalPackets.Count} additional (not validated) packets at stage {stage} (expected {expectedCountForStage}, captured {allPacketsForStage.Count})");

                    foreach (var packet in additionalPackets)
                    {
                        int col = 1;

                        // Common columns
                        netWs.Cell(netRow, col++).Value = packet.Stage;  // Stage
                        netWs.Cell(netRow, col++).Value = "(Not validated by this test case)";  // Time

                        // Leave expected columns empty (Info, Source, Destination, Flags, State, protocol fields, roles)
                        int expectedColumnCount = isHttpProtocol ? 12 : 8;  // HTTP has more columns
                        for (int i = 0; i < expectedColumnCount; i++)
                            netWs.Cell(netRow, col++).Value = "";

                        // Write actual packet data
                        netWs.Cell(netRow, col++).Value = packet.Flags;  // ActualFlags
                        netWs.Cell(netRow, col++).Value = packet.State;  // ActualState

                        if (isHttpProtocol)
                        {
                            // HTTP-specific actual columns
                            netWs.Cell(netRow, col++).Value = packet.URI ?? "";  // ActualURI
                            netWs.Cell(netRow, col++).Value = packet.Method ?? "";  // ActualMethod
                            netWs.Cell(netRow, col++).Value = packet.Status ?? "";  // ActualStatus
                            netWs.Cell(netRow, col++).Value = packet.HttpVersion ?? "";  // ActualHttpVersion
                            netWs.Cell(netRow, col++).Value = packet.HttpBody ?? "";  // ActualHttpBody
                            netWs.Cell(netRow, col++).Value = packet.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = packet.DestinationRole;  // ActualDestRole
                        }
                        else
                        {
                            // TCP-specific actual columns
                            netWs.Cell(netRow, col++).Value = packet.SourceRole;  // ActualSourceRole
                            netWs.Cell(netRow, col++).Value = packet.DestinationRole;  // ActualDestRole
                            netWs.Cell(netRow, col++).Value = packet.Data ?? "";  // ActualData
                        }

                        // Port columns (common)
                        netWs.Cell(netRow, col++).Value = packet.SourcePort;  // ActualSourcePort
                        netWs.Cell(netRow, col++).Value = packet.DestinationPort;  // ActualDestPort

                        netWs.Cell(netRow, col).Value = "INFO";  // Informational - not validated
                        netWs.Cell(netRow, col).Style.Fill.BackgroundColor = XLColor.LightGray;

                        netRow++;
                    }
                }
            }

            // === SECTION 3: No network data case ===
            if (expectedNetworkFlows.Count == 0 && result.NetworkCaptures.Count == 0)
            {
                // No expected flows and no captures - add a note
                netWs.Cell(netRow, 1).Value = "N/A";
                netWs.Cell(netRow, 2).Value = "No network flows expected or captured for this test case";
            }

            netWs.Columns().AdjustToContents();

            wb.SaveAs(detailPath);

            await Task.CompletedTask;
        }

        private static void SetUserSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Input", "Action", "DataType", "Result", "ErrorCode", "ErrorCategory",
                "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message", "DiffIndex",
                "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }

        private static void SetClientSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", "ErrorCode",
                "ErrorCategory", "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message",
                "DiffIndex", "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ClientStdout" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }

        private static void SetServerSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", "ErrorCode",
                "ErrorCategory", "PointsAwarded", "PointsPossible", "DurationMs", "DetailPath", "Message",
                "DiffIndex", "ExpectedOutput", "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ServerStdout" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Row(1).Style.Font.Bold = true;
        }

        /// <summary>
        /// Set Network sheet headers dynamically based on protocol (TCP or HTTP).
        /// TCP: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole, Actual*, NetworkResult
        /// HTTP: Same as TCP plus URI, Method, Status, HttpVersion, HttpBody columns
        /// </summary>
        private void SetNetworkSheetHeaders(IXLWorksheet ws)
        {
            // Network sheet format matching ExcelDetailLogService naming convention (NO underscores).
            // Format: Stage, expected columns (Time, Flags, etc.), then Actual* columns, then NetworkResult.
            // CRITICAL FIX: Added SourcePort and DestPort columns for debugging network traffic
            // This ensures consistency across both Docker and regular grading flows.

            var protocol = _currentTestKitProtocol ?? "TCP";

            if (protocol.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP protocol: Include HTTP-specific fields
                var headers = new[] {
                    "Stage", "Time", "Info", "Source", "Destination",
                    "Flags", "State", "URI", "Method", "Status", "HttpVersion", "HttpBody", "SourceRole", "DestinationRole",
                    "ActualFlags", "ActualState", "ActualURI", "ActualMethod", "ActualStatus", "ActualHttpVersion", "ActualHttpBody",
                    "ActualSourceRole", "ActualDestRole", "ActualSourcePort", "ActualDestPort",
                    "NetworkResult"
                };
                for (int i = 0; i < headers.Length; i++)
                    ws.Cell(1, i + 1).Value = headers[i];
            }
            else
            {
                // TCP protocol: Traditional format with Data field
                var headers = new[] {
                    "Stage", "Time", "Info", "Source", "Destination",
                    "Flags", "State", "Data", "SourceRole", "DestinationRole",
                    "ActualFlags", "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData",
                    "ActualSourcePort", "ActualDestPort",
                    "NetworkResult"
                };
                for (int i = 0; i < headers.Length; i++)
                    ws.Cell(1, i + 1).Value = headers[i];
            }

            ws.Row(1).Style.Font.Bold = true;
        }

        private async Task WriteOverallSummaryAsync(string studentResultPath, List<TestCaseResult> results)
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
        /// Move per-stage PCAP snapshots from student root to TC-specific folder.
        /// This organizes network captures per test case for easier debugging.
        /// Format: snapshot_TC3_stage1.pcap -> TC3/snapshot_TC3_stage1.pcap
        /// </summary>
        private void MoveSnapshotsToTCFolder(string studentResultPath, string tcResultPath, string testCaseName)
        {
            try
            {
                // Find all snapshot files for this test case
                var snapshotPattern = $"snapshot_{testCaseName}_stage*.pcap";
                var snapshotFiles = Directory.GetFiles(studentResultPath, snapshotPattern, SearchOption.TopDirectoryOnly);

                if (snapshotFiles.Length > 0)
                {
                    OnProgress($"[TC Organization] Moving {snapshotFiles.Length} snapshot files to {testCaseName} folder...");

                    foreach (var snapshotFile in snapshotFiles)
                    {
                        var fileName = Path.GetFileName(snapshotFile);
                        var destPath = Path.Combine(tcResultPath, fileName);

                        try
                        {
                            // Move (not copy) to avoid duplication
                            File.Move(snapshotFile, destPath, overwrite: true);
                            OnProgress($"[TC Organization] Moved {fileName} to {testCaseName}/");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[TC Organization] WARNING: Failed to move {fileName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[TC Organization] WARNING: Failed to move snapshots for {testCaseName}: {ex.Message}");
            }
        }

        #endregion
    }
}
