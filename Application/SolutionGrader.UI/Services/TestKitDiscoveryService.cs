using System.IO;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Service for discovering test kits in the TestKit folder.
/// </summary>
public class TestKitDiscoveryService
{
    private readonly ILoggingService _logger;
    private readonly Dictionary<string, string> _paperToTestKitMap = new();
    
    public TestKitDiscoveryService(ILoggingService logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Gets the test kit path for a specific paper number.
    /// </summary>
    /// <param name="testKitFolderPath">Path to the TestKit folder.</param>
    /// <param name="paperNo">The paper number.</param>
    /// <returns>Path to the test kit for the paper, or null if not found.</returns>
    public string? GetTestKitForPaper(string testKitFolderPath, string paperNo)
    {
        // Check cache
        if (_paperToTestKitMap.TryGetValue(paperNo, out var cachedPath))
            return cachedPath;
        
        // Discover test kit mapping
        DiscoverTestKits(testKitFolderPath);
        
        return _paperToTestKitMap.TryGetValue(paperNo, out var path) ? path : null;
    }
    
    /// <summary>
    /// Discovers all test kits and builds the paper-to-testkit mapping.
    /// </summary>
    /// <param name="testKitFolderPath">Path to the TestKit folder.</param>
    /// <returns>Dictionary mapping paper numbers to test kit paths.</returns>
    public Dictionary<string, string> DiscoverTestKits(string testKitFolderPath)
    {
        _paperToTestKitMap.Clear();
        
        if (!Directory.Exists(testKitFolderPath))
        {
            _logger.LogWarning($"TestKit folder not found: {testKitFolderPath}");
            return _paperToTestKitMap;
        }
        
        // Look for test kit folders (Q1, Q2, etc. or Paper1, Paper2, etc.)
        foreach (var kitDir in Directory.GetDirectories(testKitFolderPath))
        {
            var kitName = Path.GetFileName(kitDir);
            
            // Check for mapping file (mapping.xlsx or mapping.json)
            var mappingFile = Path.Combine(kitDir, "mapping.xlsx");
            if (!File.Exists(mappingFile))
            {
                mappingFile = Path.Combine(kitDir, "mapping.json");
            }
            
            if (File.Exists(mappingFile))
            {
                // TODO: Parse mapping file for paper numbers
                _logger.LogInfo($"Found mapping file: {mappingFile}");
            }
            else
            {
                // Assume folder name indicates paper number
                // E.g., "Q1" -> paper "1", "Q2" -> paper "2"
                var paperNo = ExtractPaperNumber(kitName);
                if (!string.IsNullOrEmpty(paperNo))
                {
                    _paperToTestKitMap[paperNo] = kitDir;
                    _logger.LogInfo($"Mapped paper {paperNo} to test kit: {kitDir}");
                }
            }
        }
        
        return _paperToTestKitMap;
    }
    
    /// <summary>
    /// Extracts a paper number from a folder name.
    /// </summary>
    private static string? ExtractPaperNumber(string folderName)
    {
        // Handle formats like "Q1", "Q2", "Paper1", "1", etc.
        if (folderName.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
        {
            return folderName.Substring(1);
        }
        
        if (folderName.StartsWith("Paper", StringComparison.OrdinalIgnoreCase))
        {
            return folderName.Substring(5);
        }
        
        if (int.TryParse(folderName, out _))
        {
            return folderName;
        }
        
        return null;
    }
}
