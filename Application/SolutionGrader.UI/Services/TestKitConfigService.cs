using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for reading test kit configuration from Excel files.
    /// Reads Environment.xlsx for port configurations and Header.xlsx for mark allocations.
    /// 
    /// This service ensures that:
    /// 1. Code_Container_Internal_Port and Code_Container_Host_Port are read from environment.xlsx
    /// 2. Max points for each test case are read from Header.xlsx (QuestionMark sheet)
    /// 3. Database configuration is properly loaded for MSSQL container setup
    /// 
    /// Note: TestKitConfig class is now shared with CLI in Lib/SolutionGrader.Core/Domain/Models/TestKitConfig.cs
    /// </summary>
    public class TestKitConfigService
    {
        private readonly ILoggingService _logger;

        public TestKitConfigService(ILoggingService logger)
        {
            _logger = logger;
        }

        // TestKitConfig class removed - now using shared version from SolutionGrader.Core.Domain.Models


        /// <summary>
        /// Loads the complete test kit configuration from Environment.xlsx and Header.xlsx
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder (e.g., TestKit/Q1)</param>
        /// <returns>TestKitConfig with all settings, or null if files are missing</returns>
        public TestKitConfig? LoadTestKitConfig(string testKitPath)
        {
            if (!Directory.Exists(testKitPath))
            {
                _logger.LogError($"Test kit folder not found: {testKitPath}");
                return null;
            }

            var config = new TestKitConfig();

            // Load Environment.xlsx
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (File.Exists(envPath))
            {
                LoadEnvironmentConfig(envPath, config);
            }
            else
            {
                _logger.LogWarning($"Environment.xlsx not found in {testKitPath}");
            }

            // Load Header.xlsx for test case marks
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                LoadHeaderConfig(headerPath, config);
            }
            else
            {
                _logger.LogWarning($"Header.xlsx not found in {testKitPath}");
            }

            return config;
        }

        /// <summary>
        /// Loads configuration from Environment.xlsx
        /// </summary>
        private void LoadEnvironmentConfig(string envPath, TestKitConfig config)
        {
            try
            {
                _logger.LogDebug($"Loading environment configuration from {envPath}");

                using var workbook = new XLWorkbook(envPath);
                
                // Find Config sheet
                if (!workbook.TryGetWorksheet("Config", out var configSheet))
                {
                    _logger.LogWarning("Config sheet not found in Environment.xlsx");
                    return;
                }

                // Read key-value pairs from Config sheet
                var configDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rows = configSheet.RowsUsed().Skip(1); // Skip header row

                foreach (var row in rows)
                {
                    var key = row.Cell(1).GetValue<string>()?.Trim();
                    var value = row.Cell(2).GetValue<string>()?.Trim();

                    if (!string.IsNullOrEmpty(key))
                    {
                        configDict[key] = value ?? "";
                    }
                }

                // Map configuration values
                // Port configurations - these are critical for network monitoring
                if (configDict.TryGetValue("Code_Container_Internal_Port", out var internalPort) && int.TryParse(internalPort, out var ip))
                {
                    config.CodeContainerInternalPort = ip;
                    _logger.LogDebug($"Code_Container_Internal_Port: {ip}");
                }

                if (configDict.TryGetValue("Code_Container_Host_Port", out var hostPort) && int.TryParse(hostPort, out var hp))
                {
                    config.CodeContainerHostPort = hp;
                    _logger.LogDebug($"Code_Container_Host_Port: {hp}");
                }

                // Database configurations
                if (configDict.TryGetValue("Database_Image_Name", out var dbImage))
                    config.DatabaseImageName = dbImage;

                if (configDict.TryGetValue("Database_Container_Name", out var dbContainer))
                    config.DatabaseContainerName = dbContainer;

                if (configDict.TryGetValue("Database_Container_Internal_Port", out var dbInternalPort) && int.TryParse(dbInternalPort, out var dbip))
                    config.DatabaseContainerInternalPort = dbip;

                if (configDict.TryGetValue("Database_Container_Host_Port", out var dbHostPort) && int.TryParse(dbHostPort, out var dbhp))
                    config.DatabaseContainerHostPort = dbhp;

                if (configDict.TryGetValue("Database_Username", out var dbUser))
                    config.DatabaseUsername = dbUser;

                if (configDict.TryGetValue("Database_Password", out var dbPass))
                    config.DatabasePassword = dbPass;

                if (configDict.TryGetValue("Default_Database_Name", out var dbName))
                    config.DefaultDatabaseName = dbName;

                if (configDict.TryGetValue("Default_Database_File_Path", out var dbFilePath))
                    config.DefaultDatabaseFilePath = dbFilePath;

                // Code container configurations
                if (configDict.TryGetValue("Code_Image_Name", out var codeImage))
                    config.CodeImageName = codeImage;

                if (configDict.TryGetValue("Code_Container_Name", out var codeContainer))
                    config.CodeContainerName = codeContainer;

                if (configDict.TryGetValue("Docker_Network", out var network))
                    config.DockerNetwork = network;

                if (configDict.TryGetValue("App_Type", out var appType))
                    config.AppType = appType;

                if (configDict.TryGetValue("Environment_Type", out var envType))
                    config.EnvironmentType = envType;

                if (configDict.TryGetValue("Runtimes_Folder", out var runtimes))
                    config.RuntimesFolder = runtimes;

                // Given (reference) executable paths
                // These are used when a student provides only client or only server
                // The other component is taken from the testkit's Meta/Given folder
                if (configDict.TryGetValue("Client", out var givenClient))
                {
                    config.GivenClientPath = givenClient;
                    _logger.LogDebug($"Given Client path: {givenClient}");
                }
                
                if (configDict.TryGetValue("Server", out var givenServer))
                {
                    config.GivenServerPath = givenServer;
                    _logger.LogDebug($"Given Server path: {givenServer}");
                }

                _logger.LogInfo($"Loaded environment config: Internal Port={config.CodeContainerInternalPort}, Host Port={config.CodeContainerHostPort}, DB={config.DatabaseContainerName}");
                if (!string.IsNullOrEmpty(config.GivenServerPath) || !string.IsNullOrEmpty(config.GivenClientPath))
                {
                    _logger.LogInfo($"Given executables: Client={config.GivenClientPath}, Server={config.GivenServerPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading Environment.xlsx: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads test case marks from Header.xlsx
        /// </summary>
        private void LoadHeaderConfig(string headerPath, TestKitConfig config)
        {
            try
            {
                _logger.LogDebug($"Loading header configuration from {headerPath}");

                using var workbook = new XLWorkbook(headerPath);
                
                // Try QuestionMark sheet first (new format)
                if (workbook.TryGetWorksheet("QuestionMark", out var markSheet))
                {
                    var rows = markSheet.RowsUsed().Skip(1); // Skip header row
                    foreach (var row in rows)
                    {
                        var testCaseName = row.Cell(1).GetString()?.Trim();
                        
                        // Safely parse mark value - handle both numeric and text cells
                        double markValue = 0.0;
                        if (row.Cell(2).TryGetValue<double>(out var directValue))
                        {
                            markValue = directValue;
                        }
                        else
                        {
                            var markStr = row.Cell(2).GetString().Trim();
                            if (!double.TryParse(markStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out markValue))
                            {
                                _logger.LogWarning($"Cannot parse mark value '{markStr}' for test case '{testCaseName}' - defaulting to 0");
                                markValue = 0.0;
                            }
                        }

                        if (!string.IsNullOrEmpty(testCaseName))
                        {
                            config.TestCaseMarks[testCaseName] = markValue;
                            _logger.LogDebug($"Test case {testCaseName}: {markValue} points");
                        }
                    }
                }
                // Fallback to TestSuite sheet
                else if (workbook.TryGetWorksheet("TestSuite", out var suiteSheet))
                {
                    var rows = suiteSheet.RowsUsed().Skip(1); // Skip header row
                    foreach (var row in rows)
                    {
                        var testCaseName = row.Cell(1).GetString()?.Trim();
                        
                        // Safely parse mark value - handle both numeric and text cells
                        double markValue = 0.0;
                        if (row.Cell(2).TryGetValue<double>(out var directValue))
                        {
                            markValue = directValue;
                        }
                        else
                        {
                            var markStr = row.Cell(2).GetString().Trim();
                            if (!double.TryParse(markStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out markValue))
                            {
                                markValue = 0.0;
                            }
                        }

                        if (!string.IsNullOrEmpty(testCaseName))
                        {
                            config.TestCaseMarks[testCaseName] = markValue;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Neither QuestionMark nor TestSuite sheet found in Header.xlsx");
                }

                // Try Config sheet for protocol
                if (workbook.TryGetWorksheet("Config", out var configSheet))
                {
                    var rows = configSheet.RowsUsed().Skip(1);
                    foreach (var row in rows)
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim();
                        var value = row.Cell(2).GetValue<string>()?.Trim();

                        if (string.Equals(key, "Protocol", StringComparison.OrdinalIgnoreCase))
                        {
                            config.Protocol = value ?? "TCP";
                        }
                    }
                }

                _logger.LogInfo($"Loaded {config.TestCaseMarks.Count} test cases with total max mark: {config.TotalMaxMark}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading Header.xlsx: {ex.Message}");
            }
        }
        
        // NOTE: GetTestCaseNames, GetTestCaseActions, GetExpectedOutputs, and GetExpectedNetworkFlow methods 
        // were removed as they were never used. DockerGradingService has its own implementation:
        // - ReadExpectedOutputs() reads expected outputs from Detail.xlsx
        // - ReadExpectedNetwork() reads expected network flow from Detail.xlsx
    }
}
