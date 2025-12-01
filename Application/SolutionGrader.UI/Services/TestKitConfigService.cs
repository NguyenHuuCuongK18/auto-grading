using System.IO;
using ClosedXML.Excel;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Configuration loaded from a test kit's Excel files.
/// </summary>
public class TestKitConfig
{
    /// <summary>
    /// Gets or sets the total maximum mark for all test cases.
    /// </summary>
    public double TotalMaxMark { get; set; }
    
    /// <summary>
    /// Gets or sets the code container internal port.
    /// </summary>
    public int CodeContainerInternalPort { get; set; } = 5000;
    
    /// <summary>
    /// Gets or sets the code container host port.
    /// </summary>
    public int CodeContainerHostPort { get; set; } = 5000;
    
    /// <summary>
    /// Gets or sets the database image name.
    /// </summary>
    public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2019-latest";
    
    /// <summary>
    /// Gets or sets the database container name.
    /// </summary>
    public string DatabaseContainerName { get; set; } = "ag-database";
    
    /// <summary>
    /// Gets or sets the database internal port.
    /// </summary>
    public int DatabaseContainerInternalPort { get; set; } = 1433;
    
    /// <summary>
    /// Gets or sets the database host port.
    /// </summary>
    public int DatabaseContainerHostPort { get; set; } = 1433;
    
    /// <summary>
    /// Gets or sets the database username.
    /// </summary>
    public string DatabaseUsername { get; set; } = "sa";
    
    /// <summary>
    /// Gets or sets the database password.
    /// </summary>
    public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";
    
    /// <summary>
    /// Gets or sets the list of test case names.
    /// </summary>
    public List<string> TestCases { get; set; } = new();
    
    /// <summary>
    /// Gets or sets the marks per test case.
    /// </summary>
    public Dictionary<string, double> TestCaseMarks { get; set; } = new();
}

/// <summary>
/// Service for loading test kit configuration from Excel files.
/// </summary>
public class TestKitConfigService
{
    private readonly ILoggingService _logger;
    
    public TestKitConfigService(ILoggingService logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Loads configuration from a test kit folder.
    /// </summary>
    /// <param name="testKitPath">Path to the test kit folder.</param>
    /// <returns>The test kit configuration, or null if loading failed.</returns>
    public TestKitConfig? LoadTestKitConfig(string testKitPath)
    {
        var config = new TestKitConfig();
        
        try
        {
            // Load Header.xlsx for test case marks
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                LoadHeader(headerPath, config);
            }
            else
            {
                _logger.LogWarning($"Header.xlsx not found in: {testKitPath}");
            }
            
            // Load Environment.xlsx for port configurations
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (File.Exists(envPath))
            {
                LoadEnvironment(envPath, config);
            }
            
            // Discover test cases by looking for TC* folders
            foreach (var tcDir in Directory.GetDirectories(testKitPath, "TC*"))
            {
                var tcName = Path.GetFileName(tcDir);
                if (!config.TestCases.Contains(tcName))
                {
                    config.TestCases.Add(tcName);
                }
            }
            
            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to load test kit config: {testKitPath}", ex);
            return null;
        }
    }
    
    /// <summary>
    /// Loads Header.xlsx and extracts test case marks.
    /// </summary>
    private void LoadHeader(string headerPath, TestKitConfig config)
    {
        using var workbook = new XLWorkbook(headerPath);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        
        if (worksheet == null)
            return;
        
        // Look for Mark column and test case names
        var headerRow = worksheet.Row(1);
        int markColumn = -1;
        int nameColumn = -1;
        
        for (int col = 1; col <= worksheet.LastColumnUsed()?.ColumnNumber(); col++)
        {
            var cellValue = headerRow.Cell(col).GetString();
            if (cellValue.Contains("Mark", StringComparison.OrdinalIgnoreCase))
            {
                markColumn = col;
            }
            else if (cellValue.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                     cellValue.Contains("TestCase", StringComparison.OrdinalIgnoreCase) ||
                     cellValue.Contains("TC", StringComparison.OrdinalIgnoreCase))
            {
                nameColumn = col;
            }
        }
        
        // Sum up marks from test cases
        double totalMark = 0;
        for (int row = 2; row <= worksheet.LastRowUsed()?.RowNumber(); row++)
        {
            if (markColumn > 0)
            {
                var markValue = worksheet.Cell(row, markColumn).GetValue<double>();
                totalMark += markValue;
                
                if (nameColumn > 0)
                {
                    var tcName = worksheet.Cell(row, nameColumn).GetString();
                    if (!string.IsNullOrEmpty(tcName))
                    {
                        config.TestCaseMarks[tcName] = markValue;
                    }
                }
            }
        }
        
        config.TotalMaxMark = totalMark;
        _logger.LogInfo($"Loaded Header.xlsx: Total mark = {totalMark}");
    }
    
    /// <summary>
    /// Loads Environment.xlsx and extracts port configurations.
    /// </summary>
    private void LoadEnvironment(string envPath, TestKitConfig config)
    {
        using var workbook = new XLWorkbook(envPath);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        
        if (worksheet == null)
            return;
        
        // Look for port configuration in key-value pairs
        for (int row = 1; row <= worksheet.LastRowUsed()?.RowNumber(); row++)
        {
            var key = worksheet.Cell(row, 1).GetString();
            var value = worksheet.Cell(row, 2).GetString();
            
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                continue;
            
            switch (key)
            {
                case "Code_Container_Internal_Port":
                    if (int.TryParse(value, out var internalPort))
                        config.CodeContainerInternalPort = internalPort;
                    break;
                    
                case "Code_Container_Host_Port":
                    if (int.TryParse(value, out var hostPort))
                        config.CodeContainerHostPort = hostPort;
                    break;
                    
                case "Database_Image_Name":
                    config.DatabaseImageName = value;
                    break;
                    
                case "Database_Container_Name":
                    config.DatabaseContainerName = value;
                    break;
                    
                case "Database_Container_Internal_Port":
                    if (int.TryParse(value, out var dbInternalPort))
                        config.DatabaseContainerInternalPort = dbInternalPort;
                    break;
                    
                case "Database_Container_Host_Port":
                    if (int.TryParse(value, out var dbHostPort))
                        config.DatabaseContainerHostPort = dbHostPort;
                    break;
                    
                case "Database_Username":
                    config.DatabaseUsername = value;
                    break;
                    
                case "Database_Password":
                    config.DatabasePassword = value;
                    break;
            }
        }
    }
}
