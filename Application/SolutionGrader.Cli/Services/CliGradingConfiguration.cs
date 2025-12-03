using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.Cli.Services
{
    /// <summary>
    /// Configuration settings for CLI Docker grading session.
    /// This class mirrors SolutionGrader.UI.Models.GradingConfiguration to ensure
    /// the CLI and UI use the same configuration model.
    /// 
    /// Note: This is a simplified version without INotifyPropertyChanged since the CLI
    /// doesn't need data binding support.
    /// </summary>
    public class CliGradingConfiguration
    {
        /// <summary>
        /// Path to the submit folder containing all student solutions.
        /// Structure: Submit/[PaperNo]/[StudentCode]/[QuestionNo]/solution
        /// </summary>
        public string SubmitFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the test kit folder containing test cases.
        /// Structure: TestKit/[QuestionName]/Header.xlsx, Detail.xlsx, etc.
        /// </summary>
        public string TestKitFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the folder where grading results will be saved.
        /// Results include StudentsSolution.xlsx and student-specific folders.
        /// </summary>
        public string SaveResultFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// Whether the student solution contains a client component
        /// </summary>
        public bool HasClient { get; set; } = true;

        /// <summary>
        /// Whether the student solution contains a server component
        /// </summary>
        public bool HasServer { get; set; } = true;

        /// <summary>
        /// Project name for the client DLL (used to find the DLL file).
        /// Example: "Project12" will search for "Project12.dll"
        /// </summary>
        public string ClientProjectName { get; set; } = "Project12";

        /// <summary>
        /// Project name for the server DLL (used to find the DLL file).
        /// Example: "Project11" will search for "Project11.dll"
        /// </summary>
        public string ServerProjectName { get; set; } = "Project11";

        /// <summary>
        /// Internal port inside the Docker container for the application
        /// </summary>
        public int CodeContainerInternalPort { get; set; } = 8000;

        /// <summary>
        /// Host port mapped to the container port (for network monitoring)
        /// </summary>
        public int CodeContainerHostPort { get; set; } = 8000;

        /// <summary>
        /// Docker network name for container communication
        /// </summary>
        public string DockerNetwork { get; set; } = "auto-grading-network";

        /// <summary>
        /// Timeout in seconds for grading operations (overall)
        /// </summary>
        public int GradingTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Timeout in seconds per test case. If a test case exceeds this limit,
        /// it is stopped and marked as failed. Default: 15 seconds.
        /// </summary>
        public int TestCaseTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Database Docker image name (e.g., mcr.microsoft.com/mssql/server:2019-latest)
        /// </summary>
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2019-latest";

        /// <summary>
        /// Database container name
        /// </summary>
        public string DatabaseContainerName { get; set; } = "auto-grading-sqlserver";

        /// <summary>
        /// Database container internal port
        /// </summary>
        public int DatabaseContainerInternalPort { get; set; } = 1433;

        /// <summary>
        /// Database container host port
        /// </summary>
        public int DatabaseContainerHostPort { get; set; } = 1434;

        /// <summary>
        /// Database username (e.g., sa for MSSQL)
        /// </summary>
        public string DatabaseUsername { get; set; } = "sa";

        /// <summary>
        /// Database password
        /// </summary>
        public string DatabasePassword { get; set; } = "";

        /// <summary>
        /// Maximum number of students to grade in parallel.
        /// Default is 1 (sequential grading).
        /// Each student gets their own pair of containers with incrementing ports.
        /// </summary>
        public int MaxParallelStudents { get; set; } = 1;

        /// <summary>
        /// Start index for selective grading (0-based).
        /// Allows restarting from a specific student in case of incidents.
        /// </summary>
        public int StartIndex { get; set; } = 0;

        /// <summary>
        /// End index for selective grading (0-based, inclusive).
        /// -1 means grade all students from StartIndex to the end.
        /// </summary>
        public int EndIndex { get; set; } = -1;
    }
}
