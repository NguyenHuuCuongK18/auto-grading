using System;
using System.Collections.Generic;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration loaded from a test kit's Header.xlsx and Environment.xlsx files.
    /// Contains all settings needed to grade students against this test kit.
    /// </summary>
    public class TestKitConfig
    {
        /// <summary>
        /// Path to the test kit root folder
        /// </summary>
        public string TestKitPath { get; set; } = string.Empty;

        /// <summary>
        /// Protocol type (TCP, HTTP, etc.)
        /// </summary>
        public string Protocol { get; set; } = "TCP";

        /// <summary>
        /// Total maximum mark for all test cases
        /// </summary>
        public double TotalMaxMark { get; set; }

        /// <summary>
        /// Dictionary of test case names to their max marks
        /// </summary>
        public Dictionary<string, double> TestCaseMarks { get; set; } = new();

        /// <summary>
        /// List of test case names in order
        /// </summary>
        public List<string> TestCaseNames { get; set; } = new();

        #region Port Configuration

        /// <summary>
        /// Port for network monitoring (server listens, client connects)
        /// </summary>
        public int MonitorPort { get; set; } = 8888;

        /// <summary>
        /// Internal port inside the code container
        /// </summary>
        public int CodeContainerInternalPort { get; set; } = 5000;

        /// <summary>
        /// Host port mapped to the code container
        /// </summary>
        public int CodeContainerHostPort { get; set; } = 5000;

        /// <summary>
        /// Internal port for the client (given console) container
        /// </summary>
        public int GivenConsoleContainerInternalPort { get; set; } = 5001;

        /// <summary>
        /// Host port mapped to the client container
        /// </summary>
        public int GivenConsoleContainerHostPort { get; set; } = 5001;

        #endregion

        #region Database Configuration
        // NOTE: All database configuration is read from Environment.xlsx
        // No hardcoded defaults - these MUST be provided by the test kit

        /// <summary>
        /// Docker image name for the database container.
        /// Read from Environment.xlsx (Database_Image_Name key).
        /// </summary>
        public string DatabaseImageName { get; set; } = string.Empty;

        /// <summary>
        /// Name of the database container.
        /// Read from Environment.xlsx (Database_Container_Name key).
        /// </summary>
        public string DatabaseContainerName { get; set; } = string.Empty;

        /// <summary>
        /// Internal port for the database container.
        /// Read from Environment.xlsx (Database_Container_Internal_Port key).
        /// </summary>
        public int DatabaseContainerInternalPort { get; set; }

        /// <summary>
        /// Host port mapped to the database container.
        /// Read from Environment.xlsx (Database_Container_Host_Port key).
        /// </summary>
        public int DatabaseContainerHostPort { get; set; }

        /// <summary>
        /// Database username (e.g., SA for SQL Server).
        /// Read from Environment.xlsx (Database_Username key).
        /// </summary>
        public string DatabaseUsername { get; set; } = string.Empty;

        /// <summary>
        /// Database password.
        /// Read from Environment.xlsx (Database_Password key).
        /// IMPORTANT: Never hardcode - always read from Environment.xlsx.
        /// </summary>
        public string DatabasePassword { get; set; } = string.Empty;

        /// <summary>
        /// Default database name.
        /// Read from Environment.xlsx (Default_Database_Name key).
        /// </summary>
        public string DatabaseName { get; set; } = string.Empty;

        /// <summary>
        /// Path to the default database script file.
        /// Read from Environment.xlsx (Default_Database_File_Path key).
        /// </summary>
        public string? DefaultDatabaseFilePath { get; set; }

        #endregion

        #region Container Configuration

        /// <summary>
        /// Docker image name for the code container
        /// </summary>
        public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8:latest";

        /// <summary>
        /// Docker image name for the client (given console) container
        /// </summary>
        public string GivenConsoleImageName { get; set; } = "fptuxaes/aes-dotnet8:latest";

        /// <summary>
        /// Docker network name for container communication
        /// </summary>
        public string DockerNetwork { get; set; } = "auto-grading-network";

        #endregion

        #region Given Executables

        /// <summary>
        /// Path to the given/reference server executable
        /// </summary>
        public string? GivenServerPath { get; set; }

        /// <summary>
        /// Path to the given/reference client executable
        /// </summary>
        public string? GivenClientPath { get; set; }

        /// <summary>
        /// Path to the runtimes folder for native dependencies
        /// </summary>
        public string? RuntimesFolder { get; set; }

        #endregion

        #region DateTime Configuration

        /// <summary>
        /// DateTime format pattern for grading (from DataPattern sheet)
        /// </summary>
        public string? DateTimeFormat { get; set; }

        /// <summary>
        /// Whether DateTime values should be excluded from grading
        /// </summary>
        public bool ExcludeDateTimeFromGrading { get; set; } = true;

        /// <summary>
        /// Whether Time values should be excluded from grading
        /// </summary>
        public bool ExcludeTimeFromGrading { get; set; } = true;

        #endregion
    }
}
