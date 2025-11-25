namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords and constants used for grading and validation.
/// 
/// NEW FORMAT Step ID Structure:
/// - USER-{ACTION}-{STAGE} - User actions (STARTCLIENT, STARTSERVER, INPUT, WAIT, PROXY)
/// - CLIENT-{TYPE}-{STAGE} - Client sheet validations (CONSOLE)
/// - SERVER-{TYPE}-{STAGE} - Server sheet validations (CONSOLE)
/// - NETWORK-{TYPE}-{STAGE} - Network sheet validations (METHOD, STATUS, REQPAYLOAD, RESPAYLOAD, DATA)
/// 
/// Example step IDs:
/// - USER-STARTCLIENT-1, USER-INPUT-3
/// - CLIENT-CONSOLE-1, CLIENT-CONSOLE-3
/// - SERVER-CONSOLE-2, SERVER-CONSOLE-3
/// - NETWORK-METHOD-3, NETWORK-STATUS-3, NETWORK-RESPAYLOAD-3
/// </summary>
public static class GradingKeywords
{
    #region New Format - Step ID Prefixes
    
    /// <summary>User sheet prefix (actions like CLIENTSTART, SERVERSTART, CLIENT_INPUT)</summary>
    public const string StepPrefix_User    = "USER-";
    
    /// <summary>Client sheet prefix (console output validation)</summary>
    public const string StepPrefix_Client  = "CLIENT-";
    
    /// <summary>Server sheet prefix (console output validation)</summary>
    public const string StepPrefix_Server  = "SERVER-";
    
    /// <summary>Network sheet prefix (HTTP/TCP validation)</summary>
    public const string StepPrefix_Network = "NETWORK-";
    
    #endregion
    
    #region New Format - Validation Types
    
    /// <summary>Console output validation</summary>
    public const string Validation_Console = "CONSOLE";
    
    /// <summary>HTTP method validation (GET, POST, etc.)</summary>
    public const string Validation_Method = "METHOD";
    
    /// <summary>HTTP status code validation (200 OK, 404 Not Found)</summary>
    public const string Validation_Status = "STATUS";
    
    /// <summary>Network request payload validation</summary>
    public const string Validation_ReqPayload = "REQPAYLOAD";
    
    /// <summary>Network response payload validation</summary>
    public const string Validation_ResPayload = "RESPAYLOAD";
    
    /// <summary>TCP data payload validation</summary>
    public const string Validation_Data = "DATA";
    
    /// <summary>HTTP body validation</summary>
    public const string Validation_HttpBody = "HTTPBODY";
    
    /// <summary>HTTP URI validation</summary>
    public const string Validation_Uri = "URI";
    
    #endregion
    
    #region HTTP Methods
    
    public const string Method_GET     = "GET";
    public const string Method_POST    = "POST";
    public const string Method_PUT     = "PUT";
    public const string Method_DELETE  = "DELETE";
    public const string Method_PATCH   = "PATCH";
    public const string Method_HEAD    = "HEAD";
    public const string Method_OPTIONS = "OPTIONS";
    
    public static readonly string[] AllHttpMethods =
    [
        Method_GET,
        Method_POST,
        Method_PUT,
        Method_DELETE,
        Method_PATCH,
        Method_HEAD,
        Method_OPTIONS
    ];
    
    #endregion
    
    #region HTTP Status Codes
    
    public const string Status_OK                  = "OK";                  // 200
    public const string Status_Created             = "Created";             // 201
    public const string Status_NoContent           = "NoContent";           // 204
    public const string Status_BadRequest          = "BadRequest";          // 400
    public const string Status_Unauthorized        = "Unauthorized";        // 401
    public const string Status_Forbidden           = "Forbidden";           // 403
    public const string Status_NotFound            = "NotFound";            // 404
    public const string Status_InternalServerError = "InternalServerError"; // 500
    
    public const string StatusCategory_Success     = "2xx";
    public const string StatusCategory_Redirect    = "3xx";
    public const string StatusCategory_ClientError = "4xx";
    public const string StatusCategory_ServerError = "5xx";
    
