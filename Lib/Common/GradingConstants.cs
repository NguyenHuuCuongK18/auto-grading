namespace Common
{
    /// <summary>
    /// Common constants and utilities shared across the grading system.
    /// </summary>
    public static class GradingConstants
    {
        /// <summary>
        /// Default timeout in seconds for waiting after an action.
        /// </summary>
        public const int DefaultActionTimeoutSeconds = 10;

        /// <summary>
        /// Delay in milliseconds after input to allow console output to complete.
        /// </summary>
        public const int PostInputDelayMs = 2000;

        /// <summary>
        /// Delay in milliseconds after stage-changing actions.
        /// </summary>
        public const int PostStageChangeDelayMs = 1500;

        /// <summary>
        /// Default server port for TCP networking applications.
        /// </summary>
        public const int DefaultServerPort = 5000;

        /// <summary>
        /// Docker network name for container communication.
        /// </summary>
        public const string DockerNetworkName = "ag-network";

        /// <summary>
        /// Container name prefix for grading.
        /// </summary>
        public const string ContainerNamePrefix = "ag-";
    }

    /// <summary>
    /// Stage-changing action types that trigger stage increments.
    /// </summary>
    public enum StageAction
    {
        None,
        StartClient,
        StartServer,
        CloseClient,
        CloseServer,
        Input
    }

    /// <summary>
    /// Represents grading status for a student submission.
    /// </summary>
    public enum GradingStatus
    {
        NotRun,
        InProgress,
        Paused,
        Success,
        Failed,
        Disposed
    }
}
