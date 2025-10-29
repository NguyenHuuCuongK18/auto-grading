using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Keywords;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Comparers with "ignore if expected is empty" policy and helpful text diff.
    /// </summary>
    public sealed class DataComparisonService : IDataComparisonService
    {
        public (bool, string) CompareFile(string? expectedPath, string? actualPath)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: {infoExpected.Description}");
            }

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual output file not found at {actualPath}");
            }

            using var e = File.OpenText(expectedPath!);
            using var a = File.OpenText(actualPath!);
            var exp = Normalize(e.ReadToEnd(), false);
            var act = Normalize(a.ReadToEnd(), false);

            if (exp == act) return (true, "Files match exactly");

            var (idx, _, _, eCtx, aCtx) = FirstDiff(exp, act);
            return (false, idx >= 0 ? $"Content differs (first diff at idx {idx})" : "Content differs");
        }

        public (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            // Determine if this is client or server output based on path
            var isClientOutput = actualPath?.Contains($"\\{FileKeywords.Folder_Clients}\\") ?? false;
            var isServerOutput = actualPath?.Contains($"\\{FileKeywords.Folder_Servers}\\") ?? false;

            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
            {
                string errorCode = isClientOutput ? ErrorCodes.EXPECTED_CLIENT_OUTPUT_MISSING
                                 : isServerOutput ? ErrorCodes.EXPECTED_SERVER_OUTPUT_MISSING
                                 : ErrorCodes.EXPECTED_FILE_MISSING;
                var infoExpected = ErrorCodes.GetInfo(errorCode);
                return (true, $"{infoExpected.Title}: {infoExpected.Description}");
            }

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                string outputType = isClientOutput ? "client console output"
                                  : isServerOutput ? "server console output"
                                  : "actual output";
                return (false, $"{infoActual.Title}: Expected {outputType} at {actualPath} was not generated");
            }

            var exp = Normalize(File.ReadAllText(expectedPath!), caseInsensitive);
            var act = Normalize(File.ReadAllText(actualPath!), caseInsensitive);

            if (exp == act)
            {
                string outputType = isClientOutput ? "client output"
                                  : isServerOutput ? "server output"
                                  : "text content";
                return (true, $"Text comparison passed: {outputType} matches expected");
            }

            var (idx, _, _, eCtx, aCtx) = FirstDiff(exp, act);
            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.TEXT_MISMATCH);
            return (false, idx >= 0 
                ? $"{infoMismatch.Title}: Content differs at position {idx}" 
                : $"{infoMismatch.Title}: {infoMismatch.Description}");
        }

        public (bool, string) CompareJson(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected JSON file is missing (test ignored)");
            }

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual JSON output at {actualPath} was not generated");
            }

            try
            {
                var eDoc = JsonDocument.Parse(File.ReadAllText(expectedPath!));
                var aDoc = JsonDocument.Parse(File.ReadAllText(actualPath!));
                var eNorm = JsonNormalize(eDoc.RootElement, ignoreOrder);
                var aNorm = JsonNormalize(aDoc.RootElement, ignoreOrder);

                if (eNorm == aNorm) return (true, "JSON comparison passed: structure and content match");

                var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.JSON_MISMATCH);
                return (false, $"{infoMismatch.Title}: {infoMismatch.Description}");
            }
            catch (JsonException ex)
            {
                return (false, $"JSON Parse Error: {ex.Message}");
            }
        }

        public (bool, string) CompareCsv(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
            {
                var infoExpected = ErrorCodes.GetInfo(ErrorCodes.EXPECTED_FILE_MISSING);
                return (true, $"{infoExpected.Title}: Expected CSV file is missing (test ignored)");
            }

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
            {
                var infoActual = ErrorCodes.GetInfo(ErrorCodes.ACTUAL_FILE_MISSING);
                return (false, $"{infoActual.Title}: Actual CSV output at {actualPath} was not generated");
            }

            var exp = Normalize(File.ReadAllText(expectedPath!), true);
            var act = Normalize(File.ReadAllText(actualPath!), true);

            if (exp == act) return (true, "CSV comparison passed: content matches");

            var infoMismatch = ErrorCodes.GetInfo(ErrorCodes.CSV_MISMATCH);
            return (false, $"{infoMismatch.Title}: {infoMismatch.Description}");
        }

        private static bool IsMissing(string? p) => string.IsNullOrWhiteSpace(p) || p == FileKeywords.Value_MissingPlaceholder;

        private static string Normalize(string s, bool ci)
        {
            // Normalize newlines and collapse all whitespace runs to a single space
            s = s.Replace("\r", "").Replace("\n", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return ci ? s.ToLowerInvariant() : s;
        }

        private static (int idx, char? e, char? a, string eCtx, string aCtx) FirstDiff(string e, string a, int context = 24)
        {
            var len = Math.Min(e.Length, a.Length);
            int i = 0;
            for (; i < len; i++) if (e[i] != a[i]) break;
            if (i == len && e.Length == a.Length)
                return (-1, null, null, "", "");

            char? ec = i < e.Length ? e[i] : null;
            char? ac = i < a.Length ? a[i] : null;

            int s = Math.Max(0, i - context);
            int le = Math.Min(context * 2, Math.Max(0, e.Length - s));
            int la = Math.Min(context * 2, Math.Max(0, a.Length - s));
            return (i, ec, ac, e.Substring(s, le), a.Substring(s, la));
        }

        private static string JsonNormalize(JsonElement el, bool ignoreOrder)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var props = new List<(string, string)>();
                    foreach (var p in el.EnumerateObject())
                        props.Add((p.Name, JsonNormalize(p.Value, ignoreOrder)));
                    props.Sort((x, y) => string.CompareOrdinal(x.Item1, y.Item1));
                    var b1 = new StringBuilder("{");
                    for (int i = 0; i < props.Count; i++)
                    {
                        if (i > 0) b1.Append(',');
                        b1.Append('\"').Append(props[i].Item1).Append("\":").Append(props[i].Item2);
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
