using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration settings for a grading session.
    /// Contains paths, project names, and Docker container settings.
    /// 
    /// Note: Port configurations (CodeContainerInternalPort, CodeContainerHostPort) are now
    /// read from each test kit's environment.xlsx file rather than user input. This ensures
    /// consistency with the test kit's expected network configuration and allows the 
    /// network monitor on Windows to sniff the correct exposed port.
    /// </summary>
    public class GradingConfiguration : INotifyPropertyChanged
    {
        private string _submitFolderPath = string.Empty;
        private string _testKitFolderPath = string.Empty;
        private string _saveResultFolderPath = string.Empty;
        private bool _hasClient = true;
        private bool _hasServer = true;
        private string _clientProjectName = "Project12";
        private string _serverProjectName = "Project11";
        private int _codeContainerInternalPort = 5000;
        private int _codeContainerHostPort = 5000;
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

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Path to the submit folder containing all student solutions.
        /// Structure: Submit/[PaperNo]/[StudentCode]/[QuestionNo]/solution
        /// </summary>
        public string SubmitFolderPath
        {
            get => _submitFolderPath;
            set { _submitFolderPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Path to the test kit folder containing test cases.
        /// Structure: TestKit/[QuestionName]/Header.xlsx, Detail.xlsx, etc.
        /// </summary>
        public string TestKitFolderPath
        {
            get => _testKitFolderPath;
            set { _testKitFolderPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Path to the folder where grading results will be saved.
        /// Results include StudentsSolution.xlsx and student-specific folders.
        /// </summary>
        public string SaveResultFolderPath
        {
            get => _saveResultFolderPath;
            set { _saveResultFolderPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the student solution contains a client component
        /// </summary>
        public bool HasClient
        {
            get => _hasClient;
            set { _hasClient = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the student solution contains a server component
        /// </summary>
        public bool HasServer
        {
            get => _hasServer;
            set { _hasServer = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Project name for the client DLL (used to find the DLL file).
        /// Example: "Project12" will search for "Project12.dll"
        /// </summary>
        public string ClientProjectName
        {
            get => _clientProjectName;
            set { _clientProjectName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Project name for the server DLL (used to find the DLL file).
        /// Example: "Project11" will search for "Project11.dll"
        /// </summary>
        public string ServerProjectName
        {
            get => _serverProjectName;
            set { _serverProjectName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Internal port inside the Docker container for the application
        /// </summary>
        public int CodeContainerInternalPort
        {
            get => _codeContainerInternalPort;
            set { _codeContainerInternalPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Host port mapped to the container port (for network monitoring)
        /// Note: This is now read from environment.xlsx but kept for runtime state
        /// </summary>
        public int CodeContainerHostPort
        {
            get => _codeContainerHostPort;
            set { _codeContainerHostPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Docker network name for container communication
        /// </summary>
        public string DockerNetwork
        {
            get => _dockerNetwork;
            set { _dockerNetwork = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Timeout in seconds for grading operations
        /// </summary>
        public int GradingTimeoutSeconds
        {
            get => _gradingTimeoutSeconds;
            set { _gradingTimeoutSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database Docker image name (e.g., mcr.microsoft.com/mssql/server:2019-latest)
        /// </summary>
        public string DatabaseImageName
        {
            get => _databaseImageName;
            set { _databaseImageName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database container name
        /// </summary>
        public string DatabaseContainerName
        {
            get => _databaseContainerName;
            set { _databaseContainerName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database container internal port
        /// </summary>
        public int DatabaseContainerInternalPort
        {
            get => _databaseContainerInternalPort;
            set { _databaseContainerInternalPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database container host port
        /// </summary>
        public int DatabaseContainerHostPort
        {
            get => _databaseContainerHostPort;
            set { _databaseContainerHostPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database username (e.g., sa for MSSQL)
        /// </summary>
        public string DatabaseUsername
        {
            get => _databaseUsername;
            set { _databaseUsername = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database password
        /// </summary>
        public string DatabasePassword
        {
            get => _databasePassword;
            set { _databasePassword = value; OnPropertyChanged(); }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Creates a deep copy of this configuration
        /// </summary>
        public GradingConfiguration Clone()
        {
            return new GradingConfiguration
            {
                SubmitFolderPath = this.SubmitFolderPath,
                TestKitFolderPath = this.TestKitFolderPath,
                SaveResultFolderPath = this.SaveResultFolderPath,
                HasClient = this.HasClient,
                HasServer = this.HasServer,
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
                DatabasePassword = this.DatabasePassword
            };
        }
    }
}
