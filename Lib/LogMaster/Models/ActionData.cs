namespace LogMaster.Models
{
    /// <summary>
    /// Data structure for an action in a test case.
    /// </summary>
    public class ActionData
    {
        /// <summary>Stage number</summary>
        public int Stage { get; set; }
        
        /// <summary>Input value if any</summary>
        public string? Input { get; set; }
        
        /// <summary>Type of action (StartClient, StartServer, Input, etc.)</summary>
        public string? ActionType { get; set; }
    }
}
