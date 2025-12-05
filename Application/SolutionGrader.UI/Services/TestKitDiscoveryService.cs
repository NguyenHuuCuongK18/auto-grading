using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolutionGrader.Core.Services;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for discovering and managing test kits.
    /// Handles the mapping between paper numbers and their corresponding test kits.
    /// </summary>
    public class TestKitDiscoveryService
    {
        private readonly ILoggingService _logger;

        public TestKitDiscoveryService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Discovers all available test kits in the test kit folder.
        /// </summary>
        /// <param name="testKitFolderPath">Path to the TestKit folder</param>
        /// <returns>Dictionary mapping question names (e.g., "Q1") to their paths</returns>
        public Dictionary<string, string> DiscoverTestKits(string testKitFolderPath)
        {
            var testKits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(testKitFolderPath))
            {
                _logger.LogError($"Test kit folder not found: {testKitFolderPath}");
                return testKits;
            }

            _logger.LogInfo($"Scanning test kit folder: {testKitFolderPath}");

            // Get all subfolders that contain a Header.xlsx file (indicating a valid test kit)
            var directories = Directory.GetDirectories(testKitFolderPath);

            foreach (var dir in directories)
            {
                var dirName = Path.GetFileName(dir);
                var headerPath = Path.Combine(dir, "Header.xlsx");
                var environmentPath = Path.Combine(dir, "Environment.xlsx");

                // A valid test kit must have Header.xlsx
                if (File.Exists(headerPath))
                {
                    testKits[dirName] = dir;
                    _logger.LogDebug($"Found test kit: {dirName} at {dir}");
                }
                else
                {
                    _logger.LogWarning($"Folder {dirName} does not contain Header.xlsx, skipping");
                }
            }

            _logger.LogInfo($"Discovered {testKits.Count} test kits");
            return testKits;
        }

        /// <summary>
        /// Gets the test kit path for a specific paper number.
        /// 
        /// REFACTORED: Now uses SharedDiscoveryServices to eliminate code duplication
        /// with CliDockerGradingService.
        /// </summary>
        /// <param name="testKitFolderPath">Path to the TestKit folder</param>
        /// <param name="paperNo">Paper number (e.g., "1")</param>
        /// <returns>Path to the test kit for this paper, or null if not found</returns>
        public string? GetTestKitForPaper(string testKitFolderPath, string paperNo)
        {
            // Use shared discovery service to eliminate code duplication
            return SharedDiscoveryServices.GetTestKitForPaper(
                testKitFolderPath,
                paperNo,
                logger: msg => _logger.LogDebug(msg));
        }

        /// <summary>
        /// Gets the list of test cases within a test kit.
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder</param>
        /// <returns>List of test case folder paths</returns>
        public List<string> GetTestCases(string testKitPath)
        {
            var testCases = new List<string>();

            if (!Directory.Exists(testKitPath))
                return testCases;

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
                    testCases.Add(dir);
                }
            }

            return testCases.OrderBy(tc => Path.GetFileName(tc)).ToList();
        }

        /// <summary>
        /// Gets the environment configuration path for a test kit.
        /// </summary>
        public string? GetEnvironmentPath(string testKitPath)
        {
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            return File.Exists(envPath) ? envPath : null;
        }

        /// <summary>
        /// Gets the header configuration path for a test kit.
        /// </summary>
        public string? GetHeaderPath(string testKitPath)
        {
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            return File.Exists(headerPath) ? headerPath : null;
        }

        /// <summary>
        /// Gets the given server/client executables from the Meta/Given folder.
        /// </summary>
        public (string? ServerPath, string? ClientPath) GetGivenExecutables(string testKitPath)
        {
            string? serverPath = null;
            string? clientPath = null;

            var givenPath = Path.Combine(testKitPath, "Meta", "Given");
            if (!Directory.Exists(givenPath))
                return (null, null);

            // Check for Server folder
            var serverFolder = Path.Combine(givenPath, "Server");
            if (Directory.Exists(serverFolder))
            {
                // Look for any DLL file
                var dllFiles = Directory.GetFiles(serverFolder, "*.dll");
                if (dllFiles.Length > 0)
                {
                    // Find the main executable DLL (not dependency DLLs)
                    serverPath = dllFiles
                        .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                }
            }

            // Check for Client folder
            var clientFolder = Path.Combine(givenPath, "Client");
            if (Directory.Exists(clientFolder))
            {
                var dllFiles = Directory.GetFiles(clientFolder, "*.dll");
                if (dllFiles.Length > 0)
                {
                    clientPath = dllFiles
                        .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                }
            }

            return (serverPath, clientPath);
        }

        /// <summary>
        /// Gets the total maximum marks for a test kit by reading Header.xlsx.
        /// Sums up all test case marks from the QuestionMark sheet.
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder</param>
        /// <returns>Total maximum marks for the test kit, or 0 if not found</returns>
        public double GetTestKitMaxMark(string testKitPath)
        {
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (!File.Exists(headerPath))
            {
                _logger.LogWarning($"Header.xlsx not found in {testKitPath}");
                return 0.0;
            }

            try
            {
                using var workbook = new ClosedXML.Excel.XLWorkbook(headerPath);
                if (workbook.TryGetWorksheet("QuestionMark", out var markSheet))
                {
                    double totalMark = 0.0;
                    foreach (var row in markSheet.RowsUsed().Skip(1)) // Skip header
                    {
                        var mark = row.Cell(2).GetValue<double>();
                        totalMark += mark;
                    }
                    return totalMark;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading max marks from {headerPath}: {ex.Message}");
            }

            return 0.0;
        }
    }
}
