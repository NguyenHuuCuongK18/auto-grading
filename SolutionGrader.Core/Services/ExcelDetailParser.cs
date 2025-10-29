namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ExcelDetailParser : ITestCaseParser
{
    public IReadOnlyList<Step> ParseDetail(string xlsxPath, string questionCode)
    {
        using var wb = new XLWorkbook(xlsxPath);
        var steps = new List<Step>();

        void ReadSheet(string name, Action<IXLWorksheet> parse)
        {
            var w = wb.Worksheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (w != null && w.RangeUsed() != null) parse(w);
        }

        // InputClients
        ReadSheet(SuiteKeywords.Sheet_InputClients, ws =>
        {
            var map = Header(ws);
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_IC_Stage);
                var input = Get(row, map, SuiteKeywords.Col_IC_Input);
                var dataType = Get(row, map, SuiteKeywords.Col_IC_DataType);
                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                if (!string.IsNullOrWhiteSpace(input))
                {
                    steps.Add(new Step
                    {
                        Id = $"IC-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = input
                    });
                }
            }
        });

        // OutputClients
        ReadSheet(SuiteKeywords.Sheet_OutputClients, ws =>
        {
            var map = Header(ws);
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_OC_Stage);
                var method = Get(row, map, SuiteKeywords.Col_OC_Method);
                var status = Get(row, map, SuiteKeywords.Col_OC_StatusCode);
                var dataResponse = Get(row, map, SuiteKeywords.Col_OC_DataResponse);
                var output = Get(row, map, SuiteKeywords.Col_OC_Output);
                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                if (!string.IsNullOrWhiteSpace(method) && !string.IsNullOrWhiteSpace(output) && output.StartsWith("/"))
                {
                    steps.Add(new Step
                    {
                        Id = $"OC-HTTP-{stage}",
                        QuestionCode = qcode,
                        Stage = "VERIFY",
                        Action = ActionKeywords.HttpRequest,
                        // METHOD|URL|STATUS|EXPECTED_BODY (last two optional)
                        Value = $"{method}|http://127.0.0.1:5000{output}|{status}|{dataResponse}"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(output))
                {
                    steps.Add(new Step
                    {
                        Id = $"OC-CMP-{stage}",
                        QuestionCode = qcode,
                        Stage = "VERIFY",
                        Action = ActionKeywords.CompareText,
                        Target = string.Format(FileKeywords.PathTemplate_ExpectedClient, qcode, stage),
                        Value = string.Format(FileKeywords.PathTemplate_ActualClient, qcode, stage)
                    });
                }
            }
        });

        // OutputServers
        ReadSheet(SuiteKeywords.Sheet_OutputServers, ws =>
        {
            var map = Header(ws);
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_OS_Stage);
                var method = Get(row, map, SuiteKeywords.Col_OS_Method);
                var req = Get(row, map, SuiteKeywords.Col_OS_DataRequest);
                var output = Get(row, map, SuiteKeywords.Col_OS_Output);
                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    steps.Add(new Step
                    {
                        Id = $"OS-CMP-{stage}",
                        QuestionCode = qcode,
                        Stage = "VERIFY",
                        Action = ActionKeywords.CompareText,
                        Target = string.Format(FileKeywords.PathTemplate_ExpectedServer, qcode, stage),
                        Value = string.Format(FileKeywords.PathTemplate_ActualServer, qcode, stage)
                    });
                }
            }
        });

        return steps;
    }

    private static Dictionary<string, int> Header(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var last = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (int c = 1; c <= last; c++)
        {
            var k = ws.Cell(1, c).GetString();
            if (!string.IsNullOrWhiteSpace(k)) map[k] = c;
        }
        return map;
    }

    private static string Get(IXLRangeRow row, Dictionary<string, int> map, string key)
        => map.TryGetValue(key, out var c) ? row.Cell(c).GetString() : "";
}
