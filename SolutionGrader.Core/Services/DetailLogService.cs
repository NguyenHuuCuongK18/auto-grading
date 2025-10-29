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

        public void BeginCase(string outFolder, string questionCode, string detailTemplatePath)
        {
            _caseOutFolder = outFolder;
            _questionCode = questionCode;
            _detailTemplatePath = detailTemplatePath;

            _files.EnsureDirectory(outFolder);
            _gradesCsvPath = Path.Combine(outFolder, "grades.csv");
            _jsonlPath = Path.Combine(outFolder, "detailed_log.jsonl");

            if (!File.Exists(_gradesCsvPath))
            {
                File.WriteAllText(_gradesCsvPath,
                    "Question,StepId,Stage,Action,Passed,PointsAwarded,PointsPossible,ErrorCode,ErrorCategory,DurationMs,DetailPath,Message\n",
                    Encoding.UTF8);
            }
            if (!File.Exists(_jsonlPath))
            {
                File.WriteAllText(_jsonlPath, "", Encoding.UTF8);
            }
        }

        public void EndCase()
        {
            _caseOutFolder = null;
            _questionCode = null;
            _detailTemplatePath = null;
            _gradesCsvPath = null;
            _jsonlPath = null;
        }

        public void LogStepGrade(
            Step step,
            bool passed,
            string message,
            int pointsAwarded,
            int pointsPossible,
            double durationMs,
            string errorCode,
            string? detailPath = null,
            string? actualPath = null)
        {
            if (_caseOutFolder == null || _gradesCsvPath == null || _jsonlPath == null) return;

            var rec = new StepGradeRecord
            {
                QuestionCode = _questionCode ?? step.QuestionCode,
                StepId = step.Id,
                Stage = step.Stage,
                Action = step.Action,
                Passed = passed,
                PointsAwarded = pointsAwarded,
                PointsPossible = pointsPossible,
                ErrorCode = errorCode,
                ErrorCategory = ErrorCodes.CategoryOf(errorCode),
                DetailPath = detailPath,
                Message = message,
                DurationMs = durationMs
            };

            var csvMsg = (rec.Message ?? "").Replace("\"", "\"\"");
            var csvDetail = (rec.DetailPath ?? "").Replace("\"", "\"\"");
            var line = $"{rec.QuestionCode},{rec.StepId},{rec.Stage},{rec.Action},{rec.Passed},{rec.PointsAwarded},{rec.PointsPossible},{rec.ErrorCode},{rec.ErrorCategory},{rec.DurationMs:0},\"{csvDetail}\",\"{csvMsg}\"\n";
            File.AppendAllText(_gradesCsvPath, line, Encoding.UTF8);

            var json = JsonSerializer.Serialize(rec);
            File.AppendAllText(_jsonlPath, json + "\n", Encoding.UTF8);
        }

        public string WriteTextMismatchDiff(string questionCode, int stage, string expectedPath, string actualPath, DetailedCompareResult detail)
        {
            if (_caseOutFolder == null) return string.Empty;
            var folder = Path.Combine(_caseOutFolder, "mismatches", questionCode);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{stage}.diff.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Step: {questionCode}-{stage}");
            sb.AppendLine($"Expected: {expectedPath}");
            sb.AppendLine($"Actual  : {actualPath}");
            sb.AppendLine($"First diff at index: {detail.FirstDiffIndex}");
            sb.AppendLine();

            sb.AppendLine("[Expected]");
            sb.AppendLine(detail.ExpectedContext);
            sb.AppendLine();

            sb.AppendLine("[Actual]");
            sb.AppendLine(detail.ActualContext);
            sb.AppendLine();

            sb.AppendLine(detail.Message);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        public void LogSkip(Step step, string reason, string errorCode)
        {
            LogStepGrade(step, passed: true, message: $"SKIP: {reason}", pointsAwarded: 0, pointsPossible: 0, durationMs: 0, errorCode: errorCode);
        }
    }
}
