using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for discovering and mapping test kits to papers.
    /// 
    /// Expected TestKit folder structure:
    /// TestKit/
    ///   {TestKitName}/  (e.g., Q1, HTTP_1, TCP_3)
    ///     Environment.xlsx  - Configuration for Docker setup
    ///     Header.xlsx       - Test case headers with max marks
    ///     Meta/
    ///       Given/          - "Golden" client/server implementations
    ///         Client/
    ///         Server/
    ///       runtimes/       - Runtime dependencies
    ///     TC1/
    ///       Detail.xlsx     - Test steps for this test case
    ///       Environment.xlsx - Optional per-TC environment override
    ///       Header.xlsx     - Optional per-TC marks
    ///     TC2/
    ///       ...
    /// </summary>
    public class TestKitDiscoveryService
    {
        private readonly ILoggingService _logger;

        public TestKitDiscoveryService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the test kit path for a specific paper number.
        /// Uses naming convention to match papers to test kits:
        /// - Q{paperNo} matches paper number directly
        /// - Or first test kit if only one exists
        /// </summary>
        /// <param name="testKitFolderPath">Root TestKit folder path.</param>
        /// <param name="paperNo">Paper number to find test kit for.</param>
        /// <returns>Path to test kit folder, or null if not found.</returns>
        public string? GetTestKitForPaper(string testKitFolderPath, string paperNo)
        {
            if (!Directory.Exists(testKitFolderPath))
            {
                _logger.LogWarning($"TestKit folder does not exist: {testKitFolderPath}");
                return null;
            }

            // Get all test kit folders (contain Environment.xlsx or Header.xlsx)
            var testKitFolders = GetTestKitFolders(testKitFolderPath);

            if (testKitFolders.Count == 0)
            {
                _logger.LogWarning("No test kits found in TestKit folder");
                return null;
            }

            // Try exact match: Q{paperNo}
            var exactMatch = testKitFolders.FirstOrDefault(
                f => Path.GetFileName(f).Equals($"Q{paperNo}", StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                _logger.LogDebug($"Found exact test kit match for paper {paperNo}: {Path.GetFileName(exactMatch)}");
                return exactMatch;
            }

            // Try contains match: *{paperNo}*
            var containsMatch = testKitFolders.FirstOrDefault(
                f => Path.GetFileName(f).Contains(paperNo, StringComparison.OrdinalIgnoreCase));

            if (containsMatch != null)
            {
                _logger.LogDebug($"Found partial test kit match for paper {paperNo}: {Path.GetFileName(containsMatch)}");
                return containsMatch;
            }

            // If only one test kit, use it for all papers
            if (testKitFolders.Count == 1)
            {
                _logger.LogDebug($"Using single test kit for paper {paperNo}: {Path.GetFileName(testKitFolders[0])}");
                return testKitFolders[0];
            }

            // Try numeric matching: test kit folder contains same number
            if (int.TryParse(paperNo, out var paperNum))
            {
                foreach (var folder in testKitFolders)
                {
                    var folderName = Path.GetFileName(folder);
                    // Extract numbers from folder name
                    var numbers = new string(folderName.Where(char.IsDigit).ToArray());
                    if (!string.IsNullOrEmpty(numbers) && int.TryParse(numbers, out var folderNum) && folderNum == paperNum)
                    {
                        _logger.LogDebug($"Found numeric match test kit for paper {paperNo}: {folderName}");
                        return folder;
                    }
                }
            }

            _logger.LogWarning($"No test kit found for paper {paperNo}");
            return null;
        }

        /// <summary>
        /// Gets all test kit folders in the TestKit directory.
        /// A folder is considered a test kit if it contains Environment.xlsx or Header.xlsx,
        /// or if it contains TC* subfolders.
        /// </summary>
        private List<string> GetTestKitFolders(string testKitFolderPath)
        {
            var testKits = new List<string>();

            foreach (var folder in Directory.GetDirectories(testKitFolderPath))
            {
                var folderName = Path.GetFileName(folder);
                
                // Skip hidden folders and special folders
                if (folderName.StartsWith("."))
                    continue;

                // Check if it's a test kit by looking for marker files
                var hasEnvironment = File.Exists(Path.Combine(folder, "Environment.xlsx"));
                var hasHeader = File.Exists(Path.Combine(folder, "Header.xlsx"));
                var hasTestCases = Directory.GetDirectories(folder)
                    .Any(d => Path.GetFileName(d).StartsWith("TC", StringComparison.OrdinalIgnoreCase));

                if (hasEnvironment || hasHeader || hasTestCases)
                {
                    testKits.Add(folder);
                }
            }

            return testKits;
        }

        /// <summary>
        /// Gets all test case folders within a test kit.
        /// Test case folders start with "TC" (e.g., TC1, TC2, TC_Send).
        /// </summary>
        public List<string> GetTestCaseFolders(string testKitPath)
        {
            if (!Directory.Exists(testKitPath))
                return new List<string>();

            return Directory.GetDirectories(testKitPath)
                .Where(d => Path.GetFileName(d).StartsWith("TC", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => ExtractTestCaseNumber(Path.GetFileName(d)))
                .ToList();
        }

        /// <summary>
        /// Extracts the numeric part of a test case name for sorting.
        /// "TC1" -> 1, "TC2_Send" -> 2, "TC10" -> 10
        /// </summary>
        private int ExtractTestCaseNumber(string testCaseName)
        {
            // Remove "TC" prefix
            var remainder = testCaseName.Substring(2);
            
            // Extract leading digits
            var digits = new string(remainder.TakeWhile(char.IsDigit).ToArray());
            
            return int.TryParse(digits, out var num) ? num : 999;
        }

        /// <summary>
        /// Gets all paper to test kit mappings.
        /// </summary>
        public List<TestKitMapping> GetAllMappings(string testKitFolderPath, IEnumerable<string> paperNumbers)
        {
            var mappings = new List<TestKitMapping>();

            foreach (var paperNo in paperNumbers)
            {
                var testKitPath = GetTestKitForPaper(testKitFolderPath, paperNo);
                mappings.Add(new TestKitMapping
                {
                    PaperNo = paperNo,
                    TestKitName = testKitPath != null ? Path.GetFileName(testKitPath) : string.Empty,
                    HasTestKit = testKitPath != null
                });
            }

            return mappings;
        }

        /// <summary>
        /// Gets the path to the "golden" client in the test kit Meta/Given folder.
        /// </summary>
        public string? GetGoldenClientPath(string testKitPath)
        {
            var clientPath = Path.Combine(testKitPath, "Meta", "Given", "Client");
            if (Directory.Exists(clientPath))
                return clientPath;

            // Try alternative name: given/client
            clientPath = Path.Combine(testKitPath, "Meta", "given", "Client");
            if (Directory.Exists(clientPath))
                return clientPath;

            return null;
        }

        /// <summary>
        /// Gets the path to the "golden" server in the test kit Meta/Given folder.
        /// </summary>
        public string? GetGoldenServerPath(string testKitPath)
        {
            var serverPath = Path.Combine(testKitPath, "Meta", "Given", "Server");
            if (Directory.Exists(serverPath))
                return serverPath;

            // Try alternative name: given/server
            serverPath = Path.Combine(testKitPath, "Meta", "given", "Server");
            if (Directory.Exists(serverPath))
                return serverPath;

            return null;
        }

        /// <summary>
        /// Gets the path to the runtimes folder in the test kit.
        /// </summary>
        public string? GetRuntimesPath(string testKitPath)
        {
            var runtimesPath = Path.Combine(testKitPath, "Meta", "runtimes");
            if (Directory.Exists(runtimesPath))
                return runtimesPath;

            // Try at root of test kit
            runtimesPath = Path.Combine(testKitPath, "runtimes");
            if (Directory.Exists(runtimesPath))
                return runtimesPath;

            return null;
        }
    }
}
