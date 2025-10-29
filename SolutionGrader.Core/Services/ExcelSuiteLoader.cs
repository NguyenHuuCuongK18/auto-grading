namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using System.Globalization;
using System.IO;

public sealed class ExcelSuiteLoader : ITestSuiteLoader
{
    public SuiteDefinition Load(string suitePathOrHeaderXlsx)
    {
        var headerPath = ResolveHeaderPath(suitePathOrHeaderXlsx);
        var protocol = ReadProtocolFromHeader(headerPath);
        var marks = ReadMarksFromHeader(headerPath);
        var cases = BuildCasesFromDirectory(Path.GetDirectoryName(headerPath)!, marks);
        return new SuiteDefinition
        {
            HeaderPath = headerPath,
            Protocol = protocol,
            Cases = cases
        };
    }

    private static string ResolveHeaderPath(string input)
    {
        if (File.Exists(input) && Path.GetFileName(input).Equals("Header.xlsx", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(input);

        if (Directory.Exists(input))
        {
            var candidate = Path.Combine(input, "Header.xlsx");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException("Could not find Header.xlsx from: " + input);
    }

    private static string ReadProtocolFromHeader(string headerPath)
    {
        try
        {
            using var wb = new XLWorkbook(headerPath);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Header", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheet(1);

            // Look for a cell in the first column named "Type" or "Protocol"
            for (int r = 1; r <= Math.Min(50, ws.RowCount()); r++)
            {
                var key = ws.Cell(r, 1).GetString().Trim();
                if (key.Equals("Type", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Protocol", StringComparison.OrdinalIgnoreCase))
                {
                    var val = ws.Cell(r, 2).GetString().Trim();
                    if (!string.IsNullOrEmpty(val)) return val.ToUpperInvariant();
                }
            }
        }
        catch { }

        return "HTTP"; // default
    }

    private static Dictionary<string, double> ReadMarksFromHeader(string headerPath)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        using var wb = new XLWorkbook(headerPath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Header", StringComparison.OrdinalIgnoreCase))
                 ?? wb.Worksheet(1);

        // Find the row that contains "TestCase" and "Mark" headers
        int headerRow = -1, tcCol = -1, markCol = -1;
        for (int r = 1; r <= Math.Min(100, ws.RowCount()); r++)
        {
            var row = ws.Row(r);
            var cells = row.CellsUsed().ToList();
            if (cells.Count == 0) continue;

            for (int c = 1; c <= Math.Min(50, ws.ColumnCount()); c++)
            {
                var text = ws.Cell(r, c).GetString().Trim();
                if (text.Equals("TestCase", StringComparison.OrdinalIgnoreCase)) tcCol = c;
                if (text.Equals("Mark", StringComparison.OrdinalIgnoreCase)) markCol = c;
            }

            if (tcCol > 0 && markCol > 0) { headerRow = r; break; }
            tcCol = markCol = -1;
        }

        if (headerRow < 0) return result; // none found; marks default to 0

        // Read until a blank TestCase cell
        for (int r = headerRow + 1; r <= ws.RowCount(); r++)
        {
            var tc = ws.Cell(r, tcCol).GetString().Trim();
            if (string.IsNullOrEmpty(tc)) break;

            var markStr = ws.Cell(r, markCol).GetString().Trim();
            if (!double.TryParse(markStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mark))
                mark = 0;

            result[tc] = mark;
        }

        return result;
    }

    private static IReadOnlyList<TestCaseDefinition> BuildCasesFromDirectory(string root, Dictionary<string, double> marks)
    {
        var list = new List<TestCaseDefinition>();

        foreach (var dir in Directory.EnumerateDirectories(root)
                     .Where(p => !Path.GetFileName(p).Equals("mismatches", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(dir);
            var detail = Path.Combine(dir, "Detail.xlsx");
            if (!File.Exists(detail)) continue;

            marks.TryGetValue(name, out var mark);
            list.Add(new TestCaseDefinition
            {
                Name = name,
                Mark = mark,
                DirectoryPath = dir,
                DetailPath = detail
            });
        }

        if (list.Count == 0)
            throw new InvalidDataException("No test cases found under: " + root);

        return list;
    }
}
