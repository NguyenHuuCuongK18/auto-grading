namespace SolutionGrader.Core.Domain.Models
{
    /// <summary>
    /// Configuration for controlling which aspects of the test kit to validate during grading.
    /// This allows easy toggling of validation checks for debugging and incremental testing.
    /// 
    /// Grading modes control which sheets to grade:
    /// - DEFAULT: Grade both OutputClients and OutputServers sheets (all validations)
    /// - CLIENT: Grade only OutputClients sheet (all validations on that sheet)
    /// - SERVER: Grade only OutputServers sheet (all validations on that sheet)
    /// - CONSOLE: Grade only console output columns from both sheets
    /// - HTTP: Grade only HTTP-related columns from both sheets
    /// </summary>
    public sealed class GradingConfig
    {
        /// <summary>
        /// Enable/disable grading of OutputClients sheet (client-side validations).
        /// When false, all steps from OutputClients sheet are skipped.
        /// </summary>
        public bool GradeOutputClientsSheet { get; set; } = true;

        /// <summary>
        /// Enable/disable grading of OutputServers sheet (server-side validations).
        /// When false, all steps from OutputServers sheet are skipped.
        /// </summary>
        public bool GradeOutputServersSheet { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of client console output against expected output.
        /// </summary>
        public bool ValidateClientOutput { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of server console output against expected output.
        /// </summary>
        public bool ValidateServerOutput { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of HTTP data response from server to client.
        /// </summary>
        public bool ValidateDataResponse { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of HTTP data request from client to server.
        /// </summary>
        public bool ValidateDataRequest { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of HTTP method (GET, POST, etc.).
        /// </summary>
        public bool ValidateHttpMethod { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of HTTP status code.
        /// </summary>
        public bool ValidateStatusCode { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of response byte size.
        /// </summary>
        public bool ValidateByteSize { get; set; } = false;

        /// <summary>
        /// Enable/disable validation of data type (JSON, CSV, Text, etc.).
        /// </summary>
        public bool ValidateDataType { get; set; } = true;

        /// <summary>
        /// Gets the default configuration with all validations enabled on both sheets.
        /// Grades both OutputClients and OutputServers sheets with all validations.
        /// </summary>
        public static GradingConfig Default => new GradingConfig();

        /// <summary>
        /// Creates a configuration for grading only OutputClients sheet (client-side).
        /// Grades client console output, data responses, HTTP methods, status codes, and byte sizes
        /// from the OutputClients sheet. Skips OutputServers sheet entirely.
        /// </summary>
        public static GradingConfig ClientOnly => new GradingConfig
        {
            GradeOutputClientsSheet = true,
            GradeOutputServersSheet = false,
            // Enable all validations for the OutputClients sheet
            ValidateClientOutput = true,
            ValidateDataResponse = true,
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateByteSize = false,
            ValidateDataType = true,
            // Server validations not applicable since OutputServers sheet is skipped
            ValidateServerOutput = false,
            ValidateDataRequest = false
        };

        /// <summary>
        /// Creates a configuration for grading only OutputServers sheet (server-side).
        /// Grades server console output, data requests, HTTP methods, and byte sizes
        /// from the OutputServers sheet. Skips OutputClients sheet entirely.
        /// </summary>
        public static GradingConfig ServerOnly => new GradingConfig
        {
            GradeOutputClientsSheet = false,
            GradeOutputServersSheet = true,
            // Enable all validations for the OutputServers sheet
            ValidateServerOutput = true,
            ValidateDataRequest = true,
            ValidateHttpMethod = true,
            ValidateByteSize = false,
            ValidateDataType = true,
            // Client validations not applicable since OutputClients sheet is skipped
            ValidateClientOutput = false,
            ValidateDataResponse = false,
            ValidateStatusCode = false
        };

        /// <summary>
        /// Creates a configuration for validating only console outputs from both sheets.
        /// Grades Output columns from both OutputClients and OutputServers sheets.
        /// Skips HTTP-related validations (methods, status codes, data requests/responses, byte sizes).
        /// </summary>
        public static GradingConfig ConsoleOutputOnly => new GradingConfig
        {
            GradeOutputClientsSheet = true,
            GradeOutputServersSheet = true,
            ValidateClientOutput = true,
            ValidateServerOutput = true,
            ValidateDataResponse = false,
            ValidateDataRequest = false,
            ValidateHttpMethod = false,
            ValidateStatusCode = false,
            ValidateByteSize = false,
            ValidateDataType = false
        };

        /// <summary>
        /// Creates a configuration for validating only HTTP traffic from both sheets.
        /// Grades HTTP-related columns (methods, status codes, data requests/responses, byte sizes)
        /// from both OutputClients and OutputServers sheets.
        /// Skips console output validations.
        /// </summary>
        public static GradingConfig HttpTrafficOnly => new GradingConfig
        {
            GradeOutputClientsSheet = true,
            GradeOutputServersSheet = true,
            ValidateClientOutput = false,
            ValidateServerOutput = false,
            ValidateDataResponse = true,
            ValidateDataRequest = true,
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateByteSize = false,
            ValidateDataType = true
        };

        /// <summary>
        /// Checks if a specific validation is enabled based on the step type.
        /// </summary>
        public bool IsEnabled(string stepType)
        {
            return stepType?.ToUpperInvariant() switch
            {
                "CLIENT_OUTPUT" => ValidateClientOutput,
                "SERVER_OUTPUT" => ValidateServerOutput,
                "DATA_RESPONSE" => ValidateDataResponse,
                "DATA_REQUEST" => ValidateDataRequest,
                "HTTP_METHOD" => ValidateHttpMethod,
                "STATUS_CODE" => ValidateStatusCode,
                "BYTE_SIZE" => ValidateByteSize,
                "DATA_TYPE" => ValidateDataType,
                _ => true
            };
        }

        /// <summary>
        /// Checks if a step should be graded based on its sheet origin (OC- or OS- prefix).
        /// </summary>
        /// <param name="stepId">The step ID, which should start with OC- or OS-</param>
        /// <returns>True if the step should be graded, false if it should be skipped</returns>
        public bool ShouldGradeStep(string stepId)
        {
            if (string.IsNullOrEmpty(stepId)) return true;
            
            // Steps from OutputClients sheet start with "OC-"
            if (stepId.StartsWith("OC-", StringComparison.OrdinalIgnoreCase))
                return GradeOutputClientsSheet;
            
            // Steps from OutputServers sheet start with "OS-"
            if (stepId.StartsWith("OS-", StringComparison.OrdinalIgnoreCase))
                return GradeOutputServersSheet;
            
            // Steps from other sources (e.g., InputClients) are always graded
            return true;
        }
    }
}
