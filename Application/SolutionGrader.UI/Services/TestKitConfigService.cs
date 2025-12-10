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

        /// <summary>
        /// Gets the list of test cases available in a test kit
        /// </summary>
        public List<string> GetTestCaseNames(string testKitPath)
        {
            var testCases = new List<string>();

            if (!Directory.Exists(testKitPath))
                return testCases;

            // Get subdirectories that contain Detail.xlsx (test case folders)
            var directories = Directory.GetDirectories(testKitPath);
            foreach (var dir in directories)
            {
                var dirName = Path.GetFileName(dir);
                
                // Skip Meta folder
                if (dirName.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if this is a test case folder (contains Detail.xlsx)
                var detailPath = Path.Combine(dir, "Detail.xlsx");
                if (File.Exists(detailPath))
                {
                    testCases.Add(dirName);
                }
            }

            return testCases.OrderBy(tc => tc).ToList();
        }

        /// <summary>
        /// Reads the test case actions from Detail.xlsx for proper execution order
        /// Returns actions like StartClient, StartServer, Input, CloseClient, CloseServer
        /// </summary>
        public List<(int Stage, string Input, string Action)> GetTestCaseActions(string testCasePath)
        {
            var actions = new List<(int Stage, string Input, string Action)>();
            
            var detailPath = Path.Combine(testCasePath, "Detail.xlsx");
            if (!File.Exists(detailPath))
            {
                _logger.LogWarning($"Detail.xlsx not found in {testCasePath}");
                return actions;
            }

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                
                // Read User sheet for actions
                if (workbook.TryGetWorksheet("User", out var userSheet))
                {
                    var rows = userSheet.RowsUsed().Skip(1); // Skip header
                    foreach (var row in rows)
                    {
                        var stageValue = row.Cell(1).GetValue<string>();
                        var input = row.Cell(2).GetValue<string>() ?? "";
                        var action = row.Cell(3).GetValue<string>() ?? "";

                        if (int.TryParse(stageValue, out var stage) && !string.IsNullOrEmpty(action))
                        {
                            actions.Add((stage, input, action));
                            _logger.LogDebug($"Action: Stage={stage}, Input='{input}', Action={action}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading Detail.xlsx: {ex.Message}");
            }

            return actions;
        }

        /// <summary>
        /// Represents expected outputs for a specific stage from Detail.xlsx
        /// </summary>
        public class ExpectedOutput
        {
            public int Stage { get; set; }
            public string? ClientConsole { get; set; }
            public string? ServerConsole { get; set; }
        }
        
        /// <summary>
        /// Represents expected network traffic for a specific stage from Detail.xlsx Network sheet.
        /// The Network sheet defines the expected TCP handshake and data flow patterns.
        /// </summary>
        public class ExpectedNetworkFlow
        {
            public int Stage { get; set; }
            public string? Time { get; set; }
            public string? Info { get; set; }
            public string? Source { get; set; }
            public string? Destination { get; set; }
            public string? Flags { get; set; }
            public string? State { get; set; }
            public string? Data { get; set; }
            public string? SourceRole { get; set; }
            public string? DestinationRole { get; set; }
        }

        /// <summary>
        /// Reads the expected outputs from Detail.xlsx Client and Server sheets.
        /// These are the expected console outputs that need to be compared against actual outputs.
        /// </summary>
        /// <param name="testCasePath">Path to the test case folder</param>
        /// <returns>Dictionary mapping stage number to expected outputs</returns>
        public Dictionary<int, ExpectedOutput> GetExpectedOutputs(string testCasePath)
        {
            var expectedOutputs = new Dictionary<int, ExpectedOutput>();
            
            var detailPath = Path.Combine(testCasePath, "Detail.xlsx");
            if (!File.Exists(detailPath))
            {
                _logger.LogWarning($"Detail.xlsx not found in {testCasePath}");
                return expectedOutputs;
            }

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                
                // Read Client sheet for expected client console output
                if (workbook.TryGetWorksheet("Client", out var clientSheet))
                {
                    var rows = clientSheet.RowsUsed().Skip(1); // Skip header
                    foreach (var row in rows)
                    {
                        var stageValue = row.Cell(1).GetValue<string>();
                        var consoleOutput = row.Cell(2).GetValue<string>();

                        if (int.TryParse(stageValue, out var stage))
                        {
                            if (!expectedOutputs.ContainsKey(stage))
                            {
                                expectedOutputs[stage] = new ExpectedOutput { Stage = stage };
                            }
                            expectedOutputs[stage].ClientConsole = consoleOutput;
                            _logger.LogDebug($"Expected Client output at Stage {stage}: '{consoleOutput}'");
                        }
                    }
                }

                // Read Server sheet for expected server console output
                if (workbook.TryGetWorksheet("Server", out var serverSheet))
                {
                    var rows = serverSheet.RowsUsed().Skip(1); // Skip header
                    foreach (var row in rows)
                    {
                        var stageValue = row.Cell(1).GetValue<string>();
                        var consoleOutput = row.Cell(2).GetValue<string>();

                        if (int.TryParse(stageValue, out var stage))
                        {
                            if (!expectedOutputs.ContainsKey(stage))
                            {
                                expectedOutputs[stage] = new ExpectedOutput { Stage = stage };
                            }
                            expectedOutputs[stage].ServerConsole = consoleOutput;
                            _logger.LogDebug($"Expected Server output at Stage {stage}: '{consoleOutput}'");
                        }
                    }
                }

                _logger.LogInfo($"Loaded expected outputs for {expectedOutputs.Count} stages");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading Detail.xlsx for expected outputs: {ex.Message}");
            }

            return expectedOutputs;
        }
        
        /// <summary>
        /// Reads the expected network flow from Detail.xlsx Network sheet.
        /// The Network sheet defines the expected TCP handshake (SYN, SYN-ACK, ACK)
        /// and data flow patterns that should occur during the test case.
        /// 
        /// This is the whole point of network monitoring - to capture and verify
        /// the TCP/UDP communication patterns between client and server.
        /// </summary>
        /// <param name="testCasePath">Path to the test case folder</param>
        /// <returns>List of expected network flow entries</returns>
        public List<ExpectedNetworkFlow> GetExpectedNetworkFlow(string testCasePath)
        {
            var networkFlows = new List<ExpectedNetworkFlow>();
            
            var detailPath = Path.Combine(testCasePath, "Detail.xlsx");
            if (!File.Exists(detailPath))
            {
                _logger.LogWarning($"Detail.xlsx not found in {testCasePath}");
                return networkFlows;
            }

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                
                // Read Network sheet for expected network traffic patterns
                if (workbook.TryGetWorksheet("Network", out var networkSheet))
                {
                    _logger.LogInfo("Found Network sheet in Detail.xlsx - reading expected network flow");
                    
                    // Get header row to find column indices
                    var headerRow = networkSheet.Row(1);
                    var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    
                    // Get the last used column number, defaulting to 0 if no cells are used
                    var lastCell = headerRow.LastCellUsed();
                    int lastColumnNumber = lastCell?.Address.ColumnNumber ?? 0;
                    
                    for (int col = 1; col <= lastColumnNumber; col++)
                    {
                        var headerName = headerRow.Cell(col).GetValue<string>()?.Trim();
                        if (!string.IsNullOrEmpty(headerName))
                        {
                            headers[headerName] = col;
                        }
                    }
                    
                    _logger.LogDebug($"Network sheet columns: {string.Join(", ", headers.Keys)}");
                    
                    var rows = networkSheet.RowsUsed().Skip(1); // Skip header
                    foreach (var row in rows)
                    {
                        var flow = new ExpectedNetworkFlow();
                        
                        // Read stage
                        if (headers.TryGetValue("Stage", out var stageCol))
                        {
                            var stageValue = row.Cell(stageCol).GetValue<string>();
                            if (int.TryParse(stageValue, out var stage))
                            {
                                flow.Stage = stage;
                            }
                        }
                        
                        // Read other columns
                        if (headers.TryGetValue("Time", out var timeCol))
                            flow.Time = row.Cell(timeCol).GetValue<string>();
                        if (headers.TryGetValue("Info", out var infoCol))
                            flow.Info = row.Cell(infoCol).GetValue<string>();
                        if (headers.TryGetValue("Source", out var srcCol))
                            flow.Source = row.Cell(srcCol).GetValue<string>();
                        if (headers.TryGetValue("Destination", out var dstCol))
                            flow.Destination = row.Cell(dstCol).GetValue<string>();
                        if (headers.TryGetValue("Flags", out var flagsCol))
                            flow.Flags = row.Cell(flagsCol).GetValue<string>();
                        if (headers.TryGetValue("State", out var stateCol))
                            flow.State = row.Cell(stateCol).GetValue<string>();
                        if (headers.TryGetValue("Data", out var dataCol))
                            flow.Data = row.Cell(dataCol).GetValue<string>();
                        if (headers.TryGetValue("SourceRole", out var srcRoleCol))
                            flow.SourceRole = row.Cell(srcRoleCol).GetValue<string>();
                        if (headers.TryGetValue("DestinationRole", out var dstRoleCol))
                            flow.DestinationRole = row.Cell(dstRoleCol).GetValue<string>();
                        
                        // Only add if there's meaningful data
                        if (!string.IsNullOrEmpty(flow.Flags) || !string.IsNullOrEmpty(flow.Data))
                        {
                            networkFlows.Add(flow);
                            _logger.LogDebug($"Expected Network flow at Stage {flow.Stage}: [{flow.Flags}] {flow.SourceRole}->{flow.DestinationRole}");
                        }
                    }
                    
                    _logger.LogInfo($"Loaded {networkFlows.Count} expected network flow entries");
                }
                else
                {
                    _logger.LogDebug("No Network sheet found in Detail.xlsx - network grading will be skipped for this test case");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading Detail.xlsx for expected network flow: {ex.Message}");
            }

            return networkFlows;
        }
    }
}
