using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SolutionGrader.UI.Models;
using SolutionGrader.UI.Services;

namespace SolutionGrader.UI
{
    /// <summary>
    /// Grading window that handles the actual grading operations.
    /// 
    /// This window is displayed after configuration is complete in SetupWindow.
    /// It provides:
    /// - Student list display with filtering by paper number
    /// - Start/Pause/Resume/Reset grading controls
    /// - Real-time progress tracking
    /// - Grading logs display
    /// 
    /// Grading results are written to the save folder in the SampleLogging format:
    /// - StudentsSolution.xlsx: Overall summary
    /// - student/{StudentCode}/OverallSummary.xlsx: Per-student summary
    /// - student/{StudentCode}/{TC}/GradeDetail.xlsx: Per-test-case details
    /// - student/{StudentCode}/{TC}/{TC}_Result.xlsx: Raw test results
    /// </summary>
    public partial class GradingWindow : Window
    {
        private readonly GradingConfiguration _configuration;
        private readonly LoggingService _logger;
        private readonly StudentDiscoveryService _studentDiscovery;
        private readonly TestKitDiscoveryService _testKitDiscovery;
        private readonly TestKitConfigService _testKitConfigService;
        private readonly GradingOrchestrationService _gradingService;
        private readonly ResultWriterService _resultWriter;
        
        private readonly ObservableCollection<StudentSolution> _students = new ObservableCollection<StudentSolution>();
        private readonly ObservableCollection<StudentSolution> _filteredStudents = new ObservableCollection<StudentSolution>();
        private readonly StringBuilder _logBuffer = new StringBuilder();
        
        private CancellationTokenSource? _cancellationTokenSource;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _sessionStartTime;
        private bool _isPaused;
        private bool _isRunning;

        public GradingWindow(GradingConfiguration configuration)
        {
            InitializeComponent();
            _configuration = configuration;
            
            // Initialize services
            _logger = new LoggingService(_configuration.SaveResultFolderPath);
            _studentDiscovery = new StudentDiscoveryService(_logger);
            _testKitDiscovery = new TestKitDiscoveryService(_logger);
            _testKitConfigService = new TestKitConfigService(_logger);
            _gradingService = new GradingOrchestrationService(_logger);
            _resultWriter = new ResultWriterService(_logger, _configuration.SaveResultFolderPath);
            
            // Wire up events
            _logger.LogAdded += Logger_LogAdded;
            _gradingService.StudentGradingStarted += GradingService_StudentGradingStarted;
            _gradingService.StudentGradingCompleted += GradingService_StudentGradingCompleted;
            _gradingService.StudentProgressUpdated += GradingService_StudentProgressUpdated;
            _gradingService.SessionStateChanged += GradingService_SessionStateChanged;
            
            // Bind data
            dgStudents.ItemsSource = _filteredStudents;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Display configuration info
            txtConfigInfo.Text = $"Submit: {_configuration.SubmitFolderPath} | TestKit: {_configuration.TestKitFolderPath} | Save: {_configuration.SaveResultFolderPath}";
            
            // Initialize batch grading configuration controls with default values
            txtMaxParallelStudents.Text = _configuration.MaxParallelStudents.ToString();
            txtStartIndex.Text = _configuration.StartIndex.ToString();
            txtEndIndex.Text = _configuration.EndIndex.ToString();
            
            // Load students
            LoadStudents();
            
            // Setup elapsed timer
            _elapsedTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _elapsedTimer.Tick += ElapsedTimer_Tick;
            
            _logger.LogInfo("Grading window initialized");
            _logger.LogInfo($"Batch grading configuration: Number of Solutions={_configuration.MaxParallelStudents}, StartIndex={_configuration.StartIndex}, EndIndex={_configuration.EndIndex}");
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isRunning)
            {
                var result = System.Windows.MessageBox.Show(
                    "Grading is still in progress. Are you sure you want to close?",
                    "Confirm Close",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            
            _elapsedTimer?.Stop();
            _logger.Dispose();
        }

        private void LoadStudents()
        {
            _logger.LogInfo("Loading students...");
            
            try
            {
                var students = _studentDiscovery.DiscoverStudents(_configuration.SubmitFolderPath, _configuration);
                
                _students.Clear();
                _filteredStudents.Clear();
                cmbPaperSelection.Items.Clear();
                
                // First item is instruction/placeholder
                cmbPaperSelection.Items.Add("-- Select Paper --");
                cmbPaperSelection.SelectedIndex = 0;
                
                // Get unique paper numbers
                var paperNumbers = students.Select(s => s.PaperNo).Distinct().OrderBy(p => int.TryParse(p, out var n) ? n : 0);
                foreach (var paper in paperNumbers)
                {
                    cmbPaperSelection.Items.Add($"Paper {paper}");
                }
                
                // Load test kit configs for each paper to get max marks
                foreach (var student in students)
                {
                    // Get test kit config for this paper to set max mark
                    var testKitPath = _testKitDiscovery.GetTestKitForPaper(_configuration.TestKitFolderPath, student.PaperNo);
                    if (!string.IsNullOrEmpty(testKitPath))
                    {
                        var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
                        if (testKitConfig != null)
                        {
                            student.MaxMark = testKitConfig.TotalMaxMark;
                            
                            // Also update configuration with port settings from first test kit
                            _configuration.CodeContainerInternalPort = testKitConfig.CodeContainerInternalPort;
                            _configuration.CodeContainerHostPort = testKitConfig.CodeContainerHostPort;
                        }
                    }
                    else
                    {
                        // No test kit for this paper - log only for this paper's students
                        student.StatusMessage = $"No test kit for paper {student.PaperNo}";
                    }
                    
                    _students.Add(student);
                    _filteredStudents.Add(student);  // Show all students initially
                }
                
                UpdateStatusBar();
                
                _logger.LogInfo($"Loaded {students.Count} students");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load students", ex);
                System.Windows.MessageBox.Show($"Failed to load students: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// When a paper is selected from the dropdown, select all students with that paper number.
        /// This allows multi-selection of different groups by selecting papers multiple times.
        /// </summary>
        private void PaperSelection_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbPaperSelection.SelectedIndex <= 0) return; // Skip placeholder
            
            var selectedItem = cmbPaperSelection.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedItem) || !selectedItem.StartsWith("Paper ")) return;
            
            var paperNo = selectedItem.Replace("Paper ", "");
            
            // Select all students with this paper number
            foreach (var student in _students.Where(s => s.PaperNo == paperNo))
            {
                student.IsSelected = true;
            }
            
            dgStudents.Items.Refresh();
            _logger.LogInfo($"Selected all students with Paper {paperNo}");
            
            // Reset dropdown to placeholder to allow re-selection
            cmbPaperSelection.SelectedIndex = 0;
        }

