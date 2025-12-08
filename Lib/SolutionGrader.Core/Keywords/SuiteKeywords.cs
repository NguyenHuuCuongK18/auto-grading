namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords for test suite sheet names and column names.
/// 
/// NEW FORMAT (current):
/// - User sheet: Stage, Input, Action
/// - Client sheet: Stage, Console
/// - Server sheet: Stage, Console
/// - Network sheet: Stage, Time, Info, Source, Destination, Flags, State, 
///   - HTTP: URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody, SourceRole, DestinationRole
///   - TCP: Data, SourceRole, DestinationRole
/// 
/// OLD FORMAT (deprecated, kept for backward compatibility):
/// - InputClients/InputClient
/// - OutputClients/OutputClient  
/// - OutputServers/OutputServer
/// </summary>
public static class SuiteKeywords
{
    #region File Names
    
    public const string HeaderFileName = "Header.xlsx";
    public const string DetailFileName = "Detail.xlsx";
    
    #endregion
    
    #region New Format Sheet Names
    
    /// <summary>User sheet - contains actions and inputs (Stage, Input, Action)</summary>
    public const string Sheet_User    = "User";
    
    /// <summary>Client sheet - contains client console output (Stage, Console)</summary>
    public const string Sheet_Client  = "Client";
    
    /// <summary>Server sheet - contains server console output (Stage, Console)</summary>
    public const string Sheet_Server  = "Server";
    
    /// <summary>Network sheet - contains network traffic data</summary>
    public const string Sheet_Network = "Network";
    
    /// <summary>Database sheet - contains database operations (optional)</summary>
    public const string Sheet_Database = "Database";
    
    #endregion
    
    #region New Format - User Sheet Columns
    
    /// <summary>Stage number for grouping related actions</summary>
    public const string Col_User_Stage  = "Stage";
    
    /// <summary>Input value to send (e.g., " 1" for menu selection)</summary>
    public const string Col_User_Input  = "Input";
    
    /// <summary>Action to perform (CLIENTSTART, SERVERSTART, CLIENT_INPUT, WAIT, TCP_RELAY)</summary>
    public const string Col_User_Action = "Action";
    
    #endregion
    
    #region New Format - Client/Server Sheet Columns
    
    /// <summary>Stage number matching User sheet stages</summary>
    public const string Col_Console_Stage   = "Stage";
    
    /// <summary>Expected console output for the stage</summary>
    public const string Col_Console_Output  = "Console";
    
    #endregion
    
    #region New Format - Network Sheet Columns (Common)
    
    /// <summary>Stage number matching User sheet stages</summary>
    public const string Col_Network_Stage       = "Stage";
    
    /// <summary>Timestamp of packet capture (excluded from grading by default)</summary>
    public const string Col_Network_Time        = "Time";
    
    /// <summary>Packet info type (TCP, HTTP Request, HTTP Response)</summary>
    public const string Col_Network_Info        = "Info";
    
    /// <summary>Source IP:Port</summary>
    public const string Col_Network_Source      = "Source";
    
    /// <summary>Destination IP:Port</summary>
    public const string Col_Network_Destination = "Destination";
    
    /// <summary>TCP flags (SYN, ACK, PSH, FIN, etc.)</summary>
    public const string Col_Network_Flags       = "Flags";
    
    /// <summary>Connection state description</summary>
    public const string Col_Network_State       = "State";
    
    /// <summary>Role of source (Client/Server)</summary>
    public const string Col_Network_SourceRole  = "SourceRole";
    
    /// <summary>Role of destination (Client/Server)</summary>
    public const string Col_Network_DestRole    = "DestinationRole";
    
    #endregion
    
    #region New Format - Network Sheet Columns (HTTP Protocol)
    
    /// <summary>HTTP request URI path</summary>
    public const string Col_Network_URI         = "URI";
    
    /// <summary>HTTP Host header value</summary>
    public const string Col_Network_Host        = "Host";
    
    /// <summary>HTTP method (GET, POST, PUT, DELETE)</summary>
    public const string Col_Network_Method      = "Method";
    
    /// <summary>HTTP response status (200 OK, 404 Not Found)</summary>
    public const string Col_Network_Status      = "Status";
    
    /// <summary>HTTP version (HTTP/1.1)</summary>
    public const string Col_Network_HttpVersion = "HttpVersion";
    
    /// <summary>HTTP headers (semicolon-separated)</summary>
    public const string Col_Network_HttpHeaders = "HttpHeaders";
    
    /// <summary>HTTP body content</summary>
    public const string Col_Network_HttpBody    = "HttpBody";
    
    #endregion
    
    #region New Format - Network Sheet Columns (TCP Protocol)
    
    /// <summary>Raw TCP payload data</summary>
    public const string Col_Network_Data = "Data";
    
    #endregion
    
    #region Step ID Prefixes (New Format)
    
    /// <summary>Prefix for steps from User sheet</summary>
    public const string StepPrefix_User    = "USER-";
    
    /// <summary>Prefix for steps from Client sheet</summary>
    public const string StepPrefix_Client  = "CLIENT-";
    
