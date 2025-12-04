using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Represents the grading status of a student solution.
    /// Not_Run: Solution has not been graded yet or test kit is missing
    /// InProgress: Grading is currently in progress
    /// Paused: Grading was paused and can be resumed
    /// Success: Grading completed successfully
    /// Failed: Grading completed with failures
    /// Disposed: Solution state was reset/cleared
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
    /// Represents a student's solution for grading.
    /// Contains information about the student, their solution paths,
    /// and grading status/results.
    /// </summary>
    public class StudentSolution : INotifyPropertyChanged
    {
        private int _id; // 1-based index in UI list
        private string _studentCode = string.Empty;
        private string _paperNo = string.Empty;
        private string _solutionPath = string.Empty;
        private string? _clientDllPath;
        private string? _serverDllPath;
        private GradingStatus _status = GradingStatus.Not_Run;
        private double _mark;
        private double _maxMark;
        private DateTime? _startTime;
        private DateTime? _endTime;
        private string? _statusMessage;
        private int _progressPercent;
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 1-based identifier used for UI selection by index.
        /// </summary>
        public int Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Unique identifier for the student (e.g., "cuongnhhe186494")
        /// </summary>
        public string StudentCode
        {
            get => _studentCode;
            set { _studentCode = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Paper number this solution belongs to (e.g., "1", "2")
        /// </summary>
        public string PaperNo
        {
            get => _paperNo;
            set { _paperNo = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Full path to the solution folder (e.g., Submit/1/cuongnhhe186494/1/solution)
        /// </summary>
        public string SolutionPath
        {
            get => _solutionPath;
            set { _solutionPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Path to the client DLL file (found dynamically based on project name)
        /// </summary>
        public string? ClientDllPath
        {
            get => _clientDllPath;
            set { _clientDllPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Path to the server DLL file (found dynamically based on project name)
        /// </summary>
        public string? ServerDllPath
        {
            get => _serverDllPath;
            set { _serverDllPath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Current grading status of this solution
        /// </summary>
        public GradingStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplay)); }
        }

        /// <summary>
        /// Display string for the status
        /// </summary>
        public string StatusDisplay => _status.ToString().Replace("_", " ");

        /// <summary>
        /// Final mark/score for this solution
        /// </summary>
        public double Mark
        {
            get => _mark;
            set { _mark = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Maximum possible mark for this solution (read from Header.xlsx)
        /// </summary>
        public double MaxMark
        {
            get => _maxMark;
            set { _maxMark = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Time when grading started
        /// </summary>
        public DateTime? StartTime
        {
            get => _startTime;
            set { _startTime = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Time when grading completed
        /// </summary>
        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Additional status message (e.g., error details, "No test kit for this paper")
        /// </summary>
        public string? StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Progress percentage (0-100) for grading
        /// </summary>
        public int ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = Math.Max(0, Math.Min(100, value)); OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether this student is selected in the UI for batch operations
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Duration of grading in human-readable format
        /// </summary>
        public string Duration
        {
            get
            {
                if (!StartTime.HasValue) return "-";
                var end = EndTime ?? DateTime.Now;
                var duration = end - StartTime.Value;
                return duration.TotalMinutes < 1
                    ? $"{duration.Seconds}s"
                    : $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
