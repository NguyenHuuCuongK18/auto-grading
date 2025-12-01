using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.Core.Models;

namespace SolutionGrader.Core.Services.UI
{
    /// <summary>
    /// Service for loading test kit configuration from Excel files.
    /// 
    /// Reads:
    /// - Header.xlsx: Test case marks and metadata
    /// - Environment.xlsx: Docker and network configuration
    /// 
    /// These files use specific column naming conventions that must match
    /// the existing test kit format used by the grader.
    /// </summary>
    public class TestKitConfigService
    {
        private readonly ILoggingService _logger;

        public TestKitConfigService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads the test kit configuration from Environment.xlsx and Header.xlsx.
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder.</param>
        /// <returns>TestKitConfig with loaded settings, or null if loading fails.</returns>
        public TestKitConfig? LoadTestKitConfig(string testKitPath)
        {
            if (!Directory.Exists(testKitPath))
            {
                _logger.LogWarning($"Test kit path does not exist: {testKitPath}");
                return null;
            }

            var config = new TestKitConfig
            {
                Name = Path.GetFileName(testKitPath),
                Path = testKitPath
            };

            try
            {
                // Load Header.xlsx for marks
                var headerPath = Path.Combine(testKitPath, "Header.xlsx");
                if (File.Exists(headerPath))
                {
                    LoadHeaderConfig(headerPath, config);
                }
                else
                {
                    _logger.LogWarning($"Header.xlsx not found in test kit: {testKitPath}");
                }

                // Load Environment.xlsx for Docker config
                var envPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (File.Exists(envPath))
                {
                    LoadEnvironmentConfig(envPath, config);
                }
                else
                {
                    _logger.LogWarning($"Environment.xlsx not found in test kit: {testKitPath}");
                }

                // Discover test cases
                var tcFolders = Directory.GetDirectories(testKitPath)
                    .Where(d => Path.GetFileName(d).StartsWith("TC", StringComparison.OrdinalIgnoreCase))
                    .Select(d => Path.GetFileName(d))
                    .OrderBy(n => ExtractNumber(n))
                    .ToList();

                config.TestCases = tcFolders;
                _logger.LogInfo($"Loaded test kit config: {config.Name} with {config.TestCases.Count} test cases, max mark: {config.TotalMaxMark}");

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load test kit config from {testKitPath}", ex);
                return null;
            }
        }

        /// <summary>
        /// Loads configuration from Header.xlsx.
        /// Expected structure in "QuestionMark" worksheet:
        /// - Column "Cases" or "TestCase": Test case name (TC1, TC2, etc.)
        /// - Column "Mark" or "MaxMark": Points for each test case
        /// </summary>
        private void LoadHeaderConfig(string headerPath, TestKitConfig config)
        {
            try
            {
                using var workbook = new XLWorkbook(headerPath);
                
                // Look for QuestionMark worksheet first, fall back to first worksheet
                var worksheet = workbook.Worksheets.FirstOrDefault(ws => 
                    ws.Name.Equals("QuestionMark", StringComparison.OrdinalIgnoreCase)) 
                    ?? workbook.Worksheets.FirstOrDefault();
                    
                if (worksheet == null)
                {
                    _logger.LogWarning($"No worksheet found in Header.xlsx");
                    return;
                }
                
                _logger.LogDebug($"Using worksheet: {worksheet.Name}");

                // Find column indices
                var headerRow = worksheet.Row(1);
                int markCol = -1;
                int testCaseCol = -1;

                // Find column indices - check up to 10 columns
                var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 10;
                _logger.LogDebug($"Header.xlsx: Checking columns 1 to {lastCol}");
                
                for (int col = 1; col <= lastCol; col++)
                {
                    var cellValue = headerRow.Cell(col).GetString()?.Trim().ToUpperInvariant() ?? "";
                    _logger.LogDebug($"Header.xlsx Col {col}: \"{cellValue}\"");
                    
                    if (cellValue == "MARK" || cellValue == "MAXMARK" || cellValue == "MAX_MARK" || cellValue == "ĐIỂM")
                        markCol = col;
                    else if (cellValue == "CASES" || cellValue == "TESTCASE" || cellValue == "TC" || cellValue == "TEST_CASE")
                        testCaseCol = col;
                }

                _logger.LogDebug($"Header.xlsx: Found markCol={markCol}, testCaseCol={testCaseCol}");

                // Sum up marks from all rows
                double totalMark = 0;
                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;

                for (int row = 2; row <= lastRow; row++)
                {
                    if (markCol > 0)
                    {
                        var markCell = worksheet.Cell(row, markCol);
                        if (markCell.TryGetValue<double>(out var mark))
                        {
                            totalMark += mark;
                            var tcName = testCaseCol > 0 ? worksheet.Cell(row, testCaseCol).GetString() : $"Row{row}";
                            _logger.LogDebug($"  {tcName}: {mark} marks");
                        }
                    }
                }

                config.TotalMaxMark = totalMark;
                _logger.LogInfo($"Loaded marks from Header.xlsx: Total = {totalMark}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load Header.xlsx", ex);
            }
        }

