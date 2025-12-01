namespace SolutionGrader.Core.Abstractions;

using SolutionGrader.Core.Domain.Models;

public interface ITestCaseParser
{
    System.Collections.Generic.IReadOnlyList<Step> ParseDetail(string detailXlsxPath, string questionCode);
}
