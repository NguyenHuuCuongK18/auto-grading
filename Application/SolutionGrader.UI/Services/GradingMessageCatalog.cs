using System;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Centralized catalog of all grading messages, errors, and status codes.
    /// 
    /// PURPOSE: Provides a single, easily accessible file containing all possible messages
    /// that can occur during the grading process. This makes it easy to:
    /// - Review all error messages in one place
    /// - Edit message text without searching through code
    /// - Understand what errors can occur and their severity
    /// - Add new messages consistently
    /// - Document error handling behavior
    /// </summary>
    public static class GradingMessageCatalog
    {
        #region Message Categories

        /// <summary>
        /// Student-related errors that should NOT abort the grading flow.
        /// These are logged but grading continues for other students.
        /// </summary>
        public static class StudentError
        {
            // Project/DLL errors
            public const string MissingClientDll = "Student did not provide required client DLL ({0})";
            public const string MissingServerDll = "Student did not provide required server DLL ({0})";
            public const string MissingBothDlls = "Student did not provide any DLLs (expected {0})";
            public const string DllNotFound = "DLL file not found at path: {0}";
            public const string InvalidDllPath = "Invalid DLL path structure: {0}";
            
            // Configuration errors
            public const string AppsettingsNotUsed = "Student code does not appear to use appsettings.json properly";
            public const string InvalidAppsettingsFormat = "Student's appsettings.json has invalid format";
            public const string MissingAppsettingsFile = "Student's solution missing appsettings.json";
            
            // Runtime errors
            public const string ProjectCrashed = "Student's {0} crashed during execution: {1}";
            public const string ProjectTimeout = "Student's {0} timed out after {1} seconds";
            public const string ProjectExitedEarly = "Student's {0} exited unexpectedly at stage {1}";
            public const string InvalidOutput = "Student's {0} produced invalid output format at stage {1}";
            
            // Connection errors
            public const string ConnectionFailed = "Student's client failed to connect to server";
            public const string PortAlreadyInUse = "Student's server port {0} is already in use";
            public const string DatabaseConnectionFailed = "Student's code failed to connect to database";
            
            // Compilation errors
            public const string CompilationError = "Student's project failed to compile: {0}";
            public const string MissingDependency = "Student's project missing required dependency: {0}";
        }

        /// <summary>
        /// Grader system errors that indicate problems with the grading infrastructure.
        /// These may abort grading for the current student but should be logged and handled.
        /// </summary>
        public static class GraderError
        {
            // Docker errors
            public const string DockerNotAvailable = "Docker is not available or not running";
            public const string DockerContainerFailed = "Failed to create Docker container: {0}";
            public const string DockerNetworkFailed = "Failed to create Docker network: {0}";
            public const string DockerCopyFailed = "Failed to copy files to container: {0}";
            public const string DockerExecFailed = "Failed to execute command in container: {0}";
            public const string DockerCleanupFailed = "Failed to cleanup Docker resources: {0}";
            
            // Network monitoring errors
            public const string NetworkMonitorFailed = "Network monitor failed to start: {0}";
            public const string NetworkCaptureFailed = "Failed to capture network traffic: {0}";
            public const string NetworkMonitorPermissionDenied = "Network monitor requires elevated permissions (sudo on Linux)";
            public const string LibpcapNotInstalled = "Network capture library not installed (libpcap on Linux, NPcap on Windows)";
            
            // File system errors
            public const string ResultFolderCreationFailed = "Failed to create result folder: {0}";
            public const string LogFileWriteFailed = "Failed to write log file: {0}";
            public const string ExcelFileWriteFailed = "Failed to write Excel file: {0}";
            public const string FileAccessDenied = "Access denied to file: {0}";
            
            // Grading flow errors
            public const string GradingFlowAborted = "Grading flow aborted due to critical error: {0}";
            public const string UnexpectedError = "Unexpected error during grading: {0}";
            public const string CancellationRequested = "Grading was cancelled by user";
        }

        /// <summary>
        /// Test kit and test case configuration errors.
        /// These prevent grading from starting for affected tests.
        /// </summary>
        public static class TestKitError
        {
            // Test kit structure errors
            public const string TestKitNotFound = "Test kit not found at path: {0}";
            public const string TestKitMalformed = "Test kit is malformed: {0}";
            public const string HeaderMissing = "Test kit missing required Header.xlsx file";
            public const string EnvironmentMissing = "Test kit missing required Environment.xlsx file";
            public const string InvalidHeaderFormat = "Header.xlsx has invalid format: {0}";
            public const string InvalidEnvironmentFormat = "Environment.xlsx has invalid format: {0}";
            
            // Test case errors
            public const string TestCaseMalformed = "Test case '{0}' is malformed: {1}";
            public const string DetailMissing = "Test case '{0}' missing required Detail.xlsx file";
            public const string InvalidDetailFormat = "Test case '{0}' has invalid Detail.xlsx format: {1}";
            public const string InvalidTestCaseConfig = "Test case '{0}' has invalid configuration: {1}";
            public const string MissingExpectedOutput = "Test case '{0}' missing expected output at stage {1}";
            
            // Mapping errors
            public const string MappingNotFound = "No test kit mapping found for paper {0}";
            public const string InvalidMapping = "Mapping.xlsx has invalid format: {0}";
            public const string PaperNotInMapping = "Paper {0} not found in Mapping.xlsx";
            
            // Golden code errors
            public const string GoldenServerMissing = "Test kit missing golden server in Meta/Given/Server";
            public const string GoldenClientMissing = "Test kit missing golden client in Meta/Given/Client";
            public const string InvalidGoldenCode = "Golden code in Meta/Given is invalid: {0}";
        }

        /// <summary>
        /// Informational and debug messages for logging.
        /// </summary>
        public static class Info
        {
            // Grading progress
            public const string GradingStarted = "Started grading for student: {0} (Paper {1})";
            public const string GradingCompleted = "Completed grading for student: {0} - Score: {1}/{2}";
            public const string TestCaseStarted = "Executing test case: {0}";
            public const string TestCaseCompleted = "Test case {0}: {1} ({2}/{3})";
            
            // Setup messages
            public const string DockerSetupStarted = "Setting up Docker containers for student {0}";
            public const string DockerSetupCompleted = "Docker containers ready for student {0}";
            public const string NetworkMonitorStarted = "Network monitor started on port {0}";
            public const string NetworkMonitorStopped = "Network monitor stopped";
            
            // File operations
            public const string FileCopied = "Copied {0} to container";
            public const string AppsettingsGenerated = "Generated appsettings.json for {0}";
            public const string ResultsWritten = "Results written to {0}";
            
            // Debug messages
            public const string DebugOutput = "DEBUG: {0}";
            public const string DebugContainerState = "Container {0} state: {1}";
            public const string DebugPortAllocation = "Allocated port {0} for student {1}";
        }

        /// <summary>
        /// Warning messages that don't stop grading but indicate potential issues.
        /// </summary>
        public static class Warning
        {
            public const string SlowExecution = "Test case {0} is taking longer than expected ({1}s)";
            public const string PartialOutput = "Received partial output from {0} at stage {1}";
            public const string NetworkPacketsDropped = "Some network packets may have been dropped during capture";
            public const string ConfigurationOverride = "Test kit configuration overridden by user settings";
            public const string LegacyTestKit = "Using legacy test kit format - consider updating";
            public const string PortConflict = "Port {0} may be in use, assigned alternative port {1}";
        }

        #endregion

        #region Error Severity Levels

        /// <summary>
        /// Defines how severely an error affects the grading process.
        /// </summary>
        public enum ErrorSeverity
        {
            /// <summary>
            /// Information only, no error
            /// </summary>
            Info = 0,
            
            /// <summary>
            /// Warning - grading continues normally
            /// </summary>
            Warning = 1,
            
            /// <summary>
            /// Student error - affects this student only, grading continues for others
            /// </summary>
            StudentError = 2,
            
            /// <summary>
            /// Test case error - skips this test case, continues with others
            /// </summary>
            TestCaseError = 3,
            
            /// <summary>
            /// Grader error - may affect current student, but session continues
            /// </summary>
            GraderError = 4,
            
            /// <summary>
            /// Critical error - requires manual intervention, session may abort
            /// </summary>
            Critical = 5
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Formats a message with parameters.
        /// </summary>
        public static string Format(string messageTemplate, params object[] args)
        {
            try
            {
                return string.Format(messageTemplate, args);
            }
            catch
            {
                return messageTemplate; // Return template if formatting fails
            }
        }

        /// <summary>
        /// Determines if an error should abort grading for the current student.
        /// </summary>
        public static bool ShouldAbortStudent(ErrorSeverity severity)
        {
            return severity >= ErrorSeverity.TestCaseError;
        }

        /// <summary>
        /// Determines if an error should abort the entire grading session.
        /// </summary>
        public static bool ShouldAbortSession(ErrorSeverity severity)
        {
            return severity == ErrorSeverity.Critical;
        }

        #endregion
    }

    /// <summary>
    /// Represents a structured grading message with severity and context.
    /// </summary>
    public class GradingMessage
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string StudentCode { get; set; } = "";
        public string? TestCase { get; set; }
        public int? Stage { get; set; }
        public GradingMessageCatalog.ErrorSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public string? StackTrace { get; set; }
        public Exception? Exception { get; set; }

        public override string ToString()
        {
            var parts = new System.Collections.Generic.List<string>
            {
                $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}]",
                $"[{Severity}]"
            };

            if (!string.IsNullOrEmpty(StudentCode))
                parts.Add($"[{StudentCode}]");

            if (!string.IsNullOrEmpty(TestCase))
                parts.Add($"[{TestCase}]");

            if (Stage.HasValue)
                parts.Add($"[Stage {Stage}]");

            parts.Add(Message);

            return string.Join(" ", parts);
        }
    }
}
