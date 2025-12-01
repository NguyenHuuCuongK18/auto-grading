namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Centralized logging messages and prefixes for console output.
/// Provides consistent logging format across all services.
/// </summary>
public static class LoggingKeywords
{
    // Log prefixes
    public const string LOG_PREFIX_SUITE = "[Suite]";
    public const string LOG_PREFIX_TESTCASE = "[TestCase]";
    public const string LOG_PREFIX_STEP = "[Step]";
    public const string LOG_PREFIX_ACTION = "[Action]";
    public const string LOG_PREFIX_PROXY = "[Proxy]";
    public const string LOG_PREFIX_HTTP_PROXY_ERR = "[HTTP Proxy ERR]";
    public const string LOG_PREFIX_TCP_PROXY_ERR = "[TCP Proxy ERR]";
    public const string LOG_PREFIX_CLIENT_INPUT = "[ClientInput]";
    public const string LOG_PREFIX_DATABASE_CONFIG = "[DatabaseConfig]";
    public const string LOG_PREFIX_NETWORK_MONITOR = "[NetworkMonitor]";

    // Suite messages
    public const string MSG_SUITE_LOADING = "Loading test suite from: {0}";
    public const string MSG_SUITE_PROTOCOL = "Protocol: {0}";
    public const string MSG_SUITE_CASES_FOUND = "Found {0} test case(s)";

    // Test case messages
    public const string MSG_TESTCASE_STARTING = "Starting: {0} (Mark: {1})";
    public const string MSG_TESTCASE_LOADED_STEPS = "Loaded {0} step(s)";
    public const string MSG_TESTCASE_EXTRA_DELAY = "Adding extra delay before comparisons to ensure async output is captured...";
    public const string MSG_TESTCASE_WRITING_RESULTS = "Writing results to: {0}";
    public const string MSG_TESTCASE_CLEANING_PROCESSES = "Cleaning up processes...";
    public const string MSG_TESTCASE_COMPLETED = "Completed: {0}";

    // Step execution messages
    public const string MSG_STEP_EXECUTING = "Executing: {0} (Stage: {1}, ID: {2})";
    public const string MSG_STEP_RESULT = "Result: {0} - {1} ({2:F0}ms)";

    // Action messages
    public const string MSG_ACTION_SERVER_START = "ServerStart: Starting server application...";
    public const string MSG_ACTION_SERVER_NOT_INITIALIZED = "ServerStart: Warning - Server may not be fully initialized";
    public const string MSG_ACTION_SERVER_OUTPUT = "ServerStart: Server output: {0}";
    public const string MSG_ACTION_SERVER_FULL_LOG_AVAILABLE = "ServerStart: Full error output available in server.log";
    public const string MSG_ACTION_CLIENT_START = "ClientStart: Starting client application...";
    public const string MSG_ACTION_CLIENT_OUTPUT = "ClientStart: Client output: {0}";
    public const string MSG_ACTION_CLIENT_FULL_LOG_AVAILABLE = "ClientStart: Full error output available in client.log";
    public const string MSG_ACTION_CLIENT_INPUT_SENDING = "ClientInput: Sending input to client: {0}";
    public const string MSG_ACTION_TCP_RELAY_STARTING = "TcpRelay: Starting middleware proxy (protocol: {0})...";

    // Proxy messages
    public const string MSG_PROXY_HTTP_LISTENING = "HTTP proxy listening on http://localhost:{0}/ -> http://localhost:{1}/";
    public const string MSG_PROXY_TCP_LISTENING = "TCP proxy listening on 127.0.0.1:{0} -> 127.0.0.1:{1}";
    public const string MSG_PROXY_HTTP_ERROR = "{0}";
    public const string MSG_PROXY_TCP_ERROR = "{0}";

    // Client input messages
    public const string MSG_CLIENT_INPUT_NOT_RUNNING = "Cannot send input - client not running";
    public const string MSG_CLIENT_INPUT_SENT = "Sent: {0}";
    public const string MSG_CLIENT_INPUT_ERROR = "Error sending input: {0}";
    public const string MSG_CLIENT_PROCESS_EXITED = "Client process exited";
    public const string MSG_CLIENT_PRODUCED_OUTPUT = "Client produced output ({0} bytes)";
    public const string MSG_CLIENT_INPUT_WAIT_CANCELLED = "Wait cancelled";
    public const string MSG_CLIENT_INPUT_WAIT_TIMEOUT = "Wait timed out after {0}s";

    // Process management messages
    public const string LOG_PREFIX_PUMP_ASYNC = "[PumpAsync]";
    public const string LOG_PREFIX_PROCESS = "[Process]";
    public const string MSG_PUMP_ERROR_READING_STREAM = "Error reading stream: {0}";
    public const string MSG_PROCESS_TASKKILL_USED = "Process {0} did not exit gracefully, using TaskKill...";
    public const string MSG_PROCESS_KILL_ERROR = "Error killing process: {0}";
    public const string MSG_PROCESS_TASKKILL_FAILED = "TaskKill/kill failed: {0}";

    // Database config messages
    public const string MSG_DB_CONFIG_ERROR = "Error reading database config: {0}";

    // Result status strings
    public const string RESULT_PASS = "PASS";
    public const string RESULT_FAIL = "FAIL";
}
