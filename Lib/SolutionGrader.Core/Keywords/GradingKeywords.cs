namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords and constants used for grading and validation.
/// Centralizes all grading-related string constants for maintainability.
/// </summary>
public static class GradingKeywords
{
    // Validation types
    public const string Validation_ClientOutput = "CLIENT_OUTPUT";
    public const string Validation_ServerOutput = "SERVER_OUTPUT";
    public const string Validation_DataResponse = "DATA_RESPONSE";
    public const string Validation_DataRequest = "DATA_REQUEST";
    public const string Validation_HttpMethod = "HTTP_METHOD";
    public const string Validation_StatusCode = "STATUS_CODE";
    public const string Validation_ByteSize = "BYTE_SIZE";
    public const string Validation_DataType = "DATA_TYPE";

    // HTTP Methods
    public const string Method_GET = "GET";
    public const string Method_POST = "POST";
    public const string Method_PUT = "PUT";
    public const string Method_DELETE = "DELETE";
    public const string Method_PATCH = "PATCH";
    public const string Method_HEAD = "HEAD";
    public const string Method_OPTIONS = "OPTIONS";

    // HTTP Status Code Categories
    public const string StatusCategory_Success = "2xx";
    public const string StatusCategory_Redirect = "3xx";
    public const string StatusCategory_ClientError = "4xx";
    public const string StatusCategory_ServerError = "5xx";

    // Common HTTP Status Codes
    public const string Status_OK = "OK"; // 200
    public const string Status_Created = "Created"; // 201
    public const string Status_NoContent = "NoContent"; // 204
    public const string Status_BadRequest = "BadRequest"; // 400
    public const string Status_Unauthorized = "Unauthorized"; // 401
    public const string Status_Forbidden = "Forbidden"; // 403
    public const string Status_NotFound = "NotFound"; // 404
    public const string Status_InternalServerError = "InternalServerError"; // 500

    // Data Types
    public const string DataType_JSON = "JSON";
    public const string DataType_CSV = "CSV";
    public const string DataType_Text = "Text";
    public const string DataType_XML = "XML";
    public const string DataType_Binary = "Binary";
    public const string DataType_Empty = "Empty";

    // Result values
    public const string Result_Pass = "PASS";
    public const string Result_Fail = "FAIL";
    public const string Result_Skip = "SKIP";
    public const string Result_Ignored = "IGNORED";

    // Excel Sheet Names (in output/grading files)
    public const string Sheet_TestRunData = "TestRunData";
    public const string Sheet_ErrorReport = "ErrorReport";
    public const string Sheet_Summary = "Summary";
    public const string Sheet_ValidationDetails = "ValidationDetails";

    // Excel Column Names for grading output
    public const string Col_Stage = "Stage";
    public const string Col_ValidationType = "ValidationType";
    public const string Col_Expected = "Expected";
    public const string Col_Actual = "Actual";
    public const string Col_Result = "Result";
    public const string Col_ErrorCode = "ErrorCode";
    public const string Col_ErrorCategory = "ErrorCategory";
    public const string Col_Message = "Message";
    public const string Col_PointsAwarded = "PointsAwarded";
    public const string Col_PointsPossible = "PointsPossible";
    public const string Col_DurationMs = "DurationMs";
    public const string Col_Timestamp = "Timestamp";

    // Tolerance values
    public const int ByteSizeTolerance = 10; // Allow ±10 bytes difference
    public const double ByteSizeTolerancePercent = 0.05; // Allow ±5% difference

    // Comparison modes
    public const string CompareMode_Exact = "EXACT";
    public const string CompareMode_Contains = "CONTAINS";
    public const string CompareMode_Normalized = "NORMALIZED";
    public const string CompareMode_Loose = "LOOSE";

    public static readonly string[] AllValidationTypes =
    [
        Validation_ClientOutput,
        Validation_ServerOutput,
        Validation_DataResponse,
        Validation_DataRequest,
        Validation_HttpMethod,
        Validation_StatusCode,
        Validation_ByteSize,
        Validation_DataType
    ];

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

    public static readonly string[] AllDataTypes =
    [
        DataType_JSON,
        DataType_CSV,
        DataType_Text,
        DataType_XML,
        DataType_Binary,
        DataType_Empty
    ];

    /// <summary>
    /// Normalizes HTTP status code text to standard format.
    /// E.g., "200", "OK", "200 OK" all become "OK"
    /// </summary>
    public static string NormalizeStatusCode(string? statusCode)
    {
        if (string.IsNullOrWhiteSpace(statusCode)) return Status_OK;

        var upper = statusCode.Trim().ToUpperInvariant();
        
        // Handle numeric codes
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

        // Handle text codes
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
}
