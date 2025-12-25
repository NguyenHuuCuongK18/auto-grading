using System.Collections.Generic;
using System.Linq;

namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Test kit configuration containing all settings from Environment.xlsx and Header.xlsx.
/// This is a shared model used by both CLI and UI implementations.
/// </summary>
public class TestKitConfig
{
    // Port configurations
    public int CodeContainerInternalPort { get; set; } = 8000;
    public int CodeContainerHostPort { get; set; } = 8000;
    
    // Docker configurations
    public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8-console:latest";
    public string DockerNetwork { get; set; } = "auto-grading-network";
    
    // Database configurations
    public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2019-latest";
    public string DatabaseContainerName { get; set; } = "auto-grading-sqlserver";
    public int DatabaseContainerInternalPort { get; set; } = 1433;
    public int DatabaseContainerHostPort { get; set; } = 1434;
    public string DatabaseName { get; set; } = "Library";
    public string DatabaseUsername { get; set; } = "sa";
    public string DatabasePassword { get; set; } = "";
    public string DefaultDatabaseName { get; set; } = "";
    public string DefaultDatabaseFilePath { get; set; } = "";
    
    // Code container configurations
    public string CodeContainerName { get; set; } = "auto-grading-dotnet-console-app";
    public string AppType { get; set; } = "Console";
    public string EnvironmentType { get; set; } = "dotnet";
    public string RuntimesFolder { get; set; } = "";
    
    // Protocol configuration
    public string Protocol { get; set; } = "TCP";
    
    /// <summary>
    /// Default Grade_Content from test kit root Header.xlsx Config sheet.
    /// Determines what students submit: "Server", "Client", or "Client/Server".
    /// This is used to automatically set HasServer and HasClient if not explicitly provided.
    /// </summary>
    public string DefaultGradeContent { get; set; } = "Client/Server";
    
    /// <summary>
    /// Path to the given/golden server DLL from Meta/Given/Server folder.
    /// This is used when the student only provides a client (Project12).
    /// </summary>
    public string? GivenServerPath { get; set; }
    
    /// <summary>
    /// Path to the given/golden client DLL from Meta/Given/Client folder.
    /// This is used when the student only provides a server (Project11).
    /// </summary>
    public string? GivenClientPath { get; set; }
    
    // Test case marks from Header.xlsx
    public Dictionary<string, double> TestCaseMarks { get; set; } = new();
    public List<TestCaseInfo> TestCases { get; set; } = new();
    
    /// <summary>
    /// Total max mark for this test kit (sum of all test case marks).
    /// 
    /// FIX: Now sums from TestCaseMarks dictionary first (populated from Header.xlsx QuestionMark sheet),
    /// falling back to TestCases list if dictionary is empty.
    /// Previously this only summed from TestCases list which was never populated, causing MaxMark to always be 0.
    /// </summary>
    public double TotalMaxMark => TestCaseMarks.Count > 0 
        ? TestCaseMarks.Values.Sum() 
        : TestCases.Sum(tc => tc.MaxMark);
}
