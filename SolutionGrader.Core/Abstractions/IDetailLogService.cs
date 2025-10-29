using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions
{
    public interface IDetailLogService
    {
        // detailTemplatePath: the Test Kit's Detail.xlsx for this case (q.DetailPath)
        void BeginCase(string outFolder, string questionCode, string detailTemplatePath);
        void EndCase();

        void LogStepGrade(
            Step step,
            bool passed,
            string message,
            int pointsAwarded,
            int pointsPossible,
            double durationMs,
            string errorCode,
            string? detailPath = null,
            string? actualPath = null);

        string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail);

        void LogSkip(Step step, string reason, string errorCode);
    }
}
