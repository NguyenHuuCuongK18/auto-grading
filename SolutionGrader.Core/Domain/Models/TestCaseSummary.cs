namespace SolutionGrader.Core.Domain.Models;

public sealed class TestCaseSummary
{
    public required string TestCase { get; set; }
    public bool Passed { get; set; }
    public double PointsAwarded { get; set; }
    public double PointsPossible { get; set; }
}
