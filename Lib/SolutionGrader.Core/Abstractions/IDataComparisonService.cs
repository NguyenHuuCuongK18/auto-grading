namespace SolutionGrader.Core.Abstractions;

public interface IDataComparisonService
{
    // Custom policy:
    // 1) If expected is missing (null/empty or file doesn't exist) -> IGNORE (pass).
    // 2) Normalize by removing newlines from both sides before comparing (also trim).
    (bool, string) CompareFile(string? expectedPath, string? actualPath);
    (bool, string) CompareText(string? expectedPath, string? actualPath, bool caseInsensitive = true);
    (bool, string) CompareJson(string? expectedPath, string? actualPath, bool ignoreOrder = true);
    (bool, string) CompareCsv(string? expectedPath, string? actualPath, bool ignoreOrder = true);
}
