using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration settings for a grading session.
    /// Contains paths, project names, and Docker container settings.
    /// 
    /// Note: Port configurations (CodeContainerInternalPort, CodeContainerHostPort) are read from
    /// each test kit's Environment.xlsx file. Defaults here are 0 to indicate "unspecified" so the
    /// Lib services will use values from the test kit and not override them from the UI.
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

        public string ClientProjectName
        {
            get => _clientProjectName;
            set { _clientProjectName = value; OnPropertyChanged(); }
        }

        public string ServerProjectName
        {
            get => _serverProjectName;
            set { _serverProjectName = value; OnPropertyChanged(); }
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
                EndIndex = this.EndIndex
            };
        }
    }
}
