using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Configuration settings for grading session.
    /// </summary>
    public class GradingConfiguration : INotifyPropertyChanged
    {
        private string _submitFolderPath = string.Empty;
        private string _testKitFolderPath = string.Empty;
        private string _saveResultFolderPath = string.Empty;
        private bool _hasClient = true;
        private bool _hasServer = true;
        private string _clientProjectName = "Client";
        private string _serverProjectName = "Server";
        private int _serverPort = 5000;

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

        public int ServerPort
        {
            get => _serverPort;
            set { _serverPort = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Maps paper numbers to testkit names.
        /// Key: Paper number (e.g., "1", "2")
        /// Value: TestKit folder name (e.g., "Q1", "Q2")
        /// </summary>
        public Dictionary<string, string> PaperToTestKitMapping { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents a student submission for grading.
    /// </summary>
    public class StudentSolution : INotifyPropertyChanged
    {
        private string _studentCode = string.Empty;
        private string _paperNo = string.Empty;
        private string _solutionPath = string.Empty;
        private GradingStatus _status = GradingStatus.Not_Run;
        private double _score;
        private double _maxScore;
        private string _message = string.Empty;
        private DateTime? _startTime;
        private DateTime? _endTime;

        public string StudentCode
        {
            get => _studentCode;
            set { _studentCode = value; OnPropertyChanged(); }
        }

        public string PaperNo
        {
            get => _paperNo;
            set { _paperNo = value; OnPropertyChanged(); }
        }

        public string SolutionPath
        {
            get => _solutionPath;
            set { _solutionPath = value; OnPropertyChanged(); }
        }

        public GradingStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public double Score
        {
            get => _score;
            set { _score = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScoreDisplay)); }
        }

        public double MaxScore
        {
            get => _maxScore;
            set { _maxScore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScoreDisplay)); }
        }

        public string ScoreDisplay => $"{Score:F2}/{MaxScore:F2}";

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        public DateTime? StartTime
        {
            get => _startTime;
            set { _startTime = value; OnPropertyChanged(); }
        }

        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(Duration)); }
        }

        public TimeSpan? Duration => EndTime.HasValue && StartTime.HasValue
            ? EndTime.Value - StartTime.Value
            : null;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Grading status for a student submission.
    /// </summary>
    public enum GradingStatus
    {
        Not_Run,
        InProgress,
        Paused,
        Success,
        Failed,
        Disposed
    }

    /// <summary>
    /// State of the grading session.
    /// </summary>
    public enum GradingSessionState
    {
        Idle,
        Running,
        Paused,
        Completed,
        Cancelled
    }

    /// <summary>
    /// Event arguments for log events.
    /// </summary>
    public class LogEventArgs : EventArgs
    {
        public string Message { get; }
        public LogLevel Level { get; }
        public DateTime Timestamp { get; }

        public LogEventArgs(string message, LogLevel level = LogLevel.Info)
        {
            Message = message;
            Level = level;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Log level for logging events.
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Test kit information.
    /// </summary>
    public class TestKitInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public List<string> TestCases { get; set; } = new();
        public string? MappedPaper { get; set; }
    }
}
