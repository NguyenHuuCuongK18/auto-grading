using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Domain.Models
{
    /// <summary>
    /// Configuration for controlling which aspects of the test kit to validate during grading.
    /// This allows easy toggling of validation checks for debugging and incremental testing.
    /// 
    /// The new test kit format has the following sheets:
    /// - User: Actions and inputs (Stage, Input, Action)
    /// - Client: Client console output (Stage, Console)
    /// - Server: Server console output (Stage, Console)
    /// - Network: Network traffic data
    ///   - HTTP: Stage, Time, Info, Source, Destination, Flags, State, URI, Host, Method, Status, HttpVersion, HttpHeaders, HttpBody, SourceRole, DestinationRole
    ///   - TCP: Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole
    /// 
    /// Note: Time column and DateTime headers are excluded from grading by default as they vary with each run.
    /// </summary>
    public sealed class GradingConfig
    {
        #region Port Configuration
        
        /// <summary>
        /// The single port used for all grading communication:
        /// - Server listens on this port
        /// - Client connects to this port
        /// - Network monitor captures traffic on this port
        /// 
        /// This is a fixed port to ensure consistent grading behavior.
        /// Default: Defined in PortKeywords.DEFAULT_GRADER_PORT
        /// </summary>
        public int GraderPort { get; set; } = PortKeywords.DEFAULT_GRADER_PORT;
        
        #endregion
        
        #region Sheet Grading Toggles
        
        /// <summary>
        /// Enable/disable grading of Client sheet (client console output).
        /// When false, all steps from Client sheet are skipped.
        /// </summary>
        public bool GradeClientSheet { get; set; } = true;

        /// <summary>
        /// Enable/disable grading of Server sheet (server console output).
        /// When false, all steps from Server sheet are skipped.
        /// </summary>
        public bool GradeServerSheet { get; set; } = true;
        
        /// <summary>
        /// Enable/disable grading of Network sheet data.
        /// When false, all network validation steps are skipped.
        /// </summary>
        public bool GradeNetworkSheet { get; set; } = true;
        
        #endregion
        
        #region Console Output Validation
        
        /// <summary>
        /// Enable/disable validation of client console output against expected output.
        /// </summary>
        public bool ValidateClientConsole { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of server console output against expected output.
        /// </summary>
        public bool ValidateServerConsole { get; set; } = true;
        
        #endregion
        
        #region Network Validation - HTTP Protocol
        
        /// <summary>
        /// Enable/disable validation of HTTP method (GET, POST, PUT, DELETE, etc.).
        /// </summary>
        public bool ValidateHttpMethod { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of HTTP status code (200 OK, 404 Not Found, etc.).
        /// </summary>
        public bool ValidateStatusCode { get; set; } = true;
        
        /// <summary>
        /// Enable/disable validation of HTTP body content.
        /// </summary>
        public bool ValidateHttpBody { get; set; } = true;
        
        /// <summary>
        /// Enable/disable validation of HTTP headers.
        /// Note: Date header is excluded by default (see ValidateDateTimeValues).
        /// </summary>
        public bool ValidateHttpHeaders { get; set; } = false;
        
        /// <summary>
        /// Enable/disable validation of HTTP URI.
        /// </summary>
        public bool ValidateHttpUri { get; set; } = true;
        
        #endregion
        
        #region Network Validation - TCP Protocol
        
        /// <summary>
        /// Enable/disable validation of raw TCP data payload.
        /// </summary>
        public bool ValidateTcpData { get; set; } = true;
        
        #endregion
        
        #region Network Validation - Common
        
        /// <summary>
        /// Enable/disable validation of network request data (client to server).
        /// </summary>
        public bool ValidateNetworkRequest { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of network response data (server to client).
        /// </summary>
        public bool ValidateNetworkResponse { get; set; } = true;
        
        #endregion
        
        #region Time/DateTime Exclusions
        
        /// <summary>
        /// Enable/disable validation of Time column in Network sheet.
        /// Time values vary with each run, so this is DISABLED by default.
        /// </summary>
        public bool ValidateTimeColumn { get; set; } = false;

        /// <summary>
        /// Enable/disable validation of DateTime values and Date headers.
        /// DateTime varies between runs and HTTP Date headers change each request.
        /// DISABLED by default to prevent false failures.
        /// </summary>
        public bool ValidateDateTimeValues { get; set; } = false;
        
        #endregion
        
        #region Deprecated - Backward Compatibility
        
        /// <summary>
        /// [DEPRECATED] Use GradeClientSheet instead.
        /// </summary>
        [Obsolete("Use GradeClientSheet instead. The old OutputClients sheet is now called Client.")]
        public bool GradeOutputClientsSheet 
        { 
            get => GradeClientSheet; 
            set => GradeClientSheet = value; 
        }

        /// <summary>
        /// [DEPRECATED] Use GradeServerSheet instead.
        /// </summary>
        [Obsolete("Use GradeServerSheet instead. The old OutputServers sheet is now called Server.")]
        public bool GradeOutputServersSheet 
        { 
            get => GradeServerSheet; 
            set => GradeServerSheet = value; 
        }
        
        /// <summary>
        /// [DEPRECATED] Use ValidateClientConsole instead.
        /// </summary>
        [Obsolete("Use ValidateClientConsole instead.")]
        public bool ValidateClientOutput 
        { 
            get => ValidateClientConsole; 
            set => ValidateClientConsole = value; 
        }
        
        /// <summary>
        /// [DEPRECATED] Use ValidateServerConsole instead.
        /// </summary>
        [Obsolete("Use ValidateServerConsole instead.")]
        public bool ValidateServerOutput 
        { 
            get => ValidateServerConsole; 
            set => ValidateServerConsole = value; 
        }
        
        /// <summary>
        /// [DEPRECATED] Use ValidateNetworkResponse instead.
        /// </summary>
        [Obsolete("Use ValidateNetworkResponse instead.")]
        public bool ValidateDataResponse 
        { 
            get => ValidateNetworkResponse; 
            set => ValidateNetworkResponse = value; 
        }
        
        /// <summary>
        /// [DEPRECATED] Use ValidateNetworkRequest instead.
        /// </summary>
        [Obsolete("Use ValidateNetworkRequest instead.")]
        public bool ValidateDataRequest 
        { 
            get => ValidateNetworkRequest; 
            set => ValidateNetworkRequest = value; 
        }
        
        /// <summary>
        /// [DEPRECATED] Byte size validation is no longer used in new test kit format.
        /// </summary>
        [Obsolete("Byte size validation is no longer used in new test kit format.")]
        public bool ValidateByteSize { get; set; } = false;
        
        /// <summary>
        /// [DEPRECATED] Data type is inferred from content, not explicitly validated.
        /// </summary>
        [Obsolete("Data type is inferred from content in new test kit format.")]
        public bool ValidateDataType { get; set; } = true;
        
        #endregion
        
        #region Preset Configurations
        
        /// <summary>
        /// Default configuration with all validations enabled.
        /// Time column and DateTime values are excluded from grading.
        /// GraderPort is set to the default port for consistent port usage.
        /// </summary>
        public static GradingConfig Default => new GradingConfig
        {
            // Port configuration
            GraderPort = PortKeywords.DEFAULT_GRADER_PORT,
            
            // Sheet grading
            GradeClientSheet = true,
            GradeServerSheet = true,
            GradeNetworkSheet = true,
            
            // Console validation
            ValidateClientConsole = true,
            ValidateServerConsole = true,
            
            // HTTP validation
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateHttpBody = true,
            ValidateHttpHeaders = false,  // Headers often contain dynamic data
            ValidateHttpUri = true,
            
            // TCP validation
            ValidateTcpData = true,
            
            // Network validation
            ValidateNetworkRequest = true,
            ValidateNetworkResponse = true,
            
            // Time exclusions (always off by default)
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };

        /// <summary>
        /// Configuration for grading only Client sheet (client-side).
        /// </summary>
        public static GradingConfig ClientOnly => new GradingConfig
        {
            GradeClientSheet = true,
            GradeServerSheet = false,
            GradeNetworkSheet = false,
            ValidateClientConsole = true,
            ValidateServerConsole = false,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };

        /// <summary>
        /// Configuration for grading only Server sheet (server-side).
        /// </summary>
        public static GradingConfig ServerOnly => new GradingConfig
        {
            GradeClientSheet = false,
            GradeServerSheet = true,
            GradeNetworkSheet = false,
            ValidateClientConsole = false,
            ValidateServerConsole = true,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };

        /// <summary>
        /// Configuration for validating only console outputs from both Client and Server sheets.
        /// Network validation is disabled.
        /// </summary>
        public static GradingConfig ConsoleOnly => new GradingConfig
        {
            GradeClientSheet = true,
            GradeServerSheet = true,
            GradeNetworkSheet = false,
            ValidateClientConsole = true,
            ValidateServerConsole = true,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };

        /// <summary>
        /// Configuration for validating only Network sheet data.
        /// Console output validation is disabled.
        /// </summary>
        public static GradingConfig NetworkOnly => new GradingConfig
        {
            GradeClientSheet = false,
            GradeServerSheet = false,
            GradeNetworkSheet = true,
            ValidateClientConsole = false,
            ValidateServerConsole = false,
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateHttpBody = true,
            ValidateTcpData = true,
            ValidateNetworkRequest = true,
            ValidateNetworkResponse = true,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };
        
        /// <summary>
        /// Configuration optimized for HTTP protocol grading.
        /// Enables validation of: Info, Flags, State, URI, Host, Method, Status, HttpVersion, HttpBody, SourceRole, DestinationRole
        /// Disables TCP-specific validation.
        /// </summary>
        public static GradingConfig HttpProtocol => new GradingConfig
        {
            GraderPort = PortKeywords.DEFAULT_GRADER_PORT,
            GradeClientSheet = true,
            GradeServerSheet = true,
            GradeNetworkSheet = true,
            ValidateClientConsole = true,
            ValidateServerConsole = true,
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateHttpBody = true,
            ValidateHttpHeaders = false,  // Headers often contain dynamic data
            ValidateHttpUri = true,
            ValidateTcpData = false,      // TCP data not relevant for HTTP
            ValidateNetworkRequest = true,
            ValidateNetworkResponse = true,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };
        
        /// <summary>
        /// Configuration optimized for TCP protocol grading.
        /// Enables validation of: Info, Flags, State, Data, SourceRole, DestinationRole
        /// Disables HTTP-specific validation.
        /// </summary>
        public static GradingConfig TcpProtocol => new GradingConfig
        {
            GraderPort = PortKeywords.DEFAULT_GRADER_PORT,
            GradeClientSheet = true,
            GradeServerSheet = true,
            GradeNetworkSheet = true,
            ValidateClientConsole = true,
            ValidateServerConsole = true,
            ValidateHttpMethod = false,   // HTTP method not relevant for TCP
            ValidateStatusCode = false,   // Status code not relevant for TCP
            ValidateHttpBody = false,     // HTTP body not relevant for TCP
            ValidateHttpHeaders = false,  // HTTP headers not relevant for TCP
            ValidateHttpUri = false,      // HTTP URI not relevant for TCP
            ValidateTcpData = true,       // TCP data is the main validation target
            ValidateNetworkRequest = true,
            ValidateNetworkResponse = true,
            ValidateTimeColumn = false,
            ValidateDateTimeValues = false
        };
        
        #endregion
        
        #region Protocol-Based Factory Methods
        
        /// <summary>
        /// Gets the appropriate grading config based on the protocol type.
        /// </summary>
        /// <param name="protocol">Protocol type (HTTP or TCP)</param>
        /// <returns>GradingConfig optimized for the specified protocol</returns>
        public static GradingConfig ForProtocol(string? protocol)
        {
            if (string.IsNullOrWhiteSpace(protocol))
                return Default;
            
            return protocol.ToUpperInvariant() switch
            {
                "HTTP" => HttpProtocol,
                "TCP" => TcpProtocol,
                _ => Default
            };
        }
        
        /// <summary>
        /// Gets the list of columns to grade for HTTP protocol.
        /// Excludes Time column as it varies with each run.
        /// </summary>
        public static readonly string[] HttpGradingColumns = new[]
        {
            NetworkKeywords.Col_Info,
            NetworkKeywords.Col_Flags,
            NetworkKeywords.Col_State,
            NetworkKeywords.Col_URI,
            NetworkKeywords.Col_Host,
            NetworkKeywords.Col_Method,
            NetworkKeywords.Col_Status,
            NetworkKeywords.Col_HttpVersion,
            NetworkKeywords.Col_HttpBody,
            NetworkKeywords.Col_SourceRole,
            NetworkKeywords.Col_DestinationRole
        };
        
        /// <summary>
        /// Gets the list of columns to grade for TCP protocol.
        /// Excludes Time column as it varies with each run.
        /// </summary>
        public static readonly string[] TcpGradingColumns = new[]
        {
            NetworkKeywords.Col_Info,
            NetworkKeywords.Col_Flags,
            NetworkKeywords.Col_State,
            NetworkKeywords.Col_Data,
            NetworkKeywords.Col_SourceRole,
            NetworkKeywords.Col_DestinationRole
        };
        
        /// <summary>
        /// Gets the appropriate grading columns based on the protocol type.
        /// </summary>
        /// <param name="protocol">Protocol type (HTTP or TCP)</param>
        /// <returns>Array of column names to grade</returns>
        public static string[] GetGradingColumnsForProtocol(string? protocol)
        {
            if (string.IsNullOrWhiteSpace(protocol))
                return HttpGradingColumns; // Default to HTTP
            
            return protocol.ToUpperInvariant() switch
            {
                "HTTP" => HttpGradingColumns,
                "TCP" => TcpGradingColumns,
                _ => HttpGradingColumns
            };
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Checks if a specific validation type is enabled.
        /// </summary>
        /// <param name="validationType">The validation type from step metadata.</param>
        /// <returns>True if the validation is enabled, false otherwise.</returns>
        public bool IsValidationEnabled(string validationType)
        {
            return validationType?.ToUpperInvariant() switch
            {
                "CLIENT_CONSOLE" or "CLIENT_OUTPUT" => ValidateClientConsole,
                "SERVER_CONSOLE" or "SERVER_OUTPUT" => ValidateServerConsole,
                "HTTP_METHOD" => ValidateHttpMethod,
                "STATUS_CODE" => ValidateStatusCode,
                "HTTP_BODY" => ValidateHttpBody,
                "HTTP_HEADERS" => ValidateHttpHeaders,
                "HTTP_URI" => ValidateHttpUri,
                "TCP_DATA" or "DATA" => ValidateTcpData,
                "NETWORK_REQUEST" or "DATA_REQUEST" => ValidateNetworkRequest,
                "NETWORK_RESPONSE" or "DATA_RESPONSE" => ValidateNetworkResponse,
                "TIME" => ValidateTimeColumn,
                "DATETIME" or "DATE" => ValidateDateTimeValues,
                _ => true
            };
        }
        
        /// <summary>
        /// Checks if a step should be graded based on its sheet origin.
        /// Step IDs follow the pattern: {SHEET}-{TYPE}-{STAGE}
        /// e.g., CLIENT-CONSOLE-1, SERVER-CONSOLE-2, NETWORK-METHOD-3
        /// </summary>
        /// <param name="stepId">The step ID.</param>
        /// <returns>True if the step should be graded, false if it should be skipped.</returns>
        public bool ShouldGradeStep(string stepId)
        {
            if (string.IsNullOrEmpty(stepId)) return true;
            
            var upperStepId = stepId.ToUpperInvariant();
            
            // Steps from Client sheet
            if (upperStepId.StartsWith("CLIENT-"))
                return GradeClientSheet;
            
            // Steps from Server sheet
            if (upperStepId.StartsWith("SERVER-"))
                return GradeServerSheet;
            
            // Steps from Network sheet
            if (upperStepId.StartsWith("NETWORK-"))
                return GradeNetworkSheet;
            
            // User sheet steps (actions) are always executed
            if (upperStepId.StartsWith("USER-"))
                return true;
            
            // Legacy format support (OC- and OS- prefixes)
            if (upperStepId.StartsWith("OC-"))
                return GradeClientSheet;
            if (upperStepId.StartsWith("OS-"))
                return GradeServerSheet;
            
            // Unknown prefix - grade by default
            return true;
        }
        
        /// <summary>
        /// [DEPRECATED] Use IsValidationEnabled instead.
        /// </summary>
        [Obsolete("Use IsValidationEnabled instead.")]
        public bool IsEnabled(string stepType) => IsValidationEnabled(stepType);
        
        #endregion
    }
}
