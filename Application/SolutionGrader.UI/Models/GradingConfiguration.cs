using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration settings for a grading session.
    /// Contains paths, project names, and Docker container settings.
    /// </summary>
    public class GradingConfiguration : INotifyPropertyChanged
    {
        private string _submitFolderPath = string.Empty;
        private string _testKitFolderPath = string.Empty;
        private bool _hasClient = true;
        private bool _hasServer = true;
        private string _clientProjectName = "Project12";
        private string _serverProjectName = "Project11";
        private int _codeContainerInternalPort = 5000;
        private int _codeContainerHostPort = 5000;
        private string _dockerNetwork = "auto-grading-network";
        private int _gradingTimeoutSeconds = 60;

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

        #region Database Configuration

        // NOTE: Database configuration should be read from Environment.xlsx, not hardcoded
        // These fields will be populated from TestKitConfig when loading a test kit
        private string _databaseImageName = string.Empty;
        private string _databaseContainerName = string.Empty;
        private int _databaseContainerInternalPort;
        private int _databaseContainerHostPort;
        private string _databaseUsername = string.Empty;
        private string _databasePassword = string.Empty;
        private string _databaseName = string.Empty;
        private string _saveResultFolderPath = string.Empty;

        /// <summary>
        /// Docker image name for the database container.
        /// Read from Environment.xlsx (Database_Image_Name key).
        /// </summary>
        public string DatabaseImageName
        {
            get => _databaseImageName;
            set { _databaseImageName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Name of the database container.
        /// Read from Environment.xlsx (Database_Container_Name key).
        /// </summary>
        public string DatabaseContainerName
        {
            get => _databaseContainerName;
            set { _databaseContainerName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Internal port for the database container.
        /// Read from Environment.xlsx (Database_Container_Internal_Port key).
        /// </summary>
        public int DatabaseContainerInternalPort
        {
            get => _databaseContainerInternalPort;
            set { _databaseContainerInternalPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Host port mapped to the database container.
        /// Read from Environment.xlsx (Database_Container_Host_Port key).
        /// </summary>
        public int DatabaseContainerHostPort
        {
            get => _databaseContainerHostPort;
            set { _databaseContainerHostPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database username (e.g., SA for SQL Server).
        /// Read from Environment.xlsx (Database_Username key).
        /// </summary>
        public string DatabaseUsername
        {
            get => _databaseUsername;
            set { _databaseUsername = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Database password.
        /// Read from Environment.xlsx (Database_Password key).
        /// IMPORTANT: This should never be hardcoded - always read from Environment.xlsx.
        /// </summary>
        public string DatabasePassword
        {
            get => _databasePassword;
            set { _databasePassword = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Default database name.
        /// Read from Environment.xlsx (Default_Database_Name key).
        /// </summary>
        public string DatabaseName
        {
            get => _databaseName;
            set { _databaseName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Path to save grading results
        /// </summary>
        public string SaveResultFolderPath
        {
            get => _saveResultFolderPath;
            set { _saveResultFolderPath = value; OnPropertyChanged(); }
        }

        #endregion

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
                DatabasePassword = this.DatabasePassword,
                DatabaseName = this.DatabaseName,
                SaveResultFolderPath = this.SaveResultFolderPath
            };
        }
    }
}
