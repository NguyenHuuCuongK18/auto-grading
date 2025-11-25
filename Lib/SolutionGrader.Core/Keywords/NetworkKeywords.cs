namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords and constants for network monitoring and capture.
/// Centralizes all network-related string constants for maintainability.
/// </summary>
public static class NetworkKeywords
{
    // Protocol types for network capture
    public const string Protocol_TCP = "TCP";
    public const string Protocol_HTTP = "HTTP";
    
    // Network sheet column names (from new test kit format)
    public const string Col_Stage = "Stage";
    public const string Col_Time = "Time";
    public const string Col_Info = "Info";
    public const string Col_Source = "Source";
    public const string Col_Destination = "Destination";
    public const string Col_Flags = "Flags";
    public const string Col_State = "State";
    public const string Col_Data = "Data";              // TCP protocol uses Data column
    public const string Col_URI = "URI";                // HTTP protocol column
    public const string Col_Host = "Host";              // HTTP protocol column  
    public const string Col_Method = "Method";          // HTTP protocol column
    public const string Col_Status = "Status";          // HTTP protocol column
    public const string Col_HttpVersion = "HttpVersion"; // HTTP protocol column
    public const string Col_HttpHeaders = "HttpHeaders"; // HTTP protocol column
    public const string Col_HttpBody = "HttpBody";      // HTTP protocol column
    public const string Col_SourceRole = "SourceRole";
    public const string Col_DestinationRole = "DestinationRole";
    
    // Role values
    public const string Role_Client = "Client";
    public const string Role_Server = "Server";
    
    // Info types
    public const string Info_HTTP = "HTTP";
    public const string Info_HTTP_Request = "HTTP Request";
    public const string Info_HTTP_Response = "HTTP Response";
    public const string Info_TCP = "TCP";
    
    // TCP Flags
    public const string Flag_SYN = "SYN";
    public const string Flag_ACK = "ACK";
    public const string Flag_SYN_ACK = "SYN, ACK";
    public const string Flag_PSH_ACK = "PSH, ACK";
    public const string Flag_FIN_ACK = "FIN, ACK";
    public const string Flag_RST = "RST";
    
    // Log prefixes
    public const string LOG_PREFIX_NETWORK = "[Network]";
    public const string LOG_PREFIX_MONITOR = "[Monitor]";
    public const string LOG_PREFIX_CAPTURE = "[Capture]";
    
    // Messages
    public const string MSG_MONITOR_STARTING = "Starting network monitor on port {0} ({1} mode)...";
    public const string MSG_MONITOR_STARTED = "Network monitor started successfully";
    public const string MSG_MONITOR_STOPPING = "Stopping network monitor...";
    public const string MSG_MONITOR_STOPPED = "Network monitor stopped";
    
    // Columns to exclude from grading (time-related)
    public static readonly string[] ExcludedGradingColumns = new[]
    {
        Col_Time,           // Time column varies with each run
        "Date",             // HTTP Date header varies
    };
    
    /// <summary>
    /// Checks if a column should be excluded from grading due to time/date sensitivity.
    /// </summary>
    public static bool ShouldExcludeFromGrading(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return false;
        
        foreach (var excluded in ExcludedGradingColumns)
        {
            if (columnName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        // Also exclude any header containing "Date" for HTTP headers
        if (columnName.Contains("Date", StringComparison.OrdinalIgnoreCase))
            return true;
            
        return false;
    }
}