    #endregion
    
    #region Result Values
    
    public const string Result_Pass    = "PASS";
    public const string Result_Fail    = "FAIL";
    public const string Result_Skip    = "SKIP";
    public const string Result_Ignored = "IGNORED";
    
    #endregion
    
    #region Data Types
    
    public const string DataType_JSON   = "JSON";
    public const string DataType_CSV    = "CSV";
    public const string DataType_Text   = "Text";
    public const string DataType_XML    = "XML";
    public const string DataType_Binary = "Binary";
    public const string DataType_Empty  = "Empty";
    
    public static readonly string[] AllDataTypes =
    [
        DataType_JSON,
        DataType_CSV,
        DataType_Text,
        DataType_XML,
        DataType_Binary,
        DataType_Empty
    ];
    
    #endregion
    
    #region Excel Sheet Names (Output/Grading Files)
    
    public const string Sheet_TestRunData       = "TestRunData";
    public const string Sheet_ErrorReport       = "ErrorReport";
    public const string Sheet_Summary           = "Summary";
    public const string Sheet_ValidationDetails = "ValidationDetails";
    public const string Sheet_FailedTests       = "FailedTests";
    
    #endregion
    
    #region Excel Column Names
    
    public const string Col_Stage           = "Stage";
    public const string Col_ValidationType  = "ValidationType";
    public const string Col_Expected        = "Expected";
    public const string Col_Actual          = "Actual";
    public const string Col_Result          = "Result";
    public const string Col_ErrorCode       = "ErrorCode";
    public const string Col_ErrorCategory   = "ErrorCategory";
    public const string Col_Message         = "Message";
    public const string Col_PointsAwarded   = "PointsAwarded";
    public const string Col_PointsPossible  = "PointsPossible";
    public const string Col_DurationMs      = "DurationMs";
    public const string Col_Timestamp       = "Timestamp";
    public const string Col_DetailPath      = "DetailPath";
    public const string Col_DiffIndex       = "DiffIndex";
    public const string Col_ExpectedOutput  = "ExpectedOutput";
    public const string Col_ActualOutput    = "ActualOutput";
    public const string Col_ExpectedExcerpt = "ExpectedExcerpt";
    public const string Col_ActualExcerpt   = "ActualExcerpt";
    public const string Col_StepId          = "StepId";
    public const string Col_HttpMethod      = "HttpMethod";
    public const string Col_StatusCode      = "StatusCode";
    public const string Col_ByteSize        = "ByteSize";
    public const string Col_PointsLost      = "PointsLost";
    public const string Col_ErrorNotes      = "ErrorNotes";
    public const string Col_FailedStages    = "FailedStages";
    
    #endregion
    
    #region Comparison Settings
    
    public const string CompareMode_Exact      = "EXACT";
    public const string CompareMode_Contains   = "CONTAINS";
    public const string CompareMode_Normalized = "NORMALIZED";
    public const string CompareMode_Loose      = "LOOSE";
    
    public const int ByteSizeTolerance = 10;
    public const double ByteSizeTolerancePercent = 0.05;
    
    #endregion
    
    #region Helper Methods
    
    /// <summary>
    /// Normalizes HTTP status code text to standard format.
    /// </summary>
    public static string NormalizeStatusCode(string? statusCode)
    {
        if (string.IsNullOrWhiteSpace(statusCode)) return Status_OK;

        var upper = statusCode.Trim().ToUpperInvariant();
        
        if (int.TryParse(upper, out int code))
        {
            return code switch
            {
                200 => Status_OK,
                201 => Status_Created,
                204 => Status_NoContent,
                400 => Status_BadRequest,
                401 => Status_Unauthorized,
                403 => Status_Forbidden,
                404 => Status_NotFound,
                500 => Status_InternalServerError,
                _ => upper
            };
        }

        if (upper.Contains("OK")) return Status_OK;
        if (upper.Contains("CREATED")) return Status_Created;
        if (upper.Contains("NOCONTENT") || upper.Contains("NO CONTENT")) return Status_NoContent;
        if (upper.Contains("BADREQUEST") || upper.Contains("BAD REQUEST")) return Status_BadRequest;
        if (upper.Contains("UNAUTHORIZED")) return Status_Unauthorized;
        if (upper.Contains("FORBIDDEN")) return Status_Forbidden;
        if (upper.Contains("NOTFOUND") || upper.Contains("NOT FOUND")) return Status_NotFound;
        if (upper.Contains("INTERNALSERVERERROR") || upper.Contains("INTERNAL SERVER ERROR")) return Status_InternalServerError;

        return statusCode.Trim();
    }

