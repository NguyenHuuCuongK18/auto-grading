using System.Text;
using System.Text.Json;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services
{
    public sealed class DetailLogService : IDetailLogService
    {
        private readonly IFileService _files;
        private string? _caseOutFolder;
        private string? _questionCode;
        private string? _detailTemplatePath;
        private string? _gradesCsvPath;
        private string? _jsonlPath;

        public DetailLogService(IFileService files) => _files = files;

        public void BeginCase(string outFolder, string questionCode, string detailTemplatePath, double pointsPossible)
        {
            _caseOutFolder = outFolder; _questionCode = questionCode; _detailTemplatePath = detailTemplatePath;
            _files.EnsureDirectory(outFolder);

            _gradesCsvPath = System.IO.Path.Combine(outFolder, "grades.csv");
            _jsonlPath = System.IO.Path.Combine(outFolder, "detailed_log.jsonl");

            if (!File.Exists(_gradesCsvPath))
            {
                // No ActualPath column here on purpose
                File.WriteAllText(_gradesCsvPath,
                    "Question,StepId,Stage,Action,Passed,PointsAwarded,PointsPossible,ErrorCode,ErrorCategory,DurationMs,DetailPath,Message\n",
                    Encoding.UTF8);
            }
            if (!File.Exists(_jsonlPath))
            {
                File.WriteAllText(_jsonlPath, "", Encoding.UTF8);
            }
        }

        public void EndCase() { }

        public void SetTestCaseMark(double mark) { }
        public void SetTotalCompareSteps(int count) { }

        public void LogStepGrade(
            Step step,
            bool passed,
            string message,
            double pointsAwarded,
            double pointsPossible,
            double durationMs,
            string errorCode,
            string? detailPath = null,
            string? actualPath = null)
        {
            var errorCategory = ErrorCodes.CategoryOf(errorCode).ToString();
            
            var csv = string.Join(',',
                Escape(_questionCode),
                Escape(step.Id),
                Escape(step.Stage),
                Escape(step.Action),
                passed ? "true" : "false",
                pointsAwarded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pointsPossible.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(errorCode),
                Escape(errorCategory),
                durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(detailPath ?? ""),
                Escape(message ?? "")
            ) + "\n";

            File.AppendAllText(_gradesCsvPath!, csv, Encoding.UTF8);

            var json = JsonSerializer.Serialize(new
            {
                Question = _questionCode,
                StepId = step.Id,
                Stage = step.Stage,
                Action = step.Action,
                Passed = passed,
                PointsAwarded = pointsAwarded,
                PointsPossible = pointsPossible,
                ErrorCode = errorCode,
                ErrorCategory = errorCategory,
                DurationMs = durationMs,
                DetailPath = detailPath,
                Message = message
            });
            File.AppendAllText(_jsonlPath!, json + "\n", Encoding.UTF8);
        }

        public void LogCaseSummary(string questionCode, bool passed, double pointsAwarded, double pointsPossible, string message)
        {
            var p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_caseOutFolder!)!, "OverallSummary.csv");
            if (!File.Exists(p))
            {
                File.WriteAllText(p, "TestCase,Passed,PointsAwarded,PointsPossible,Message\n", Encoding.UTF8);
            }
            File.AppendAllText(p, string.Join(',',
                Escape(questionCode),
                passed ? "true" : "false",
                pointsAwarded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pointsPossible.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(message ?? "")) + "\n", Encoding.UTF8);
        }

        public string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail)
        {
            var mismRoot = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_caseOutFolder!)!, "mismatches", questionCode);
            _files.EnsureDirectory(mismRoot);
            var outPath = System.IO.Path.Combine(mismRoot, $"stage_{stage}.diff.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Question: {questionCode} | Stage: {stage}");
            sb.AppendLine($"FirstDiffIndex: {detail.FirstDiffIndex}");
            sb.AppendLine();
            sb.AppendLine("From test case (expected):");
            sb.AppendLine(detail.ExpectedContext ?? "");
            sb.AppendLine();
            sb.AppendLine("Got:");
            sb.AppendLine(detail.ActualContext ?? "");
            sb.AppendLine();

            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            return outPath;
        }

        public void LogSkip(Step step, string reason, string errorCode)
        {
            LogStepGrade(step, false, reason, 0, 0, 0, errorCode, null, null);
        }

        private static string Escape(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }
    }
}
