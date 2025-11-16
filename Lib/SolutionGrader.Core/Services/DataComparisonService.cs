using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using System;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Keywords;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// DataComparisonService that tolerates async/late console output by aggregating
    /// all captured client/server stages before comparing. Policy:
    ///  - If expected is missing => PASS (ignored).
    ///  - If actual is missing => try memory (all stages).
    ///  - Text compare tries exact, then contains, then aggressive normalization.
    /// Note: All actual outputs are stored in memory only (no txt files).
    /// </summary>
    public sealed class DataComparisonService : IDataComparisonService
    {
        private readonly IRunContext _run;

        // Keep a sane upper bound for scanning stages when we need to read from files.
        private const int MaxStagesToScanFromFiles = 200;

        public DataComparisonService(IRunContext run) => _run = run;

        /// <summary>
        /// Compares two files for equality using normalized content comparison.
        /// This comparison is case-insensitive to prevent capitalization differences from causing failures.
        /// </summary>
        /// <param name="expectedPath">Path to the expected file</param>
        /// <param name="actualPath">Path to the actual file</param>
        /// <returns>Tuple of (success, message) indicating comparison result</returns>
        public (bool, string) CompareFile(string? expectedPath, string? actualPath)
        {
            if (IsMissing(expectedPath) || !SafeFileExists(expectedPath!))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{info.Title}: {info.Description} (ignored)");
            }

            if (IsMissing(actualPath) || !SafeFileExists(actualPath!))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (true, $"{info.Title}: Actual output not available (ignored)");
            }

            // Use case-insensitive normalization (true) to ignore capitalization differences
            var expRaw = File.ReadAllText(expectedPath!);
            var actRaw = File.ReadAllText(actualPath!);
            var exp = Normalize(expRaw, true);
            var act = Normalize(actRaw, true);

            if (exp == act) return (true, "Files match exactly");
            
            var (idx, expChar, actChar, expCtx, actCtx) = FirstDiff(exp, act);
            
            // Build detailed error message
            var errorMessage = new System.Text.StringBuilder();
            errorMessage.AppendLine($"Content differs (first diff at idx {idx})");
            errorMessage.AppendLine($"Expected file: {expectedPath}");
            errorMessage.AppendLine($"Actual file:   {actualPath}");
            errorMessage.AppendLine($"Expected (first 200 chars): {FormatForDisplay(expRaw, 200)}");
            errorMessage.AppendLine($"Actual (first 200 chars):   {FormatForDisplay(actRaw, 200)}");
            
            if (idx >= 0 && expChar.HasValue && actChar.HasValue)
            {
                errorMessage.AppendLine($"First difference at index {idx}: expected '{EscapeChar(expChar.Value)}' but got '{EscapeChar(actChar.Value)}'");
            }
            
            return (false, errorMessage.ToString().TrimEnd());
        }

        /// <summary>
        /// Compares text content between expected and actual outputs with advanced normalization.
        /// This method is case-insensitive by default to prevent capitalization differences from causing failures.
        /// Uses multiple comparison strategies (exact match, contains, aggressive normalization) for robust validation.
        /// </summary>
        /// <param name="expectedPath">Path to expected content or inline content</param>
        /// <param name="actualPath">Path to actual content or memory:// URI</param>
        /// <param name="caseInsensitive">Whether to ignore case differences (default: true)</param>
        /// <returns>Tuple of (success, message) indicating comparison result</returns>
        public (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            var isClientOutput = ContainsScope(actualPath, FileKeywords.Folder_Clients);
            var isServerOutput = ContainsScope(actualPath, FileKeywords.Folder_Servers);
            string outputType = isClientOutput ? "client output" : isServerOutput ? "server output" : "text";

            // Expected may be inline (Excel cell) or a file path
            var expectedRaw = TryReadContent(expectedPath, out var expectedFromFile)
                ? expectedFromFile
                : expectedPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(expectedRaw))
            {
                string code = isClientOutput ? ErrorCodes.EXPECTED_CLIENT_OUTPUT_MISSING
                                             : isServerOutput ? ErrorCodes.EXPECTED_SERVER_OUTPUT_MISSING
                                             : ErrorCodes.EXPECTED_FILE_MISSING;
                var info = ErrorCodes.GetInfo(code);
                return (true, $"{info.Title}: {info.Description} (ignored)");
            }

            // Get actual content from memory (no txt files):
            // 1) If memory://… path, try that specific key from RunContext.
            // 2) If empty or missing, aggregate ALL stages from memory for that (scope, question).
            // 3) If still empty after memory aggregation, try path inference to aggregate from memory.
            string actualRaw = string.Empty;

            if (actualPath != null && actualPath.StartsWith("memory://", StringComparison.OrdinalIgnoreCase))
            {
                // Specific stage attempt
                if (_run.TryGetCapturedOutput(actualPath, out var stageContent))
                    actualRaw = stageContent ?? string.Empty;

                // If that specific stage was empty, try aggregated memory for all stages
                if (string.IsNullOrWhiteSpace(actualRaw))
                {
                    if (TryParseMemory(actualPath, out var scope, out var question))
                    {
                        actualRaw = ReadAllStagesFromMemory(scope, question);
                    }
                }
            }
            else
            {
                // Non-memory: try direct read; if that fails and it's a client/server compare,
                // we still attempt memory aggregation (combine all stage outputs for the scope/question)
                // because the step semantic expects console output to be available.
                if (!TryReadContent(actualPath, out actualRaw) && (isClientOutput || isServerOutput))
                {
                    // Try to infer (scope, question) from memory path
                    if (TryInferScopeQuestionFromPath(actualPath, out var scope, out var question))
                    {
                        actualRaw = ReadAllStagesFromMemory(scope, question);
                    }
                }
            }

            // If nothing was captured anywhere and this was console output, treat as "not available but expected defined" (fail)
            if (string.IsNullOrWhiteSpace(actualRaw) && (isClientOutput || isServerOutput))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{info.Title}: {outputType} not captured (expected was provided)");
            }

            // Normalize & compare (with multiple fallbacks)
            var exp = Normalize(expectedRaw, caseInsensitive);
            var act = Normalize(actualRaw, caseInsensitive);

            if (exp == act)
                return (true, $"Text comparison passed: {outputType} matches exactly");

            if ((isClientOutput || isServerOutput) && act.Contains(exp))
                return (true, $"Text comparison passed: {outputType} contains expected (loose match)");

            var expLoose = StripAggressive(exp);
            var actLoose = StripAggressive(act);

            if (expLoose == actLoose)
                return (true, $"Text comparison passed: {outputType} matches after aggressive normalization");

            if (actLoose.Contains(expLoose))
                return (true, $"Text comparison passed: {outputType} contains expected after aggressive normalization");

            var (idx, expChar, actChar, expCtx, actCtx) = FirstDiff(exp, act);
            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.TEXT_MISMATCH);
            
            // Build detailed error message with actual vs expected content
            var errorMessage = new System.Text.StringBuilder();
            errorMessage.AppendLine($"{infoMismatch.Title}: Content differs at position {idx}");
            errorMessage.AppendLine($"Expected (normalized): {FormatForDisplay(expectedRaw, 200)}");
            errorMessage.AppendLine($"Actual (normalized):   {FormatForDisplay(actualRaw, 200)}");
            
            if (idx >= 0 && expChar.HasValue && actChar.HasValue)
            {
                errorMessage.AppendLine($"First difference at index {idx}: expected '{EscapeChar(expChar.Value)}' but got '{EscapeChar(actChar.Value)}'");
                errorMessage.AppendLine($"Expected context: ...{EscapeString(expCtx)}...");
                errorMessage.AppendLine($"Actual context:   ...{EscapeString(actCtx)}...");
            }
            
            return (false, errorMessage.ToString().TrimEnd());
        }

        public (bool, string) CompareJson(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (!TryReadContent(expectedPath, out var expectedJson) || string.IsNullOrWhiteSpace(expectedJson))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{info.Title}: Expected JSON missing (ignored)");
            }

            if (!TryReadContent(actualPath, out var actualJson) || string.IsNullOrWhiteSpace(actualJson))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{info.Title}: Actual JSON not available but expected JSON was defined");
            }

            try
            {
                var expNorm = JsonNormalize(JsonDocument.Parse(expectedJson).RootElement, ignoreOrder);
                var actNorm = JsonNormalize(JsonDocument.Parse(actualJson!).RootElement, ignoreOrder);
                
                if (expNorm == actNorm)
                    return (true, "JSON matches expected");
                
                // Build detailed error message
                var errorMessage = new System.Text.StringBuilder();
                errorMessage.AppendLine("JSON differs from expected");
                errorMessage.AppendLine($"Expected JSON: {FormatForDisplay(expectedJson, 200)}");
                errorMessage.AppendLine($"Actual JSON:   {FormatForDisplay(actualJson, 200)}");
                
                return (false, errorMessage.ToString().TrimEnd());
            }
            catch (JsonException ex)
            {
                return (false, $"Invalid JSON content for comparison: {ex.Message}");
            }
        }

        /// <summary>
        /// Compares CSV content between expected and actual outputs.
        /// This comparison is case-insensitive to prevent capitalization differences from causing failures.
        /// </summary>
        /// <param name="expectedPath">Path to expected CSV content</param>
        /// <param name="actualPath">Path to actual CSV content</param>
        /// <param name="ignoreOrder">Whether to ignore row order (currently not implemented)</param>
        /// <returns>Tuple of (success, message) indicating comparison result</returns>
        public (bool, string) CompareCsv(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected CSV file is missing (test ignored)");
            }

            if (!TryReadContent(actualPath, out var actualCsv))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual CSV output was not generated but expected CSV was defined");
            }

            var expectedCsv = TryReadContent(expectedPath, out var expectedFromFile)
                ? expectedFromFile
                : expectedPath ?? string.Empty;

            var exp = Normalize(expectedCsv, true);
            var act = Normalize(actualCsv, true);

            if (exp == act) return (true, "CSV matches expected");

            var (idx, expChar, actChar, expCtx, actCtx) = FirstDiff(exp, act);
            
            // Build detailed error message
            var errorMessage = new System.Text.StringBuilder();
            errorMessage.AppendLine($"CSV differs (first diff at idx {idx})");
            errorMessage.AppendLine($"Expected CSV: {FormatForDisplay(expectedCsv, 200)}");
            errorMessage.AppendLine($"Actual CSV:   {FormatForDisplay(actualCsv, 200)}");
            
            if (idx >= 0 && expChar.HasValue && actChar.HasValue)
            {
                errorMessage.AppendLine($"First difference at index {idx}: expected '{EscapeChar(expChar.Value)}' but got '{EscapeChar(actChar.Value)}'");
            }
            
            return (false, errorMessage.ToString().TrimEnd());
        }

        // ---------------- helpers ----------------

        private static bool IsMissing(string? p) => string.IsNullOrWhiteSpace(p) || p == FileKeywords.Value_MissingPlaceholder;
        private static bool SafeFileExists(string path) { try { return File.Exists(path); } catch { return false; } }

        private bool TryReadContent(string? path, out string content)
        {
            content = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (path.StartsWith("memory://", StringComparison.OrdinalIgnoreCase))
                return _run.TryGetCapturedOutput(path, out content);

            try
            {
                if (File.Exists(path))
                {
                    content = File.ReadAllText(path);
                    return true;
                }
            }
            catch { /* ignore */ }

            // Check if this looks like inline JSON content rather than a file path
            // Inline JSON starts with '{' or '['
            var trimmed = path.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                // This is inline JSON content
                content = path;
                return true;
            }

            // Only treat as path if rooted or contains backslashes; forward slashes alone may indicate inline content (URLs, dates, etc.)
            var looksLikePath = Path.IsPathRooted(path) || path.Contains('\\');
            if (!looksLikePath)
            {
                content = path;
                return true;
            }

            return false;
        }

        private static bool ContainsScope(string? path, string scope)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.IndexOf($"\\{scope}\\", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf($"/{scope}/", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf($"memory://{scope}/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Normalizes text for comparison by removing/normalizing whitespace and special characters.
        /// This method performs aggressive whitespace normalization to prevent false failures due to
        /// spacing, line ending, or formatting differences between expected and actual output.
        /// 
        /// Normalization steps:
        /// 1. Remove BOM (Byte Order Mark)
        /// 2. Unescape Unicode escape sequences (\uXXXX)
        /// 3. Normalize smart quotes and dashes to ASCII equivalents
        /// 4. Attempt JSON canonicalization for JSON content
        /// 5. Normalize all line endings to \n
        /// 6. Convert all Unicode whitespace variants to regular spaces
        /// 7. Trim whitespace from each line
        /// 8. Remove empty lines completely
        /// 9. Collapse multiple consecutive spaces into single space
        /// 10. Final trim of entire string
        /// </summary>
        /// <param name="s">The string to normalize</param>
        /// <param name="ci">Whether to perform case-insensitive comparison (convert to lowercase)</param>
        /// <returns>Normalized string ready for comparison</returns>
        private static string Normalize(string s, bool ci)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // 1. Strip BOM (Byte Order Mark)
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);

            // 2. Unescape Unicode escape sequences (\uXXXX)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
            {
                var code = Convert.ToInt32(m.Groups[1].Value, 16);
                return char.ConvertFromUtf32(code);
            });

            // 3. Normalize smart quotes and dashes to ASCII equivalents
            s = s.Replace("\u2018", "'").Replace("\u2019", "'")
                 .Replace("\u201C", "\"").Replace("\u201D", "\"")
                 .Replace("\u2013", "-").Replace("\u2014", "-");

            // 4. Try JSON canonicalization for JSON content
            if ((s.TrimStart().StartsWith("{") || s.TrimStart().StartsWith("[")))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    s = JsonSerializer.Serialize(doc, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                }
                catch { /* Not valid JSON, continue with text normalization */ }
            }

            // 5. Convert all line endings to \n for consistency
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            
            // 6. Replace all Unicode whitespace variants with regular spaces
            s = s.Replace("\u00A0", " ")  // Non-breaking space
                 .Replace("\u2002", " ")  // En space
                 .Replace("\u2003", " ")  // Em space
                 .Replace("\u2009", " ")  // Thin space
                 .Replace("\u200A", " ")  // Hair space
                 .Replace("\u202F", " ")  // Narrow no-break space
                 .Replace("\u205F", " ")  // Medium mathematical space
                 .Replace("\u3000", " ")  // Ideographic space
                 .Replace("\t", " ");      // Tab to space

            // 7. Trim leading/trailing whitespace from each line
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].Trim();
            }
            
            // 8. Remove empty lines completely to prevent whitespace/newline mismatches
            // This is the key fix: empty lines and extra newlines will no longer cause comparison failures
            lines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            s = string.Join(" ", lines); // Join with single space instead of newline
            
            // 9. Collapse multiple consecutive spaces into single space
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
            
            // 10. Final trim
            s = s.Trim();

            return ci ? s.ToLowerInvariant() : s;
        }

        /// <summary>
        /// Performs ultra-aggressive normalization by removing ALL whitespace and punctuation.
        /// This is used as a last-resort fallback when standard normalization still shows differences.
        /// Useful for catching content matches where only formatting differs significantly.
        /// </summary>
        /// <param name="s">The string to aggressively normalize</param>
        /// <returns>String with all whitespace and common punctuation removed</returns>
        private static string StripAggressive(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Remove all whitespace characters
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");
            // Remove common punctuation that may vary in output formatting
            s = s.Replace(",", "").Replace(".", "").Replace(":", "").Replace(";", "");
            return s;
        }

        /// <summary>
        /// Formats a string for display by truncating if too long and showing ellipsis
        /// </summary>
        private static string FormatForDisplay(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            if (s.Length <= maxLength) return EscapeString(s);
            return EscapeString(s.Substring(0, maxLength)) + "...";
        }

        /// <summary>
        /// Escapes special characters in a string for display
        /// </summary>
        private static string EscapeString(string s)
        {
            return s.Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
        }

        /// <summary>
        /// Escapes a single character for display
        /// </summary>
        private static string EscapeChar(char c)
        {
            return c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString()
            };
        }

        private static (int idx, char? e, char? a, string eCtx, string aCtx) FirstDiff(string e, string a, int context = 24)
        {
            var min = Math.Min(e.Length, a.Length);
            for (int i = 0; i < min; i++)
            {
                if (e[i] != a[i])
                {
                    var s = Math.Max(0, i - context);
                    var eCtx = e.Substring(s, Math.Min(context * 2, Math.Max(0, e.Length - s)));
                    var aCtx = a.Substring(s, Math.Min(context * 2, Math.Max(0, a.Length - s)));
                    return (i, e[i], a[i], eCtx, aCtx);
                }
            }
            if (e.Length != a.Length)
            {
                var i = min;
                var s = Math.Max(0, i - context);
                var eCtx = e.Substring(s, Math.Min(context * 2, Math.Max(0, e.Length - s)));
                var aCtx = a.Substring(s, Math.Min(context * 2, Math.Max(0, a.Length - s)));
                return (i, e.Length > a.Length ? e[i] : null, a.Length > e.Length ? a[i] : null, eCtx, aCtx);
            }
            return (-1, null, null, "", "");
        }

        private static string JsonNormalize(JsonElement el, bool ignoreOrder)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var props = new List<string>();
                    foreach (var p in el.EnumerateObject())
                        props.Add("\"" + p.Name + "\":" + JsonNormalize(p.Value, ignoreOrder));
                    props.Sort(StringComparer.Ordinal);
                    var b1 = new StringBuilder();
                    b1.Append('{');
                    for (int i = 0; i < props.Count; i++)
                    {
                        if (i > 0) b1.Append(',');
                        b1.Append(props[i]);
                    }
                    return b1.Append('}').ToString();
                case JsonValueKind.Array:
                    var arr = el.EnumerateArray().Select(x => JsonNormalize(x, ignoreOrder)).ToList();
                    if (ignoreOrder) arr.Sort(StringComparer.Ordinal);
                    return "[" + string.Join(",", arr) + "]";
                case JsonValueKind.String: return "\"" + el.GetString() + "\"";
                case JsonValueKind.Number: return el.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return "null";
            }
        }

        // ---------- NEW aggregation helpers ----------

        private static bool TryParseMemory(string memoryPath, out string scope, out string question)
        {
            scope = ""; question = "";
            if (string.IsNullOrEmpty(memoryPath) || !memoryPath.StartsWith("memory://")) return false;
            var parts = memoryPath.Replace("memory://", "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            scope = parts[0]; question = parts[1];
            return true;
        }

        private string ReadAllStagesFromMemory(string scope, string question)
        {
            var sb = new StringBuilder();

            // We don't know the max stage in memory; accumulate anything that exists (1..N)
            // Probe a generous range, but stop when we miss a long stretch (gap heuristic).
            int missesInARow = 0;
            for (int i = 1; i <= MaxStagesToScanFromFiles && missesInARow < 50; i++)
            {
                var key = $"memory://{scope}/{question}/{i}";
                if (_run.TryGetCapturedOutput(key, out var chunk) && !string.IsNullOrEmpty(chunk))
                {
                    // strip BOM per chunk
                    if (chunk.Length > 0 && chunk[0] == '\uFEFF') chunk = chunk.Substring(1);
                    sb.Append(chunk);
                    missesInARow = 0;
                }
                else
                {
                    missesInARow++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Attempts to extract scope (clients/servers) and question code from a path.
        /// Used as a fallback when a comparison step provides a non-standard path format.
        /// 
        /// Primary use case: memory:// URIs (e.g., "memory://clients/TC01/3")
        /// 
        /// Fallback use cases (for backward compatibility with non-memory actualPath values):
        /// - When actualPath is provided as a simple pattern like "clients/TC01" instead of memory:// URI
        /// - When test configurations use relative path notation from older versions
        /// - Enables memory aggregation even when path format doesn't match standard memory:// scheme
        /// 
        /// Note: This method is used to parse actualPath values (not expected values).
        /// File-based txt outputs are no longer created by this system; all actual outputs
        /// are stored in memory and this method helps aggregate them by inferring scope/question.
        /// </summary>
        private static bool TryInferScopeQuestionFromPath(string? path, out string scope, out string question)
        {
            scope = ""; question = "";
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                
                // Primary case: parse memory:// path format: memory://scope/question/stage
                if (path.StartsWith("memory://", StringComparison.OrdinalIgnoreCase))
                {
                    return TryParseMemory(path, out scope, out question);
                }
                
                // Fallback: extract scope/question from path-like patterns
                // This handles cases where comparison steps use simplified notation like "clients/TC01"
                // or when actualPath is derived from test configurations that pre-date memory:// URIs
                var norm = path.Replace('\\', '/').ToLowerInvariant();
                
                // Try simple format: scope/question (e.g., "clients/tc01" or "servers/tc02")
                var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    // Extract first two parts as scope and question
                    scope = parts[0]; 
                    question = parts[1];
                    return true;
                }
                
                return false;
            }
            catch { return false; }
        }

        // ---------- NEW: Extended validation methods for comprehensive grading ----------

        public (bool, string) CompareHttpMethod(string? expectedMethod, string? actualMethod)
        {
            if (string.IsNullOrWhiteSpace(expectedMethod))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected HTTP method not specified (ignored)");
            }

            if (string.IsNullOrWhiteSpace(actualMethod))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual HTTP method not captured");
            }

            var expNorm = expectedMethod.Trim().ToUpperInvariant();
            var actNorm = actualMethod.Trim().ToUpperInvariant();

            if (expNorm == actNorm)
                return (true, $"HTTP method matches: {actualMethod}");

            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.HTTP_METHOD_MISMATCH);
            return (false, $"{infoMismatch.Title}: Expected '{expectedMethod}', got '{actualMethod}'");
        }

        public (bool, string) CompareStatusCode(string? expectedStatusCode, int? actualStatusCode)
        {
            if (string.IsNullOrWhiteSpace(expectedStatusCode))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected status code not specified (ignored)");
            }

            if (!actualStatusCode.HasValue)
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual status code not captured");
            }

            // Normalize both expected and actual status codes for comparison
            var expNorm = GradingKeywords.NormalizeStatusCode(expectedStatusCode);
            var actNorm = GradingKeywords.NormalizeStatusCode(actualStatusCode.Value.ToString());

            if (expNorm == actNorm || expNorm.Equals(actualStatusCode.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                return (true, $"Status code matches: {actualStatusCode}");

            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.STATUS_CODE_MISMATCH);
            return (false, $"{infoMismatch.Title}: Expected '{expectedStatusCode}', got '{actualStatusCode}'");
        }

        public (bool, string) CompareByteSize(int? expectedByteSize, int? actualByteSize)
        {
            if (!expectedByteSize.HasValue || expectedByteSize.Value < 0)
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected byte size not specified (ignored)");
            }

            if (!actualByteSize.HasValue)
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual byte size not captured");
            }

            if (GradingKeywords.IsByteSizeWithinTolerance(expectedByteSize.Value, actualByteSize.Value))
                return (true, $"Byte size within tolerance: Expected {expectedByteSize}, got {actualByteSize}");

            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.BYTE_SIZE_MISMATCH);
            return (false, $"{infoMismatch.Title}: Expected {expectedByteSize} bytes, got {actualByteSize} bytes (diff: {Math.Abs(expectedByteSize.Value - actualByteSize.Value)})");
        }

        public (bool, string) ValidateStep(Step step, string? actualPath, GradingConfig config)
        {
            // Check if this step's sheet should be graded based on grading mode
            if (!config.ShouldGradeStep(step.Id))
            {
                var sheetName = step.Id.StartsWith(GradingKeywords.StepPrefix_OutputClient, StringComparison.OrdinalIgnoreCase) 
                    ? SuiteKeywords.Sheet_OutputClients 
                    : SuiteKeywords.Sheet_OutputServers;
                return (true, $"Validation skipped: {sheetName} sheet not enabled in grading mode");
            }

            // Extract validation type from step metadata
            var validationType = step.Metadata?.ContainsKey(GradingKeywords.MetadataKey_ValidationType) == true
                ? step.Metadata[GradingKeywords.MetadataKey_ValidationType]?.ToString()
                : null;

            // Check if this validation is enabled in config
            if (!string.IsNullOrEmpty(validationType) && !config.IsEnabled(validationType))
            {
                return (true, $"Validation skipped by config: {validationType}");
            }

            // Route to appropriate validation based on step ID or metadata
            if (step.Id.Contains("-METHOD-", StringComparison.OrdinalIgnoreCase) || validationType == "HTTP_METHOD")
            {
                // Get actual HTTP method from captured metadata
                var questionCode = step.QuestionCode;
                var stage = step.Stage;
                if (_run.TryGetHttpMetadata(questionCode, stage, out var httpMethod, out _, out _))
                {
                    return CompareHttpMethod(step.HttpMethod ?? step.Target, httpMethod);
                }
                return (false, "HTTP method not captured from middleware");
            }

            if (step.Id.Contains("-STATUS-", StringComparison.OrdinalIgnoreCase) || validationType == "STATUS_CODE")
            {
                // Get actual status code from captured metadata
                var questionCode = step.QuestionCode;
                var stage = step.Stage;
                if (_run.TryGetHttpMetadata(questionCode, stage, out _, out var statusCode, out _))
                {
                    return CompareStatusCode(step.StatusCode ?? step.Target, statusCode);
                }
                return (false, "Status code not captured from middleware");
            }

            if (step.Id.Contains("-SIZE-", StringComparison.OrdinalIgnoreCase) || validationType == "BYTE_SIZE")
            {
                // Get actual byte size from captured metadata
                var questionCode = step.QuestionCode;
                var stage = step.Stage;
                if (_run.TryGetHttpMetadata(questionCode, stage, out _, out _, out var byteSize))
                {
                    return CompareByteSize(step.ByteSize, byteSize);
                }
                return (false, "Byte size not captured from middleware");
            }

            if (step.Id.Contains("-DATA-", StringComparison.OrdinalIgnoreCase) || validationType == "DATA_RESPONSE")
            {
                // Validate data response content
                if (step.Action == ActionKeywords.CompareJson)
                    return CompareJson(step.Target, actualPath);
                else
                    return CompareText(step.Target, actualPath);
            }

            if (step.Id.Contains("-REQ-", StringComparison.OrdinalIgnoreCase) || validationType == "DATA_REQUEST")
            {
                // Validate data request content
                return CompareText(step.Target, actualPath);
            }

            if (step.Id.Contains("-OUT-", StringComparison.OrdinalIgnoreCase))
            {
                // Validate console output
                if (step.Action == ActionKeywords.CompareJson)
                    return CompareJson(step.Target, actualPath);
                else
                    return CompareText(step.Target, actualPath);
            }

            // Fallback to original comparison logic
            return step.Action switch
            {
                var a when a == ActionKeywords.CompareText => CompareText(step.Target, actualPath),
                var a when a == ActionKeywords.CompareJson => CompareJson(step.Target, actualPath),
                var a when a == ActionKeywords.CompareCsv => CompareCsv(step.Target, actualPath),
                var a when a == ActionKeywords.CompareFile => CompareFile(step.Target, actualPath),
                _ => (false, $"Unknown comparison action: {step.Action}")
            };
        }
    }
}
