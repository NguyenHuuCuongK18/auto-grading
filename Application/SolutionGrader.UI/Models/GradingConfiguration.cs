using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration settings for a grading session.
    /// Contains paths, project names, and Docker container settings.
    /// 
    /// Important: This system only grades Question 1. Question 2, 3, etc. are not supported.
    /// 
    /// Note: Port configurations (CodeContainerInternalPort, CodeContainerHostPort) are read from
    /// each test kit's Environment.xlsx file. Defaults here are 0 to indicate "unspecified" so the
    /// Lib services will use values from the test kit and not override them from the UI.
    /// 
    /// Project Mapping (for Question 1 only):
    /// - Project1Name/Project2Name: The names of the Question 1 projects (e.g., "Q1", "Q11", "Q12", "Project11", "Project12")
    /// - Project1IsClient/Project2IsClient: Indicates the role (client or server) of each project
    /// - If only one project is specified, it's assumed to be both client and server (or the only component)
    /// - If two projects are specified (client/server architecture), roles must be explicitly defined
    /// 
    /// This flexible structure handles cases where:
    /// 1. Students submit with generic names like "Q1_studentcode" instead of "Project11_studentcode"
    /// 2. Students split Question 1 into client/server using Q11 (server) and Q12 (client)
    /// 3. Different papers require students to code different components (client, server, or both)
    /// 4. The test kit's Header.xlsx Grade content dictates which project is client/server
    /// 
    /// Examples:
    /// - Single project: Q1 → Both client and server use Q1.dll
    /// - Dual project traditional: Project11 (server) + Project12 (client)
    /// - Dual project numbered: Q11 (server) + Q12 (client) - both for Question 1!
    /// </summary>
    public class GradingConfiguration : INotifyPropertyChanged
    {
        private string _submitFolderPath = string.Empty;
        private string _testKitFolderPath = string.Empty;
        private string _saveResultFolderPath = string.Empty;
        private bool _hasClient = true;
        private bool _hasServer = true;
        
        // New project mapping structure
        private string _project1Name = string.Empty;
        private string _project2Name = string.Empty;
        private bool _project1IsClient = false; // true = client, false = server
        private bool _project2IsClient = true;  // true = client, false = server
        
        // Legacy properties for backward compatibility
        private string _clientProjectName = "Project12";
        private string _serverProjectName = "Project11";
        // Default to 0 (unspecified). DockerGradingService will read from Environment.xlsx.
        private int _codeContainerInternalPort = 0;
        private int _codeContainerHostPort = 0;
        private string _dockerNetwork = "auto-grading-network";
        private int _gradingTimeoutSeconds = 60;
        private string _databaseImageName = "mcr.microsoft.com/mssql/server:2019-latest";
        private string _databaseContainerName = "auto-grading-sqlserver";
        private int _databaseContainerInternalPort = 1433;
        private int _databaseContainerHostPort = 1434;
        private string _databaseUsername = "sa";
        // Database password should be read from Environment.xlsx or environment variable
        // Do not hardcode passwords in production code
        private string _databasePassword = Environment.GetEnvironmentVariable("AUTOGRADING_DB_PASSWORD") ?? "";
        
        // Parallel grading settings
        private int _maxParallelStudents = 1;
        private int _startIndex = 0;
        private int _endIndex = -1; // -1 means grade all students
        
        // DLL modification fallback settings
        // CRITICAL: Default to true for batch grading to ensure port overrides work
        // This patches hardcoded ports in student DLLs to match allocated container ports
        private bool _useDllModificationFallback = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SubmitFolderPath
        {
            get => _submitFolderPath;
            set { _submitFolderPath = value; OnPropertyChanged(); }
        }

        public string TestKitFolderPath
        {
            get => _testKitFolderPath;
            set { _testKitFolderPath = value; OnPropertyChanged(); }
        }

        public string SaveResultFolderPath
        {
            get => _saveResultFolderPath;
            set { _saveResultFolderPath = value; OnPropertyChanged(); }
        }

        public bool HasClient
        {
            get => _hasClient;
            set { _hasClient = value; OnPropertyChanged(); }
        }

        public bool HasServer
        {
            get => _hasServer;
            set { _hasServer = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// First project name (e.g., "Q1", "Project11"). Can be client or server based on Project1IsClient.
        /// </summary>
        public string Project1Name
        {
            get => _project1Name;
            set { _project1Name = value; OnPropertyChanged(); UpdateLegacyProperties(); }
        }

        /// <summary>
        /// Second project name (e.g., "Q2", "Project12"). Can be client or server based on Project2IsClient.
        /// </summary>
        public string Project2Name
        {
            get => _project2Name;
            set { _project2Name = value; OnPropertyChanged(); UpdateLegacyProperties(); }
        }

        /// <summary>
        /// Indicates whether Project1 is the client. If false, Project1 is the server.
        /// </summary>
        public bool Project1IsClient
        {
            get => _project1IsClient;
            set { _project1IsClient = value; OnPropertyChanged(); UpdateLegacyProperties(); }
        }

        /// <summary>
        /// Indicates whether Project2 is the client. If false, Project2 is the server.
        /// </summary>
        public bool Project2IsClient
        {
            get => _project2IsClient;
            set { _project2IsClient = value; OnPropertyChanged(); UpdateLegacyProperties(); }
        }

        /// <summary>
        /// Legacy property: Client project name for backward compatibility.
        /// This is automatically updated based on Project1/Project2 mapping.
        /// </summary>
        public string ClientProjectName
        {
            get => _clientProjectName;
            set { _clientProjectName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Legacy property: Server project name for backward compatibility.
        /// This is automatically updated based on Project1/Project2 mapping.
        /// </summary>
        public string ServerProjectName
        {
            get => _serverProjectName;
            set { _serverProjectName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Updates the legacy ClientProjectName and ServerProjectName properties
        /// based on the new Project1/Project2 mapping structure.
        /// This ensures backward compatibility with existing code that uses the legacy properties.
        /// </summary>
        private void UpdateLegacyProperties()
        {
            // Determine which project is client and which is server
            if (!string.IsNullOrWhiteSpace(_project1Name) && !string.IsNullOrWhiteSpace(_project2Name))
            {
                // Both projects specified - use role flags
                AssignRoles(_project1Name, _project2Name, _project1IsClient);
            }
            else if (!string.IsNullOrWhiteSpace(_project1Name))
            {
                // Only project1 specified - it serves both roles or is the only component
                AssignBothRoles(_project1Name);
            }
            else if (!string.IsNullOrWhiteSpace(_project2Name))
            {
                // Only project2 specified - it serves both roles or is the only component
                AssignBothRoles(_project2Name);
            }
            
            OnPropertyChanged(nameof(ClientProjectName));
            OnPropertyChanged(nameof(ServerProjectName));
        }
        
        /// <summary>
        /// Assigns client and server roles based on which project is marked as client.
        /// </summary>
        private void AssignRoles(string project1, string project2, bool project1IsClient)
        {
            if (project1IsClient)
            {
                _clientProjectName = project1;
                _serverProjectName = project2;
            }
            else
            {
                _serverProjectName = project1;
                _clientProjectName = project2;
            }
        }
        
        /// <summary>
        /// Assigns the same project name to both client and server roles.
        /// Used when only one project is specified.
        /// </summary>
        private void AssignBothRoles(string projectName)
        {
            _clientProjectName = projectName;
            _serverProjectName = projectName;
        }

        /// <summary>
        /// Internal port inside the Docker container for the application. 0 means "use test kit".
        /// </summary>
        public int CodeContainerInternalPort
        {
            get => _codeContainerInternalPort;
            set { _codeContainerInternalPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Host port mapped to the container port (for network monitoring). 0 means "use test kit".
        /// </summary>
        public int CodeContainerHostPort
        {
            get => _codeContainerHostPort;
            set { _codeContainerHostPort = value; OnPropertyChanged(); }
        }

        public string DockerNetwork
        {
            get => _dockerNetwork;
            set { _dockerNetwork = value; OnPropertyChanged(); }
        }

        public int GradingTimeoutSeconds
        {
            get => _gradingTimeoutSeconds;
            set { _gradingTimeoutSeconds = value; OnPropertyChanged(); }
        }

        public string DatabaseImageName
        {
            get => _databaseImageName;
            set { _databaseImageName = value; OnPropertyChanged(); }
        }

        public string DatabaseContainerName
        {
            get => _databaseContainerName;
            set { _databaseContainerName = value; OnPropertyChanged(); }
        }

        public int DatabaseContainerInternalPort
        {
            get => _databaseContainerInternalPort;
            set { _databaseContainerInternalPort = value; OnPropertyChanged(); }
        }

        public int DatabaseContainerHostPort
        {
            get => _databaseContainerHostPort;
            set { _databaseContainerHostPort = value; OnPropertyChanged(); }
        }

        public string DatabaseUsername
        {
            get => _databaseUsername;
            set { _databaseUsername = value; OnPropertyChanged(); }
        }

        public string DatabasePassword
        {
            get => _databasePassword;
            set { _databasePassword = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Maximum number of students to grade in parallel.
        /// Default is 1 (sequential grading).
        /// </summary>
        public int MaxParallelStudents
        {
            get => _maxParallelStudents;
            set { _maxParallelStudents = Math.Max(1, value); OnPropertyChanged(); }
        }

        /// <summary>
        /// Start index for selective grading (0-based).
        /// Allows restarting from a specific student in case of incidents.
        /// </summary>
        public int StartIndex
        {
            get => _startIndex;
            set { _startIndex = Math.Max(0, value); OnPropertyChanged(); }
        }

        /// <summary>
        /// End index for selective grading (0-based, inclusive).
        /// -1 means grade all students from StartIndex to the end.
        /// </summary>
        public int EndIndex
        {
            get => _endIndex;
            set { _endIndex = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Enables DLL modification fallback when appsettings.json is not found.
        /// When enabled, the system will attempt to directly modify the compiled DLL files
        /// to patch hardcoded IP addresses and port numbers instead of relying on appsettings.json.
        /// 
        /// CRITICAL for batch grading: This patches student-hardcoded values like:
        /// - IP addresses: localhost, 127.0.0.1 → host.docker.internal (client) or 0.0.0.0 (server)
        /// - Ports: 4000, 5000, 8080, etc. → allocated port (8000, 8001, 8002, ...)
        /// 
        /// Without this, students who hardcode ports will fail because:
        /// - Student hardcodes port 4000 in DLL
        /// - Container runs on port 8001 (dynamically allocated)
        /// - Client tries port 4000 → connection fails
        /// 
        /// With this enabled:
        /// - Student hardcodes port 4000 in DLL
        /// - DLL patched: 4000 → 8001
        /// - Container runs on port 8001
        /// - Client connects to 8001 → success!
        /// 
        /// Default: true (enabled for reliable batch grading)
        /// </summary>
        public bool UseDllModificationFallback
        {
            get => _useDllModificationFallback;
            set { _useDllModificationFallback = value; OnPropertyChanged(); }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public GradingConfiguration Clone()
        {
            return new GradingConfiguration
            {
                SubmitFolderPath = this.SubmitFolderPath,
                TestKitFolderPath = this.TestKitFolderPath,
                SaveResultFolderPath = this.SaveResultFolderPath,
                HasClient = this.HasClient,
                HasServer = this.HasServer,
                Project1Name = this.Project1Name,
                Project2Name = this.Project2Name,
                Project1IsClient = this.Project1IsClient,
                Project2IsClient = this.Project2IsClient,
                ClientProjectName = this.ClientProjectName,
                ServerProjectName = this.ServerProjectName,
                CodeContainerInternalPort = this.CodeContainerInternalPort,
                CodeContainerHostPort = this.CodeContainerHostPort,
                DockerNetwork = this.DockerNetwork,
                GradingTimeoutSeconds = this.GradingTimeoutSeconds,
                DatabaseImageName = this.DatabaseImageName,
                DatabaseContainerName = this.DatabaseContainerName,
                DatabaseContainerInternalPort = this.DatabaseContainerInternalPort,
                DatabaseContainerHostPort = this.DatabaseContainerHostPort,
                DatabaseUsername = this.DatabaseUsername,
                DatabasePassword = this.DatabasePassword,
                MaxParallelStudents = this.MaxParallelStudents,
                StartIndex = this.StartIndex,
                EndIndex = this.EndIndex,
                UseDllModificationFallback = this.UseDllModificationFallback
            };
        }
    }
}
