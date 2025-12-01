using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for loading test kit configuration from Header.xlsx and Environment.xlsx files.
    /// Reads protocol, marks, database settings, port configurations, and other test kit settings.
    /// 
    /// The configuration is used to:
    /// - Set up Docker containers with correct ports and credentials
    /// - Determine total marks for grading
    /// - Configure network monitoring
    /// - Find given/reference executables
    /// </summary>
    public class TestKitConfigService
    {
        private readonly ILoggingService _logger;

        public TestKitConfigService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads the complete test kit configuration from the specified test kit folder.
        /// Reads from Header.xlsx and Environment.xlsx files.
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder</param>
        /// <returns>TestKitConfig with all configuration settings, or null if loading fails</returns>
        public TestKitConfig? LoadTestKitConfig(string testKitPath)
        {
            if (!Directory.Exists(testKitPath))
            {
                _logger.LogError($"Test kit folder not found: {testKitPath}");
                return null;
            }

            var config = new TestKitConfig
            {
                TestKitPath = testKitPath
            };

            try
            {
                // Load configuration from Header.xlsx
                var headerPath = Path.Combine(testKitPath, "Header.xlsx");
                if (File.Exists(headerPath))
                {
                    LoadFromHeader(config, headerPath);
                }
                else
                {
                    _logger.LogWarning($"Header.xlsx not found in test kit: {testKitPath}");
                }

                // Load configuration from Environment.xlsx
                var envPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (File.Exists(envPath))
                {
                    LoadFromEnvironment(config, envPath);
                }
                else
                {
                    _logger.LogWarning($"Environment.xlsx not found in test kit: {testKitPath}");
                }

                // Discover test cases and calculate total mark
                DiscoverTestCases(config, testKitPath);

                // Find given executables from Meta/Given folder
                FindGivenExecutables(config, testKitPath);

                // Find runtimes folder
                FindRuntimesFolder(config, testKitPath);

                _logger.LogInfo($"Loaded test kit config: {config.TestCaseNames.Count} test cases, total mark: {config.TotalMaxMark}");
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load test kit config from {testKitPath}", ex);
                return null;
            }
        }

        /// <summary>
        /// Loads configuration from Header.xlsx file.
        /// Reads protocol, marks, and datetime format settings.
        /// </summary>
        private void LoadFromHeader(TestKitConfig config, string headerPath)
        {
            try
            {
                using var workbook = new XLWorkbook(headerPath);

                // Read protocol from Config sheet
                var configSheet = workbook.Worksheets.FirstOrDefault(w =>
                    w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));
                if (configSheet != null)
                {
                    config.Protocol = ReadKeyValue(configSheet, "Protocol") ?? "TCP";
                }

                // Read marks from QuestionMark sheet
                var markSheet = workbook.Worksheets.FirstOrDefault(w =>
                    w.Name.Equals("QuestionMark", StringComparison.OrdinalIgnoreCase) ||
                    w.Name.Equals("Question_Mark", StringComparison.OrdinalIgnoreCase));
                if (markSheet != null)
                {
                    ReadMarks(config, markSheet);
                }

                // Read datetime format from DataPattern sheet
                var patternSheet = workbook.Worksheets.FirstOrDefault(w =>
                    w.Name.Equals("DataPattern", StringComparison.OrdinalIgnoreCase) ||
                    w.Name.Equals("Data_Pattern", StringComparison.OrdinalIgnoreCase));
                if (patternSheet != null)
                {
                    ReadDateTimeFormat(config, patternSheet);
                }

                _logger.LogDebug($"Header loaded: Protocol={config.Protocol}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading Header.xlsx: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads configuration from Environment.xlsx file.
        /// Reads port configurations, database settings, and Docker container settings.
        /// </summary>
        private void LoadFromEnvironment(TestKitConfig config, string envPath)
        {
            try
            {
                using var workbook = new XLWorkbook(envPath);
                var configSheet = workbook.Worksheets.FirstOrDefault(w =>
                    w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));

                if (configSheet == null)
                {
                    _logger.LogWarning($"No Config sheet found in Environment.xlsx");
                    return;
                }

                // Determine start row (skip header if present)
                int startRow = 1;
                var firstCell = configSheet.Cell(1, 1).GetString().Trim();
                if (firstCell.Equals("Key", StringComparison.OrdinalIgnoreCase))
                {
                    startRow = 2;
                }

                // Read key-value pairs
                for (int r = startRow; r <= Math.Min(100, configSheet.RowCount()); r++)
                {
                    var key = configSheet.Cell(r, 1).GetString().Trim();
                    var value = configSheet.Cell(r, 2).GetString().Trim();

                    if (string.IsNullOrEmpty(key)) continue;

                    ParseEnvironmentKey(config, key, value);
                }

                _logger.LogDebug($"Environment loaded: CodeContainerInternalPort={config.CodeContainerInternalPort}, " +
                                 $"CodeContainerHostPort={config.CodeContainerHostPort}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading Environment.xlsx: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a key-value pair from Environment.xlsx and updates the config.
        /// </summary>
        private void ParseEnvironmentKey(TestKitConfig config, string key, string value)
        {
            // Normalize key for comparison
            var normalizedKey = key.Replace(" ", "_").Replace("-", "_");

            // Port configurations
            if (normalizedKey.Equals("Code_Container_Internal_Port", StringComparison.OrdinalIgnoreCase) ||
                normalizedKey.Equals("CodeContainerInternalPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.CodeContainerInternalPort = port;
            }
            else if (normalizedKey.Equals("Code_Container_Host_Port", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("CodeContainerHostPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.CodeContainerHostPort = port;
            }
            else if (normalizedKey.Equals("Given_Console_Container_Internal_Port", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("GivenConsoleContainerInternalPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.GivenConsoleContainerInternalPort = port;
            }
            else if (normalizedKey.Equals("Given_Console_Container_Host_Port", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("GivenConsoleContainerHostPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.GivenConsoleContainerHostPort = port;
            }
            else if (normalizedKey.Equals("MonitorPort", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("Monitor_Port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.MonitorPort = port;
            }
            // Database configurations
            else if (normalizedKey.Equals("Database_Image_Name", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseImageName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DatabaseImageName = value;
            }
            else if (normalizedKey.Equals("Database_Container_Name", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseContainerName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DatabaseContainerName = value;
            }
            else if (normalizedKey.Equals("Database_Container_Internal_Port", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseContainerInternalPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.DatabaseContainerInternalPort = port;
            }
            else if (normalizedKey.Equals("Database_Container_Host_Port", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseContainerHostPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port)) config.DatabaseContainerHostPort = port;
            }
            else if (normalizedKey.Equals("Database_Username", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseUsername", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DatabaseUsername = value;
            }
            else if (normalizedKey.Equals("Database_Password", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabasePassword", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DatabasePassword = value;
            }
            else if (normalizedKey.Equals("Default_Database_Name", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DatabaseName = value;
            }
            else if (normalizedKey.Equals("Default_Database_File_Path", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DatabaseFilePath", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DefaultDatabaseFilePath = value;
            }
            // Container configurations
            else if (normalizedKey.Equals("Code_Image_Name", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("CodeImageName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.CodeImageName = value;
            }
            else if (normalizedKey.Equals("Given_Console_Image_Name", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("GivenConsoleImageName", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.GivenConsoleImageName = value;
            }
            else if (normalizedKey.Equals("Docker_Network", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("DockerNetwork", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.DockerNetwork = value;
            }
            // Given paths
            else if (normalizedKey.Equals("Given_Server_Path", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("GivenServerPath", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.GivenServerPath = value;
            }
            else if (normalizedKey.Equals("Given_Client_Path", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("GivenClientPath", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.GivenClientPath = value;
            }
            else if (normalizedKey.Equals("Runtimes_Folder", StringComparison.OrdinalIgnoreCase) ||
                     normalizedKey.Equals("RuntimesFolder", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) config.RuntimesFolder = value;
            }
        }

        /// <summary>
        /// Reads marks from QuestionMark sheet.
        /// </summary>
        private void ReadMarks(TestKitConfig config, IXLWorksheet markSheet)
        {
            // Find header row with TestCase and Mark columns
            int headerRow = -1, tcCol = -1, markCol = -1;

            for (int r = 1; r <= Math.Min(10, markSheet.RowCount()); r++)
            {
                for (int c = 1; c <= Math.Min(10, markSheet.ColumnCount()); c++)
                {
                    var text = markSheet.Cell(r, c).GetString().Trim();
                    if (text.Equals("TestCase", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Cases", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Test_Case", StringComparison.OrdinalIgnoreCase))
                    {
                        tcCol = c;
                    }
                    if (text.Equals("Mark", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("Marks", StringComparison.OrdinalIgnoreCase))
                    {
                        markCol = c;
                    }
                }

                if (tcCol > 0 && markCol > 0)
                {
                    headerRow = r;
                    break;
                }
                tcCol = markCol = -1;
            }

            if (headerRow < 0)
            {
                _logger.LogWarning("Could not find TestCase/Mark columns in QuestionMark sheet");
                return;
            }

            // Read marks for each test case
            for (int r = headerRow + 1; r <= markSheet.RowCount(); r++)
            {
                var tcName = markSheet.Cell(r, tcCol).GetString().Trim();
                if (string.IsNullOrEmpty(tcName)) break;

                var markStr = markSheet.Cell(r, markCol).GetString().Trim();
                if (double.TryParse(markStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mark))
                {
                    config.TestCaseMarks[tcName] = mark;
                }
            }

            // Calculate total mark
            config.TotalMaxMark = config.TestCaseMarks.Values.Sum();
        }

        /// <summary>
        /// Reads datetime format from DataPattern sheet.
        /// </summary>
        private void ReadDateTimeFormat(TestKitConfig config, IXLWorksheet patternSheet)
        {
            // Find header row
            int headerRow = -1, typeCol = -1, patternCol = -1;

            for (int r = 1; r <= Math.Min(10, patternSheet.RowCount()); r++)
            {
                for (int c = 1; c <= Math.Min(10, patternSheet.ColumnCount()); c++)
                {
                    var text = patternSheet.Cell(r, c).GetString().Trim();
                    if (text.Equals("Data Type", StringComparison.OrdinalIgnoreCase) ||
                        text.Equals("DataType", StringComparison.OrdinalIgnoreCase))
                    {
                        typeCol = c;
                    }
                    if (text.Equals("Pattern", StringComparison.OrdinalIgnoreCase))
                    {
                        patternCol = c;
                    }
                }

                if (typeCol > 0 && patternCol > 0)
                {
                    headerRow = r;
                    break;
                }
                typeCol = patternCol = -1;
            }

            if (headerRow < 0) return;

            // Find DateTime and Time patterns
            for (int r = headerRow + 1; r <= Math.Min(50, patternSheet.RowCount()); r++)
            {
                var dataType = patternSheet.Cell(r, typeCol).GetString().Trim();
                var pattern = patternSheet.Cell(r, patternCol).GetString().Trim();

                if (dataType.Equals("DateTime", StringComparison.OrdinalIgnoreCase))
                {
                    config.DateTimeFormat = pattern;
                    // Check if "exclude" flag is set
                    var excludeCell = patternSheet.Cell(r, patternCol + 1).GetString().Trim();
                    config.ExcludeDateTimeFromGrading = excludeCell.Equals("exclude", StringComparison.OrdinalIgnoreCase) ||
                                                         excludeCell.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                                         excludeCell.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (dataType.Equals("Time", StringComparison.OrdinalIgnoreCase))
                {
                    var excludeCell = patternSheet.Cell(r, patternCol + 1).GetString().Trim();
                    config.ExcludeTimeFromGrading = excludeCell.Equals("exclude", StringComparison.OrdinalIgnoreCase) ||
                                                    excludeCell.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                                    excludeCell.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Discovers test cases from the test kit folder.
        /// Test cases are subdirectories containing Detail.xlsx.
        /// </summary>
        private void DiscoverTestCases(TestKitConfig config, string testKitPath)
        {
            var directories = Directory.GetDirectories(testKitPath);

            foreach (var dir in directories)
            {
                var dirName = Path.GetFileName(dir);

                // Skip Meta folder
                if (dirName.Equals("Meta", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("mismatches", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check if this is a test case folder
                var detailPath = Path.Combine(dir, "Detail.xlsx");
                if (File.Exists(detailPath))
                {
                    config.TestCaseNames.Add(dirName);

                    // If mark not set from Header.xlsx, try to read from test case header
                    if (!config.TestCaseMarks.ContainsKey(dirName))
                    {
                        config.TestCaseMarks[dirName] = 0;
                    }
                }
            }

            // Sort test case names
            config.TestCaseNames = config.TestCaseNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Recalculate total mark if needed
            if (config.TotalMaxMark == 0)
            {
                config.TotalMaxMark = config.TestCaseMarks.Values.Sum();
            }
        }

        /// <summary>
        /// Finds given/reference executables from Meta/Given folder.
        /// </summary>
        private void FindGivenExecutables(TestKitConfig config, string testKitPath)
        {
            var givenPath = Path.Combine(testKitPath, "Meta", "Given");
            if (!Directory.Exists(givenPath)) return;

            // Find Server
            var serverFolder = Path.Combine(givenPath, "Server");
            if (Directory.Exists(serverFolder))
            {
                var dllFiles = Directory.GetFiles(serverFolder, "*.dll")
                    .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                
                if (dllFiles.Count > 0)
                {
                    // Prefer main project DLL (not System.*, Microsoft.*, etc.)
                    config.GivenServerPath = dllFiles
                        .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                        ?? dllFiles.First();
                }
            }

            // Find Client
            var clientFolder = Path.Combine(givenPath, "Client");
            if (Directory.Exists(clientFolder))
            {
                var dllFiles = Directory.GetFiles(clientFolder, "*.dll")
                    .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (dllFiles.Count > 0)
                {
                    config.GivenClientPath = dllFiles
                        .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                        ?? dllFiles.First();
                }
            }
        }

        /// <summary>
        /// Finds the runtimes folder for native dependencies.
        /// </summary>
        private void FindRuntimesFolder(TestKitConfig config, string testKitPath)
        {
            // Check common locations
            var locations = new[]
            {
                Path.Combine(testKitPath, "Meta", "runtimes"),
                Path.Combine(testKitPath, "runtimes"),
                Path.Combine(testKitPath, "Meta", "Given", "runtimes")
            };

            foreach (var location in locations)
            {
                if (Directory.Exists(location))
                {
                    config.RuntimesFolder = location;
                    return;
                }
            }

            // If configured in Environment.xlsx with relative path, resolve it
            if (!string.IsNullOrEmpty(config.RuntimesFolder) && !Path.IsPathRooted(config.RuntimesFolder))
            {
                var resolvedPath = Path.Combine(testKitPath, config.RuntimesFolder);
                if (Directory.Exists(resolvedPath))
                {
                    config.RuntimesFolder = resolvedPath;
                }
            }
        }

        /// <summary>
        /// Helper to read a key-value pair from a worksheet.
        /// </summary>
        private static string? ReadKeyValue(IXLWorksheet sheet, string key)
        {
            int startRow = 1;
            var firstCell = sheet.Cell(1, 1).GetString().Trim();
            if (firstCell.Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                startRow = 2;
            }

            for (int r = startRow; r <= Math.Min(50, sheet.RowCount()); r++)
            {
                var cellKey = sheet.Cell(r, 1).GetString().Trim();
                if (cellKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return sheet.Cell(r, 2).GetString().Trim();
                }
            }

            return null;
        }
    }
}