    /// <summary>Prefix for steps from Server sheet</summary>
    public const string StepPrefix_Server  = "SERVER-";
    
    /// <summary>Prefix for steps from Network sheet</summary>
    public const string StepPrefix_Network = "NETWORK-";
    
    #endregion
    
    #region Step Types (New Format)
    
    public const string StepType_Console = "CONSOLE";
    public const string StepType_Method  = "METHOD";
    public const string StepType_Status  = "STATUS";
    public const string StepType_Body    = "HTTPBODY";
    public const string StepType_Data    = "DATA";
    public const string StepType_ReqPayload = "REQPAYLOAD";
    public const string StepType_ResPayload = "RESPAYLOAD";
    
    #endregion
    
    #region Config Keys
    
    public const string ConfigKey_Type     = "Type";
    public const string ConfigKey_Protocol = "Protocol";
    
    #endregion
    
    #region Header Sheet
    
    public const string Sheet_Header       = "Header";
    public const string Sheet_QuestionMark = "QuestionMark";
    
    #endregion
    
    #region Deprecated - Old Format Sheet Names
    
    [Obsolete("Use Sheet_User, Sheet_Client, Sheet_Server, Sheet_Network instead")]
    public const string Sheet_InputClients  = "InputClients";
    
    [Obsolete("Use Sheet_Client instead")]
    public const string Sheet_OutputClients = "OutputClients";
    
    [Obsolete("Use Sheet_Server instead")]
    public const string Sheet_OutputServers = "OutputServers";
    
    [Obsolete("Use Sheet_User instead")]
    public const string Sheet_InputClient   = "InputClient";
    
    [Obsolete("Use Sheet_Client instead")]
    public const string Sheet_OutputClient  = "OutputClient";
    
    [Obsolete("Use Sheet_Server instead")]
    public const string Sheet_OutputServer  = "OutputServer";
    
    #endregion
    
    #region Deprecated - Old Format Column Names
    
    // InputClients columns (old format)
    [Obsolete("Use Col_User_Stage instead")]
    public const string Col_IC_Stage    = "Stage";
    
    [Obsolete("Use Col_User_Input instead")]
    public const string Col_IC_Input    = "Input";
    
    [Obsolete("DataType is no longer used in new format")]
    public const string Col_IC_DataType = "DataType";
    
    [Obsolete("Use Col_User_Action instead")]
    public const string Col_IC_Action   = "Action";

    // OutputClients columns (old format)
    [Obsolete("Use Col_Console_Stage instead")]
    public const string Col_OC_Stage              = "Stage";
    
    [Obsolete("Use Col_Network_Method instead")]
    public const string Col_OC_Method             = "Method";
    
    [Obsolete("Use Col_Network_HttpBody instead")]
    public const string Col_OC_DataResponse       = "DataResponse";
    
    [Obsolete("Use Col_Network_Status instead")]
    public const string Col_OC_StatusCode         = "StatusCode";
    
    [Obsolete("Use Col_Console_Output instead")]
    public const string Col_OC_Output             = "Output";
    
    [Obsolete("DataType is no longer used in new format")]
    public const string Col_OC_DataType           = "DataType";
    
    [Obsolete("DataTypeMiddleware is no longer used in new format")]
    public const string Col_OC_DataTypeMiddleware = "DataTypeMiddleWare";
    
    [Obsolete("ByteSize is no longer used in new format")]
    public const string Col_OC_ByteSize           = "ByteSize";

    // OutputServers columns (old format)
    [Obsolete("Use Col_Console_Stage instead")]
    public const string Col_OS_Stage              = "Stage";
    
    [Obsolete("Use Col_Network_Method instead")]
    public const string Col_OS_Method             = "Method";
    
    [Obsolete("Use Col_Network_Data or Col_Network_HttpBody instead")]
    public const string Col_OS_DataRequest        = "DataRequest";
    
    [Obsolete("Use Col_Console_Output instead")]
    public const string Col_OS_Output             = "Output";
    
    [Obsolete("DataType is no longer used in new format")]
    public const string Col_OS_DataType           = "DataType";
    
    [Obsolete("DataTypeMiddleware is no longer used in new format")]
    public const string Col_OS_DataTypeMiddleware = "DataTypeMiddleware";
    
    [Obsolete("ByteSize is no longer used in new format")]
    public const string Col_OS_ByteSize           = "ByteSize";

    // Old format data types
    [Obsolete("DataType is inferred from content in new format")]
    public const string InputDataType_System    = "System";
    
    [Obsolete("DataType is inferred from content in new format")]
    public const string InputDataType_UserInput = "UserInput";
    
    [Obsolete("DataType is inferred from content in new format")]
    public const string InputDataType_Integer   = "Integer";
    
    [Obsolete("DataType is inferred from content in new format")]
    public const string InputDataType_String    = "String";
    
    [Obsolete("DataType is inferred from content in new format")]
    public const string InputDataType_Empty     = "";

    [Obsolete("QuestionId is no longer used in new format")]
    public const string Col_Generic_QuestionId  = "QuestionId";
    
    #endregion
}
