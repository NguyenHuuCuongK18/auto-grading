namespace SolutionGrader.Core.Domain.Models;

/// <summary>
/// Represents expected outputs for a specific stage from Detail.xlsx.
/// Used for comparing actual console outputs against expected values.
/// </summary>
public class ExpectedOutput
{
    public int Stage { get; set; }
    public string? ClientConsole { get; set; }
    public string? ServerConsole { get; set; }
}
