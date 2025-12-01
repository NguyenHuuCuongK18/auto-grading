namespace SolutionGrader.UI.Models;

/// <summary>
/// Configuration model for the grading session.
/// Contains all settings needed to run the grading process.
/// </summary>
public class GradingConfiguration
{
    /// <summary>
    /// Gets or sets the path to the Submit folder containing student solutions.
    /// </summary>
    public string SubmitFolderPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the path to the Test Kit folder.
    /// </summary>
    public string TestKitFolderPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the path where results will be saved.
    /// </summary>
    public string SaveResultFolderPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets whether the solution has a client project.
    /// </summary>
    public bool HasClient { get; set; } = true;
    
    /// <summary>
    /// Gets or sets whether the solution has a server project.
    /// </summary>
    public bool HasServer { get; set; } = true;
    
    /// <summary>
    /// Gets or sets the client project name (for finding DLL).
    /// </summary>
    public string ClientProjectName { get; set; } = "Client";
    
    /// <summary>
    /// Gets or sets the server project name (for finding DLL).
    /// </summary>
    public string ServerProjectName { get; set; } = "Server";
    
    // Port configurations (read from test kit)
    
    /// <summary>
    /// Gets or sets the internal port for the code container.
    /// </summary>
    public int CodeContainerInternalPort { get; set; } = 5000;
    
    /// <summary>
    /// Gets or sets the host port for the code container.
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
}
