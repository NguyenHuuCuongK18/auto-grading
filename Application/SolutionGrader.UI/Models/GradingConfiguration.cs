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
                GradingTimeoutSeconds = this.GradingTimeoutSeconds
            };
        }
    }
}
