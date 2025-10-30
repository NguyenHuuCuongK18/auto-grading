namespace SolutionGrader.Core.Domain.Errors
{
    public enum ErrorCategory
    {
        None = 0,
        Suite = 1,
        Parse = 2,
        Env = 3,
        Process = 4,
        Network = 5,
        IO = 6,
        Compare = 7,
        Middleware = 8,
        Timeout = 9,
        Unknown = 99
    }

    public sealed class ErrorCodeInfo
    {
        public required string Code { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required ErrorCategory Category { get; init; }
    }

    public static class ErrorCodes
    {
        // General / success
        public const string NONE = "NONE";
        public const string OK = "OK";
        public const string SKIPPED = "SKIPPED";
        public const string INPUT_VALIDATION_SKIPPED = "INPUT_VALIDATION_SKIPPED";

        // Suite / parsing
        public const string SUITE_LOAD_FAILED = "SUITE_LOAD_FAILED";
        public const string HEADER_MISSING = "HEADER_MISSING";
        public const string NO_TEST_CASES = "NO_TEST_CASES";
        public const string STEP_PARSE_ERROR = "STEP_PARSE_ERROR";
        public const string UNSUPPORTED_ACTION = "UNSUPPORTED_ACTION";

        // Environment
        public const string DB_RESET_FAILED = "DB_RESET_FAILED";
        public const string APPSETTINGS_REPLACE_FAILED = "APPSETTINGS_REPLACE_FAILED";

        // Process / executables
        public const string CLIENT_EXE_MISSING = "CLIENT_EXE_MISSING";
        public const string SERVER_EXE_MISSING = "SERVER_EXE_MISSING";
        public const string PROCESS_CRASHED = "PROCESS_CRASHED";
        public const string KILL_ALL_FAILED = "KILL_ALL_FAILED";

        // Startup / readiness
        public const string SERVER_START_TIMEOUT = "SERVER_START_TIMEOUT";
        public const string PORT_NOT_LISTENING = "PORT_NOT_LISTENING";
        public const string PROXY_START_FAILED = "PROXY_START_FAILED";

        // HTTP / TCP
        public const string HTTP_REQUEST_INVALID = "HTTP_REQUEST_INVALID";
        public const string HTTP_NON_SUCCESS = "HTTP_NON_SUCCESS";
        public const string TCP_RELAY_ERROR = "TCP_RELAY_ERROR";
        public const string MIDDLEWARE_ERROR = "MIDDLEWARE_ERROR";

        // IO / Files
        public const string FILE_NOT_FOUND = "FILE_NOT_FOUND";
        public const string ACTUAL_FILE_MISSING = "ACTUAL_FILE_MISSING";
        public const string EXPECTED_FILE_MISSING = "EXPECTED_FILE_MISSING"; // leads to IGNORE (pass)
        public const string EXPECTED_CLIENT_OUTPUT_MISSING = "EXPECTED_CLIENT_OUTPUT_MISSING";
        public const string EXPECTED_SERVER_OUTPUT_MISSING = "EXPECTED_SERVER_OUTPUT_MISSING";
        public const string FILE_COPY_FAILED = "FILE_COPY_FAILED";
        public const string PATH_NOT_FOUND = "PATH_NOT_FOUND";
        public const string PERMISSION_DENIED = "PERMISSION_DENIED";

        // Compare
        public const string TEXT_MISMATCH = "TEXT_MISMATCH";
        public const string JSON_MISMATCH = "JSON_MISMATCH";
        public const string CSV_MISMATCH = "CSV_MISMATCH";
        public const string FILE_SIZE_MISMATCH = "FILE_SIZE_MISMATCH";
        public const string FILE_HASH_MISMATCH = "FILE_HASH_MISMATCH";

        // Timeout / generic
        public const string TIMEOUT = "TIMEOUT";
        public const string STEP_TIMEOUT = "STEP_TIMEOUT";
        public const string UNKNOWN = "UNKNOWN";
        public const string UNKNOWN_EXCEPTION = "UNKNOWN_EXCEPTION";

        public static ErrorCategory CategoryOf(string code) => code switch
        {
            NONE or OK or SKIPPED or INPUT_VALIDATION_SKIPPED => ErrorCategory.None,

            SUITE_LOAD_FAILED or HEADER_MISSING or NO_TEST_CASES or STEP_PARSE_ERROR => ErrorCategory.Suite,
            UNSUPPORTED_ACTION => ErrorCategory.Parse,

            DB_RESET_FAILED or APPSETTINGS_REPLACE_FAILED => ErrorCategory.Env,

            CLIENT_EXE_MISSING or SERVER_EXE_MISSING or PROCESS_CRASHED or KILL_ALL_FAILED => ErrorCategory.Process,
            SERVER_START_TIMEOUT or PORT_NOT_LISTENING or PROXY_START_FAILED => ErrorCategory.Process,

            HTTP_REQUEST_INVALID or HTTP_NON_SUCCESS or TCP_RELAY_ERROR or MIDDLEWARE_ERROR => ErrorCategory.Network,

            FILE_NOT_FOUND or ACTUAL_FILE_MISSING or EXPECTED_FILE_MISSING or EXPECTED_CLIENT_OUTPUT_MISSING 
                or EXPECTED_SERVER_OUTPUT_MISSING or FILE_COPY_FAILED or PATH_NOT_FOUND or PERMISSION_DENIED => ErrorCategory.IO,

            TEXT_MISMATCH or JSON_MISMATCH or CSV_MISMATCH or FILE_SIZE_MISMATCH or FILE_HASH_MISMATCH => ErrorCategory.Compare,

            TIMEOUT or STEP_TIMEOUT => ErrorCategory.Timeout,

            UNKNOWN or UNKNOWN_EXCEPTION or _ => ErrorCategory.Unknown
        };

        public static ErrorCodeInfo GetInfo(string code) => code switch
        {
            // Success codes
            NONE => new() { Code = NONE, Title = "No Error", Description = "No error occurred; step not yet executed or no validation needed", Category = ErrorCategory.None },
            OK => new() { Code = OK, Title = "Success", Description = "Operation completed successfully with all validations passing", Category = ErrorCategory.None },
            SKIPPED => new() { Code = SKIPPED, Title = "Skipped", Description = "Step was intentionally skipped due to configuration or dependencies", Category = ErrorCategory.None },
            INPUT_VALIDATION_SKIPPED => new() { Code = INPUT_VALIDATION_SKIPPED, Title = "Input Validation Skipped", Description = "Input validation was skipped as expected data was not provided", Category = ErrorCategory.None },

            // Suite / Parsing
            SUITE_LOAD_FAILED => new() { Code = SUITE_LOAD_FAILED, Title = "Suite Load Failed", Description = "Failed to load test suite Excel files (Header.xlsx or Detail.xlsx)", Category = ErrorCategory.Suite },
            HEADER_MISSING => new() { Code = HEADER_MISSING, Title = "Header Missing", Description = "Required Header.xlsx file is missing from the test suite directory", Category = ErrorCategory.Suite },
            NO_TEST_CASES => new() { Code = NO_TEST_CASES, Title = "No Test Cases", Description = "No test cases found in the suite; QuestionMark sheet may be empty", Category = ErrorCategory.Suite },
            STEP_PARSE_ERROR => new() { Code = STEP_PARSE_ERROR, Title = "Step Parse Error", Description = "Failed to parse test step from Excel Detail.xlsx (invalid row format)", Category = ErrorCategory.Suite },
            UNSUPPORTED_ACTION => new() { Code = UNSUPPORTED_ACTION, Title = "Unsupported Action", Description = "Test step contains an action keyword that is not recognized or supported", Category = ErrorCategory.Parse },

            // Environment
            DB_RESET_FAILED => new() { Code = DB_RESET_FAILED, Title = "Database Reset Failed", Description = "Failed to execute database reset script before test execution", Category = ErrorCategory.Env },
            APPSETTINGS_REPLACE_FAILED => new() { Code = APPSETTINGS_REPLACE_FAILED, Title = "AppSettings Replace Failed", Description = "Failed to copy or replace appsettings.json template files", Category = ErrorCategory.Env },

            // Process
            CLIENT_EXE_MISSING => new() { Code = CLIENT_EXE_MISSING, Title = "Client Executable Missing", Description = "Client application executable file not found at specified path", Category = ErrorCategory.Process },
            SERVER_EXE_MISSING => new() { Code = SERVER_EXE_MISSING, Title = "Server Executable Missing", Description = "Server application executable file not found at specified path", Category = ErrorCategory.Process },
            PROCESS_CRASHED => new() { Code = PROCESS_CRASHED, Title = "Process Crashed", Description = "Client or server process crashed unexpectedly during test execution", Category = ErrorCategory.Process },
            KILL_ALL_FAILED => new() { Code = KILL_ALL_FAILED, Title = "Kill All Failed", Description = "Failed to terminate client and/or server processes during cleanup", Category = ErrorCategory.Process },
            SERVER_START_TIMEOUT => new() { Code = SERVER_START_TIMEOUT, Title = "Server Start Timeout", Description = "Server did not become ready within the configured timeout period", Category = ErrorCategory.Process },
            PORT_NOT_LISTENING => new() { Code = PORT_NOT_LISTENING, Title = "Port Not Listening", Description = "Server process started but is not listening on the expected port", Category = ErrorCategory.Process },
            PROXY_START_FAILED => new() { Code = PROXY_START_FAILED, Title = "Proxy Start Failed", Description = "Failed to start middleware proxy on port 5000", Category = ErrorCategory.Process },

            // Network
            HTTP_REQUEST_INVALID => new() { Code = HTTP_REQUEST_INVALID, Title = "HTTP Request Invalid", Description = "HTTP request step has invalid format (requires METHOD|URL at minimum)", Category = ErrorCategory.Network },
            HTTP_NON_SUCCESS => new() { Code = HTTP_NON_SUCCESS, Title = "HTTP Non-Success Status", Description = "HTTP request returned non-success status code or did not match expected status", Category = ErrorCategory.Network },
            TCP_RELAY_ERROR => new() { Code = TCP_RELAY_ERROR, Title = "TCP Relay Error", Description = "TCP relay middleware encountered an error during traffic forwarding", Category = ErrorCategory.Network },
            MIDDLEWARE_ERROR => new() { Code = MIDDLEWARE_ERROR, Title = "Middleware Error", Description = "Middleware proxy service encountered an unexpected error", Category = ErrorCategory.Network },

            // IO / Files
            FILE_NOT_FOUND => new() { Code = FILE_NOT_FOUND, Title = "File Not Found", Description = "Required file was not found at the specified path", Category = ErrorCategory.IO },
            ACTUAL_FILE_MISSING => new() { Code = ACTUAL_FILE_MISSING, Title = "Actual Output Missing", Description = "Actual output file was not generated by the application under test", Category = ErrorCategory.IO },
            EXPECTED_FILE_MISSING => new() { Code = EXPECTED_FILE_MISSING, Title = "Expected File Missing", Description = "Expected output file is missing from test suite (test will be ignored)", Category = ErrorCategory.IO },
            EXPECTED_CLIENT_OUTPUT_MISSING => new() { Code = EXPECTED_CLIENT_OUTPUT_MISSING, Title = "Expected Client Output Missing", Description = "Expected client console output file is missing from test suite (test will be ignored)", Category = ErrorCategory.IO },
            EXPECTED_SERVER_OUTPUT_MISSING => new() { Code = EXPECTED_SERVER_OUTPUT_MISSING, Title = "Expected Server Output Missing", Description = "Expected server console output file is missing from test suite (test will be ignored)", Category = ErrorCategory.IO },
            FILE_COPY_FAILED => new() { Code = FILE_COPY_FAILED, Title = "File Copy Failed", Description = "Failed to copy file during setup or teardown phase", Category = ErrorCategory.IO },
            PATH_NOT_FOUND => new() { Code = PATH_NOT_FOUND, Title = "Path Not Found", Description = "Specified directory path does not exist", Category = ErrorCategory.IO },
            PERMISSION_DENIED => new() { Code = PERMISSION_DENIED, Title = "Permission Denied", Description = "Access denied when attempting to read or write file", Category = ErrorCategory.IO },

            // Compare
            TEXT_MISMATCH => new() { Code = TEXT_MISMATCH, Title = "Text Content Mismatch", Description = "Text file content does not match expected output after normalization", Category = ErrorCategory.Compare },
            JSON_MISMATCH => new() { Code = JSON_MISMATCH, Title = "JSON Content Mismatch", Description = "JSON structure or content does not match expected output", Category = ErrorCategory.Compare },
            CSV_MISMATCH => new() { Code = CSV_MISMATCH, Title = "CSV Content Mismatch", Description = "CSV file content does not match expected output", Category = ErrorCategory.Compare },
            FILE_SIZE_MISMATCH => new() { Code = FILE_SIZE_MISMATCH, Title = "File Size Mismatch", Description = "File size differs from expected size", Category = ErrorCategory.Compare },
            FILE_HASH_MISMATCH => new() { Code = FILE_HASH_MISMATCH, Title = "File Hash Mismatch", Description = "File hash does not match expected hash value", Category = ErrorCategory.Compare },

            // Timeout
            TIMEOUT => new() { Code = TIMEOUT, Title = "Operation Timeout", Description = "Operation exceeded the configured timeout limit", Category = ErrorCategory.Timeout },
            STEP_TIMEOUT => new() { Code = STEP_TIMEOUT, Title = "Step Timeout", Description = "Test step execution exceeded the configured step timeout", Category = ErrorCategory.Timeout },

            // Unknown
            UNKNOWN => new() { Code = UNKNOWN, Title = "Unknown Error", Description = "An unspecified error occurred", Category = ErrorCategory.Unknown },
            UNKNOWN_EXCEPTION => new() { Code = UNKNOWN_EXCEPTION, Title = "Unknown Exception", Description = "An unexpected exception occurred during execution", Category = ErrorCategory.Unknown },

            _ => new() { Code = code, Title = "Unrecognized Error", Description = $"Error code '{code}' is not defined in the system", Category = ErrorCategory.Unknown }
        };
    }
}
