#nullable enable
using System.Collections.Generic;

namespace Domain.Models
{
    /// <summary>
    /// Result of a single test case - matches SampleLogging format.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = "";
        public double EarnedMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
        
        /// <summary>Actions executed (StartClient, StartServer, Input, etc.) - for User sheet</summary>
        public List<ActionRecord> Actions { get; set; } = new();
        
        /// <summary>Client console output comparisons - for Client sheet</summary>
        public List<ComparisonResult> ClientComparisons { get; set; } = new();
        
        /// <summary>Server console output comparisons - for Server sheet</summary>
        public List<ComparisonResult> ServerComparisons { get; set; } = new();
        
        /// <summary>Network flow comparisons - for Network sheet (expected vs actual)</summary>
        public List<ComparisonResult> NetworkComparisons { get; set; } = new();
        
        /// <summary>Captured network packets - for Network sheet (raw captures)</summary>
        public List<CapturedNetworkPacket> NetworkCaptures { get; set; } = new();
        
        /// <summary>Grading process records - for GradeProcess sheet (execution log)</summary>
        public List<GradeProcessRecord> GradeProcessRecords { get; set; } = new();
    }
}
