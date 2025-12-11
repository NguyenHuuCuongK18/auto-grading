using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for comparing network flows.
    /// Handles:
    /// - TCP/HTTP flag comparison
    /// - Source/Destination role validation
    /// - Data payload comparison
    /// - Network flow matching and scoring
    /// </summary>
    public sealed class DockerNetworkComparisonService
    {
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        /// <summary>
        /// Compares expected network flows against captured packets.
        /// Uses strict positional matching - each expected flow is compared to the
        /// captured packet at the same position.
        /// </summary>
        public List<ComparisonResult> CompareNetwork(
            List<ExpectedNetworkFlow> expected,
            List<CapturedNetworkPacket> captured)
        {
            var results = new List<ComparisonResult>();
            
            if (expected.Count == 0)
            {
                OnProgress($"[NetworkCompare] No expected network flows to compare");
                return results;
            }
            
            OnProgress($"[NetworkCompare] Comparing {expected.Count} expected flows against {captured.Count} captured packets");
            
            for (int i = 0; i < expected.Count; i++)
            {
                var expectedFlow = expected[i];
                var result = new ComparisonResult
                {
                    Stage = expectedFlow.Stage,
                    Source = "Network",
                    Expected = $"Flags={expectedFlow.Flags}, SrcRole={expectedFlow.SourceRole}, DstRole={expectedFlow.DestinationRole}"
                };
                
                if (i < captured.Count)
                {
                    var capturedPacket = captured[i];
                    
                    result.Actual = $"Flags={capturedPacket.Flags}, SrcRole={capturedPacket.SourceRole}, DstRole={capturedPacket.DestinationRole}";
                    
                    // Compare all fields
                    var (passed, message) = ComparePacket(expectedFlow, capturedPacket);
                    result.Passed = passed;
                    result.Message = message;
                }
                else
                {
                    // Missing packet at this position
                    result.Passed = false;
                    result.Actual = "(MISSING)";
                    result.Message = "(MISSING - not captured)";
                }
                
                results.Add(result);
                
                var statusSymbol = result.Passed ? "✓" : "✗";
                OnProgress($"[NetworkCompare] Flow {i + 1}: {statusSymbol} {result.Message}");
            }
            
            var passCount = results.Count(r => r.Passed);
            var failCount = results.Count(r => !r.Passed);
            OnProgress($"[NetworkCompare] Results: {passCount} PASS, {failCount} FAIL");
            
            return results;
        }
        
        /// <summary>
        /// Compares a single expected flow against a captured packet.
        /// </summary>
        private (bool passed, string message) ComparePacket(
            ExpectedNetworkFlow expected, 
            CapturedNetworkPacket actual)
        {
            var mismatches = new List<string>();
            
            // Compare flags (normalized)
            if (!string.IsNullOrEmpty(expected.Flags))
            {
                if (!FlagsMatch(expected.Flags, actual.Flags ?? ""))
                {
                    mismatches.Add($"flags: expected '{expected.Flags}' but got '{actual.Flags}'");
                }
            }
            
            // Compare source role
            if (!string.IsNullOrEmpty(expected.SourceRole))
            {
                if (!string.Equals(expected.SourceRole, actual.SourceRole, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"source role: expected '{expected.SourceRole}' but got '{actual.SourceRole}'");
                }
            }
            
            // Compare destination role
            if (!string.IsNullOrEmpty(expected.DestinationRole))
            {
                if (!string.Equals(expected.DestinationRole, actual.DestinationRole, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"dest role: expected '{expected.DestinationRole}' but got '{actual.DestinationRole}'");
                }
            }
            
            // Compare data (if expected)
            if (!string.IsNullOrEmpty(expected.Data) && expected.Data != NetworkKeywords.Data_NoPayload)
            {
                if (!DataMatches(expected.Data, actual.Data ?? ""))
                {
                    var expectedPreview = expected.Data.Length > 50 ? expected.Data.Substring(0, 50) + "..." : expected.Data;
                    var actualPreview = (actual.Data ?? "").Length > 50 ? actual.Data!.Substring(0, 50) + "..." : actual.Data ?? "";
                    mismatches.Add($"data mismatch (expected: '{expectedPreview}', got: '{actualPreview}')");
                }
            }
            
            if (mismatches.Count == 0)
            {
                return (true, "PASS");
            }
            else
            {
                return (false, string.Join("; ", mismatches));
            }
        }
        
        /// <summary>
        /// Compares two flag strings, handling different formats (comma-separated vs hyphen-separated).
        /// </summary>
        public static bool FlagsMatch(string flags1, string flags2)
        {
            var set1 = ParseFlagsToSet(flags1);
            var set2 = ParseFlagsToSet(flags2);
            
            return set1.SetEquals(set2);
        }
        
        /// <summary>
        /// Parses a flag string into a normalized set of flags.
        /// Handles formats like "FIN, ACK", "FIN-ACK", "FIN,ACK", etc.
        /// </summary>
        public static HashSet<string> ParseFlagsToSet(string flags)
        {
            if (string.IsNullOrWhiteSpace(flags))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            
            // Normalize: replace various separators with comma, then split
            var normalized = flags
                .Replace("-", ",")
                .Replace(", ", ",")
                .Replace(" ", ",");
            
            return normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().ToUpperInvariant())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Normalizes a flag string to a consistent format.
        /// </summary>
        public static string NormalizeFlags(string flags)
        {
            var flagSet = ParseFlagsToSet(flags);
            
            // Sort in standard TCP flag order: SYN, ACK, PSH, FIN, RST, URG
            var orderedFlags = new[] { "SYN", "ACK", "PSH", "FIN", "RST", "URG" };
            var result = orderedFlags.Where(f => flagSet.Contains(f));
            
            return string.Join(", ", result);
        }
        
        /// <summary>
        /// Compares data payloads with STRICT case-sensitive matching.
        /// 
        /// STRICT GRADING: Data must match 100% including case.
        /// - No partial/contains matching
        /// - No case-insensitive comparison
        /// - Only whitespace trimming is allowed
        /// 
        /// Per user requirement: "data comparison is strict. if they do not match 100% including case -> FAIL"
        /// </summary>
        private bool DataMatches(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected))
            {
                return true; // No expected data, anything is fine
            }
            
            // STRICT COMPARISON: Use Ordinal (case-sensitive) string comparison
            // Only trim whitespace, no other normalization
            var trimmedExpected = expected.Trim();
            var trimmedActual = (actual ?? "").Trim();
            
            // Exact match required - case-sensitive, no partial matching
            return string.Equals(trimmedExpected, trimmedActual, StringComparison.Ordinal);
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
