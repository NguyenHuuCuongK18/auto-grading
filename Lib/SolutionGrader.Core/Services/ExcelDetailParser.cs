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

        // Check if this is the new format (User/Client/Server/Network sheets) or old format (InputClients/OutputClients)
        // NEW format: Uses User, Client, Server, Network sheets
        // OLD format: Uses InputClients/OutputClients/OutputServers (or singular variations)
        bool hasNewFormat = wb.Worksheets.Any(s => s.Name.Equals("User", StringComparison.OrdinalIgnoreCase));

        if (hasNewFormat)
        {
            // Use new format parser
            return NewFormatDetailParser.ParseDetail(wb, questionCode);
        }

        // Old format parsing below
        // The OLD format has two variations:
        // 1. Plural: InputClients, OutputClients, OutputServers
        // 2. Singular: InputClient, OutputClient, OutputServer
        // This helper tries both variations for backward compatibility
        void ReadSheetFlexible(string primaryName, string alternateName, Action<IXLWorksheet> parse)
        {
            var w = wb.Worksheets.FirstOrDefault(s => 
                string.Equals(s.Name, primaryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, alternateName, StringComparison.OrdinalIgnoreCase));
            if (w != null && w.RangeUsed() != null) parse(w);
        }

        // InputClients - try both "InputClient" (singular variation) and "InputClients" (plural variation)
        ReadSheetFlexible(SuiteKeywords.Sheet_InputClient, SuiteKeywords.Sheet_InputClients, ws =>
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

                // First step with "Connect" (old format) or "Start" (new format) action needs to start processes
                if (isFirstRow && (string.Equals(action, ActionKeywords.Connect, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(action, ActionKeywords.Start, StringComparison.OrdinalIgnoreCase)))
                {
                    isFirstRow = false;
                    
                    // Add server start step
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}SERVER-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ServerStart,
                        Value = null,
                        DataType = dataType
                    });

                    // Add middleware/proxy step after server starts
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}PROXY-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.TcpRelay,
                        Value = null,
                        DataType = dataType
                    });
                    
                    // Add wait for middleware to initialize before starting client
                    // This ensures middleware is ready to intercept connections
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}MIDDLEWAIT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "1000", // Increased to 1 second for middleware to be fully ready
                        DataType = dataType
                    });

                    // Add client start step
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}CLIENT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ClientStart,
                        Value = null,
                        DataType = dataType
                    });

                    // Add wait for processes to initialize
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}WAIT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "2000", // Increased to 2 seconds for better output capture
                        DataType = dataType
                    });
                }
                else if (!string.IsNullOrWhiteSpace(input))
                {
                    isFirstRow = false;
                    
                    // For other steps with input, send the input to client
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}INPUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.ClientInput,
                        Value = input,
                        DataType = dataType
                    });
                    
                    // Add a wait after input to let it process (increased for HTTP requests)
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_InputClient}WAIT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "2000", // Increased to 2 seconds for better output capture
                        DataType = dataType
                    });
                }
            }
        });

        // OutputClients - try both "OutputClient" (singular variation) and "OutputClients" (plural variation)
        ReadSheetFlexible(SuiteKeywords.Sheet_OutputClient, SuiteKeywords.Sheet_OutputClients, ws =>
        {
            var map = Header(ws);
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_OC_Stage);
                if (string.IsNullOrWhiteSpace(stage)) continue;

                var method = Get(row, map, SuiteKeywords.Col_OC_Method);
                var dataResponse = Get(row, map, SuiteKeywords.Col_OC_DataResponse);
                var statusCode = Get(row, map, SuiteKeywords.Col_OC_StatusCode);
                var output = Get(row, map, SuiteKeywords.Col_OC_Output);
                var dataType = Get(row, map, SuiteKeywords.Col_OC_DataTypeMiddleware);
                var byteSizeStr = Get(row, map, SuiteKeywords.Col_OC_ByteSize);

                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                int? byteSize = null;
                if (!string.IsNullOrWhiteSpace(byteSizeStr) && double.TryParse(byteSizeStr, out var bs))
                    byteSize = (int)bs;

                // Test execution relies solely on the Excel-provided payload instead of separate expected files.
                
                // Validate HTTP Method if provided
                if (!string.IsNullOrWhiteSpace(method))
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputClient}METHOD-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = method,
                        HttpMethod = method,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_HttpMethod }
                    });
                }

                // Validate Status Code if provided
                if (!string.IsNullOrWhiteSpace(statusCode))
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputClient}STATUS-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = statusCode,
                        StatusCode = statusCode,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_StatusCode }
                    });
                }

                // Validate Data Response if provided
                if (!string.IsNullOrWhiteSpace(dataResponse))
                {
                    var action = string.Equals(dataType, GradingKeywords.DataType_JSON, StringComparison.OrdinalIgnoreCase)
                        ? ActionKeywords.CompareJson
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputClient}DATA-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = action,
                        Target = dataResponse,
                        DataType = dataType,
                        ByteSize = byteSize,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_DataResponse }
                    });
                }
                
                // Validate Byte Size if provided
                if (byteSize.HasValue)
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputClient}SIZE-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = byteSizeStr,
                        ByteSize = byteSize,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ByteSize }
                    });
                }
                
                // Validate Client Output if provided
                if (!string.IsNullOrWhiteSpace(output))
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputClient}OUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = output,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ClientOutput }
                    });
                }
            }
        });

        // OutputServers - try both "OutputServer" (singular variation) and "OutputServers" (plural variation)
        ReadSheetFlexible(SuiteKeywords.Sheet_OutputServer, SuiteKeywords.Sheet_OutputServers, ws =>
        {
            var map = Header(ws);
            foreach (var row in ws.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, SuiteKeywords.Col_OS_Stage);
                if (string.IsNullOrWhiteSpace(stage)) continue;
                
                var method = Get(row, map, SuiteKeywords.Col_OS_Method);
                var req = Get(row, map, SuiteKeywords.Col_OS_DataRequest);
                var output = Get(row, map, SuiteKeywords.Col_OS_Output);
                var dataType = Get(row, map, SuiteKeywords.Col_OS_DataTypeMiddleware);
                var byteSizeStr = Get(row, map, SuiteKeywords.Col_OS_ByteSize);

                var qid = Get(row, map, SuiteKeywords.Col_Generic_QuestionId);
                var qcode = string.IsNullOrWhiteSpace(qid) ? questionCode : qid;

                int? byteSize = null;
                if (!string.IsNullOrWhiteSpace(byteSizeStr) && double.TryParse(byteSizeStr, out var bs))
                    byteSize = (int)bs;

                // Validate HTTP Method if provided
                if (!string.IsNullOrWhiteSpace(method))
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputServer}METHOD-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = method,
                        HttpMethod = method,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_HttpMethod }
                    });
                }

                // Validate Data Request if provided
                if (!string.IsNullOrWhiteSpace(req))
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputServer}REQ-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = req,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_DataRequest }
                    });
                }
                
                // Validate Byte Size if provided
                if (byteSize.HasValue)
                {
                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputServer}SIZE-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = byteSizeStr,
                        ByteSize = byteSize,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ByteSize }
                    });
                }

                // Validate Server Output if provided
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var action = string.Equals(dataType, GradingKeywords.DataType_JSON, StringComparison.OrdinalIgnoreCase)
                        ? ActionKeywords.CompareJson
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"{GradingKeywords.StepPrefix_OutputServer}OUT-{stage}",
                        QuestionCode = qcode,
                        Stage = stage,
                        Action = action,
                        Target = output,
                        DataType = dataType,
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ServerOutput }
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
