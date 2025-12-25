namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Information about a single test case in the test kit.
/// </summary>
public class TestCaseInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public double MaxMark { get; set; }
    
    /// <summary>
    /// Per-test-case timeout in seconds, read from Header.xlsx Testcase_Property sheet.
    /// Defaults to 120 seconds if not specified in the test kit.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
    
    /// <summary>
    /// Specifies what should be graded for this test case.
    /// Values: "Client", "Server", or "Client/Server"
    /// - "Client": Grade student's client with golden server
    /// - "Server": Grade student's server with golden client
    /// - "Client/Server": Grade both student's client and server (no golden used)
    /// Read from Header.xlsx Testcase_Property sheet.
    /// Defaults to "Client/Server" if not specified.
    /// </summary>
    public string GradeContent { get; set; } = "Client/Server";
}
