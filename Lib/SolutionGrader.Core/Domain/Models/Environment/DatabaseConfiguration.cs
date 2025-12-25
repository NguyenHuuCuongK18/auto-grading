namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Configuration for database connection read from Header.xlsx
/// </summary>
public sealed class DatabaseConfiguration
{
    /// <summary>
    /// Database type (e.g., "HTTP", "TCP")
    /// </summary>
    public string Type { get; set; } = "HTTP";

    /// <summary>
    /// SQL Server instance (e.g., "SQLExpress", ".\\SQLEXPRESS")
    /// </summary>
    public string? SqlServer { get; set; }

    /// <summary>
    /// Database name
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Database username
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Database password
    /// </summary>
    public string? Password { get; set; }
}
