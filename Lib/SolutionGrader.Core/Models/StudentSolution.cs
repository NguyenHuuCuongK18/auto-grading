using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.Core.Models
{
    /// <summary>
    /// Represents the grading status of a student submission.
    /// </summary>
    public enum GradingStatus
    {
        /// <summary>Student has not been graded yet</summary>
        Not_Run,
        
        /// <summary>Grading is currently in progress</summary>
        InProgress,
        
        /// <summary>Grading was paused by user</summary>
        Paused,
        
        /// <summary>Grading completed successfully</summary>
        Success,
        
        /// <summary>Grading failed due to an error</summary>
        Failed,

        /// <summary>Student resources have been disposed/cleaned up</summary>
        Disposed
    }

    /// <summary>
    /// Represents a student's solution submission for grading.
    /// Implements INotifyPropertyChanged for WPF data binding.
    /// 
    /// This model tracks:
    /// - Student identification (code, paper number)
    /// - Solution paths (client, server)
    /// - Grading status and results
    /// - Progress information for UI updates
    /// </summary>
    public class StudentSolution : INotifyPropertyChanged
    {
        #region Private Fields

        private bool _isSelected;
        private GradingStatus _status = GradingStatus.Not_Run;
        private double _mark;
        private double _maxMark;
        private int _progressPercent;
        private string? _statusMessage;
        private DateTime? _startTime;
        private DateTime? _endTime;

        #endregion

        #region Identification Properties

        /// <summary>
        /// Student code/ID (e.g., "cuongnhhe186494")
        /// </summary>
        public string StudentCode { get; set; } = string.Empty;

        /// <summary>
        /// Paper/exam number this submission is for (e.g., "1", "2")
        /// </summary>
        public string PaperNo { get; set; } = string.Empty;

        /// <summary>
        /// Question number within the paper (e.g., "1", "2")
        /// </summary>
        public string QuestionNo { get; set; } = string.Empty;

        #endregion

        #region Path Properties

        /// <summary>
        /// Root path to the student's solution folder.
        /// Structure: Submit/{PaperNo}/{StudentCode}/{QuestionNo}/solution
        /// </summary>
        public string SolutionPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the client project folder, if exists.
        /// Usually: {SolutionPath}/{ClientProjectName}
        /// </summary>
        public string? ClientPath { get; set; }

        /// <summary>
        /// Path to the server project folder, if exists.
        /// Usually: {SolutionPath}/{ServerProjectName}
        /// </summary>
        public string? ServerPath { get; set; }

        /// <summary>
        /// Path to the result folder for this student.
        /// Structure: {SaveResultFolderPath}/{PaperNo}/student/{StudentCode}
        /// </summary>
        public string? ResultPath { get; set; }

        #endregion

        #region Selection and Status (Observable)

        /// <summary>
        /// Whether this student is selected for grading in the UI.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// Current grading status.
        /// </summary>
        public GradingStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        /// <summary>
        /// Marks awarded to this student.
        /// </summary>
        public double Mark
        {
            get => _mark;
            set => SetProperty(ref _mark, value);
        }

        /// <summary>
        /// Maximum marks possible (from Header.xlsx in TestKit).
        /// </summary>
        public double MaxMark
        {
            get => _maxMark;
            set => SetProperty(ref _maxMark, value);
        }

        /// <summary>
        /// Progress percentage (0-100) for current grading.
        /// </summary>
        public int ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        /// <summary>
        /// Status message to display in UI (e.g., current test case, error message).
        /// </summary>
        public string? StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        /// <summary>
        /// When grading started for this student.
        /// </summary>
        public DateTime? StartTime
        {
            get => _startTime;
            set => SetProperty(ref _startTime, value);
        }

        /// <summary>
        /// When grading completed for this student.
        /// </summary>
        public DateTime? EndTime
        {
            get => _endTime;
            set => SetProperty(ref _endTime, value);
        }

        #endregion

        #region Computed Properties

        /// <summary>
        /// Duration of grading in a human-readable format.
        /// </summary>
        public string Duration
        {
            get
            {
                if (!StartTime.HasValue) return "-";
                var end = EndTime ?? DateTime.Now;
                var duration = end - StartTime.Value;
                return duration.TotalMinutes >= 1
                    ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                    : $"{duration.Seconds}s";
            }
        }

        /// <summary>
        /// Display string showing marks: "Mark / MaxMark"
        /// </summary>
        public string MarkDisplay => $"{Mark:F1} / {MaxMark:F1}";

        /// <summary>
        /// Status display with color coding hint.
        /// </summary>
        public string StatusDisplay => Status switch
        {
            GradingStatus.Not_Run => "Not Run",
            GradingStatus.InProgress => $"Running ({ProgressPercent}%)",
            GradingStatus.Paused => "Paused",
            GradingStatus.Success => "Passed",
            GradingStatus.Failed => "Failed",
            GradingStatus.Disposed => "Disposed",
            _ => "Unknown"
        };

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            
            // Also notify computed properties that depend on this property
            if (propertyName == nameof(Mark) || propertyName == nameof(MaxMark))
                OnPropertyChanged(nameof(MarkDisplay));
            if (propertyName == nameof(Status) || propertyName == nameof(ProgressPercent))
                OnPropertyChanged(nameof(StatusDisplay));
            if (propertyName == nameof(StartTime) || propertyName == nameof(EndTime))
                OnPropertyChanged(nameof(Duration));
            
            return true;
        }

        #endregion

        /// <summary>
        /// Creates a display-friendly string representation.
        /// </summary>
        public override string ToString()
        {
            return $"{StudentCode} (Paper {PaperNo}) - {StatusDisplay}";
        }
    }
}
