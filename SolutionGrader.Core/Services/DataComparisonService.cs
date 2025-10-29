using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
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
                return (true, "Ignored: expected missing");

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual file missing: {actualPath}");

            using var e = File.OpenRead(expectedPath!);
            using var a = File.OpenRead(actualPath!);

            if (e.Length != a.Length) return (false, $"Size differs: {e.Length} vs {a.Length}");

            using var he = SHA256.Create();
            using var ha = SHA256.Create();
            var h1 = Convert.ToHexString(he.ComputeHash(e));
            var h2 = Convert.ToHexString(ha.ComputeHash(a));
            return (h1 == h2, h1 == h2 ? "Equal" : "Hash differs");
        }

        public (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected missing");

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual file missing: {actualPath}");

            var exp = Normalize(File.ReadAllText(expectedPath!), caseInsensitive);
            var act = Normalize(File.ReadAllText(actualPath!), caseInsensitive);

            return (exp == act, exp == act ? "Equal" : "Content differs");
        }

        public (bool, string) CompareJson(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected missing");

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual file missing: {actualPath}");

            var eDoc = JsonDocument.Parse(File.ReadAllText(expectedPath!));
            var aDoc = JsonDocument.Parse(File.ReadAllText(actualPath!));
            var eNorm = JsonNormalize(eDoc.RootElement, ignoreOrder);
            var aNorm = JsonNormalize(aDoc.RootElement, ignoreOrder);
            return (eNorm == aNorm, eNorm == aNorm ? "Equal" : "JSON differs");
        }

        public (bool, string) CompareCsv(string? expectedPath, string? actualPath, bool ignoreOrder = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath!))
                return (true, "Ignored: expected missing");

            if (IsMissing(actualPath) || !File.Exists(actualPath!))
                return (false, $"Actual file missing: {actualPath}");

            var e = File.ReadAllLines(expectedPath!).Select(l => l.Trim()).ToList();
            var a = File.ReadAllLines(actualPath!).Select(l => l.Trim()).ToList();

            if (ignoreOrder)
            {
                e.Sort(StringComparer.OrdinalIgnoreCase);
                a.Sort(StringComparer.OrdinalIgnoreCase);
            }

            var eJoined = string.Join("\n", e);
            var aJoined = string.Join("\n", a);
            return (eJoined.Equals(aJoined, StringComparison.OrdinalIgnoreCase), eJoined == aJoined ? "Equal" : "CSV differs");
        }

        // ---- Helpers used by the logger to create a detailed diff ----
        public DetailedCompareResult CompareTextDetailed(string expectedPath, string actualPath, bool caseInsensitive = true)
        {
            if (IsMissing(expectedPath) || !File.Exists(expectedPath))
                return new DetailedCompareResult { AreEqual = true, Message = "Ignored: expected missing" };

            if (IsMissing(actualPath) || !File.Exists(actualPath))
                return new DetailedCompareResult { AreEqual = false, Message = $"Actual file missing: {actualPath}" };

            var expRaw = File.ReadAllText(expectedPath);
            var actRaw = File.ReadAllText(actualPath);

            var exp = Normalize(expRaw, caseInsensitive);
            var act = Normalize(actRaw, caseInsensitive);

            if (exp == act)
                return new DetailedCompareResult
                {
                    AreEqual = true,
                    Message = "Equal",
                    NormalizedExpected = exp,
                    NormalizedActual = act
                };

            var (idx, eCh, aCh, eCtx, aCtx) = FirstDiff(exp, act);

            return new DetailedCompareResult
            {
                AreEqual = false,
                Message = "Content differs",
                FirstDiffIndex = idx,
                ExpectedChar = eCh,
                ActualChar = aCh,
                ExpectedContext = eCtx,
                ActualContext = aCtx,
                NormalizedExpected = exp,
                NormalizedActual = act
            };
        }

        private static bool IsMissing(string? p) => string.IsNullOrWhiteSpace(p);

        private static string Normalize(string s, bool ci)
        {
            s = s.Replace("\r", "").Trim();
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

            string Slice(string s) =>
                s.Substring(Math.Max(0, i - context), Math.Min(context * 2 + 1, Math.Max(0, s.Length - Math.Max(0, i - context))));

            return (i, ec, ac, Slice(e), Slice(a));
        }

        private static string JsonNormalize(JsonElement el, bool ignoreOrder)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var props = el.EnumerateObject().ToList();
                    if (ignoreOrder) props.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    var b1 = new StringBuilder().Append('{');
                    for (int i = 0; i < props.Count; i++)
                    {
                        if (i > 0) b1.Append(',');
                        b1.Append(props[i].Name).Append(':').Append(JsonNormalize(props[i].Value, ignoreOrder));
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
