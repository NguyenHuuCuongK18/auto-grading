namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Parser for the new detail.xlsx format with User/Client/Server/Network sheets
/// </summary>
public sealed class NewFormatDetailParser
{
    public static List<Step> ParseDetail(XLWorkbook wb, string questionCode)
    {
        var steps = new List<Step>();

        // Parse User sheet for actions and inputs
        var userSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals("User", StringComparison.OrdinalIgnoreCase));
        var clientSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals("Client", StringComparison.OrdinalIgnoreCase));
        var serverSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals("Server", StringComparison.OrdinalIgnoreCase));
        var networkSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals("Network", StringComparison.OrdinalIgnoreCase));

        // Parse User sheet to determine process start/stop and input actions
        if (userSheet != null && userSheet.RangeUsed() != null)
        {
            var map = Header(userSheet);
            foreach (var row in userSheet.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, "Stage");
                var input = Get(row, map, "Input");
                var action = Get(row, map, "Action");

                if (string.IsNullOrWhiteSpace(stage)) continue;

                // Handle different action types
                if (action.Equals("StartClient", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step
                    {
                        Id = $"USER-STARTCLIENT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.ClientStart,
                        Value = null,
                        DataType = null
                    });
                }
                else if (action.Equals("StartServer", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step
                    {
                        Id = $"USER-STARTSERVER-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.ServerStart,
                        Value = null,
                        DataType = null
                    });
                    
                    // Add middleware/proxy step after server start
                    steps.Add(new Step
                    {
                        Id = $"USER-PROXY-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.TcpRelay,
                        Value = null,
                        DataType = null
                    });
                    
                    // Add wait for processes to initialize
                    steps.Add(new Step
                    {
                        Id = $"USER-WAIT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "2000", // Increased to 2 seconds for better output capture
                        DataType = null
                    });
                }
                else if (action.Equals("CloseClient", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step
                    {
                        Id = $"USER-CLOSECLIENT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.ClientClose,
                        Value = null,
                        DataType = null
                    });
                }
                else if (action.Equals("CloseServer", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new Step
                    {
                        Id = $"USER-CLOSESERVER-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.ServerClose,
                        Value = null,
                        DataType = null
                    });
                }
                else if (action.Equals("Input", StringComparison.OrdinalIgnoreCase))
                {
                    // Send input to client (including empty input for validation testing)
                    steps.Add(new Step
                    {
                        Id = $"USER-INPUT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.ClientInput,
                        Value = input ?? string.Empty, // Allow empty input
                        DataType = null
                    });
                    
                    // Add a wait after input
                    steps.Add(new Step
                    {
                        Id = $"USER-WAIT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "2000", // Increased to 2 seconds for better output capture
                        DataType = null
                    });
                }
            }
        }

        // Parse Client sheet for expected console output (if missing, ignore)
        if (clientSheet != null && clientSheet.RangeUsed() != null)
        {
            var map = Header(clientSheet);
            foreach (var row in clientSheet.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, "Stage");
                var console = Get(row, map, "Console");

                // Missing stage or console means ignore
                if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(console)) continue;

                steps.Add(new Step
                {
                    Id = $"CLIENT-CONSOLE-{stage}",
                    QuestionCode = questionCode,
                    Stage = stage,
                    Action = ActionKeywords.CompareText,
                    Target = console,
                    DataType = "TEXT",
                    Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ClientOutput }
                });
            }
        }

        // Parse Server sheet for expected console output (if missing, ignore)
        if (serverSheet != null && serverSheet.RangeUsed() != null)
        {
            var map = Header(serverSheet);
            foreach (var row in serverSheet.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, "Stage");
                var console = Get(row, map, "Console");

                // Missing stage or console means ignore
                if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(console)) continue;

                steps.Add(new Step
                {
                    Id = $"SERVER-CONSOLE-{stage}",
                    QuestionCode = questionCode,
                    Stage = stage,
                    Action = ActionKeywords.CompareText,
                    Target = console,
                    DataType = "TEXT",
                    Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_ServerOutput }
                });
            }
        }

        // Parse Network sheet for expected network payloads (if missing, ignore)
        if (networkSheet != null && networkSheet.RangeUsed() != null)
        {
            var map = Header(networkSheet);
            foreach (var row in networkSheet.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, "Stage");
                var url = Get(row, map, "Url");
                var httpMethod = Get(row, map, "HTTP_Method");
                var reqPayload = Get(row, map, "REQ_Payload");
                var resPayload = Get(row, map, "RES_Payload");

                if (string.IsNullOrWhiteSpace(stage)) continue;

                // Validate HTTP Method if provided (missing = ignore)
                if (!string.IsNullOrWhiteSpace(httpMethod))
                {
                    steps.Add(new Step
                    {
                        Id = $"NETWORK-METHOD-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.CompareText,
                        Target = httpMethod,
                        HttpMethod = httpMethod,
                        DataType = "TEXT",
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_HttpMethod }
                    });
                }

                // Validate Request Payload if provided (missing = ignore)
                if (!string.IsNullOrWhiteSpace(reqPayload))
                {
                    var action = IsXml(reqPayload) || IsJson(reqPayload)
                        ? ActionKeywords.CompareText
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"NETWORK-REQPAYLOAD-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = action,
                        Target = reqPayload,
                        DataType = IsXml(reqPayload) ? "XML" : (IsJson(reqPayload) ? "JSON" : "TEXT"),
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_DataRequest }
                    });
                }

                // Validate Response Payload if provided (missing = ignore)
                if (!string.IsNullOrWhiteSpace(resPayload))
                {
                    var action = IsXml(resPayload) || IsJson(resPayload)
                        ? ActionKeywords.CompareText
                        : ActionKeywords.CompareText;

                    steps.Add(new Step
                    {
                        Id = $"NETWORK-RESPAYLOAD-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = action,
                        Target = resPayload,
                        DataType = IsXml(resPayload) ? "XML" : (IsJson(resPayload) ? "JSON" : "TEXT"),
                        Metadata = new Dictionary<string, object> { [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_DataResponse }
                    });
                }
            }
        }

        return steps;
    }

    private static bool IsXml(string text)
    {
        return text.TrimStart().StartsWith("<");
    }

    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{") || trimmed.StartsWith("[");
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