    /// <summary>
    /// Checks if a byte size is within acceptable tolerance.
    /// </summary>
    public static bool IsByteSizeWithinTolerance(int expected, int actual)
    {
        if (expected == actual) return true;
        if (expected == 0) return actual <= ByteSizeTolerance;
        
        var diff = Math.Abs(expected - actual);
        var percentDiff = (double)diff / expected;
        
        return diff <= ByteSizeTolerance || percentDiff <= ByteSizeTolerancePercent;
    }
    
    /// <summary>
    /// Gets the sheet type from a step ID (CLIENT, SERVER, NETWORK, USER).
    /// </summary>
    public static string GetSheetFromStepId(string stepId)
    {
        if (string.IsNullOrEmpty(stepId)) return "UNKNOWN";
        
        var upper = stepId.ToUpperInvariant();
        if (upper.StartsWith(StepPrefix_Client)) return "CLIENT";
        if (upper.StartsWith(StepPrefix_Server)) return "SERVER";
        if (upper.StartsWith(StepPrefix_Network)) return "NETWORK";
        if (upper.StartsWith(StepPrefix_User)) return "USER";
        
        // Legacy format support
        if (upper.StartsWith("OC-")) return "CLIENT";
        if (upper.StartsWith("OS-")) return "SERVER";
        if (upper.StartsWith("IC-")) return "USER";
        
        return "UNKNOWN";
    }
    
    #endregion
    
    #region Deprecated - Old Format Constants
    
    [Obsolete("Use StepPrefix_User instead")]
    public const string StepPrefix_InputClient = "IC-";
    
    [Obsolete("Use StepPrefix_Client instead")]
    public const string StepPrefix_OutputClient = "OC-";
    
    [Obsolete("Use StepPrefix_Server instead")]
    public const string StepPrefix_OutputServer = "OS-";
    
    [Obsolete("Use Validation_Console instead")]
    public const string Validation_ClientOutput = "CLIENT_OUTPUT";
    
    [Obsolete("Use Validation_Console instead")]
    public const string Validation_ServerOutput = "SERVER_OUTPUT";
    
    [Obsolete("Use Validation_ResPayload instead")]
    public const string Validation_DataResponse = "DATA_RESPONSE";
    
    [Obsolete("Use Validation_ReqPayload instead")]
    public const string Validation_DataRequest = "DATA_REQUEST";
    
    [Obsolete("Use Validation_Method instead")]
    public const string Validation_HttpMethod = "HTTP_METHOD";
    
    [Obsolete("Use Validation_Status instead")]
    public const string Validation_StatusCode = "STATUS_CODE";
    
    [Obsolete("ByteSize is no longer used in new format")]
    public const string Validation_ByteSize = "BYTE_SIZE";
    
    [Obsolete("DataType is inferred from content in new format")]
    public const string Validation_DataType = "DATA_TYPE";
    
    [Obsolete("Use specific validation type instead")]
    public const string Validation_Other = "OTHER";
    
    [Obsolete("Use MetadataKey instead")]
    public const string MetadataKey_ValidationType = "ValidationType";
    
    [Obsolete("Use AllValidationTypes property instead")]
    public static readonly string[] AllValidationTypes =
    [
        Validation_Console,
        Validation_Method,
        Validation_Status,
        Validation_ReqPayload,
        Validation_ResPayload,
        Validation_Data,
        Validation_HttpBody
    ];
    
    #endregion
}
