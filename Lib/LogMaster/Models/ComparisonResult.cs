namespace LogMaster.Models
{
    /// <summary>
    /// Represents the result of comparing an expected output with an actual output.
    /// </summary>
    public class ComparisonResult
    {
        /// <summary>Source of the output (Client, Server, Network)</summary>
        public string Source { get; set; } = "";
        
        /// <summary>Stage number in the test case</summary>
        public int Stage { get; set; }
        
        /// <summary>Expected output value</summary>
        public string? Expected { get; set; }
        
        /// <summary>Actual output value</summary>
        public string? Actual { get; set; }
        
        /// <summary>Whether the comparison passed</summary>
        public bool Passed { get; set; }
        
        /// <summary>Points awarded for this comparison</summary>
        public double PointsAwarded { get; set; }
        
        /// <summary>Maximum points possible for this comparison</summary>
        public double PointsPossible { get; set; }
        
        /// <summary>Duration of the comparison in milliseconds</summary>
        public long DurationMs { get; set; }
    }
}
