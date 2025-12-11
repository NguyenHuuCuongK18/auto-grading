namespace Domain.Models
{
    /// <summary>
    /// Configuration for Docker-based grading containing all settings for
    /// container creation, port allocation, database setup, and timeouts.
    /// This is the primary configuration model used by DockerGradingService.
    /// </summary>
    public class DockerGradingConfig
    {
        /// <summary>
        /// Whether the examiner expects the student to provide a CLIENT component.
        /// If true, the grader will search for the client DLL in student's solution.
        /// If false and a client is needed, use the golden client from Meta/Given/Client.
        /// </summary>
        public bool HasClient { get; set; } = true;
        
        /// <summary>
        /// Whether the examiner expects the student to provide a SERVER component.
        /// If true, the grader will search for the server DLL in student's solution.
        /// If false and a server is needed, use the golden server from Meta/Given/Server.
        /// </summary>
        public bool HasServer { get; set; } = true;
        
        /// <summary>
        /// Project name for the client DLL (e.g., "Project12" searches for "Project12.dll")
        /// </summary>
        public string ClientProjectName { get; set; } = "Project12";
        
        /// <summary>
        /// Project name for the server DLL (e.g., "Project11" searches for "Project11.dll")
        /// </summary>
        public string ServerProjectName { get; set; } = "Project11";
        
        public int CodeContainerInternalPort { get; set; } = 8000;
        public int CodeContainerHostPort { get; set; } = 8000;
        public string DockerNetwork { get; set; } = "auto-grading-network";
        
        // Database container settings
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2019-latest";
        public string DatabaseContainerName { get; set; } = "auto-grading-sqlserver";
        public int DatabaseContainerInternalPort { get; set; } = 1433;
        public int DatabaseContainerHostPort { get; set; } = 1434;
        public string? DatabaseUsername { get; set; } = "sa";
        public string? DatabasePassword { get; set; }
        
        /// <summary>
        /// Total grading timeout in seconds (overall timeout for all test cases).
        /// Default: 60 seconds.
        /// </summary>
        public int GradingTimeoutSeconds { get; set; } = 180;
        
        /// <summary>
        /// Per-test-case timeout in seconds. If a test case takes longer than this,
        /// it is stopped and marked as failed. Default: 15 seconds.
        /// </summary>
        public int TestCaseTimeoutSeconds { get; set; } = 120;
        
        /// <summary>
        /// Enables DLL modification fallback when appsettings.json is not found.
        /// When enabled and appsettings.json doesn't exist, the system will attempt to 
        /// directly patch the compiled DLL files to set correct IP addresses and ports.
        /// Default: false (disabled).
        /// </summary>
        public bool UseDllModificationFallback { get; set; } = false;
        
        /// <summary>
        /// Use shared MSSQL container for all students instead of per-student containers.
        /// When enabled:
        /// - Single MSSQL container is created for entire grading session
        /// - Each student gets their own database (Student_{StudentCode})
        /// - Massive resource savings (1 container vs N containers)
        /// - Faster database reset (drop/create DB vs restart container)
        /// Default: true (enabled).
        /// </summary>
        public bool UseSharedDatabaseContainer { get; set; } = true;
        
        /// <summary>
        /// Name of the shared MSSQL container.
        /// Only used when UseSharedDatabaseContainer is true.
        /// Default: "auto-grading-mssql-shared"
        /// </summary>
        public string SharedDatabaseContainerName { get; set; } = "auto-grading-mssql-shared";
        
        /// <summary>
        /// Port for the shared MSSQL container.
        /// Only used when UseSharedDatabaseContainer is true.
        /// Default: 1433
        /// </summary>
        public int SharedDatabasePort { get; set; } = 1433;
        
        /// <summary>
        /// Image name for unified containers that run both client and server processes.
        /// This image has supervisord installed for process management.
        /// Default: "fptuxaes/aes-dotnet8-console:latest"
        /// </summary>
        public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8-console:latest";
    }
}
