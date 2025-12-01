namespace SolutionGrader.Core.Services;

using ClosedXML.Excel;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Parser for the new detail.xlsx format with User/Client/Server/Network sheets.
/// 
/// Key Features:
/// - Parses User sheet for actions (StartClient, StartServer, Input, CloseClient, CloseServer)
/// - Parses Client/Server sheets for expected console output validation
/// - Parses Network sheet for expected TCP/HTTP network flow validation
/// 
/// Network Sheet Parsing:
/// - Each row in the Network sheet represents one expected network packet
/// - Network row indices are tracked PER STAGE, not globally
/// - This ensures that when validating, packet index 1 of stage 3 maps to the first
///   captured packet for stage 3, not the first packet globally
/// - Supports TCP (SYN, SYN-ACK, ACK, PSH-ACK, FIN-ACK, RST) and HTTP validation
/// </summary>
public sealed class NewFormatDetailParser
{
    public static List<Step> ParseDetail(XLWorkbook wb, string questionCode)
    {
        var steps = new List<Step>();

        // Parse User sheet for actions and inputs
        var userSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals(SuiteKeywords.Sheet_User, StringComparison.OrdinalIgnoreCase));
        var clientSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals(SuiteKeywords.Sheet_Client, StringComparison.OrdinalIgnoreCase));
        var serverSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals(SuiteKeywords.Sheet_Server, StringComparison.OrdinalIgnoreCase));
        var networkSheet = wb.Worksheets.FirstOrDefault(s => s.Name.Equals(SuiteKeywords.Sheet_Network, StringComparison.OrdinalIgnoreCase));

        // Track whether client and server have been started to inject middleware after both are running
        bool clientStarted = false;
        bool serverStarted = false;
        bool middlewareStarted = false;

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
                    clientStarted = true;
                    
                    // Start middleware after BOTH client and server are started
                    // This ensures proper exception handling when connections are attempted
                    if (serverStarted && !middlewareStarted)
                    {
                        steps.Add(new Step
                        {
                            Id = $"USER-PROXY-{stage}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.TcpRelay,
                            Value = null,
                            DataType = null
                        });
                        
                        // Add wait for middleware to initialize
                        steps.Add(new Step
                        {
                            Id = $"USER-MIDDLEWAIT-{stage}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.Wait,
                            Value = "500", // Wait for middleware to be ready
                            DataType = null
                        });
                        
                        middlewareStarted = true;
                    }
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
                    serverStarted = true;
                    
                    // Start middleware after BOTH client and server are started
                    // This ensures proper exception handling when connections are attempted
                    if (clientStarted && !middlewareStarted)
                    {
                        steps.Add(new Step
                        {
                            Id = $"USER-PROXY-{stage}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.TcpRelay,
                            Value = null,
                            DataType = null
                        });
                        
                        // Add wait for middleware to initialize
                        steps.Add(new Step
                        {
                            Id = $"USER-MIDDLEWAIT-{stage}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.Wait,
                            Value = "500", // Wait for middleware to be ready
                            DataType = null
                        });
                        
                        middlewareStarted = true;
                    }
                    
                    // Add wait for processes to initialize
                    steps.Add(new Step
                    {
                        Id = $"USER-WAIT-{stage}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = ActionKeywords.Wait,
                        Value = "1000", // Increased to 2 seconds for better output capture
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
                    
                    // Mark client as stopped - if it restarts, middleware can be re-injected
                    clientStarted = false;
                    middlewareStarted = false; // Allow middleware to be re-injected if both processes start again
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
                    
                    // Mark server as stopped - if it restarts, middleware can be re-injected
                    serverStarted = false;
                    middlewareStarted = false; // Allow middleware to be re-injected if both processes start again
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
                        Value = "1000", // Increased to 2 seconds for better output capture
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

        // Parse Network sheet for expected network flow (ALL rows, not just PSH packets)
        // The test kit creator intentionally includes/excludes rows to control what should be graded.
        // If a row exists in the test kit, it MUST be validated against captured network traffic.
        // 
        // NEW TEST KIT FORMAT columns:
        // - Stage: Stage number (matches User sheet)
        // - Time: Timestamp (excluded from grading - varies each run)
        // - Info: Protocol type (TCP, HTTP)
        // - Source: Source IP:Port (excluded from grading - port varies)
        // - Destination: Destination IP:Port (excluded from grading - port varies)
        // - Flags: TCP flags (SYN, SYN-ACK, ACK, PSH-ACK, FIN-ACK) - GRADED
        // - State: Connection state description - GRADED
        // - Data: TCP payload data (only for PSH packets) - GRADED
        // - SourceRole: Client or Server - GRADED
        // - DestinationRole: Client or Server - GRADED
        //
        // HTTP-specific columns (optional):
        // - URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody
        if (networkSheet != null && networkSheet.RangeUsed() != null)
        {
            var map = Header(networkSheet);
            
            // Track network row index PER STAGE, not globally
            // This ensures that when validating, the index correctly maps to captured packets for each stage
            var stageRowIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var row in networkSheet.RangeUsed()!.Rows().Skip(1))
            {
                var stage = Get(row, map, NetworkKeywords.Col_Stage);
                
                // Skip rows without stage
                if (string.IsNullOrWhiteSpace(stage)) continue;
                
                // Get or initialize the per-stage row index counter
                if (!stageRowIndexMap.TryGetValue(stage, out var currentIndex))
                {
                    currentIndex = 0;
                }
                currentIndex++;
                stageRowIndexMap[stage] = currentIndex;
                
                // Use per-stage index for network row matching
                var networkRowIndex = currentIndex;
                
                var info = Get(row, map, NetworkKeywords.Col_Info);
                var flags = Get(row, map, NetworkKeywords.Col_Flags);
                var state = Get(row, map, NetworkKeywords.Col_State);
                var sourceRole = Get(row, map, NetworkKeywords.Col_SourceRole);
                var destRole = Get(row, map, NetworkKeywords.Col_DestinationRole);
                var tcpData = Get(row, map, NetworkKeywords.Col_Data);
                
                // HTTP-specific columns
                var httpBody = Get(row, map, NetworkKeywords.Col_HttpBody);
                var method = Get(row, map, NetworkKeywords.Col_Method);
                var status = Get(row, map, NetworkKeywords.Col_Status);
                var uri = Get(row, map, NetworkKeywords.Col_URI);
                
                // Determine if this is a request or response based on source role
                bool isRequest = sourceRole.Equals(NetworkKeywords.Role_Client, StringComparison.OrdinalIgnoreCase);
                bool isResponse = sourceRole.Equals(NetworkKeywords.Role_Server, StringComparison.OrdinalIgnoreCase);
                
                // IMPORTANT: Create validation steps for EVERY row in the test kit
                // The test kit creator controls what to grade by including/excluding rows
                
                // Create a network flow validation step for this row
                // This validates Flags, State, SourceRole, DestinationRole
                // Time and Source/Destination are NOT graded (they vary)
                var stepId = $"NETWORK-FLOW-{stage}-{networkRowIndex}";
                var validationMetadata = new Dictionary<string, object>
                {
                    [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_NetworkFlow,
                    ["NetworkRowIndex"] = networkRowIndex,
                    ["ExpectedFlags"] = flags,
                    ["ExpectedState"] = state,
                    ["ExpectedSourceRole"] = sourceRole,
                    ["ExpectedDestRole"] = destRole
                };
                
                // Add network flow step (validates flags, state, roles)
                steps.Add(new Step
                {
                    Id = stepId,
                    QuestionCode = questionCode,
                    Stage = stage,
                    Action = ActionKeywords.CompareNetworkFlow,
                    Target = null, // No target - validation uses metadata
                    TcpFlags = flags,
                    ConnectionState = state,
                    SourceRole = sourceRole,
                    DestinationRole = destRole,
                    NetworkRowIndex = networkRowIndex,
                    DataType = "TCP",
                    Metadata = validationMetadata
                });
                
                // For HTTP protocol, also add HTTP-specific validations
                if (info.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
                {
                    // Validate HTTP Method for requests (missing = ignore)
                    if (isRequest && !string.IsNullOrWhiteSpace(method))
                    {
                        steps.Add(new Step
                        {
                            Id = $"NETWORK-METHOD-{stage}-{networkRowIndex}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.CompareText,
                            Target = method,
                            HttpMethod = method,
                            NetworkRowIndex = networkRowIndex,
                            DataType = "TEXT",
                            Metadata = new Dictionary<string, object> 
                            { 
                                [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_HttpMethod,
                                ["NetworkRowIndex"] = networkRowIndex
                            }
                        });
                    }
                    
                    // Validate HTTP Status for responses (missing = ignore)
                    if (isResponse && !string.IsNullOrWhiteSpace(status))
                    {
                        steps.Add(new Step
                        {
                            Id = $"NETWORK-STATUS-{stage}-{networkRowIndex}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = ActionKeywords.CompareText,
                            Target = status,
                            StatusCode = status,
                            NetworkRowIndex = networkRowIndex,
                            DataType = "TEXT",
                            Metadata = new Dictionary<string, object> 
                            { 
                                [GradingKeywords.MetadataKey_ValidationType] = GradingKeywords.Validation_StatusCode,
                                ["NetworkRowIndex"] = networkRowIndex
                            }
                        });
                    }
                    
                    // Validate HTTP Body/Payload (missing = ignore)
                    if (!string.IsNullOrWhiteSpace(httpBody))
                    {
                        var validationType = isRequest ? GradingKeywords.Validation_DataRequest : GradingKeywords.Validation_DataResponse;
                        var stepIdPrefix = isRequest ? "NETWORK-REQPAYLOAD" : "NETWORK-RESPAYLOAD";
                        var action = IsJson(httpBody) ? ActionKeywords.CompareJson : ActionKeywords.CompareText;
                        
                        steps.Add(new Step
                        {
                            Id = $"{stepIdPrefix}-{stage}-{networkRowIndex}",
                            QuestionCode = questionCode,
                            Stage = stage,
                            Action = action,
                            Target = httpBody,
                            NetworkRowIndex = networkRowIndex,
                            DataType = IsJson(httpBody) ? "JSON" : "TEXT",
                            Metadata = new Dictionary<string, object> 
                            { 
                                [GradingKeywords.MetadataKey_ValidationType] = validationType,
                                ["NetworkRowIndex"] = networkRowIndex
                            }
                        });
                    }
                }
                // For TCP protocol with PSH flag (data transfer)
                else if (flags.Contains("PSH", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tcpData))
                {
                    var validationType = isRequest ? GradingKeywords.Validation_DataRequest : GradingKeywords.Validation_DataResponse;
                    var stepIdPrefix = isRequest ? "NETWORK-REQPAYLOAD" : "NETWORK-RESPAYLOAD";
                    var action = IsXml(tcpData) ? ActionKeywords.CompareText : 
                                 (IsJson(tcpData) ? ActionKeywords.CompareJson : ActionKeywords.CompareText);
                    
                    steps.Add(new Step
                    {
                        Id = $"{stepIdPrefix}-{stage}-{networkRowIndex}",
                        QuestionCode = questionCode,
                        Stage = stage,
                        Action = action,
                        Target = tcpData,
                        NetworkRowIndex = networkRowIndex,
                        DataType = IsXml(tcpData) ? "XML" : (IsJson(tcpData) ? "JSON" : "TEXT"),
                        Metadata = new Dictionary<string, object> 
                        { 
                            [GradingKeywords.MetadataKey_ValidationType] = validationType,
                            ["NetworkRowIndex"] = networkRowIndex
                        }
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

    /// <summary>
    /// Gets a cell value from a row, with special handling for Input columns.
    /// For Input columns, preserve leading/trailing spaces to allow testing space handling.
    /// For other columns, trim spaces as before for consistency.
    /// </summary>
    private static string Get(IXLRangeRow row, Dictionary<string, int> map, string key)
    {
        if (!map.TryGetValue(key, out var c)) return "";
        
        var value = row.Cell(c).GetString();
        
        // Preserve spaces for Input column to allow testing with space inputs like " A ^ B" or " "
        // This is critical for validating input handling in test cases
        // Using the same constant as ExcelDetailParser for consistency
        if (string.Equals(key, "Input", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        
        // For all other columns, trim as before for consistency
        return value.Trim();
    }
}
