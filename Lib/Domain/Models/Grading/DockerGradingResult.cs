#nullable enable
using System.Collections.Generic;

namespace Domain.Models.Grading
{
    /// <summary>
    /// Complete grading result for a student containing all test case results.
    /// Note: No "Passed" field at grading level - grading either completes or errors.
    /// Individual test cases have Pass/Fail status.
    /// </summary>
    public class DockerGradingResult
    {
        public string StudentCode { get; set; } = "";
        public double TotalMark { get; set; }
        public double MaxMark { get; set; }
        public string? ErrorMessage { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new();
    }
}
