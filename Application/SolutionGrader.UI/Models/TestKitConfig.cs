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

        /// <summary>
        /// Docker image name for the database container (e.g., mcr.microsoft.com/mssql/server:2022-latest)
        /// </summary>
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";

        /// <summary>
        /// Name of the database container
        /// </summary>
        public string DatabaseContainerName { get; set; } = "ag-database";

        /// <summary>
        /// Internal port for the database container
        /// </summary>
        public int DatabaseContainerInternalPort { get; set; } = 1433;

        /// <summary>
        /// Host port mapped to the database container
        /// </summary>
        public int DatabaseContainerHostPort { get; set; } = 1433;

        /// <summary>
        /// Database username (e.g., SA for SQL Server)
        /// </summary>
        public string DatabaseUsername { get; set; } = "SA";

        /// <summary>
        /// Database password
        /// </summary>
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";

        /// <summary>
        /// Default database name
        /// </summary>
        public string DatabaseName { get; set; } = "TestDB";

        /// <summary>
        /// Path to the default database script file
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
