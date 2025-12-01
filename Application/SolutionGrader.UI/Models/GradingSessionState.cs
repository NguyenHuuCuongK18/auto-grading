using System;
using System.Collections.Generic;

namespace SolutionGrader.UI.Models
{
    /// <summary>
    /// Represents the state of a grading session.
    /// Tracks overall progress and statistics for the grading run.
    /// </summary>
    public class GradingSessionState
    {
        /// <summary>
        /// When the grading session started.
        /// </summary>
        public DateTime? SessionStartTime { get; set; }

        /// <summary>
        /// When the grading session ended.
        /// </summary>
        public DateTime? SessionEndTime { get; set; }

        /// <summary>
        /// Whether grading is currently in progress.
        /// </summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// Whether grading is paused.
        /// </summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Total number of students to grade.
        /// </summary>
        public int TotalStudents { get; set; }

        /// <summary>
        /// Number of students successfully graded.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Number of students that failed grading.
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// Number of students skipped (no test kit, etc.)
        /// </summary>
        public int SkippedCount { get; set; }

        /// <summary>
        /// Number of students not yet run.
        /// </summary>
        public int NotRunCount { get; set; }

        /// <summary>
        /// Whether grading can be paused (running and not already paused).
        /// </summary>
        public bool CanPause => IsRunning && !IsPaused;

        /// <summary>
        /// Index of the current student being graded.
        /// </summary>
        public int CurrentStudentIndex { get; set; }

        /// <summary>
        /// Code of the current student being graded.
        /// </summary>
        public string? CurrentStudentCode { get; set; }

        /// <summary>
        /// Name of the current test case being executed.
        /// </summary>
        public string? CurrentTestCase { get; set; }

        /// <summary>
        /// Number of graded students (success + failed).
        /// </summary>
        public int GradedCount => SuccessCount + FailedCount;

        /// <summary>
        /// Number of remaining students to grade.
        /// </summary>
        public int RemainingCount => TotalStudents - GradedCount - SkippedCount;

        /// <summary>
        /// Overall progress percentage.
        /// </summary>
        public int ProgressPercent => TotalStudents > 0 
            ? (GradedCount + SkippedCount) * 100 / TotalStudents 
            : 0;

        /// <summary>
        /// Duration of the session so far.
        /// </summary>
        public TimeSpan Duration => SessionStartTime.HasValue 
            ? (SessionEndTime ?? DateTime.Now) - SessionStartTime.Value 
            : TimeSpan.Zero;

        /// <summary>
        /// Resets the session state for a new grading run.
        /// </summary>
        public void Reset()
        {
            SessionStartTime = null;
            SessionEndTime = null;
            IsRunning = false;
            IsPaused = false;
            TotalStudents = 0;
            SuccessCount = 0;
            FailedCount = 0;
            SkippedCount = 0;
            CurrentStudentIndex = 0;
            CurrentStudentCode = null;
            CurrentTestCase = null;
        }
    }

    /// <summary>
    /// Configuration for a test kit folder.
    /// Contains information parsed from Header.xlsx and Environment.xlsx
    /// </summary>
    public class TestKitConfig
    {
        /// <summary>
        /// Name of the test kit (folder name).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Path to the test kit folder.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Total maximum marks from Header.xlsx.
        /// </summary>
        public double TotalMaxMark { get; set; }

        /// <summary>
        /// Network protocol (HTTP or TCP).
        /// </summary>
        public string Protocol { get; set; } = "TCP";

        /// <summary>
        /// List of test case names (folder names starting with TC).
        /// </summary>
        public List<string> TestCases { get; set; } = new();

        /// <summary>
        /// Server internal port from Environment.xlsx.
        /// </summary>
        public int CodeContainerInternalPort { get; set; } = 5001;

        /// <summary>
        /// Server host port from Environment.xlsx.
        /// </summary>
        public int CodeContainerHostPort { get; set; } = 5001;

        /// <summary>
        /// Database image name from Environment.xlsx.
        /// </summary>
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";

        /// <summary>
        /// Database container name from Environment.xlsx.
        /// </summary>
        public string DatabaseContainerName { get; set; } = "ag-db";

        /// <summary>
        /// Database internal port from Environment.xlsx.
        /// </summary>
        public int DatabaseContainerInternalPort { get; set; } = 1433;

        /// <summary>
        /// Database host port from Environment.xlsx.
        /// </summary>
        public int DatabaseContainerHostPort { get; set; } = 1433;

        /// <summary>
        /// Database username from Environment.xlsx.
        /// </summary>
        public string DatabaseUsername { get; set; } = "SA";

        /// <summary>
        /// Database password from Environment.xlsx.
        /// </summary>
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";
    }

    /// <summary>
    /// Paper to TestKit mapping configuration.
    /// Maps paper numbers to their corresponding test kits.
    /// </summary>
    public class TestKitMapping
    {
        /// <summary>
        /// Paper number (e.g., "1", "2").
        /// </summary>
        public string PaperNo { get; set; } = string.Empty;

        /// <summary>
        /// Test kit name or path for this paper.
        /// </summary>
        public string TestKitName { get; set; } = string.Empty;

        /// <summary>
        /// Whether a valid test kit exists for this paper.
        /// </summary>
        public bool HasTestKit { get; set; }
    }
}
