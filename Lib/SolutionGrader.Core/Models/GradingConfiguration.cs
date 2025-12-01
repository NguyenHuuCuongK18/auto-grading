using System;

namespace SolutionGrader.Core.Models
{
    /// <summary>
    /// Configuration model for grading sessions.
    /// Contains all configurable parameters for Docker-based grading including:
    /// - Folder paths for submissions, test kits, and results
    /// - Client/Server project configuration
    /// - Port mappings for container networking
    /// - Database configuration
    /// 
    /// This configuration is set in SetupWindow and used throughout the grading process.
    /// </summary>
    public class GradingConfiguration
    {
        #region Folder Paths
        
        /// <summary>
        /// Path to the Submit folder containing student solutions.
        /// Structure: Submit/{PaperNo}/{StudentCode}/{QuestionNo}/solution/{ProjectName}
        /// </summary>
        public string SubmitFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the TestKit folder containing test cases.
        /// Structure: TestKit/{TestKitName}/TC{n}/Detail.xlsx
        /// </summary>
        public string TestKitFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to save grading results (logs, summaries, etc.)
        /// Output structure matches SampleLogging format:
        /// - {PaperNo}/StudentsSolution.xlsx
        /// - {PaperNo}/student/{StudentCode}/OverallSummary.xlsx
        /// - {PaperNo}/student/{StudentCode}/{TC}/GradeDetail.xlsx
        /// </summary>
        public string SaveResultFolderPath { get; set; } = string.Empty;

        #endregion

        #region Client/Server Configuration

        /// <summary>
        /// Whether the student solution includes a client component.
        /// If false, the "golden" Client from TestKit Meta/Given folder is used.
        /// </summary>
        public bool HasClient { get; set; } = true;

        /// <summary>
        /// Whether the student solution includes a server component.
        /// If false, the "golden" Server from TestKit Meta/Given folder is used.
        /// </summary>
        public bool HasServer { get; set; } = true;

        /// <summary>
        /// Project name for client DLL lookup.
        /// Used to find the published DLL file: {ClientProjectName}.dll
        /// </summary>
        public string ClientProjectName { get; set; } = "Client";

        /// <summary>
        /// Project name for server DLL lookup.
        /// Used to find the published DLL file: {ServerProjectName}.dll
        /// </summary>
        public string ServerProjectName { get; set; } = "Server";

        #endregion

        #region Docker Network Configuration

        /// <summary>
        /// Docker network name for container communication.
        /// Default: ag-network
        /// </summary>
        public string DockerNetwork { get; set; } = "ag-network";

        /// <summary>
        /// Internal port the server listens on inside the container.
        /// Read from TestKit Environment.xlsx
        /// </summary>
        public int CodeContainerInternalPort { get; set; } = 5001;

        /// <summary>
        /// Host port mapped to the server container port.
        /// This is the port the Network Monitor will sniff.
        /// Read from TestKit Environment.xlsx
        /// </summary>
        public int CodeContainerHostPort { get; set; } = 5001;

        /// <summary>
        /// Internal port for the client container (usually not exposed).
        /// Set to -1 if client doesn't listen on a port.
        /// </summary>
        public int ClientContainerInternalPort { get; set; } = -1;

        /// <summary>
        /// Host port for the client container.
        /// Usually not needed unless client exposes services.
        /// </summary>
        public int ClientContainerHostPort { get; set; } = -1;

        #endregion

        #region Docker Image Configuration

        /// <summary>
        /// Docker image name for the .NET runtime container.
        /// Default: fptuxaes/aes-dotnet8:latest
        /// </summary>
        public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8:latest";

        /// <summary>
        /// Container name for the server application.
        /// Default: ag-server
        /// </summary>
        public string ServerContainerName { get; set; } = "ag-server";

        /// <summary>
        /// Container name for the client application.
        /// Default: ag-client
        /// </summary>
        public string ClientContainerName { get; set; } = "ag-client";

        #endregion

        #region Database Configuration

        /// <summary>
        /// Docker image name for the MSSQL database container.
        /// Read from TestKit Environment.xlsx
        /// </summary>
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";

        /// <summary>
        /// Container name for the database.
        /// Default: ag-db
        /// </summary>
        public string DatabaseContainerName { get; set; } = "ag-db";

        /// <summary>
        /// Internal port for database (MSSQL default: 1433)
        /// </summary>
        public int DatabaseContainerInternalPort { get; set; } = 1433;

        /// <summary>
        /// Host port mapped to database container port.
        /// </summary>
        public int DatabaseContainerHostPort { get; set; } = 1433;

        /// <summary>
        /// Database username (default: SA for MSSQL)
        /// </summary>
        public string DatabaseUsername { get; set; } = "SA";

        /// <summary>
        /// Database password.
        /// Read from TestKit Environment.xlsx
        /// </summary>
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";

        #endregion

        #region Grading Configuration

        /// <summary>
        /// Timeout in seconds for each test case execution.
        /// </summary>
        public int TestCaseTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Delay in milliseconds between input steps to allow processing.
        /// </summary>
        public int InputDelayMs { get; set; } = 2000;

        /// <summary>
        /// Whether to clean up containers after grading each student.
        /// Set to false for debugging purposes.
        /// </summary>
        public bool CleanupAfterGrading { get; set; } = true;

        #endregion

        /// <summary>
        /// Creates a deep copy of this configuration.
        /// </summary>
        public GradingConfiguration Clone()
        {
            return (GradingConfiguration)this.MemberwiseClone();
        }

        /// <summary>
        /// Validates the configuration and returns error messages if invalid.
        /// </summary>
        public (bool IsValid, string ErrorMessage) Validate()
        {
            if (string.IsNullOrWhiteSpace(SubmitFolderPath))
                return (false, "Submit folder path is required.");

            if (string.IsNullOrWhiteSpace(TestKitFolderPath))
                return (false, "TestKit folder path is required.");

            if (string.IsNullOrWhiteSpace(SaveResultFolderPath))
                return (false, "Save result folder path is required.");

            if (HasClient && string.IsNullOrWhiteSpace(ClientProjectName))
                return (false, "Client project name is required when HasClient is true.");

            if (HasServer && string.IsNullOrWhiteSpace(ServerProjectName))
                return (false, "Server project name is required when HasServer is true.");

            return (true, string.Empty);
        }
    }
}
