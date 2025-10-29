namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public sealed class ExcelSuiteLoader : ITestSuiteLoader
{
    public SuiteDefinition Load(string suitePathOrHeaderXlsx)
    {
        var headerPath = ResolveHeaderPath(suitePathOrHeaderXlsx);
        var caseMarks = ReadCaseMarks(headerPath);
        // Protocol: try Header sheet cell Type if present; default HTTP
        var protocol = ReadProtocolFromHeader(headerPath);

        var cases = ReadCasesFromDirectory(System.IO.Path.GetDirectoryName(headerPath)!, caseMarks);

        return new SuiteDefinition
        {
            HeaderPath = headerPath,
            Protocol   = protocol,
            Cases      = cases
        };
    }

    private static string ResolveHeaderPath(string suitePath)
    {
        if (System.IO.File.Exists(suitePath)) return suitePath;
        if (!System.IO.Directory.Exists(suitePath)) throw new System.IO.DirectoryNotFoundException($"Suite directory not found: {suitePath}");

        var candidate = System.IO.Path.Combine(suitePath, SuiteKeywords.HeaderFileName);
        if (System.IO.File.Exists(candidate)) return candidate;

        var header = System.IO.Directory.GetFiles(suitePath, "*.xlsx", System.IO.SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Contains("header", System.StringComparison.OrdinalIgnoreCase));
        if (header is null) throw new System.IO.FileNotFoundException($"Header.xlsx not found in {suitePath}");
        return header;
    }

    private static string ReadProtocolFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            var ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, SuiteKeywords.Sheet_Header, StringComparison.OrdinalIgnoreCase))
                  ?? wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used is null) return "HTTP";
            foreach (var row in used.Rows().Skip(1))
            {
                var key = row.Cell(1).GetString().Trim();
                if (!string.Equals(key, SuiteKeywords.ConfigKey_Type, StringComparison.OrdinalIgnoreCase)) continue;
                var val = row.Cell(2).GetString().Trim();
                return val.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? "TCP" : "HTTP";
            }
        }
        catch { }
        return "HTTP";
    }

    private static System.Collections.Generic.IReadOnlyList<TestCaseDefinition> ReadCasesFromDirectory(string root, IReadOnlyDictionary<string, double> marks)
    {
        var list = new System.Collections.Generic.List<TestCaseDefinition>();
        foreach (var dir in System.IO.Directory.GetDirectories(root))
        {
            var name = System.IO.Path.GetFileName(dir);
            var detail = System.IO.Path.Combine(dir, SuiteKeywords.DetailFileName);
            if (!System.IO.File.Exists(detail))
            {
                // fallback: anything "detail*.xlsx"
                detail = System.IO.Directory.GetFiles(dir, "*detail*.xlsx", System.IO.SearchOption.TopDirectoryOnly)
                          .FirstOrDefault() ?? detail;
            }
            if (!System.IO.File.Exists(detail)) continue; // skip folders without detail

            list.Add(new TestCaseDefinition
            {
                Name = name,
                Mark = ResolveCaseMark(name, marks),
                DirectoryPath = dir,
                DetailPath = detail,
                InnerHeaderPath = null
            });
        }
        if (list.Count == 0) throw new InvalidDataException("No test cases found in suite root.");
        return list;
    }

    private static IReadOnlyDictionary<string, double> ReadCaseMarks(string headerPath)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var wb = new XLWorkbook(headerPath);
            foreach (var ws in wb.Worksheets)
            {
                var used = ws.RangeUsed();
                if (used is null) continue;

                var header = used.FirstRowUsed();
                if (header is null) continue;

                var columnMap = header.CellsUsed()
                    .ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

                if (columnMap.Count == 0) continue;

                var questionCol = FindColumn(columnMap, new[] { "question", "testcase", "case", "code", "name" });
                var markCol = FindColumn(columnMap, new[] { "mark", "point" });
                if (questionCol == null || markCol == null) continue;

                foreach (var row in used.RowsUsed().Skip(1))
                {
                    var key = row.Cell(questionCol.Value).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    if (TryGetNumeric(row.Cell(markCol.Value), out var value))
                    {
                        map[key] = value;
                    }
                }
            }
        }
        catch
        {
            // ignore header parsing issues; default marks will be 0
        }

        return map;
    }

    private static int? FindColumn(Dictionary<string, int> header, IEnumerable<string> keywords)
    {
        foreach (var kvp in header)
        {
            var normalized = kvp.Key.Replace(" ", "");
            foreach (var keyword in keywords)
            {
                if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }
        return null;
    }

    private static bool TryGetNumeric(IXLCell cell, out double value)
    {
        if (cell.TryGetValue(out value)) return true;

        var text = cell.GetString().Trim();
        if (string.IsNullOrEmpty(text))
        {
            value = 0;
            return false;
        }

        text = text.Replace("pts", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("points", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Trim();

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
               double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private static double ResolveCaseMark(string caseName, IReadOnlyDictionary<string, double> marks)
    {
        if (marks.TryGetValue(caseName, out var exact)) return exact;

        var normalized = Normalize(caseName);
        foreach (var kvp in marks)
        {
            if (Normalize(kvp.Key) == normalized) return kvp.Value;
        }

        return 0;
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
