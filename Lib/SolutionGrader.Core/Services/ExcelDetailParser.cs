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
            bool isFirstRow = true;
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_IC_Stage);
                var input = Get(row, map, SuiteKeywords.Col_IC_Input);
                var dataType = Get(row, map, SuiteKeywords.Col_IC_DataType);
                var action = Get(row, map, SuiteKeywords.Col_IC_Action);
                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                // Skip completely empty rows
                if (string.IsNullOrWhiteSpace(stage) && string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(action))
                    continue;

                // First step with "Connect" action needs to start processes
                if (isFirstRow && string.Equals(action, "Connect", StringComparison.OrdinalIgnoreCase))
                {
                    isFirstRow = false;
                    
                    // Add server start step
                    steps.Add(new Step
                    {
                        Id = $"IC-SERVER-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ServerStart,
                        Value = null
                    });

                    // Add middleware/proxy step
                    steps.Add(new Step
                    {
                        Id = $"IC-PROXY-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.TcpRelay,
                        Value = null
                    });

                    // Add client start step
                    steps.Add(new Step
                    {
                        Id = $"IC-CLIENT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ClientStart,
                        Value = null
                    });

                    // Add wait for processes to initialize
                    steps.Add(new Step
                    {
                        Id = $"IC-WAIT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "1000"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(input))
                {
                    isFirstRow = false;
                    
                    // For other steps with input, send the input to client
                    steps.Add(new Step
                    {
                        Id = $"IC-INPUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ClientInput,
                        Value = input
                    });
                    
                    // Add a wait after input to let it process (increased for HTTP requests)
                    steps.Add(new Step
                    {
                        Id = $"IC-WAIT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "5000"
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
                if (string.IsNullOrWhiteSpace(stage)) continue;

                var dataResponse = Get(row, map, SuiteKeywords.Col_OC_DataResponse);
                var output = Get(row, map, SuiteKeywords.Col_OC_Output);
                var dataType = Get(row, map, SuiteKeywords.Col_OC_DataTypeMiddleware);

                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                // Test execution relies solely on the Excel-provided payload instead of separate expected files.

                if (!string.IsNullOrWhiteSpace(dataResponse))
                {
                    var action = string.Equals(dataType, "JSON", StringComparison.OrdinalIgnoreCase)
                        ? ActionKeywords.CompareJson
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"OC-DATA-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = action,
                        Target = dataResponse,
                    });
                }
                if (!string.IsNullOrWhiteSpace(output))
                {
                    steps.Add(new Step
                    {
                        Id = $"OC-OUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = output,
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
                if (string.IsNullOrWhiteSpace(stage)) continue;
                var req = Get(row, map, SuiteKeywords.Col_OS_DataRequest);
                var output = Get(row, map, SuiteKeywords.Col_OS_Output);
                var dataType = Get(row, map, SuiteKeywords.Col_OS_DataTypeMiddleware);

                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;


                if (!string.IsNullOrWhiteSpace(req))
                {
                    steps.Add(new Step
                    {
                        Id = $"OS-REQ-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = req,
                    });
                }

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var action = string.Equals(dataType, "JSON", StringComparison.OrdinalIgnoreCase)
                        ? ActionKeywords.CompareJson
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"OS-OUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = action,
                        Target = output
                    });
                };
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
        => map.TryGetValue(key, out var c) ? row.Cell(c).GetString().Trim() : "";
}