        /// <summary>
        /// Loads configuration from Environment.xlsx.
        /// Expected structure (key-value pairs in columns A and B):
        /// - Code_Container_Internal_Port
        /// - Code_Container_Host_Port
        /// - Database_Image_Name
        /// - Database_Container_Name
        /// - Database_Container_Internal_Port
        /// - Database_Container_Host_Port
        /// - Database_Username
        /// - Database_Password
        /// - Protocol (HTTP or TCP)
        /// </summary>
        private void LoadEnvironmentConfig(string envPath, TestKitConfig config)
        {
            try
            {
                using var workbook = new XLWorkbook(envPath);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                {
                    _logger.LogWarning($"No worksheet found in Environment.xlsx");
                    return;
                }

                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;

                for (int row = 1; row <= lastRow; row++)
                {
                    var key = worksheet.Cell(row, 1).GetString().Trim();
                    var value = worksheet.Cell(row, 2).GetString().Trim();

                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                        continue;

                    switch (key.ToUpperInvariant().Replace(" ", "_"))
                    {
                        case "CODE_CONTAINER_INTERNAL_PORT":
                            if (int.TryParse(value, out var internalPort))
                                config.CodeContainerInternalPort = internalPort;
                            break;

                        case "CODE_CONTAINER_HOST_PORT":
                            if (int.TryParse(value, out var hostPort))
                                config.CodeContainerHostPort = hostPort;
                            break;

                        case "DATABASE_IMAGE_NAME":
                            config.DatabaseImageName = value;
                            break;

                        case "DATABASE_CONTAINER_NAME":
                            config.DatabaseContainerName = value;
                            break;

                        case "DATABASE_CONTAINER_INTERNAL_PORT":
                            if (int.TryParse(value, out var dbInternalPort))
                                config.DatabaseContainerInternalPort = dbInternalPort;
                            break;

                        case "DATABASE_CONTAINER_HOST_PORT":
                            if (int.TryParse(value, out var dbHostPort))
                                config.DatabaseContainerHostPort = dbHostPort;
                            break;

                        case "DATABASE_USERNAME":
                            config.DatabaseUsername = value;
                            break;

                        case "DATABASE_PASSWORD":
                            config.DatabasePassword = value;
                            break;

                        case "PROTOCOL":
                            config.Protocol = value.ToUpperInvariant();
                            break;

                        default:
                            // Store all other values in EnvironmentConfig dictionary
                            config.EnvironmentConfig[key] = value;
                            break;
                    }
                }

                _logger.LogDebug($"Loaded environment config: Port {config.CodeContainerHostPort}, Protocol {config.Protocol}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load Environment.xlsx", ex);
            }
        }

        /// <summary>
        /// Extracts a number from a string for sorting purposes.
        /// "TC1" -> 1, "TC10" -> 10
        /// </summary>
        private int ExtractNumber(string s)
        {
            var digits = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var num) ? num : 999;
        }

        /// <summary>
        /// Gets the Detail.xlsx path for a specific test case.
        /// </summary>
        public string? GetDetailPath(string testKitPath, string testCaseName)
        {
            var tcPath = Path.Combine(testKitPath, testCaseName, "Detail.xlsx");
            if (File.Exists(tcPath))
                return tcPath;

            // Try lowercase
            tcPath = Path.Combine(testKitPath, testCaseName, "detail.xlsx");
            if (File.Exists(tcPath))
                return tcPath;

            return null;
        }

        /// <summary>
        /// Gets the marks for a specific test case from its Header.xlsx.
        /// Falls back to the test kit's Header.xlsx if not found.
        /// </summary>
        public double GetTestCaseMark(string testKitPath, string testCaseName)
        {
            // Try test case specific Header.xlsx
            var tcHeaderPath = Path.Combine(testKitPath, testCaseName, "Header.xlsx");
            if (File.Exists(tcHeaderPath))
            {
                try
                {
                    using var workbook = new XLWorkbook(tcHeaderPath);
                    var worksheet = workbook.Worksheets.FirstOrDefault();
                    if (worksheet != null)
                    {
                        // Look for mark value
                        for (int row = 1; row <= (worksheet.LastRowUsed()?.RowNumber() ?? 1); row++)
                        {
                            for (int col = 1; col <= (worksheet.LastColumnUsed()?.ColumnNumber() ?? 1); col++)
                            {
                                var header = worksheet.Cell(1, col).GetString().Trim().ToUpperInvariant();
                                if (header == "MARK" || header == "MAXMARK" || header == "ĐIỂM")
                                {
                                    if (worksheet.Cell(2, col).TryGetValue<double>(out var mark))
                                        return mark;
                                }
                            }
                        }
                    }
                }
                catch { /* Fall through */ }
            }

            // Return default mark (evenly distributed)
            return 2.5; // Default 10 points / 4 test cases
        }
    }
}
