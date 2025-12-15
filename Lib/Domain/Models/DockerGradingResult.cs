using System;
using System.Collections.Generic;

namespace Domain.Models
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
    
    /// <summary>
    /// Action record for User sheet (StartClient, StartServer, Input, etc.)
    /// </summary>
    public class ActionRecord
    {
        public int Stage { get; set; }
        public string? Input { get; set; }
        public string? ActionType { get; set; }
    }
    
    /// <summary>
    /// Comparison result for console output or network - extended with SampleLogging fields.
    /// </summary>
    public class ComparisonResult
    {
        public string Source { get; set; } = "";
        public int Stage { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public bool Passed { get; set; }
        
        // Additional fields for SampleLogging format
        public double PointsAwarded { get; set; }
        public double PointsPossible { get; set; }
        public double DurationMs { get; set; }
        public string? Message { get; set; }
    }
    
    /// <summary>
    /// Event arguments for grading progress updates.
    /// </summary>
    public class GradingProgressEventArgs : EventArgs
    {
        public string Message { get; }
        public GradingProgressEventArgs(string message) => Message = message;
    }
}
