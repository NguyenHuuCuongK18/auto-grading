namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Globalization;
using System.IO;

public sealed class ExcelSuiteLoader : ITestSuiteLoader
{
    public SuiteDefinition Load(string suitePathOrHeaderXlsx)
    {
        var headerPath = ResolveHeaderPath(suitePathOrHeaderXlsx);
        // Protocol: try Header sheet cell Type if present; default HTTP
        var protocol = ReadProtocolFromHeader(headerPath);

        // Read test case marks from Header.xlsx
        var marksMap = ReadMarksFromHeader(headerPath);

        var cases = ReadCasesFromDirectory(System.IO.Path.GetDirectoryName(headerPath)!, marksMap);

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

    private static Dictionary<string, double> ReadMarksFromHeader(string headerPath)
    {
        var marks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var wb = new XLWorkbook(headerPath);
            var ws = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, SuiteKeywords.Sheet_Header, StringComparison.OrdinalIgnoreCase))
                  ?? wb.Worksheets.First();
            
            var used = ws.RangeUsed();
            if (used is null) return marks;

            // Find TestCase and Mark columns
            var headerRow = ws.Row(1);
            int? testCaseCol = null, markCol = null;
            
            foreach (var cell in headerRow.CellsUsed())
            {
                var colName = cell.GetString().Trim();
                if (string.Equals(colName, "TestCase", StringComparison.OrdinalIgnoreCase))
                    testCaseCol = cell.Address.ColumnNumber;
                else if (string.Equals(colName, "Mark", StringComparison.OrdinalIgnoreCase))
                    markCol = cell.Address.ColumnNumber;
            }

            if (testCaseCol.HasValue && markCol.HasValue)
            {
                foreach (var row in used.Rows().Skip(1))
                {
                    var testCase = row.Cell(testCaseCol.Value).GetString().Trim();
                    var markStr = row.Cell(markCol.Value).GetString().Trim();
                    
                    if (!string.IsNullOrWhiteSpace(testCase) && double.TryParse(markStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mark))
                    {
                        marks[testCase] = mark;
                    }
                }
            }
        }
        catch { }
        return marks;
    }

    private static System.Collections.Generic.IReadOnlyList<TestCaseDefinition> ReadCasesFromDirectory(string root, Dictionary<string, double> marksMap)
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

            // Get mark from marksMap, default to 0 if not found
            var mark = marksMap.TryGetValue(name, out var m) ? m : 0;

            list.Add(new TestCaseDefinition
            {
                Name = name,
                Mark = mark,
                DirectoryPath = dir,
                DetailPath = detail,
                InnerHeaderPath = null
            });
        }
        if (list.Count == 0) throw new InvalidDataException("No test cases found in suite root.");
        return list;
    }
}
