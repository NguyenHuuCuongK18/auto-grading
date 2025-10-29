using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Text/JSON/CSV/file comparers used by Executor.
    /// - Provides CompareTextDetailed required by Executor (for diffs) 
    /// - Logs expected/actual paths & normalized previews BEFORE comparison.
    /// - Keeps messages compatible with Executor's error-code mapping.
    /// </summary>
    public sealed class DataComparisonService : IDataComparisonService
    {
        private static bool IsMissing(string? s) => string.IsNullOrWhiteSpace(s);
        private static string Norm(string s) => (s ?? string.Empty).Replace("\r", "").Replace("\n", "").Trim();

        private const int PreviewLimit = 600;
        private static string Preview(string s) => s.Length <= PreviewLimit ? s : s[..PreviewLimit] + $"... [truncated {s.Length - PreviewLimit} chars]";

        public (bool, string) CompareFile(string? expectedPath, string? actualPath)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected missing");
            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual file missing: {actualPath}");

            using var e = File.OpenRead(expectedPath!);
            using var a = File.OpenRead(actualPath!);

            Console.WriteLine($"[CompareFile] Expected: {expectedPath} ({e.Length} bytes)");
            Console.WriteLine($"[CompareFile]   Actual: {actualPath} ({a.Length} bytes)");

            if (e.Length != a.Length) return (false, $"Size differs: {e.Length} vs {a.Length}");

            using var he = SHA256.Create(); using var ha = SHA256.Create();
            var de = he.ComputeHash(e); var da = ha.ComputeHash(a);
            return de.SequenceEqual(da) ? (true, "Files equal (SHA256)") : (false, "Content differs (SHA256)");
        }

        public (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            var d = CompareTextDetailed(expectedPath, actualPath, caseInsensitive);
            return (d.AreEqual, d.Message);
        }

        public DetailedCompareResult CompareTextDetailed(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return new DetailedCompareResult { AreEqual = true, Message = "Ignored: expected missing" };

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return new DetailedCompareResult { AreEqual = false, Message = $"Actual text missing: {actualPath}", FirstDiffIndex = -1 };

            var eRaw = File.ReadAllText(expectedPath!);
            var aRaw = File.ReadAllText(actualPath!);

            // Log BEFORE compare so you can see actual output even on PASS
            Console.WriteLine($"[CompareText] Expected path: {expectedPath}");
            Console.WriteLine($"[CompareText] Actual   path: {actualPath}");
            Console.WriteLine($"[CompareText] --- Expected (normalized preview) ---");
            Console.WriteLine(Preview(Norm(eRaw)));
            Console.WriteLine($"[CompareText] --- Actual   (normalized preview) ---");
            Console.WriteLine(Preview(Norm(aRaw)));

            var e = Norm(eRaw);
            var a = Norm(aRaw);

            var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(e, a, comparison))
            {
                return new DetailedCompareResult
                {
                    AreEqual = true,
                    Message = "Text equal (normalized)",
                    NormalizedExpected = e,
                    NormalizedActual = a
                };
            }

            int idx = FirstDiff(e, a, caseInsensitive);
            var eChar = CharAt(e, idx);
            var aChar = CharAt(a, idx);
            string eCtx = ContextWithCaret(e, idx);
            string aCtx = ContextWithCaret(a, idx);
            var msg = $"Text differs (normalized). First diff at {idx}: expected '{Printable(eChar)}' vs actual '{Printable(aChar)}'.";

            return new DetailedCompareResult
            {
                AreEqual = false,
                Message = msg,
                FirstDiffIndex = idx,
                ExpectedChar = eChar,
                ActualChar = aChar,
                ExpectedContext = eCtx,
                ActualContext = aCtx,
                NormalizedExpected = e,
                NormalizedActual = a
            };
        }

        public (bool, string) CompareJson(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected JSON missing");
            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual JSON missing: {actualPath}");

            Console.WriteLine($"[CompareJson] Expected path: {expectedPath}");
            Console.WriteLine($"[CompareJson] Actual   path: {actualPath}");

            try
            {
                using var je = JsonDocument.Parse(File.ReadAllText(expectedPath!));
                using var ja = JsonDocument.Parse(File.ReadAllText(actualPath!));
                var ok = JsonEquals(je.RootElement, ja.RootElement, ignoreOrder);
                return ok ? (true, "JSON equal") : (false, "JSON differs");
            }
            catch (Exception ex) { return (false, "JSON compare error: " + ex.Message); }
        }

        public (bool, string) CompareCsv(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected CSV missing");
            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual CSV missing: {actualPath}");

            Console.WriteLine($"[CompareCsv] Expected path: {expectedPath}");
            Console.WriteLine($"[CompareCsv] Actual   path: {actualPath}");

            var eLines = File.ReadAllLines(expectedPath!).Select(Norm).Where(l => !string.IsNullOrWhiteSpace(l));
            var aLines = File.ReadAllLines(actualPath!).Select(Norm).Where(l => !string.IsNullOrWhiteSpace(l));
            if (ignoreOrder)
            {
                var eSet = new HashSet<string>(eLines, StringComparer.OrdinalIgnoreCase);
                var aSet = new HashSet<string>(aLines, StringComparer.OrdinalIgnoreCase);
                return eSet.SetEquals(aSet) ? (true, "CSV equal (unordered)") : (false, "CSV differs");
            }
            else
            {
                return eLines.SequenceEqual(aLines, StringComparer.OrdinalIgnoreCase) ? (true, "CSV equal (ordered)") : (false, "CSV differs");
            }
        }

        private static int FirstDiff(string s1, string s2, bool ci)
        {
            var c1 = ci ? s1.ToUpperInvariant() : s1;
            var c2 = ci ? s2.ToUpperInvariant() : s2;
            int n = Math.Min(c1.Length, c2.Length);
            for (int i = 0; i < n; i++) if (c1[i] != c2[i]) return i;
            return n;
        }

        private static char? CharAt(string s, int idx) => (idx >= 0 && idx < s.Length) ? s[idx] : (char?)null;

        private static string ContextWithCaret(string s, int idx, int padding = 24)
        {
            idx = Math.Max(0, idx);
            int start = Math.Max(0, idx - padding);
            int end = Math.Min(s.Length, idx + padding);
            var snippet = s.Substring(start, end - start);
            var caretPos = idx - start;
            var caretLine = new string(' ', Math.Max(0, caretPos)) + "^";
            snippet = snippet.Replace("\t", "\\t");
            var sb = new StringBuilder();
            sb.AppendLine(snippet);
            sb.AppendLine(caretLine);
            sb.Append($"index={idx}");
            return sb.ToString();
        }

        private static string Printable(char? c)
        {
            if (c == null) return "<null>";
            return c switch
            {
                '\t' => @"\t",
                '\n' => @"\n",
                '\r' => @"\r",
                ' ' => "␠",
                _ => c.ToString()!
            };
        }

        private static bool JsonEquals(JsonElement x, JsonElement y, bool ignoreOrder)
        {
            if (x.ValueKind != y.ValueKind) return false;
            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    var xProps = x.EnumerateObject().ToList();
                    var yProps = y.EnumerateObject().ToList();
                    if (xProps.Count != yProps.Count) return false;
                    foreach (var xp in xProps)
                    {
                        if (!y.TryGetProperty(xp.Name, out var yp)) return false;
                        if (!JsonEquals(xp.Value, yp, ignoreOrder)) return false;
                    }
                    return true;

                case JsonValueKind.Array:
                    var xa = x.EnumerateArray().ToList();
                    var ya = y.EnumerateArray().ToList();
                    if (xa.Count != ya.Count) return false;
                    if (ignoreOrder)
                    {
                        var canonX = xa.Select(Canonicalize).OrderBy(s => s, StringComparer.Ordinal).ToList();
                        var canonY = ya.Select(Canonicalize).OrderBy(s => s, StringComparer.Ordinal).ToList();
                        return canonX.SequenceEqual(canonY, StringComparer.Ordinal);
                    }
                    else
                    {
                        for (int i = 0; i < xa.Count; i++) if (!JsonEquals(xa[i], ya[i], ignoreOrder)) return false;
                        return true;
                    }

                default:
                    var sx = x.ToString()?.Replace("\r", "").Replace("\n", "");
                    var sy = y.ToString()?.Replace("\r", "").Replace("\n", "");
                    return string.Equals(sx, sy, StringComparison.Ordinal);
            }
        }

        private static string Canonicalize(JsonElement e)
        {
            using var buf = new MemoryStream();
            using var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = false });
            e.WriteTo(w); w.Flush();
            return Encoding.UTF8.GetString(buf.ToArray());
        }
    }
}
