using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions
{
    public interface IDetailLogService
    {
        // detailTemplatePath: the Test Kit's Detail.xlsx for this case (q.DetailPath)
        void BeginCase(string outFolder, string questionCode, string detailTemplatePath, double pointsPossible);
        void EndCase();

        void SetTestCaseMark(double mark);
        void SetTotalCompareSteps(int count);

        void LogStepGrade(
            Step step,
            bool passed,
            string message,
            double pointsAwarded,
            double pointsPossible,
            double durationMs,
            string errorCode,
            string? detailPath = null,
            string? actualPath = null);

        void LogCaseSummary(string questionCode, bool passed, double pointsAwarded, double pointsPossible, string message);

        string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail);

        void LogSkip(Step step, string reason, string errorCode);
    }
}
