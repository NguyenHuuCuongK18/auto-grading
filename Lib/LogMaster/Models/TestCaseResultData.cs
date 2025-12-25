using System.Collections.Generic;

namespace LogMaster.Models
{
    /// <summary>
    /// Data structure for test case results to be written to Excel.
    /// </summary>
    public class TestCaseResultData
    {
        /// <summary>Actions performed in the test case</summary>
        public List<ActionData> Actions { get; set; } = new();
        
        /// <summary>Client console output comparisons</summary>
        public List<ComparisonResult> ClientComparisons { get; set; } = new();
        
        /// <summary>Server console output comparisons</summary>
        public List<ComparisonResult> ServerComparisons { get; set; } = new();
        
        /// <summary>Network traffic comparisons</summary>
        public List<ComparisonResult> NetworkComparisons { get; set; } = new();
    }
}
