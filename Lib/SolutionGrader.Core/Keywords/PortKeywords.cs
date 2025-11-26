namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords and constants for port configuration.
/// Centralizes all port-related constants to avoid hardcoded values throughout the codebase.
/// </summary>
public static class PortKeywords
{
    /// <summary>
    /// Default port for grading communication.
    /// Server listens on this port, client connects to this port, network monitor captures on this port.
    /// </summary>
    public const int DEFAULT_GRADER_PORT = 8000;
    
    /// <summary>
    /// Default SQL Server port for TCP connections.
    /// </summary>
    public const int DEFAULT_SQL_SERVER_PORT = 1433;
    
    /// <summary>
    /// Default HTTP server port.
    /// </summary>
    public const int DEFAULT_HTTP_PORT = 80;
    
    /// <summary>
    /// Default HTTPS server port.
    /// </summary>
    public const int DEFAULT_HTTPS_PORT = 443;
    
    /// <summary>
    /// Start of ephemeral port range for dynamic allocation.
    /// </summary>
    public const int EPHEMERAL_PORT_START = 49152;
    
    /// <summary>
    /// End of ephemeral port range for dynamic allocation.
    /// </summary>
    public const int EPHEMERAL_PORT_END = 65535;
    
    /// <summary>
    /// Maximum port number (inclusive).
    /// </summary>
    public const int MAX_PORT = 65535;
    
    /// <summary>
    /// Minimum valid port number (inclusive).
    /// </summary>
    public const int MIN_PORT = 1;
    
    /// <summary>
    /// Default server port fallback when no configuration is available.
    /// </summary>
    public const int DEFAULT_SERVER_FALLBACK_PORT = 5001;
    
    /// <summary>
    /// Health check poll interval in milliseconds.
    /// </summary>
    public const int HEALTH_CHECK_POLL_INTERVAL_MS = 100;
    
    /// <summary>
    /// Server ready timeout in seconds.
    /// </summary>
    public const int SERVER_READY_TIMEOUT_SECONDS = 5;
    
    /// <summary>
    /// Connection output wait time in seconds (for server output after connection-triggering actions).
    /// </summary>
    public const int CONNECTION_OUTPUT_WAIT_SECONDS = 2;
    
    /// <summary>
    /// Maximum characters to show in console/log output preview.
    /// </summary>
    public const int OUTPUT_PREVIEW_MAX_CHARS = 500;
    
    /// <summary>
    /// Maximum characters to show in network data preview.
    /// </summary>
    public const int NETWORK_PREVIEW_MAX_CHARS = 250;
    
    /// <summary>
    /// Maximum characters to show in packet payload preview for logging.
    /// </summary>
    public const int PACKET_PAYLOAD_PREVIEW_MAX_CHARS = 60;
    
    /// <summary>
    /// Maximum characters to show in network flow data preview (per packet).
    /// </summary>
    public const int NETWORK_FLOW_DATA_PREVIEW_MAX_CHARS = 50;
    
    /// <summary>
    /// Maximum characters to show in actual data column for Network sheet.
    /// </summary>
    public const int ACTUAL_DATA_COLUMN_MAX_CHARS = 100;
    
    /// <summary>
    /// Minimum column width for Excel column auto-adjustment.
    /// </summary>
    public const int EXCEL_COLUMN_MIN_WIDTH = 5;
    
    /// <summary>
    /// Maximum column width for Excel column auto-adjustment.
    /// </summary>
    public const int EXCEL_COLUMN_MAX_WIDTH = 80;
    
    /// <summary>
    /// Key pattern for network flow capture storage.
    /// Format: network.{stage}.flow
    /// </summary>
    public const string NETWORK_FLOW_KEY_PATTERN = "network.{0}.flow";
}
