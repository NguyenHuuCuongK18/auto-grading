using SolutionGrader.Core.Domain.Errors;

namespace SolutionGrader.Core.Domain.Models
{
    public sealed class StepGradeRecord
    {
        public required string QuestionCode { get; init; }
        public required string StepId { get; init; }
        public required string Stage { get; init; }
        public required string Action { get; init; }
        public required bool Passed { get; init; }

        public required double PointsAwarded { get; init; }
        public required double PointsPossible { get; init; }

        public required string ErrorCode { get; init; }
        public required ErrorCategory ErrorCategory { get; init; }

        public string? DetailPath { get; init; } // e.g., diff file on mismatch
        public string? ActualPath { get; init; }

        public required string Message { get; init; }
        public required double DurationMs { get; init; }
    }
}
