// This file contains the Output Comparison region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Output Comparison

        /// <summary>
        /// Compares actual outputs against expected outputs using ALL-OR-NOTHING grading policy.
        /// 
        /// GRADING POLICY: ALL-OR-NOTHING
        /// - If ALL comparisons pass, student earns FULL marks for the test case
        /// - If ANY comparison fails, student earns ZERO marks for the test case
        /// - This policy ensures students implement complete functionality, not partial solutions
        /// </summary>
        /// <param name="expected">Expected outputs by stage</param>
        /// <param name="clientOutputs">Actual client outputs by stage</param>
        /// <param name="serverOutputs">Actual server outputs by stage</param>
        /// <param name="maxMark">Maximum marks for this test case</param>
        /// <returns>Earned mark, pass status, and comparison details</returns>
        private (double earnedMark, bool passed, List<ComparisonResult> comparisons) CompareOutputs(
            Dictionary<int, (string? ClientConsole, string? ServerConsole)> expected,
            Dictionary<int, string> clientOutputs,
            Dictionary<int, string> serverOutputs,
            double maxMark)
        {
            var comparisons = new List<ComparisonResult>();
            int total = 0;
            int passed = 0;

            foreach (var (stage, exp) in expected)
            {
                if (!string.IsNullOrEmpty(exp.ClientConsole))
                {
                    total++;
                    var actual = clientOutputs.TryGetValue(stage, out var c) ? c : "";
                    var match = NormalizeAndContains(actual, exp.ClientConsole);
                    if (match) passed++;

                    // Log detailed comparison for debugging (NO TRUNCATION - full output for debugging)
                    OnProgress($"  [Stage {stage}] Client comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        OnProgress($"    Expected (contains): '{exp.ClientConsole}'");
                        OnProgress($"    Actual output: '{actual}'");
                    }

                    comparisons.Add(new ComparisonResult
                    {
                        Source = "Client",
                        Stage = stage,
                        Expected = exp.ClientConsole,
                        Actual = actual,
                        Passed = match
                    });
                }

                if (!string.IsNullOrEmpty(exp.ServerConsole))
                {
                    total++;
                    var actual = serverOutputs.TryGetValue(stage, out var s) ? s : "";
                    var match = NormalizeAndContains(actual, exp.ServerConsole);
                    if (match) passed++;

                    // Log detailed comparison for debugging (NO TRUNCATION - full output for debugging)
                    OnProgress($"  [Stage {stage}] Server comparison: {(match ? "PASS" : "FAIL")}");
                    if (!match)
                    {
                        OnProgress($"    Expected (contains): '{exp.ServerConsole}'");
                        OnProgress($"    Actual output: '{actual}'");
                    }

                    comparisons.Add(new ComparisonResult
                    {
                        Source = "Server",
                        Stage = stage,
                        Expected = exp.ServerConsole,
                        Actual = actual,
                        Passed = match
                    });
                }
            }

            // ALL-OR-NOTHING policy for console output comparison
            // CRITICAL FIX: If total == 0 (no console output expectations), treat as PASS
            // Only enforce ALL-OR-NOTHING when there ARE expectations to check
            bool allPassed = total == 0 || (passed == total && total > 0);
            double earnedMark = allPassed ? maxMark : 0;

            if (total == 0)
            {
                OnProgress($"  Comparison summary: No console output expectations - PASS by default");
            }
            else
            {
                OnProgress($"  Comparison summary: {passed}/{total} checks passed, earned {earnedMark:F2}/{maxMark:F2} marks");
            }

            return (earnedMark, allPassed, comparisons);
        }

        private List<ComparisonResult> CompareNetwork(List<ExpectedNetworkFlow> expected)
        {
            var results = new List<ComparisonResult>();

            // CRITICAL FIX: Get ALL captured packets for this stage (regardless of questionCode)
            // because packets may be stored with various questionCode values or empty string
            var allCapturedPackets = _runContext.GetAllCapturedNetworkPackets();

            // DIAGNOSTIC LOGGING - Written to GradingLogs for debugging
            OnProgress($"[CompareNetwork] Expected network flows from Detail.xlsx: {expected.Count}");
            OnProgress($"[CompareNetwork] Total captured packets in RunContext: {allCapturedPackets.Count}");
            if (expected.Count > 0)
            {
                OnProgress($"[CompareNetwork] Expected stages: {string.Join(", ", expected.Select(e => e.Stage).Distinct().OrderBy(s => s))}");
            }
            if (allCapturedPackets.Count > 0)
            {
                OnProgress($"[CompareNetwork] Captured stages: {string.Join(", ", allCapturedPackets.Select(p => p.Stage).Distinct().OrderBy(s => s))}");
            }

            // LINUX 3-WAY TO 4-WAY TCP CLOSE NORMALIZATION:
            // Linux TCP stack optimizes connection close to 3-way (FIN-ACK → FIN-ACK → ACK)
            // Windows TCP stack uses 4-way (FIN-ACK → ACK → FIN-ACK → ACK)
            // Since test kits expect Windows 4-way pattern, we normalize captured packets
            // by injecting synthetic ACK packets where the 3-way pattern is detected.
            //
            // This normalization must happen BEFORE grouping by stage to ensure correct packet order.
            var normalizedPackets = Normalize3WayTo4WayClose(allCapturedPackets.ToList());

            OnProgress($"[CompareNetwork] After 3→4 way normalization: {normalizedPackets.Count} packets (was {allCapturedPackets.Count})");

            // CRITICAL FIX: Positional/Sequential matching within each stage
            // Network flow order matters! Must match flow-by-flow in sequence.
            // Expected flow[0] must match Captured flow[0], not just "any flow with matching flags"
            // This catches errors like "Server closes connection before Client" which violates protocol.
            //
            // Group expected flows by stage to handle per-stage sequential matching
            var expectedByStage = expected.GroupBy(e => e.Stage).ToDictionary(g => g.Key, g => g.ToList());
            var capturedByStage = normalizedPackets.GroupBy(p => p.Stage).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var exp in expected)
            {
                // Get all flows for this stage (both expected and captured)
                var expectedFlowsForStage = expectedByStage[exp.Stage];
                var capturedFlowsForStage = capturedByStage.ContainsKey(exp.Stage)
                    ? capturedByStage[exp.Stage]
                    : new List<CapturedNetworkPacket>();

                // Find position of this expected flow within its stage
                var positionInStage = expectedFlowsForStage.IndexOf(exp);

                // SEQUENTIAL MATCHING: Match by position within stage
                // If we expect the 3rd flow in stage 5, we check the 3rd captured flow in stage 5
                CapturedNetworkPacket? matchingPacket = null;
                if (positionInStage >= 0 && positionInStage < capturedFlowsForStage.Count)
                {
                    matchingPacket = capturedFlowsForStage[positionInStage];
                }

                if (matchingPacket != null)
                {
                    // STRICT GRADING: Check if it's an exact match (PASS) or mismatch (FAIL)
                    // Per user requirement: "remove all PARTIAL and just defaults to FAIL or NOT FAIL"
                    bool exactMatch = true;
                    var mismatchReasons = new List<string>();

                    // Compare flags using set comparison (already matched in FirstOrDefault above)
                    // This is redundant but kept for clarity
                    if (!string.IsNullOrEmpty(exp.Flags) && !FlagsMatch(exp.Flags, matchingPacket.Flags))
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"flags: expected '{exp.Flags}' but got '{matchingPacket.Flags}'");
                    }

                    // Compare roles exactly
                    if (!string.IsNullOrEmpty(exp.SourceRole) && matchingPacket.SourceRole != exp.SourceRole)
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"source role: expected '{exp.SourceRole}' but got '{matchingPacket.SourceRole}'");
                    }
                    if (!string.IsNullOrEmpty(exp.DestinationRole) && matchingPacket.DestinationRole != exp.DestinationRole)
                    {
                        exactMatch = false;
                        mismatchReasons.Add($"dest role: expected '{exp.DestinationRole}' but got '{matchingPacket.DestinationRole}'");
                    }

                    // Compare Data payload if expected data is provided (for TCP)
                    // Note: Expected data from Excel uses null or empty string to indicate "no data expected"
                    // We need to check if exp.Data is not null/empty AND not the string "None" (which Excel uses for null)
                    if (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals(NetworkKeywords.Data_None, StringComparison.OrdinalIgnoreCase))
                    {
                        var actualData = matchingPacket.Data ?? "";
                        var expectedData = exp.Data;

                        // Compare data - trim whitespace but use STRICT case-sensitive comparison
                        // Network data must match exactly (no normalization) to catch encoding/casing bugs
                        if (!actualData.Trim().Equals(expectedData.Trim(), StringComparison.Ordinal))
                        {
                            exactMatch = false;
                            var expPreview = expectedData.Length > 50 ? expectedData.Substring(0, 50) + "..." : expectedData;
                            var actPreview = actualData.Length > 50 ? actualData.Substring(0, 50) + "..." : actualData;
                            mismatchReasons.Add($"data: expected '{expPreview}' but got '{actPreview}'");
                        }
                    }

                    // Compare HTTP-specific fields if expected (for HTTP protocol)
                    if (!string.IsNullOrEmpty(exp.URI))
                    {
                        var actualURI = matchingPacket.URI ?? "";
                        if (!actualURI.Equals(exp.URI, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"URI: expected '{exp.URI}' but got '{actualURI}'");
                        }
                    }

                    if (!string.IsNullOrEmpty(exp.Method))
                    {
                        var actualMethod = matchingPacket.Method ?? "";
                        if (!actualMethod.Equals(exp.Method, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"Method: expected '{exp.Method}' but got '{actualMethod}'");
                        }
                    }

                    if (!string.IsNullOrEmpty(exp.Status))
                    {
                        var actualStatus = matchingPacket.Status ?? "";
                        // Use StartsWith for status code matching to avoid false positives
                        // e.g., expected "200" matches "200 OK" but not "404" or "520"
                        if (!actualStatus.StartsWith(exp.Status, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"Status: expected '{exp.Status}' but got '{actualStatus}'");
                        }
                    }

                    if (!string.IsNullOrEmpty(exp.HttpVersion))
                    {
                        var actualHttpVersion = matchingPacket.HttpVersion ?? "";
                        if (!actualHttpVersion.Equals(exp.HttpVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            mismatchReasons.Add($"HttpVersion: expected '{exp.HttpVersion}' but got '{actualHttpVersion}'");
                        }
                    }

                    if (!string.IsNullOrEmpty(exp.HttpBody))
                    {
                        var actualHttpBody = matchingPacket.HttpBody ?? "";
                        if (!actualHttpBody.Trim().Equals(exp.HttpBody.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatch = false;
                            var expPreview = exp.HttpBody.Length > 50 ? exp.HttpBody.Substring(0, 50) + "..." : exp.HttpBody;
                            var actPreview = actualHttpBody.Length > 50 ? actualHttpBody.Substring(0, 50) + "..." : actualHttpBody;
                            mismatchReasons.Add($"HttpBody: expected '{expPreview}' but got '{actPreview}'");
                        }
                    }

                    // Log detailed comparison
                    if (exactMatch)
                    {
                        var dataInfo = (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals(NetworkKeywords.Data_None, StringComparison.OrdinalIgnoreCase))
                            ? $" with data='{exp.Data}'" : "";
                        OnProgress($"[COMPARISON] ✓ PASS - Stage {exp.Stage}: {exp.Flags} from {exp.SourceRole} to {exp.DestinationRole}{dataInfo}");
                    }
                    else
                    {
                        OnProgress($"[COMPARISON] ✗ FAIL - Stage {exp.Stage}: {string.Join(", ", mismatchReasons)}");
                    }

                    var expectedStr = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}";
                    var actualStr = $"Flags={matchingPacket.Flags}, From={matchingPacket.SourceRole}, To={matchingPacket.DestinationRole}";

                    if (!string.IsNullOrEmpty(exp.Data) && !exp.Data.Equals(NetworkKeywords.Data_None, StringComparison.OrdinalIgnoreCase))
                    {
                        expectedStr += $", Data={exp.Data}";
                        actualStr += $", Data={matchingPacket.Data ?? "(empty)"}";
                    }

                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = expectedStr,
                        Actual = actualStr,
                        Passed = exactMatch
                    });
                }
                else
                {
                    // No matching packet found - FAIL
                    OnProgress($"[COMPARISON] ✗ FAIL - Stage {exp.Stage}: MISSING {exp.Flags} from {exp.SourceRole} to {exp.DestinationRole} (position {positionInStage}) - Captured: {(capturedFlowsForStage.Any() ? string.Join(", ", capturedFlowsForStage.Select(p => $"{p.Flags}({p.SourceRole}→{p.DestinationRole})")) : "none")}");

                    results.Add(new ComparisonResult
                    {
                        Source = "Network",
                        Stage = exp.Stage,
                        Expected = $"Flags={exp.Flags}, From={exp.SourceRole}, To={exp.DestinationRole}",
                        Actual = capturedFlowsForStage.Any() ? string.Join("; ", capturedFlowsForStage.Select(p => p.Flags)) : "(no captures)",
                        Passed = false
                    });
                }
            }

            // Summary of comparison results
            int passCount = results.Count(r => r.Passed);
            int failCount = results.Count(r => !r.Passed);

            OnProgress($"[COMPARISON] RESULTS: {results.Count} total - PASS={passCount}, FAIL={failCount}");
            if (failCount > 0)
            {
                OnProgress($"[COMPARISON] WARNING: {failCount} network flows FAILED - test will FAIL (ALL-OR-NOTHING)");
            }

            return results;
        }

        /// <summary>
        /// Normalizes console output for comparison, handling:
        /// - Line ending differences (Windows \r\n vs Linux \n vs old Mac \r)
        /// - Console.Write vs Console.WriteLine differences (trailing newlines)
        /// - Leading/trailing whitespace per line
        /// - Multiple consecutive newlines/empty lines
        /// 
        /// This allows students to pass even if they use Console.Write instead of Console.WriteLine
        /// or if there are minor formatting differences due to running in Linux environment.
        /// </summary>
        /// <param name="input">The console output to normalize</param>
        /// <returns>Normalized console output for comparison</returns>
        private static string NormalizeConsoleOutput(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            
            // Step 1: Normalize all line endings to \n
            var normalized = input.Replace("\r\n", "\n").Replace("\r", "\n");
            
            // Step 2: Split into lines, trim each line, remove completely empty lines
            var lines = normalized.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            
            // Step 3: Join with single space (makes Console.Write vs WriteLine equivalent)
            // This means "Hello\nWorld" and "Hello World" will both become "Hello World"
            return string.Join(" ", lines);
        }

        /// <summary>
        /// Simple string contains check with console output normalization.
        /// Handles line ending differences, Console.Write vs WriteLine, and whitespace.
        /// For more robust comparison, use DataComparisonService.CompareText().
        /// </summary>
        private bool NormalizeAndContains(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            
            var normExpected = NormalizeConsoleOutput(expected);
            var normActual = NormalizeConsoleOutput(actual);
            
            return normActual.Contains(normExpected);
        }

        /// <summary>
        /// Normalize TCP flags for comparison - sorts flags alphabetically and removes whitespace.
        /// REUSES logic from Executor.NormalizeFlags() to avoid code duplication.
        /// 
        /// Examples:
        /// - "PSH, ACK" -> "ACK, PSH"
        /// - "SYN" -> "SYN"
        /// - "ACK, RST" -> "ACK, RST"
        /// </summary>
        private static string NormalizeFlags(string flags)
        {
            if (string.IsNullOrWhiteSpace(flags)) return "";

            // CRITICAL FIX: Replace hyphens with commas so tcpdump format matches Excel format
            // tcpdump outputs: "SYN-ACK", "PSH-ACK", "FIN-ACK"
            // Excel expects: "SYN, ACK", "PSH, ACK", "FIN, ACK"
            flags = flags.Replace("-", ", ");

            var flagList = flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .OrderBy(f => f)
                .ToList();

            return string.Join(", ", flagList);
        }

        /// <summary>
        /// Parse flags string into a HashSet of individual flags for comparison
        /// Handles both comma-separated (Excel) and hyphen-separated (tcpdump) formats
        /// </summary>
        private static HashSet<string> ParseFlagsToSet(string flags)
        {
            if (string.IsNullOrWhiteSpace(flags))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Replace hyphens with commas to handle both formats
            flags = flags.Replace("-", ",");

            return flags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compare two flag strings as sets (order-independent, format-independent)
        /// Returns true if both contain the same flags, false otherwise
        /// </summary>
        private static bool FlagsMatch(string flags1, string flags2)
        {
            var set1 = ParseFlagsToSet(flags1);
            var set2 = ParseFlagsToSet(flags2);

            return set1.SetEquals(set2);
        }

        /// <summary>
        /// Normalizes captured network packets from Linux 3-way TCP close to Windows 4-way TCP close.
        /// 
        /// PROBLEM:
        /// Linux TCP stack optimizes connection close by combining ACK with FIN into a single packet:
        ///   3-way (Linux):  FIN-ACK (A→B) → FIN-ACK (B→A) → ACK (A→B)
        /// 
        /// Windows TCP stack sends them separately:
        ///   4-way (Windows): FIN-ACK (A→B) → ACK (B→A) → FIN-ACK (B→A) → ACK (A→B)
        /// 
        /// Since test kits are designed for Windows 4-way handshake, grading on Linux fails.
        /// 
        /// SOLUTION:
        /// Detect the 3-way pattern (two consecutive FIN-ACK from opposite directions) and inject
        /// a synthetic ACK packet between them to transform it into the expected 4-way pattern.
        /// 
        /// Pattern Detection:
        /// - Packet[i] has FIN flag and is from Role A to Role B
        /// - Packet[i+1] has FIN flag and is from Role B to Role A (opposite direction)
        /// 
        /// Transformation:
        /// - Insert synthetic ACK packet from Role B to Role A between them
        /// </summary>
        /// <param name="packets">List of captured network packets (modified in place)</param>
        /// <returns>List of normalized packets with synthetic ACK packets injected where needed</returns>
        private List<CapturedNetworkPacket> Normalize3WayTo4WayClose(List<CapturedNetworkPacket> packets)
        {
            if (packets == null || packets.Count < 2)
            {
                return packets ?? new List<CapturedNetworkPacket>();
            }

            var result = new List<CapturedNetworkPacket>();
            int injectedCount = 0;

            for (int i = 0; i < packets.Count; i++)
            {
                var current = packets[i];
                result.Add(current);

                // Check if this is a FIN packet and there's a next packet
                if (i + 1 < packets.Count)
                {
                    var next = packets[i + 1];

                    // Detect 3-way close pattern:
                    // Current: FIN-ACK from Role A to Role B
                    // Next: FIN-ACK from Role B to Role A (opposite direction, also has FIN)
                    bool currentHasFin = HasFinFlag(current.Flags);
                    bool nextHasFin = HasFinFlag(next.Flags);
                    bool oppositeDirection = !string.IsNullOrEmpty(current.SourceRole) &&
                                            !string.IsNullOrEmpty(next.SourceRole) &&
                                            current.SourceRole == next.DestinationRole &&
                                            current.DestinationRole == next.SourceRole;
                    bool sameStage = current.Stage == next.Stage;

                    if (currentHasFin && nextHasFin && oppositeDirection && sameStage)
                    {
                        // Inject synthetic ACK packet between them
                        // The ACK should be from B to A (same direction as the second FIN-ACK)
                        // This transforms: FIN-ACK(A→B), FIN-ACK(B→A), ACK(A→B)
                        // Into:            FIN-ACK(A→B), ACK(B→A), FIN-ACK(B→A), ACK(A→B)
                        // Calculate timestamp as midpoint between current and next packets
                        // This ensures correct ordering even with high-precision timestamps
                        var midpointTicks = (current.Timestamp.Ticks + next.Timestamp.Ticks) / 2;
                        var syntheticTimestamp = new DateTime(midpointTicks);

                        var syntheticAck = new CapturedNetworkPacket
                        {
                            Stage = current.Stage,
                            Timestamp = syntheticTimestamp,
                            Flags = "ACK",
                            State = "FIN_WAIT",
                            SourceRole = next.SourceRole,        // Same as the second FIN-ACK's source (B)
                            DestinationRole = next.DestinationRole,  // Same as the second FIN-ACK's destination (A)
                            Source = next.Source ?? current.Destination ?? "",
                            Destination = next.Destination ?? current.Source ?? "",
                            Protocol = current.Protocol ?? "TCP",
                            Length = 0,  // ACK-only packets have no payload
                            Info = "[Synthetic ACK - normalized from 3-way to 4-way close]",
                            Data = null,
                            SourcePort = next.SourcePort != 0 ? next.SourcePort : current.DestinationPort,
                            DestinationPort = next.DestinationPort != 0 ? next.DestinationPort : current.SourcePort
                        };

                        result.Add(syntheticAck);
                        injectedCount++;

                        OnProgress($"[3Way→4Way] Injected synthetic ACK at stage {current.Stage}: " +
                                  $"{syntheticAck.SourceRole}→{syntheticAck.DestinationRole} " +
                                  $"(between FIN-ACK packets to normalize to 4-way close)");
                    }
                }
            }

            if (injectedCount > 0)
            {
                OnProgress($"[3Way→4Way] Normalization complete: Injected {injectedCount} synthetic ACK packet(s)");
                OnProgress($"[3Way→4Way] Original packet count: {packets.Count}, Normalized count: {result.Count}");
            }

            return result;
        }

        /// <summary>
        /// Checks if a TCP flags string contains the FIN flag.
        /// Handles various formats: "FIN", "FIN, ACK", "FIN-ACK", "ACK, FIN", etc.
        /// </summary>
        private static bool HasFinFlag(string? flags)
        {
            if (string.IsNullOrWhiteSpace(flags))
            {
                return false;
            }

            // Parse flags into a set and check for FIN
            var flagSet = ParseFlagsToSet(flags);
            return flagSet.Contains("FIN");
        }

        #endregion
    }
}
