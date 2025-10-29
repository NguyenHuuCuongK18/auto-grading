namespace SolutionGrader.Core.Domain.Models
{
    public sealed class DetailedCompareResult
    {
        public bool AreEqual { get; init; }
        public string Message { get; init; } = string.Empty;

        public int FirstDiffIndex { get; init; } = -1;
        public char? ExpectedChar { get; init; }
        public char? ActualChar { get; init; }

        public string ExpectedContext { get; init; } = string.Empty;
        public string ActualContext { get; init; } = string.Empty;

        public string NormalizedExpected { get; init; } = string.Empty;
        public string NormalizedActual { get; init; } = string.Empty;
    }
}
