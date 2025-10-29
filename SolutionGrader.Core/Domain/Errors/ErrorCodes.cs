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

    public static class ErrorCodes
    {
        // General / success
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
        public const string STEP_TIMEOUT = "STEP_TIMEOUT";

        public const string UNKNOWN_EXCEPTION = "UNKNOWN_EXCEPTION";

        public static ErrorCategory CategoryOf(string code) => code switch
        {
            OK or SKIPPED or INPUT_VALIDATION_SKIPPED => ErrorCategory.None,

            SUITE_LOAD_FAILED or HEADER_MISSING or NO_TEST_CASES or STEP_PARSE_ERROR => ErrorCategory.Suite,
            UNSUPPORTED_ACTION => ErrorCategory.Parse,

            DB_RESET_FAILED or APPSETTINGS_REPLACE_FAILED => ErrorCategory.Env,

            CLIENT_EXE_MISSING or SERVER_EXE_MISSING or PROCESS_CRASHED or KILL_ALL_FAILED => ErrorCategory.Process,
            SERVER_START_TIMEOUT or PORT_NOT_LISTENING or PROXY_START_FAILED => ErrorCategory.Process,

            HTTP_REQUEST_INVALID or HTTP_NON_SUCCESS or TCP_RELAY_ERROR or MIDDLEWARE_ERROR => ErrorCategory.Network,

            FILE_NOT_FOUND or ACTUAL_FILE_MISSING or EXPECTED_FILE_MISSING or FILE_COPY_FAILED or PATH_NOT_FOUND or PERMISSION_DENIED => ErrorCategory.IO,

            TEXT_MISMATCH or JSON_MISMATCH or CSV_MISMATCH or FILE_SIZE_MISMATCH or FILE_HASH_MISMATCH => ErrorCategory.Compare,

            STEP_TIMEOUT => ErrorCategory.Timeout,

            _ => ErrorCategory.Unknown
        };
    }
}
