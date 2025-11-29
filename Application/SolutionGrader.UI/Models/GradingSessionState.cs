using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Represents the overall grading session state.
    /// Tracks progress, paused students, and completion status.
    /// </summary>
    public class GradingSessionState : INotifyPropertyChanged
    {
        private bool _isRunning;
        private bool _isPaused;
        private int _totalStudents;
        private int _gradedStudents;
        private int _successCount;
        private int _failedCount;
        private int _notRunCount;
        private string? _currentStudentCode;
        private DateTime? _sessionStartTime;
        private DateTime? _sessionEndTime;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// List of student codes that are currently paused and can be resumed
        /// </summary>
        public List<string> PausedStudentCodes { get; } = new List<string>();

        /// <summary>
        /// Whether grading is currently running (not paused)
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanPause)); }
        }

        /// <summary>
        /// Whether grading is paused
        /// </summary>
        public bool IsPaused
        {
            get => _isPaused;
            set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanPause)); }
        }

        /// <summary>
        /// Whether the Start button should be enabled
        /// </summary>
        public bool CanStart => !IsRunning || IsPaused;

        /// <summary>
        /// Whether the Pause button should be enabled
        /// </summary>
        public bool CanPause => IsRunning && !IsPaused;

        /// <summary>
        /// Total number of students to grade
        /// </summary>
        public int TotalStudents
        {
            get => _totalStudents;
            set { _totalStudents = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverallProgress)); }
        }

        /// <summary>
        /// Number of students already graded
        /// </summary>
        public int GradedStudents
        {
            get => _gradedStudents;
            set { _gradedStudents = value; OnPropertyChanged(); OnPropertyChanged(nameof(OverallProgress)); }
        }

        /// <summary>
        /// Number of successful gradings
        /// </summary>
        public int SuccessCount
        {
            get => _successCount;
            set { _successCount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Number of failed gradings
        /// </summary>
        public int FailedCount
        {
            get => _failedCount;
            set { _failedCount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Number of students not yet run (including those without test kits)
        /// </summary>
        public int NotRunCount
        {
            get => _notRunCount;
            set { _notRunCount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Student code currently being graded
        /// </summary>
        public string? CurrentStudentCode
        {
            get => _currentStudentCode;
            set { _currentStudentCode = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Overall progress percentage (0-100)
        /// </summary>
        public int OverallProgress
        {
            get
            {
                if (TotalStudents == 0) return 0;
                return (int)((double)GradedStudents / TotalStudents * 100);
            }
        }

        /// <summary>
        /// Time when the grading session started
        /// </summary>
        public DateTime? SessionStartTime
        {
            get => _sessionStartTime;
            set { _sessionStartTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedTime)); }
        }

        /// <summary>
        /// Time when the grading session ended
        /// </summary>
        public DateTime? SessionEndTime
        {
            get => _sessionEndTime;
            set { _sessionEndTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedTime)); }
        }

        /// <summary>
        /// Elapsed time for the grading session
        /// </summary>
        public string ElapsedTime
        {
            get
            {
                if (!SessionStartTime.HasValue) return "-";
                var end = SessionEndTime ?? DateTime.Now;
                var duration = end - SessionStartTime.Value;
                if (duration.TotalHours >= 1)
                    return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
                if (duration.TotalMinutes >= 1)
                    return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
                return $"{duration.Seconds}s";
            }
        }

        /// <summary>
        /// Resets all session state to initial values
        /// </summary>
        public void Reset()
        {
            IsRunning = false;
            IsPaused = false;
            GradedStudents = 0;
            SuccessCount = 0;
            FailedCount = 0;
            NotRunCount = TotalStudents;
            CurrentStudentCode = null;
            SessionStartTime = null;
            SessionEndTime = null;
            PausedStudentCodes.Clear();
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