        /// <summary>
        /// Select all visible students
        /// </summary>
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var student in _filteredStudents)
            {
                student.IsSelected = true;
            }
            dgStudents.Items.Refresh();
            _logger.LogInfo("Selected all students");
        }

        /// <summary>
        /// Unselect all students
        /// </summary>
        private void UnselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var student in _students)
            {
                student.IsSelected = false;
            }
            dgStudents.Items.Refresh();
            _logger.LogInfo("Unselected all students");
        }

        /// <summary>
        /// Apply index range filtering to the student list.
        /// Allows grading from startIndex to endIndex (inclusive).
        /// If endIndex is -1, grade from startIndex to the end.
        /// </summary>
        /// <param name="students">List of students to filter</param>
        /// <param name="startIndex">Start index (0-based, inclusive)</param>
        /// <param name="endIndex">End index (0-based, inclusive, or -1 for all)</param>
        /// <returns>Filtered list of students</returns>
        private List<StudentSolution> ApplyIndexRange(List<StudentSolution> students, int startIndex, int endIndex)
        {
            if (startIndex < 0) startIndex = 0;
            if (startIndex >= students.Count) return new List<StudentSolution>();
            
            if (endIndex == -1 || endIndex >= students.Count)
            {
                // Grade from startIndex to end
                return students.Skip(startIndex).ToList();
            }
            else
            {
                // Grade from startIndex to endIndex (inclusive)
                var count = endIndex - startIndex + 1;
                if (count <= 0) return new List<StudentSolution>();
                return students.Skip(startIndex).Take(count).ToList();
            }
        }

        private async void StartAll_Click(object sender, RoutedEventArgs e)
        {
            await StartGradingAsync(false);
        }

        private async void StartSelected_Click(object sender, RoutedEventArgs e)
        {
            await StartGradingAsync(true);
        }

        private async Task StartGradingAsync(bool selectedOnly)
        {
            if (_isRunning && !_isPaused)
            {
                System.Windows.MessageBox.Show("Grading is already in progress.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Read and update configuration from UI
            if (int.TryParse(txtMaxParallelStudents.Text.Trim(), out int maxParallel))
            {
                _configuration.MaxParallelStudents = Math.Max(1, maxParallel);
            }
            if (int.TryParse(txtStartIndex.Text.Trim(), out int startIndex))
            {
                _configuration.StartIndex = Math.Max(0, startIndex);
            }
            if (int.TryParse(txtEndIndex.Text.Trim(), out int endIndex))
            {
                _configuration.EndIndex = endIndex;
            }
            
            // Get all students to grade (either selected or all with Not_Run/Paused status)
            var allStudentsToGrade = selectedOnly
                ? _filteredStudents.Where(s => s.IsSelected && s.Status != GradingStatus.Success).ToList()
                : _filteredStudents.Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused).ToList();
            
            // Apply index range filtering
            var studentsToGrade = ApplyIndexRange(allStudentsToGrade, _configuration.StartIndex, _configuration.EndIndex);
            
            if (studentsToGrade.Count == 0)
            {
                System.Windows.MessageBox.Show("No students to grade in the specified range.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _sessionStartTime = DateTime.Now;
            _elapsedTimer?.Start();
            
            UpdateButtonStates();
            _logger.LogInfo($"Starting grading for {studentsToGrade.Count} students (from index {_configuration.StartIndex} to {(_configuration.EndIndex == -1 ? "end" : _configuration.EndIndex.ToString())})");
            _logger.LogInfo($"Batch grading mode: {_configuration.MaxParallelStudents} solution(s) will be graded simultaneously per batch");
            
            if (_configuration.MaxParallelStudents > 1)
            {
                var totalBatches = (int)Math.Ceiling((double)studentsToGrade.Count / _configuration.MaxParallelStudents);
                _logger.LogInfo($"Total batches: {totalBatches} (e.g., first batch: {Math.Min(_configuration.MaxParallelStudents, studentsToGrade.Count)} students together, etc.)");
            }
            
            try
            {
                if (_configuration.MaxParallelStudents <= 1)
                {
                    // Sequential grading (original behavior)
                    foreach (var student in studentsToGrade)
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            break;
                        
                        // Wait while paused
                        while (_isPaused && !_cancellationTokenSource.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500);
                        }
                        
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            break;
                        
                        await GradeStudentAsync(student, 0, _cancellationTokenSource.Token);
                        
                        // Write results after each student
                        _resultWriter.WriteStudentsSolutionSummary(_students.ToList());
                        
                        UpdateStatusBar();
                    }
                }
                else
                {
                    // Parallel grading using SemaphoreSlim to limit concurrency
                    // Each student gets their own port offset based on their position in the batch
                    var resultLock = new object();
                    var semaphore = new SemaphoreSlim(_configuration.MaxParallelStudents);
                    
                    var tasks = studentsToGrade.Select(async (student, index) =>
                    {
                        await semaphore.WaitAsync(_cancellationTokenSource.Token);
                        try
                        {
                            // Wait while paused
                            while (_isPaused && !_cancellationTokenSource.Token.IsCancellationRequested)
                            {
                                await Task.Delay(500, _cancellationTokenSource.Token);
                            }
                            
                            if (_cancellationTokenSource.Token.IsCancellationRequested)
                                return;
                            
                            // Calculate port offset for this student to ensure unique ports in parallel execution
                            // Port offset is based on position within the parallel batch
                            var portOffset = index % _configuration.MaxParallelStudents;
                            
                            await GradeStudentAsync(student, portOffset, _cancellationTokenSource.Token);
                            
                            // Write results after each student (with lock for thread safety)
                            lock (resultLock)
                            {
                                _resultWriter.WriteStudentsSolutionSummary(_students.ToList());
                            }
                            
                            UpdateStatusBar();
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }).ToList();
                    
                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grading cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError("Grading error", ex);
            }
            finally
            {
                _isRunning = false;
                _elapsedTimer?.Stop();
                UpdateButtonStates();
                
                // Dispose all Docker containers (including database) when grading session ends
                // Only dispose if not paused (paused sessions may resume)
                if (!_isPaused)
                {
                    _gradingService.DisposeAllContainers(_configuration);
                }
                
                _logger.LogInfo("Grading session completed");
            }
        }

        private async Task GradeStudentAsync(StudentSolution student, int portOffset, CancellationToken ct)
        {
            // Set logging context with paper number for organized logging (paper/Log_StudentCode_Date)
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            try
            {
                // NOTE: Do NOT change status to InProgress here - let the orchestration service handle it
                // This was causing the filter in StartGradingAsync to skip students
                student.StartTime = DateTime.Now;
                student.ProgressPercent = 0;
                UpdateStudentInUI(student);
                runCurrentStudent.Text = student.StudentCode;
                
                _logger.LogInfo($"Starting grading for {student.StudentCode} (Paper {student.PaperNo})");
                
                // Check if test kit exists for this paper
                var testKitPath = _testKitDiscovery.GetTestKitForPaper(_configuration.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    student.Status = GradingStatus.Not_Run;
                    student.StatusMessage = $"No test kit for paper {student.PaperNo}";
                    student.EndTime = DateTime.Now;
                    _logger.LogWarning(student.StatusMessage);
                    UpdateStudentInUI(student);
                    return;
                }
                
                // Load test kit config
                var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
                if (testKitConfig == null)
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = "Failed to load test kit configuration";
                    student.EndTime = DateTime.Now;
                    _logger.LogError(student.StatusMessage);
                    UpdateStudentInUI(student);
                    return;
                }
                
                student.MaxMark = testKitConfig.TotalMaxMark;
                student.ProgressPercent = 10;
                UpdateStudentInUI(student);
                
                // Update configuration with test kit port settings and apply port offset for parallel grading
                // Each parallel student gets incremented ports to avoid conflicts
                // Internal and external ports MUST match for network monitoring with npcap/libpcap
                _configuration.CodeContainerInternalPort = testKitConfig.CodeContainerInternalPort + portOffset;
                _configuration.CodeContainerHostPort = testKitConfig.CodeContainerHostPort + portOffset;
                _configuration.DatabaseImageName = testKitConfig.DatabaseImageName;
                _configuration.DatabaseContainerName = testKitConfig.DatabaseContainerName;
                _configuration.DatabaseContainerInternalPort = testKitConfig.DatabaseContainerInternalPort;
                _configuration.DatabaseContainerHostPort = testKitConfig.DatabaseContainerHostPort;
                _configuration.DatabaseUsername = testKitConfig.DatabaseUsername;
                _configuration.DatabasePassword = testKitConfig.DatabasePassword;
                
                _logger.LogInfo($"Using ports - Internal: {_configuration.CodeContainerInternalPort}, Host: {_configuration.CodeContainerHostPort} (base + offset {portOffset})");
                _logger.LogInfo($"Max mark from Header.xlsx: {testKitConfig.TotalMaxMark}");
                
                // Execute grading using the orchestration service - it handles status changes internally
                // Pass the cancellation token so pause can abort the current grading
                var sessionState = new GradingSessionState();
                await _gradingService.StartGradingAsync(
                    new System.Collections.Generic.List<StudentSolution> { student },
                    _configuration,
                    sessionState,
                    ct);
                
                // Update final status
                student.ProgressPercent = 100;
                student.EndTime = DateTime.Now;
                UpdateStudentInUI(student);
                
                _logger.LogInfo($"Grading completed for {student.StudentCode}. Mark: {student.Mark}/{student.MaxMark}");
            }
            catch (OperationCanceledException)
            {
                // Grading was paused/cancelled - set status to Paused so it can be resumed
                student.Status = GradingStatus.Paused;
                student.StatusMessage = "Grading paused - will resume when unpaused";
                student.EndTime = null; // Clear end time since not completed
                _logger.LogInfo($"Grading paused for {student.StudentCode}");
                UpdateStudentInUI(student);
            }
            catch (Exception ex)
            {
                student.Status = GradingStatus.Failed;
                student.StatusMessage = ex.Message;
                student.EndTime = DateTime.Now;
                _logger.LogError($"Grading failed for {student.StudentCode}", ex);
                UpdateStudentInUI(student);
            }
            finally
            {
                _logger.SetStudentContext(null);
            }
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning && !_isPaused)
            {
                _isPaused = true;
                // Cancel the current grading operation to abort the student being graded
                _cancellationTokenSource?.Cancel();
                _logger.LogInfo("Grading paused - current student will be aborted and can be resumed");
                UpdateButtonStates();
            }
        }

        private async void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (_isPaused)
            {
                _isPaused = false;
                _logger.LogInfo("Grading resumed");
                UpdateButtonStates();
                
                // Restart grading from paused students
                await StartGradingAsync(false);
            }
        }

        private void ResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                System.Windows.MessageBox.Show("Cannot reset while grading is in progress.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            foreach (var student in _students)
            {
                ResetStudent(student);
            }
            
            dgStudents.Items.Refresh();
            UpdateStatusBar();
            _logger.LogInfo("All statuses reset");
        }

        private void ResetSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                System.Windows.MessageBox.Show("Cannot reset while grading is in progress.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            foreach (var student in _filteredStudents.Where(s => s.IsSelected))
            {
                ResetStudent(student);
            }
            
            dgStudents.Items.Refresh();
            UpdateStatusBar();
            _logger.LogInfo("Selected statuses reset");
        }

        private void ResetStudent(StudentSolution student)
        {
            student.Status = GradingStatus.Not_Run;
            student.Mark = 0;
            student.StartTime = null;
            student.EndTime = null;
            student.StatusMessage = null;
            student.ProgressPercent = 0;
            
            // Delete associated result files if they exist - organized by paper
            // Try paper-organized path first
            var paperResultFolder = Path.Combine(_configuration.SaveResultFolderPath, student.PaperNo, "student", student.StudentCode);
            if (Directory.Exists(paperResultFolder))
            {
                try
                {
                    Directory.Delete(paperResultFolder, true);
                    _logger.LogInfo($"Deleted result folder for {student.StudentCode} (Paper {student.PaperNo})");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete result folder for {student.StudentCode}: {ex.Message}");
                }
            }

            // Also try legacy non-paper-organized path
            var legacyResultFolder = Path.Combine(_configuration.SaveResultFolderPath, "student", student.StudentCode);
            if (Directory.Exists(legacyResultFolder))
            {
                try
                {
                    Directory.Delete(legacyResultFolder, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete legacy result folder for {student.StudentCode}: {ex.Message}");
                }
            }
        }

        private void BackToSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                var result = System.Windows.MessageBox.Show(
                    "Grading is in progress. Going back will cancel it. Continue?",
                    "Confirm",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.No)
                    return;
                
                _cancellationTokenSource?.Cancel();
            }
            
            var setupWindow = new SetupWindow();
            setupWindow.Show();
            this.Close();
        }

        private void UpdateButtonStates()
        {
            Dispatcher.Invoke(() =>
            {
                btnStartAll.IsEnabled = !_isRunning || _isPaused;
                btnStartSelected.IsEnabled = !_isRunning || _isPaused;
                btnPause.IsEnabled = _isRunning && !_isPaused;
                btnResume.IsEnabled = _isPaused;
                btnResetAll.IsEnabled = !_isRunning;
                btnResetSelected.IsEnabled = !_isRunning;
            });
        }

        private void UpdateStatusBar()
        {
            Dispatcher.Invoke(() =>
            {
                var total = _students.Count;
                var graded = _students.Count(s => s.Status == GradingStatus.Success || s.Status == GradingStatus.Failed);
                var success = _students.Count(s => s.Status == GradingStatus.Success);
                var failed = _students.Count(s => s.Status == GradingStatus.Failed);
                var notRun = _students.Count(s => s.Status == GradingStatus.Not_Run);
                
                runTotal.Text = total.ToString();
                runGraded.Text = graded.ToString();
                runPercent.Text = total > 0 ? ((graded * 100) / total).ToString() : "0";
                runSuccess.Text = success.ToString();
                runFailed.Text = failed.ToString();
                runNotRun.Text = notRun.ToString();
            });
        }

        private void UpdateStudentInUI(StudentSolution student)
        {
            Dispatcher.Invoke(() =>
            {
                dgStudents.Items.Refresh();
                UpdateStatusBar();
            });
        }

        private void ElapsedTimer_Tick(object? sender, EventArgs e)
        {
            if (_sessionStartTime.HasValue)
            {
                var elapsed = DateTime.Now - _sessionStartTime.Value;
                runElapsed.Text = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
                    : elapsed.TotalMinutes >= 1
                        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                        : $"{elapsed.Seconds}s";
            }
        }

        #region Event Handlers

        private void Logger_LogAdded(object? sender, LogEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var logLine = $"[{e.Timestamp:HH:mm:ss}] [{e.Level}] {e.Message}\n";
                _logBuffer.Append(logLine);
                
                // Keep log buffer manageable
                if (_logBuffer.Length > 50000)
                {
                    var trimmed = _logBuffer.ToString().Substring(_logBuffer.Length - 40000);
                    _logBuffer.Clear();
                    _logBuffer.Append(trimmed);
                }
                
                txtLog.Text = _logBuffer.ToString();
                txtLog.ScrollToEnd();
            });
        }

        private void GradingService_StudentGradingStarted(object? sender, StudentSolution student)
        {
            UpdateStudentInUI(student);
        }

        private void GradingService_StudentGradingCompleted(object? sender, StudentSolution student)
        {
            UpdateStudentInUI(student);
        }

        private void GradingService_StudentProgressUpdated(object? sender, StudentSolution student)
        {
            UpdateStudentInUI(student);
        }

        private void GradingService_SessionStateChanged(object? sender, GradingSessionState state)
        {
            UpdateStatusBar();
        }

        #endregion
    }
}
