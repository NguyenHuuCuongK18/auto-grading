namespace SolutionGrader.Core.Domain.Models
{
    /// <summary>
    /// Configuration for controlling which aspects of the test kit to validate during grading.
    /// This allows easy toggling of validation checks for debugging and incremental testing.
    /// </summary>
    public sealed class GradingConfig
    {
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
        public bool ValidateByteSize { get; set; } = true;

        /// <summary>
        /// Enable/disable validation of data type (JSON, CSV, Text, etc.).
        /// </summary>
        public bool ValidateDataType { get; set; } = true;

        /// <summary>
        /// Gets the default configuration with all validations enabled.
        /// </summary>
        public static GradingConfig Default => new GradingConfig();

        /// <summary>
        /// Creates a configuration for debugging client-side only.
        /// </summary>
        public static GradingConfig ClientOnly => new GradingConfig
        {
            ValidateClientOutput = true,
            ValidateServerOutput = false,
            ValidateDataResponse = true,
            ValidateDataRequest = false,
            ValidateHttpMethod = false,
            ValidateStatusCode = false,
            ValidateByteSize = false,
            ValidateDataType = false
        };

        /// <summary>
        /// Creates a configuration for debugging server-side only.
        /// </summary>
        public static GradingConfig ServerOnly => new GradingConfig
        {
            ValidateClientOutput = false,
            ValidateServerOutput = true,
            ValidateDataResponse = false,
            ValidateDataRequest = true,
            ValidateHttpMethod = false,
            ValidateStatusCode = false,
            ValidateByteSize = false,
            ValidateDataType = false
        };

        /// <summary>
        /// Creates a configuration for validating only console outputs (no HTTP validation).
        /// </summary>
        public static GradingConfig ConsoleOutputOnly => new GradingConfig
        {
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
        /// Creates a configuration for validating only HTTP traffic (no console output validation).
        /// </summary>
        public static GradingConfig HttpTrafficOnly => new GradingConfig
        {
            ValidateClientOutput = false,
            ValidateServerOutput = false,
            ValidateDataResponse = true,
            ValidateDataRequest = true,
            ValidateHttpMethod = true,
            ValidateStatusCode = true,
            ValidateByteSize = true,
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
    }
}
