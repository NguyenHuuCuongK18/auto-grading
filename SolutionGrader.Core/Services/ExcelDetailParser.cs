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
                var action = Get(row, map, SuiteKeywords.Col_IC_Action) ?? string.Empty;
                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                if (action.Equals("Connect", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step { Id = $"IC-SERVER-{stage}", QuestionCode = qcode, Stage = "SETUP", Action = ActionKeywords.ServerStart });
                    steps.Add(new Step { Id = $"IC-CLIENT-{stage}", QuestionCode = qcode, Stage = "SETUP", Action = ActionKeywords.ClientStart });
                }
                else if (action.Equals("Client Input", StringComparison.OrdinalIgnoreCase) || action.Equals("Input", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step { Id = $"IC-IN-{stage}", QuestionCode = qcode, Stage = "INPUT", Action = ActionKeywords.AssertText, Target = input, Value = input });
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
                        Value = $"{method}|http://127.0.0.1:5000{output}|{status}"
                    });
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    steps.Add(new Step
                    {
                        Id = $"OC-CMP-{stage}",
                        QuestionCode = qcode,
                        Stage = "VERIFY",
                        Action = ActionKeywords.CompareText,
                        Target = $"expected\\clients\\{qcode}\\{stage}.txt",
                        Value = $"actual\\clients\\{qcode}\\{stage}.txt"
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
                        Target = $"expected\\servers\\{qcode}\\{stage}.txt",
                        Value = $"actual\\servers\\{qcode}\\{stage}.txt"
                    });
                }
            }
        });

        // Cleanup
        steps.Add(new Step { Id = "CLEANUP-KILL", QuestionCode = questionCode, Stage = "CLEANUP", Action = ActionKeywords.KillAll });

        return steps;
    }

    private static Dictionary<string, int> Header(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = ws.FirstRowUsed();
        if (header == null) return map;
        foreach (var c in header.CellsUsed())
        {
            var name = c.GetString().Trim();
            if (!string.IsNullOrWhiteSpace(name)) map[name] = c.Address.ColumnNumber;
        }
        return map;
    }

    private static string? Get(IXLRangeRow row, Dictionary<string, int> map, string colName)
    {
        if (!map.TryGetValue(colName, out var col)) return null;
        return row.Cell(col).GetString();
    }
}
