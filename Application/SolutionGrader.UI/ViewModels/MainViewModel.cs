using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SolutionGrader.UI.Models;
using SolutionGrader.UI.Services;

namespace SolutionGrader.UI.ViewModels
{
    /// <summary>
    /// Main ViewModel for the grading application.
    /// Handles all UI interactions and coordinates with services.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ILoggingService _logger;
        private readonly GradingOrchestrationService _gradingService;
        
        private GradingConfiguration _configuration;
        private GradingSessionState _sessionState;
        private string _selectedPaperFilter = "All";
        private string _logOutput = string.Empty;
        private bool _isLoading;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Collection of discovered students.
        /// </summary>
        public ObservableCollection<StudentSolution> Students { get; } = new ObservableCollection<StudentSolution>();

        /// <summary>
        /// Available paper numbers for filtering.
        /// </summary>
        public ObservableCollection<string> PaperNumbers { get; } = new ObservableCollection<string> { "All" };

        /// <summary>
        /// Grading configuration settings.
        /// </summary>
        public GradingConfiguration Configuration
        {
            get => _configuration;
            set { _configuration = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Current grading session state.
        /// </summary>
        public GradingSessionState SessionState
        {
            get => _sessionState;
            set { _sessionState = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Selected paper number for filtering.
        /// </summary>
        public string SelectedPaperFilter
        {
            get => _selectedPaperFilter;
            set
            {
                _selectedPaperFilter = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        /// <summary>
        /// Log output for display.
        /// </summary>
        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the application is currently loading/processing.
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        // Commands
        public ICommand BrowseSubmitFolderCommand { get; }
        public ICommand BrowseTestKitFolderCommand { get; }
        public ICommand LoadStudentsCommand { get; }
        public ICommand StartGradingCommand { get; }
        public ICommand StartSelectedGradingCommand { get; }
        public ICommand PauseGradingCommand { get; }
        public ICommand ResumeGradingCommand { get; }
        public ICommand ResetAllCommand { get; }
        public ICommand ResetSelectedCommand { get; }

        public MainViewModel()
        {
            _configuration = new GradingConfiguration();
            _sessionState = new GradingSessionState();
            
            // Initialize services
            var logPath = AppDomain.CurrentDomain.BaseDirectory;
            _logger = new LoggingService(logPath);
            _gradingService = new GradingOrchestrationService(_logger);

            // Wire up events
            _gradingService.StudentGradingStarted += OnStudentGradingStarted;
            _gradingService.StudentGradingCompleted += OnStudentGradingCompleted;
            _gradingService.StudentProgressUpdated += OnStudentProgressUpdated;
            _gradingService.SessionStateChanged += OnSessionStateChanged;
            
            if (_logger is LoggingService loggingService)
            {
                loggingService.LogAdded += OnLogAdded;
            }

            // Initialize commands
            BrowseSubmitFolderCommand = new RelayCommand(BrowseSubmitFolder);
            BrowseTestKitFolderCommand = new RelayCommand(BrowseTestKitFolder);
            LoadStudentsCommand = new RelayCommand(async () => await LoadStudentsAsync(), () => !IsLoading);
            StartGradingCommand = new RelayCommand(async () => await StartGradingAsync(false), () => CanStartGrading());
            StartSelectedGradingCommand = new RelayCommand(async () => await StartGradingAsync(true), () => CanStartGrading() && HasSelectedStudents());
            PauseGradingCommand = new RelayCommand(PauseGrading, () => SessionState.CanPause);
            ResumeGradingCommand = new RelayCommand(ResumeGrading, () => SessionState.IsPaused);
            ResetAllCommand = new RelayCommand(ResetAll, () => !SessionState.IsRunning);
            ResetSelectedCommand = new RelayCommand(ResetSelected, () => !SessionState.IsRunning && HasSelectedStudents());
        }

        private void BrowseSubmitFolder()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Submit Folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Configuration.SubmitFolderPath = dialog.SelectedPath;
                _logger.LogInfo($"Submit folder selected: {dialog.SelectedPath}");
            }
        }

        private void BrowseTestKitFolder()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Test Kit Folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Configuration.TestKitFolderPath = dialog.SelectedPath;
                _logger.LogInfo($"Test kit folder selected: {dialog.SelectedPath}");
            }
        }

        private async Task LoadStudentsAsync()
        {
            if (string.IsNullOrEmpty(Configuration.SubmitFolderPath))
            {
                _logger.LogWarning("Please select a submit folder first");
                return;
            }

            IsLoading = true;
            try
            {
                _logger.LogInfo("Loading students...");

                await Task.Run(() =>
                {
                    var students = _gradingService.DiscoverStudents(Configuration);

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        Students.Clear();
                        PaperNumbers.Clear();
                        PaperNumbers.Add("All");

                        foreach (var student in students)
                        {
                            Students.Add(student);
                        }

                        // Get unique paper numbers
                        var papers = students.Select(s => s.PaperNo).Distinct().OrderBy(p => int.Parse(p));
                        foreach (var paper in papers)
                        {
                            PaperNumbers.Add(paper);
                        }

                        SessionState.TotalStudents = students.Count;
                        SessionState.NotRunCount = students.Count;
                    });
                });

                _logger.LogInfo($"Loaded {Students.Count} students");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load students", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task StartGradingAsync(bool selectedOnly)
        {
            if (string.IsNullOrEmpty(Configuration.TestKitFolderPath))
            {
                _logger.LogWarning("Please select a test kit folder first");
                return;
            }

            var studentsToGrade = selectedOnly
                ? Students.Where(s => s.IsSelected).ToList()
                : Students.ToList();

            if (studentsToGrade.Count == 0)
            {
                _logger.LogWarning("No students to grade");
                return;
            }

            // Filter by paper if selected
            if (SelectedPaperFilter != "All")
            {
                studentsToGrade = studentsToGrade.Where(s => s.PaperNo == SelectedPaperFilter).ToList();
            }

            _logger.LogInfo($"Starting grading for {studentsToGrade.Count} students");
            await _gradingService.StartGradingAsync(studentsToGrade, Configuration, SessionState);
        }

        private void PauseGrading()
        {
            _gradingService.PauseGrading(SessionState);
        }

        private void ResumeGrading()
        {
            _gradingService.ResumeGrading(SessionState);
        }

        private void ResetAll()
        {
            _gradingService.ResetAllStatuses(Students.ToList(), SessionState);
        }

        private void ResetSelected()
        {
            var selected = Students.Where(s => s.IsSelected).ToList();
            foreach (var student in selected)
            {
                _gradingService.DisposeStudent(student);
            }
        }

        private void ApplyFilter()
        {
            // Filter is applied through data binding - the view should filter based on SelectedPaperFilter
            OnPropertyChanged(nameof(Students));
        }

        private bool CanStartGrading() => !SessionState.IsRunning && Students.Count > 0;
        private bool HasSelectedStudents() => Students.Any(s => s.IsSelected);

        // Event handlers
        private void OnStudentGradingStarted(object? sender, StudentSolution student)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var existingStudent = Students.FirstOrDefault(s => s.StudentCode == student.StudentCode);
                if (existingStudent != null)
                {
                    existingStudent.Status = student.Status;
                    existingStudent.StartTime = student.StartTime;
                }
            });
        }

        private void OnStudentGradingCompleted(object? sender, StudentSolution student)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var existingStudent = Students.FirstOrDefault(s => s.StudentCode == student.StudentCode);
                if (existingStudent != null)
                {
                    existingStudent.Status = student.Status;
                    existingStudent.Mark = student.Mark;
                    existingStudent.EndTime = student.EndTime;
                    existingStudent.StatusMessage = student.StatusMessage;
                    existingStudent.ProgressPercent = student.ProgressPercent;
                }
            });
        }

        private void OnStudentProgressUpdated(object? sender, StudentSolution student)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var existingStudent = Students.FirstOrDefault(s => s.StudentCode == student.StudentCode);
                if (existingStudent != null)
                {
                    existingStudent.ProgressPercent = student.ProgressPercent;
                }
            });
        }

        private void OnSessionStateChanged(object? sender, GradingSessionState state)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(SessionState));
            });
        }

        private void OnLogAdded(object? sender, LogEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var logLine = $"[{e.Timestamp:HH:mm:ss}] [{e.Level}] {e.Message}\n";
                LogOutput += logLine;
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Simple relay command implementation.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
    }
}
