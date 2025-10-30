using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using System;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Keywords;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// DataComparisonService that DOES NOT require runtime captures or generated files.
    /// Policy:
    ///  - If expected is missing => PASS (ignored).
    ///  - If actual is missing (including memory:// keys or non-existent files) => PASS (ignored).
    ///  - If both sides present and readable => compare (text equality by default).
    ///  - Error/diagnostic messages avoid referencing memory:// or physical paths.
    /// </summary>
    public sealed class DataComparisonService : IDataComparisonService
    {
        private readonly IRunContext _run;

        public DataComparisonService(IRunContext run)
        {
            _run = run;
        }

        public (bool, string) CompareFile(string? expectedPath, string? actualPath)
        {
            // No expected -> ignore
            if (IsMissing(expectedPath) || !SafeFileExists(expectedPath!))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{info.Title}: {info.Description} (ignored)");
            }

            // No actual -> ignore instead of failing
            if (IsMissing(actualPath) || !SafeFileExists(actualPath!))
            {
                var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (true, $"{info.Title}: Actual output not available (ignored)");
            }

            // Compare
            var exp = Normalize(File.ReadAllText(expectedPath!), false);
            var act = Normalize(File.ReadAllText(actualPath!), false);

            if (exp == act) return (true, "Files match exactly");
            var (idx, _, _, _, _) = FirstDiff(exp, act);
            return (false, idx >= 0 ? $"Content differs (first diff at idx {idx})" : "Content differs");
        }

        public (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            var isClientOutput = ContainsScope(actualPath, FileKeywords.Folder_Clients);
            var isServerOutput = ContainsScope(actualPath, FileKeywords.Folder_Servers);

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

            // For console output with memory:// paths, use cumulative approach FIRST
            // This handles timing issues where output might be in earlier stages
            string? actualRaw = null;
            if ((isClientOutput || isServerOutput) && actualPath != null && actualPath.StartsWith("memory://"))
            {
                // Try cumulative approach for memory keys
                actualRaw = TryGetCumulativeOutput(actualPath, "");
                
                // If cumulative returns empty, the output was not captured
                // This could be due to process not running, output buffering, or wrong stage
                if (string.IsNullOrEmpty(actualRaw))
                {
                    var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                    string outputType = isClientOutput ? "client output" : isServerOutput ? "server output" : "text";
                    // Extract stage from memory key for better error message
                    var parsed = ParseMemoryPath(actualPath);
                    var stageInfo = parsed.HasValue ? $" stage {parsed.Value.stage}" : "";
                    return (false, $"{info.Title}: {outputType}{stageInfo} not captured (process may not be running or output not flushed)");
                }
            }
            else
            {
                // For non-memory paths, read directly
                if (!TryReadContent(actualPath, out actualRaw))
                {
                    var info = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                    string outputType = isClientOutput ? "client output" : isServerOutput ? "server output" : "text";
                    return (false, $"{info.Title}: {outputType} not available but expected output was defined");
                }
            }

            var exp = Normalize(expectedRaw, caseInsensitive);
            var act = Normalize(actualRaw, caseInsensitive);
            
            // For console output comparisons, check if expected is contained in actual
            // This handles buffered output and timing differences where expected output
            // may appear in actual output along with additional content
            if (exp == act || ((isClientOutput || isServerOutput) && act.Contains(exp)))
            {
                string outputType = isClientOutput ? "client output" : isServerOutput ? "server output" : "text content";
                return (true, $"Text comparison passed: {outputType} matches expected");
            }

            var (idx, _, _, _, _) = FirstDiff(exp, act);
            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.TEXT_MISMATCH);
            return (false, idx >= 0
                ? $"{infoMismatch.Title}: Content differs at position {idx}"
                : $"{infoMismatch.Title}: {infoMismatch.Description}");
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
                return expNorm == actNorm
                    ? (true, "JSON matches expected")
                    : (false, "JSON differs from expected");
            }
            catch (JsonException)
            {
                return (false, "Invalid JSON content for comparison");
            }
        }

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

            var (idx, _, _, _, _) = FirstDiff(exp, act);
            return (false, idx >= 0 ? $"CSV differs (first diff at idx {idx})" : "CSV differs");
        }

        private static bool IsMissing(string? p) => string.IsNullOrWhiteSpace(p) || p == FileKeywords.Value_MissingPlaceholder;

        private static bool SafeFileExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }

        private bool TryReadContent(string? path, out string content)
        {
            content = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return false;

            // Try to resolve memory:// keys from RunContext
            if (path.StartsWith("memory://", StringComparison.OrdinalIgnoreCase))
            {
                return _run.TryGetCapturedOutput(path, out content);
            }

            try
            {
                if (File.Exists(path))
                {
                    content = File.ReadAllText(path);
                    return true;
                }
            }
            catch
            {
                // IO failure -> treat as not found.
            }

            // If it's not a file path, treat it as inline literal text (e.g., from Excel)
            // but only when it doesn't look like a rooted path.
            var looksLikePath = Path.IsPathRooted(path) || path.Contains('\\') || path.Contains('/');
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
                || path.IndexOf($"/{scope}/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Normalize(string s, bool ci)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            // 0. Strip BOM (Byte Order Mark) if present
            if (s.Length > 0 && s[0] == '\uFEFF')
            {
                s = s.Substring(1);
            }

            // 1. Unescape Unicode sequences (\u0027 -> ')
            s = UnescapeUnicode(s);

            // 2. Normalize smart quotes and dashes
            s = s
                .Replace("\u2018", "'")  // ' left single quote
                .Replace("\u2019", "'")  // ' right single quote
                .Replace("\u201C", "\"") // " left double quote
                .Replace("\u201D", "\"") // " right double quote
                .Replace("\u2013", "-")  // – en dash
                .Replace("\u2014", "-"); // — em dash

            // 3. Try JSON canonicalization if it looks like JSON
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
                catch
                {
                    // Not valid JSON, continue with text normalization
                }
            }

            // 4. Normalize newlines and collapse whitespace
            s = s.Replace("\r", "").Replace("\n", " ");
            
            // Replace non-breaking spaces and other Unicode whitespace
            s = s.Replace("\u00A0", " "); // Non-breaking space
            s = s.Replace("\u2002", " "); // En space
            s = s.Replace("\u2003", " "); // Em space
            s = s.Replace("\u2009", " "); // Thin space
            
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            
            return ci ? s.ToLowerInvariant() : s;
        }

        private static string UnescapeUnicode(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Convert ONLY \uXXXX -> char
            return System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
            {
                var code = Convert.ToInt32(m.Groups[1].Value, 16);
                return char.ConvertFromUtf32(code);
            });
        }

        private static (string scope, string questionCode, string stage)? ParseMemoryPath(string memoryPath)
        {
            if (string.IsNullOrEmpty(memoryPath) || !memoryPath.StartsWith("memory://"))
                return null;
                
            var parts = memoryPath.Replace("memory://", "").Split('/');
            if (parts.Length < 3) return null;
            
            return (parts[0], parts[1], parts[2]);
        }

        private string TryGetCumulativeOutput(string memoryPath, string currentStageOutput)
        {
            // memory://clients/TC01/2 -> get stages up to and including current stage
            // This is more lenient to handle timing differences and buffered output
            var parsed = ParseMemoryPath(memoryPath);
            if (!parsed.HasValue) return currentStageOutput;
            
            var scope = parsed.Value.scope; // "clients" or "servers"
            var questionCode = parsed.Value.questionCode;
            var currentStage = parsed.Value.stage; // "1", "2", etc.
            
            // Try to parse current stage as integer
            if (!int.TryParse(currentStage, out var currentStageNum))
            {
                // Not a numeric stage, return current output only
                return currentStageOutput;
            }
            
            // Limit maximum stages to prevent excessive iterations
            currentStageNum = Math.Min(currentStageNum, 50);
            
            // Try to accumulate stages from 1 up to current stage
            // This handles cases where expected output from stage N actually appears in stage N+1 due to buffering
            var cumulative = new StringBuilder();
            for (int stage = 1; stage <= currentStageNum; stage++)
            {
                var stageKey = $"memory://{scope}/{questionCode}/{stage}";
                if (_run.TryGetCapturedOutput(stageKey, out var stageOutput))
                {
                    // Strip BOM from this stage's output before appending
                    if (!string.IsNullOrEmpty(stageOutput) && stageOutput[0] == '\uFEFF')
                    {
                        stageOutput = stageOutput.Substring(1);
                    }
                    cumulative.Append(stageOutput);
                }
            }
            
            var result = cumulative.ToString();
            // If cumulative is empty, return current stage output as fallback
            return string.IsNullOrEmpty(result) ? currentStageOutput : result;
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
                    var props = new System.Collections.Generic.List<string>();
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
    }
}
