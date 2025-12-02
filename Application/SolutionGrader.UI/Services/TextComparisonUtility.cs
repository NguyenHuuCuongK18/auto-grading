using System;
using System.Text.RegularExpressions;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Utility class for text comparison operations used in grading.
    /// Provides flexible text matching that normalizes whitespace and performs case-insensitive comparisons.
    /// </summary>
    public static class TextComparisonUtility
    {
        /// <summary>
        /// Compares expected output with actual output (flexible matching).
        /// Returns true if expected is null/empty or if actual contains expected (case-insensitive).
        /// </summary>
        /// <param name="expected">The expected text to find</param>
        /// <param name="actual">The actual text to search in</param>
        /// <returns>True if match found, false otherwise</returns>
        public static bool CompareOutput(string? expected, string? actual)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            if (string.IsNullOrEmpty(actual)) return false;

            // Normalize for comparison
            var normalizedExpected = NormalizeText(expected);
            var normalizedActual = NormalizeText(actual);

            // Check if actual contains expected
            return normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes text for comparison by trimming and reducing multiple whitespaces to single space.
        /// </summary>
        /// <param name="text">Text to normalize</param>
        /// <returns>Normalized text</returns>
        public static string NormalizeText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text.Trim(), @"\s+", " ");
        }
    }
}
