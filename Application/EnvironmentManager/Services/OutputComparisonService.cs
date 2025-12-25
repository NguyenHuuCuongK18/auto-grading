using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EnvironmentManager.Services
{
    /// <summary>
    /// Service responsible for comparing actual outputs against expected outputs.
    /// Uses ALL-OR-NOTHING grading policy for test case evaluation.
    /// </summary>
    public class OutputComparisonService
    {
        private readonly Action<string>? _progressCallback;

        /// <summary>
        /// Creates a new instance of the output comparison service.
        /// </summary>
        public OutputComparisonService(Action<string>? progressCallback = null)
        {
            _progressCallback = progressCallback;
        }

        /// <summary>
        /// Reports progress to the callback if available.
        /// </summary>
        protected void OnProgress(string message)
        {
            _progressCallback?.Invoke(message);
        }

        /// <summary>
        /// Compares actual outputs against expected outputs using ALL-OR-NOTHING grading policy.
        /// If ALL comparisons pass, student earns FULL marks.
        /// If ANY comparison fails, student earns ZERO marks.
        /// </summary>
        public (double earnedMark, bool passed, List<ComparisonResult> comparisons) CompareOutputs(
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

            // ALL-OR-NOTHING policy
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

        /// <summary>
        /// Normalizes strings and checks if actual contains expected.
        /// Handles whitespace normalization and case-insensitive comparison.
        /// </summary>
        public bool NormalizeAndContains(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected))
                return true;

            if (string.IsNullOrEmpty(actual))
                return false;

            var normalizedActual = NormalizeForComparison(actual);
            var normalizedExpected = NormalizeForComparison(expected);

            return normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes a string for comparison by collapsing whitespace and trimming.
        /// </summary>
        public string NormalizeForComparison(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var normalized = Regex.Replace(text, @"\s+", " ");
            return normalized.Trim();
        }

        /// <summary>
        /// Compares two strings for exact match after normalization.
        /// </summary>
        public bool ExactMatch(string actual, string expected)
        {
            var normalizedActual = NormalizeForComparison(actual);
            var normalizedExpected = NormalizeForComparison(expected);

            return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Represents the result of comparing an expected output with an actual output.
    /// </summary>
    public class ComparisonResult
    {
        /// <summary>Source of the output (Client, Server, Network)</summary>
        public string Source { get; set; } = "";
        
        /// <summary>Stage number in the test case</summary>
        public int Stage { get; set; }
        
        /// <summary>Expected output value</summary>
        public string? Expected { get; set; }
        
        /// <summary>Actual output value</summary>
        public string? Actual { get; set; }
        
        /// <summary>Whether the comparison passed</summary>
        public bool Passed { get; set; }
        
        /// <summary>Points awarded for this comparison</summary>
        public double PointsAwarded { get; set; }
        
        /// <summary>Maximum points possible for this comparison</summary>
        public double PointsPossible { get; set; }
        
        /// <summary>Duration of the comparison in milliseconds</summary>
        public long DurationMs { get; set; }
    }
}
