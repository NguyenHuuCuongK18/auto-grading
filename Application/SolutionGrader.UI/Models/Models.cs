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

        // Database configuration properties for Docker container
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";
        public string DatabaseContainerName { get; set; } = "ag-database";
        public int DatabaseContainerInternalPort { get; set; } = 1433;
        public int DatabaseContainerHostPort { get; set; } = 1433;
        public string DatabaseUsername { get; set; } = "sa";
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";
        
        // Code container ports
        public int CodeContainerInternalPort { get; set; } = 5000;
        public int CodeContainerHostPort { get; set; } = 5000;

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
        private int _progressPercent;
        private bool _isSelected;

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

        // Aliases for existing UI code compatibility
        public double Mark
        {
            get => _score;
            set { _score = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScoreDisplay)); }
        }

        public double MaxMark
        {
            get => _maxScore;
            set { _maxScore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScoreDisplay)); }
        }

        public string ScoreDisplay => $"{Score:F2}/{MaxScore:F2}";

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusMessage)); }
        }

        // Alias for existing UI code compatibility
        public string StatusMessage
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

        public int ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

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
    public class GradingSessionState : INotifyPropertyChanged
    {
        private bool _isRunning;
        private bool _isPaused;
        private int _totalStudents;
        private int _completedCount;
        private int _notRunCount;
        private int _passedCount;
        private int _failedCount;

        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPause)); }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set { _isPaused = value; OnPropertyChanged(); }
        }

        public bool CanPause => IsRunning && !IsPaused;

        public int TotalStudents
        {
            get => _totalStudents;
            set { _totalStudents = value; OnPropertyChanged(); }
        }

        public int CompletedCount
        {
            get => _completedCount;
            set { _completedCount = value; OnPropertyChanged(); }
        }

        public int NotRunCount
        {
            get => _notRunCount;
            set { _notRunCount = value; OnPropertyChanged(); }
        }

        public int PassedCount
        {
            get => _passedCount;
            set { _passedCount = value; OnPropertyChanged(); }
        }

        public int FailedCount
        {
            get => _failedCount;
            set { _failedCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Reset()
        {
            IsRunning = false;
            IsPaused = false;
            CompletedCount = 0;
            PassedCount = 0;
            FailedCount = 0;
            NotRunCount = TotalStudents;
        }
    }

    /// <summary>
    /// Session state enum for compatibility.
    /// </summary>
    public enum SessionStateEnum
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
