namespace Domain.Models.Grading
{
    /// <summary>
    /// Record for GradeProcess sheet - logs grading execution details.
    /// Used to track the grading process and help users understand where grading failed or succeeded.
    /// Columns: Stage, Action, GradeAction, Message
    /// </summary>
    public sealed class GradeProcessRecord
    {
        /// <summary>
        /// Stage number from the test case execution (same as User sheet stage).
        /// </summary>
        public int Stage { get; init; }
        
        /// <summary>
        /// Action from the test case (e.g., StartServer, StartClient, Input, CompareConsole).
        /// Copied from the User sheet action.
        /// </summary>
        public string Action { get; init; } = string.Empty;
        
        /// <summary>
        /// Grading action description showing what grading step was performed.
        /// Examples: "Compare Client Console", "Compare Server Console", "Compare Network Flow", "Skip Grading"
        /// </summary>
        public string GradeAction { get; init; } = string.Empty;
        
        /// <summary>
        /// Detailed message about the grading result.
        /// Examples: 
        /// - "PASS: Output matches expected"
        /// - "FAIL: Mismatch at position 42"
        /// - "SKIP: Not found in Detail.xlsx"
        /// - "ERROR: Client crashed/timed out"
        /// </summary>
        public string Message { get; init; } = string.Empty;
    }
}
