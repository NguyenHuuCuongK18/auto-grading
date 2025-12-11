using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Domain.Models;
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
    /// 
    /// PERFORMANCE OPTIMIZATIONS:
    /// - UI updates are batched via UIUpdateBatcher (250ms intervals) to prevent lag during parallel grading
    /// - Progress updates are throttled to 500ms per student to avoid excessive DataGrid refreshes
    /// - Log display uses smart auto-scroll that only activates when user is at bottom
    /// - All optimizations preserve 100% grading accuracy - only UI rendering is affected
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
        private readonly UIUpdateBatcher _uiUpdateBatcher;
        
        // Use single collection with CollectionViewSource for memory efficiency
        // With 150 students, this saves ~50% memory vs duplicate collections
        private readonly ObservableCollection<StudentSolution> _students = new ObservableCollection<StudentSolution>();
        private System.Windows.Data.CollectionViewSource? _studentsViewSource;
        
        // CRITICAL: Lock for thread-safe access to _students collection
        // ObservableCollection is NOT thread-safe. Multiple worker threads calling _students.ToList()
        // simultaneously can cause "non-concurrent collections" exception during batch grading.
        // This lock ensures only one thread can enumerate the collection at a time.
        private readonly object _studentsLock = new object();
        
        // Log file paths for display (logs written to files, not shown in UI for performance)
        private string? _systemLogPath;
        private string? _currentStudentLogPath;
        
        // Cache test kit configurations by paper number to avoid repeated Excel file reads
        // Only loaded during grading, NOT during discovery
        private readonly Dictionary<string, (string testKitPath, TestKitConfig config)> _testKitCache = new Dictionary<string, (string, TestKitConfig)>();
        
        // PORT ALLOCATION REMOVED: No longer needed
        // All students use the same Code_Container_Internal_Port from environment.xlsx
        // Docker containers are isolated, so there's no port conflict
        // Keeping field commented for reference
        // private PortAllocator? _sharedPortAllocator;
        
        // CRITICAL: Shared GradingMessageLogger for batch/parallel grading
        // Each grading session needs ONE shared GradingMessageLogger that all parallel students use
        // This prevents file access conflicts when multiple students try to write to the same log file
        // The logger creates one timestamped file per session, not per student
        private GradingMessageLogger? _sharedMessageLogger;
        
        private CancellationTokenSource? _cancellationTokenSource;
        private DateTime? _sessionStartTime;
        private bool _isPaused;
        private bool _isRunning;

        public GradingWindow(GradingConfiguration configuration)
        {
            InitializeComponent();
            _configuration = configuration;
            
            // Initialize UI update batcher for optimal performance
            // 250ms interval provides good balance between responsiveness and performance
            _uiUpdateBatcher = new UIUpdateBatcher(Dispatcher, batchIntervalMs: 250);
            
            // Initialize services
            _logger = new LoggingService(_configuration.SaveResultFolderPath);
            _studentDiscovery = new StudentDiscoveryService(_logger);
            _testKitDiscovery = new TestKitDiscoveryService(_logger);
            _testKitConfigService = new TestKitConfigService(_logger);
            _gradingService = new GradingOrchestrationService(_logger);
            _resultWriter = new ResultWriterService(_logger, _configuration.SaveResultFolderPath);
            
            // Wire up events - OPTIMIZED: Only essential events, no log display or progress updates
            _gradingService.StudentGradingStarted += GradingService_StudentGradingStarted;
            _gradingService.StudentGradingCompleted += GradingService_StudentGradingCompleted;
            _gradingService.SessionStateChanged += GradingService_SessionStateChanged;
            
            // Setup CollectionViewSource for memory-efficient filtering
            _studentsViewSource = new System.Windows.Data.CollectionViewSource();
            _studentsViewSource.Source = _students;
            
            // Bind data to filtered view
            dgStudents.ItemsSource = _studentsViewSource.View;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Display configuration info
            txtConfigInfo.Text = $"Submit: {_configuration.SubmitFolderPath} | TestKit: {_configuration.TestKitFolderPath} | Save: {_configuration.SaveResultFolderPath}";
            
            // Initialize batch grading configuration control with default value
            txtMaxParallelStudents.Text = _configuration.MaxParallelStudents.ToString();
            
            // Initialize DLL modification fallback checkbox
            chkUseDllModFallback.IsChecked = _configuration.UseDllModificationFallback;
            
            // Initialize index selection controls with 1-based defaults
            txtSelectStartIndex.Text = "1";
            txtSelectEndIndex.Text = "-1";
            
            // Load students
            LoadStudents();
            
            // Display log file paths
            var logsFolder = Path.Combine(_configuration.SaveResultFolderPath, "Logs");
            _systemLogPath = Path.Combine(logsFolder, $"System_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            txtSystemLogPath.Text = _systemLogPath;
            txtStudentLogPath.Text = $"{logsFolder}/Log_{{StudentCode}}_{{Date}}_Paper{{N}}/";
            
            _logger.LogInfo("Grading window initialized");
            _logger.LogInfo($"Batch grading configuration: Number of Solutions={_configuration.MaxParallelStudents}");
            _logger.LogInfo($"DLL modification fallback: {(_configuration.UseDllModificationFallback ? "Enabled" : "Disabled")}");
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
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
            
            // LEGACY: Clear shared network monitors (HOST-based monitoring)
            // With sidecar pattern, monitors are per-student Docker containers
            // and are cleaned up automatically
            // Keeping this commented for reference
            /*
            try
            {
                _logger.LogInfo("[Window Close] Clearing shared network monitors...");
                await SharedNetworkMonitorManager.Instance.ClearAllAsync();
                _logger.LogInfo("[Window Close] Shared network monitors cleared");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Window Close] Error clearing monitors: {ex.Message}");
            }
            */
            
            // Flush any pending UI updates before closing
            _uiUpdateBatcher?.Flush();
            _uiUpdateBatcher?.Dispose();
            
            _logger.Dispose();
        }

        private void LoadStudents()
        {
            _logger.LogInfo("Loading students...");
            
            try
            {
                var students = _studentDiscovery.DiscoverStudents(_configuration.SubmitFolderPath, _configuration);
                
                _students.Clear();
                _testKitCache.Clear(); // Clear cache when reloading students
                cmbPaperSelection.Items.Clear();
                cmbPaperSelection.Items.Add("-- Select Paper --");
                cmbPaperSelection.SelectedIndex = 0;
                
                // Get unique paper numbers
                var paperNumbers = students.Select(s => s.PaperNo).Distinct().OrderBy(p => int.TryParse(p, out var n) ? n : 0);
                foreach (var paper in paperNumbers) { cmbPaperSelection.Items.Add($"Paper {paper}"); }
                
                // FIX: Pre-load max marks for each paper to display in the Max column
                // This is efficient because we only load once per paper, not per student
                // Previously, MaxMark was only set during grading, so it showed 0 before grading started
                var paperMaxMarks = new Dictionary<string, double>();
                foreach (var paperNo in paperNumbers)
                {
                    var testKitPath = _testKitDiscovery.GetTestKitForPaper(_configuration.TestKitFolderPath, paperNo);
                    if (!string.IsNullOrEmpty(testKitPath))
                    {
                        var maxMark = _testKitDiscovery.GetTestKitMaxMark(testKitPath);
                        paperMaxMarks[paperNo] = maxMark;
                        _logger.LogInfo($"Paper {paperNo}: Max mark = {maxMark} (from {Path.GetFileName(testKitPath)})");
                    }
                    else
                    {
                        _logger.LogWarning($"Paper {paperNo}: No test kit found, max mark will be 0");
                    }
                }
                
                int idx = 1; // 1-based ids for clarity
                foreach (var student in students)
                {
                    // assign 1-based Id
                    student.Id = idx++;
                    
                    // Set MaxMark from the pre-loaded paper max marks
                    // This ensures the Max column shows the correct value before grading starts
                    if (paperMaxMarks.TryGetValue(student.PaperNo, out var maxMark))
                    {
                        student.MaxMark = maxMark;
                    }
                    else
                    {
                        // Explicitly set MaxMark to 0 when no test kit is found for this paper
                        // This makes it clear that the test kit is missing, rather than having an uninitialized value
                        student.MaxMark = 0;
                    }
                    
                    _students.Add(student);
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
            
            // FIXED: Ensure UI updates are marshalled to UI thread and force DataGrid refresh
            // Select all students with this paper number
            int selectedCount = 0;
            lock (_studentsLock)
            {
                foreach (var student in _students.Where(s => s.PaperNo == paperNo))
                {
                    student.IsSelected = true;
                    selectedCount++;
                }
            }
            
            // Force DataGrid to refresh its display to show updated checkbox states
            // Note: dgStudents.Items.Refresh() updates the visual tree immediately
            dgStudents.Items.Refresh();
            _logger.LogInfo($"Selected {selectedCount} students with Paper {paperNo}");
            
            // Reset dropdown to placeholder to allow re-selection
            cmbPaperSelection.SelectedIndex = 0;
        }

        /// <summary>
        /// Apply index range selection to select students.
        /// This is a quick way to select a range of students, similar to selecting by paper.
        /// Useful when you have many students and need to select a specific range.
        /// 
        /// IMPORTANT: This method properly handles UI updates by modifying IsSelected property
        /// which triggers INotifyPropertyChanged events, and then refreshes the DataGrid display.
        /// </summary>
        private void ApplyIndexSelection_Click(object sender, RoutedEventArgs e)
        {
            // Parse and validate indices
            if (!int.TryParse(txtSelectStartIndex.Text.Trim(), out int startIndex) || startIndex < 1)
            {
                System.Windows.MessageBox.Show("Start Index must be a positive integer (starts at 1).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (!int.TryParse(txtSelectEndIndex.Text.Trim(), out int endIndex) || endIndex < -1)
            {
                System.Windows.MessageBox.Show("End Index must be -1 (for all) or a positive integer.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (endIndex != -1 && endIndex < startIndex)
            {
                System.Windows.MessageBox.Show("End Index must be greater than or equal to Start Index.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // FIXED: Ensure all selection state changes are visible to the DataGrid
            // Step 1: Unselect all students (clears previous selections)
            int unselectedCount = 0;
            lock (_studentsLock)
            {
                foreach (var student in _students)
                {
                    if (student.IsSelected)
                    {
                        student.IsSelected = false;
                        unselectedCount++;
                    }
                }
            }
            
            // Step 2: Apply selection to the specified index range
            List<StudentSolution> studentsInRange;
            lock (_studentsLock)
            {
                studentsInRange = ApplyIndexRange(_students.ToList(), startIndex, endIndex);
            }
            int selectedCount = 0;
            foreach (var student in studentsInRange)
            {
                student.IsSelected = true;
                selectedCount++;
            }
            
            // Step 3: Force DataGrid to refresh and display the updated checkbox states
            // This is critical to ensure the UI reflects the programmatic selection changes
            dgStudents.Items.Refresh();
            
            // Log detailed selection information for debugging
            var endText = endIndex == -1 ? "end" : endIndex.ToString();
            _logger.LogInfo($"Index selection applied: range {startIndex} to {endText}");
            _logger.LogInfo($"Selection result: {selectedCount} students selected, {unselectedCount} unselected");
            
            // Provide visual feedback to user
            System.Windows.MessageBox.Show(
                $"Selected {selectedCount} student(s) from index {startIndex} to {endText}.\n\nYou can now click 'Start Selected' to grade these students.",
                "Selection Applied",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Apply index range to get a subset of students.
        /// This is used for SELECTION purposes only.
        /// </summary>
        /// <param name="students">List of students to filter</param>
        /// <param name="startIndex">Start index (0-based, inclusive)</param>
        /// <param name="endIndex">End index (0-based, inclusive, or -1 for all)</param>
        /// <returns>Filtered list of students</returns>
        private List<StudentSolution> ApplyIndexRange(List<StudentSolution> students, int startIndex, int endIndex)
        {
            // 1-based indices: filter by Id property
            var query = students.Where(s => s.Id >= startIndex);
            if (endIndex != -1) query = query.Where(s => s.Id <= endIndex);
            return query.ToList();
        }

        /// <summary>
        /// Select all visible students
        /// </summary>
        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            lock (_studentsLock)
            {
                foreach (var student in _students)
                {
                    student.IsSelected = true;
                }
            }
            dgStudents.Items.Refresh();
            _logger.LogInfo("Selected all students");
        }

        /// <summary>
        /// Unselect all students
        /// </summary>
        private void UnselectAll_Click(object sender, RoutedEventArgs e)
        {
            lock (_studentsLock)
            {
                foreach (var student in _students)
                {
                    student.IsSelected = false;
                }
            }
            dgStudents.Items.Refresh();
            _logger.LogInfo("Unselected all students");
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
            
            // Read DLL modification fallback setting from UI
            _configuration.UseDllModificationFallback = chkUseDllModFallback.IsChecked == true;
            
            _logger.LogInfo($"=== Starting Grading Session ===");
            _logger.LogInfo($"Mode: {(selectedOnly ? "Selected Only" : "All Students")}");
            _logger.LogInfo($"DLL Modification Fallback: {(_configuration.UseDllModificationFallback ? "ENABLED" : "DISABLED")} (Checkbox: {(chkUseDllModFallback.IsChecked == true ? "Checked" : "Unchecked")})");
            _logger.LogInfo($"Total students loaded: {_students.Count}");
            _logger.LogInfo($"Students in filtered view: {_students.Count}");
            
            // CRITICAL FIX: Consistent status filtering for both "Start All" and "Start Selected"
            // Both modes should exclude ONLY students that have already been successfully graded.
            // This allows re-running failed students, which is essential for batch grading workflows.
            //
            // Previous buggy logic for "Start All":
            //   .Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused)
            // This excluded students with Status == Failed, InProgress, or Disposed, preventing re-grading.
            //
            // New consistent logic:
            //   Both modes: Exclude only Status == Success
            //   Include: Not_Run, Failed, InProgress, Paused, Disposed
            //
            // This matches user expectations:
            // - "Start All" = Grade all students that haven't succeeded yet
            // - "Start Selected" = Grade selected students that haven't succeeded yet
            
            if (selectedOnly)
            {
                List<StudentSolution> selectedStudents;
                lock (_studentsLock)
                {
                    selectedStudents = _students.Where(s => s.IsSelected).ToList();
                }
                var notSuccessStudents = selectedStudents.Where(s => s.Status != GradingStatus.Success).ToList();
                _logger.LogInfo($"Students with IsSelected=true: {selectedStudents.Count}");
                _logger.LogInfo($"Students with IsSelected=true AND Status!=Success: {notSuccessStudents.Count}");
                
                // Log detailed info about selected students for debugging
                foreach (var s in selectedStudents)
                {
                    _logger.LogInfo($"  - Student {s.Id}: {s.StudentCode}, IsSelected={s.IsSelected}, Status={s.Status}");
                }
            }
            else
            {
                // Log status distribution for "Start All" mode
                List<IGrouping<GradingStatus, StudentSolution>> statusGroups;
                lock (_studentsLock)
                {
                    statusGroups = _students.GroupBy(s => s.Status).OrderBy(g => g.Key).ToList();
                }
                _logger.LogInfo("Status distribution of all students:");
                foreach (var group in statusGroups)
                {
                    _logger.LogInfo($"  - {group.Key}: {group.Count()} student(s)");
                }
            }
            
            // Get students to grade based on selection
            // FIXED: Both modes now use the same filtering logic - exclude only Success status
            // CRITICAL: Lock collection access for thread safety
            List<StudentSolution> studentsToGrade;
            lock (_studentsLock)
            {
                studentsToGrade = selectedOnly
                    ? _students.Where(s => s.IsSelected && s.Status != GradingStatus.Success).ToList()
                    : _students.Where(s => s.Status != GradingStatus.Success).ToList();
            }
            
            _logger.LogInfo($"Students to grade after filtering: {studentsToGrade.Count}");
            
            // Log which statuses are included in this grading session for verification
            if (studentsToGrade.Count > 0)
            {
                var includedStatuses = studentsToGrade.GroupBy(s => s.Status).OrderBy(g => g.Key);
                _logger.LogInfo("Students to be graded by status:");
                foreach (var group in includedStatuses)
                {
                    _logger.LogInfo($"  - {group.Key}: {group.Count()} student(s)");
                }
            }
            
            if (studentsToGrade.Count == 0)
            {
                var message = selectedOnly 
                    ? "No students to grade.\n\nPossible reasons:\n- No students are selected (check the 'Select' checkboxes)\n- All selected students have already been successfully graded\n\nTip: Use 'Apply' button after entering index range to select students."
                    : "No students to grade.\n\nAll students have already been successfully graded.\n\nNote: Only students with Status != Success are re-graded.\nUse 'Reset' to clear student status if you want to re-grade successful students.";
                    
                _logger.LogWarning(message);
                System.Windows.MessageBox.Show(message, "No Students to Grade", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // CRITICAL: Clear all previously allocated ports at the START of a new grading session.
            // This prevents port exhaustion from previous runs while ensuring no port reuse
            // DURING the current session (which could cause race conditions in parallel grading).
            // PORT ALLOCATION REMOVED: No longer needed
            // All students use the same Code_Container_Internal_Port from environment.xlsx
            // Docker containers are isolated, so there's no port conflict
            // Keeping ClearAllAllocatedPorts for backward compatibility
            _logger.LogInfo("[UI] Clearing port allocation from previous sessions (backward compatibility)...");
            PortAllocator.ClearAllAllocatedPorts();
            
            // PORT ALLOCATION REMOVED: No longer initialize PortAllocator
            // All students will use the same Code_Container_Internal_Port from test kit environment.xlsx
            // Docker containers are isolated, so there's no port conflict between students
            // This simplifies configuration and matches CLI behavior
            _logger.LogInfo("[Port Config] Port allocation removed - using Code_Container_Internal_Port from environment.xlsx");
            _logger.LogInfo("[Port Config] All students use same internal port (Docker container isolation prevents conflicts)");
            
            // CRITICAL: Initialize shared GradingMessageLogger for THIS grading session
            // All parallel students will use this SAME GradingMessageLogger instance
            // to ensure thread-safe logging without file access conflicts
            // The logger creates ONE log file per session with a unique timestamp
            try
            {
                var resultPath = _configuration.GetEffectiveResultPath();
                if (string.IsNullOrEmpty(resultPath))
                {
                    _logger.LogWarning("[Message Logger] Result path is not configured, message logging will be disabled");
                    _sharedMessageLogger = null;
                }
                else
                {
                    _sharedMessageLogger = new GradingMessageLogger(resultPath);
                    _logger.LogInfo($"[Message Logger] Initialized SHARED GradingMessageLogger for batch grading session");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Message Logger] Failed to initialize GradingMessageLogger: {ex.Message}. Message logging will be disabled.", ex);
                _sharedMessageLogger = null;
            }
            
            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _sessionStartTime = DateTime.Now;
            
            UpdateButtonStates();
            _logger.LogInfo($"Starting grading for {studentsToGrade.Count} {(selectedOnly ? "selected" : "")} students");
            
            // Calculate actual parallelism (limited by both batch size and student count)
            var actualParallel = Math.Min(_configuration.MaxParallelStudents, studentsToGrade.Count);
            
            if (_configuration.MaxParallelStudents <= 1)
            {
                _logger.LogInfo($"Sequential grading mode: 1 solution will be graded at a time");
            }
            else if (actualParallel == studentsToGrade.Count)
            {
                _logger.LogInfo($"Parallel grading mode: All {studentsToGrade.Count} solutions will be graded simultaneously (batch size {_configuration.MaxParallelStudents} >= student count)");
            }
            else
            {
                _logger.LogInfo($"Batch grading mode: {actualParallel} solution(s) will be graded simultaneously per batch");
                var totalBatches = (int)Math.Ceiling((double)studentsToGrade.Count / _configuration.MaxParallelStudents);
                _logger.LogInfo($"Total batches: {totalBatches} (e.g., first batch: {actualParallel} students together, etc.)");
            }
            
            _logger.LogInfo($"Port configuration: All students use the same Code_Container_Internal_Port from environment.xlsx (no allocation needed).");
            
            // OPTIMIZATION: Pre-allocate shared network monitor for all students in batch
            // This dramatically reduces resource usage (97% reduction in monitor instances)
            // Per user request: Use singular network monitor with port range pre-allocation
            // CRITICAL FIX: Clear shared network monitors from previous grading session
            // This is essential when rerunning tests in the UI, as the SharedNetworkMonitorManager
            // is a singleton that persists across sessions. Without this cleanup, stale monitors
            // LEGACY: Clear shared network monitors from previous session
            // With sidecar pattern, each grading session creates fresh Docker container monitors
            // Keeping this commented for reference
            /*
            try
            {
                _logger.LogInfo("[Shared Network Monitor] Clearing monitors from previous session...");
                await SharedNetworkMonitorManager.Instance.ClearAllAsync();
                _logger.LogInfo("[Shared Network Monitor] Previous session monitors cleared");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Shared Network Monitor] Error clearing previous monitors: {ex.Message}");
            }
            */
            
            try
            {
                // PORT ALLOCATION REMOVED: Reference to _sharedPortAllocator removed
                // No longer needed since all students use Code_Container_Internal_Port from environment.xlsx
                var firstStudent = studentsToGrade.FirstOrDefault();
                if (firstStudent != null)
                {
                    var firstTestKitPath = _testKitDiscovery.GetTestKitForPaper(_configuration.TestKitFolderPath, firstStudent.PaperNo);
                    if (!string.IsNullOrEmpty(firstTestKitPath))
                    {
                        // Read the internal port that all students will use (no allocation)
                        int internalPort = ReadStartingPortFromEnvironmentXlsx(firstTestKitPath);
                        if (internalPort <= 0) internalPort = 8000;
                        
                        _logger.LogInfo($"[Port Config] All students will use Code_Container_Internal_Port={internalPort} from environment.xlsx");
                        _logger.LogInfo($"[Port Config] No port allocation needed - Docker container isolation prevents conflicts");
                        
                        // LEGACY: SharedNetworkMonitorManager for HOST-based monitoring (libpcap/NPcap)
                        // With sidecar pattern, each student gets a Docker container network monitor
                        // attached via --net=container, so this pre-allocation is no longer needed
                        // Keeping this commented for reference
                        /*
                        SharedNetworkMonitorManager.Instance.PreAllocateForBatch(startingPort, studentsToGrade.Count);
                        _logger.LogInfo($"[Shared Network Monitor] Pre-allocated for {studentsToGrade.Count} students starting from port {startingPort}");
                        _logger.LogInfo($"[Shared Network Monitor] Single monitor instance will handle all students (97% resource reduction)");
                        
                        var stats = SharedNetworkMonitorManager.Instance.GetStatistics();
                        _logger.LogInfo($"[Shared Network Monitor] Statistics: {stats}");
                        */
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Shared Network Monitor] Failed to pre-allocate: {ex.Message}");
                _logger.LogWarning($"[Shared Network Monitor] Will create monitors on-demand if needed");
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
                        
                        await GradeStudentAsync(student, _cancellationTokenSource.Token);
                        
                        // Write results after each student
                        // CRITICAL: Lock access to _students collection for thread safety
                        List<StudentSolution> studentsSnapshot;
                        lock (_studentsLock)
                        {
                            studentsSnapshot = _students.ToList();
                        }
                        _resultWriter.WriteStudentsSolutionSummary(studentsSnapshot);
                        
                        UpdateStatusBar();
                    }
                }
                else
                {
                    // OPTIMIZED: TRUE CONTINUOUS BATCH PROCESSING using producer-consumer pattern
                    // OLD behavior: Batch size 10 = grade 10, wait for all 10 to finish, then start next 10
                    // NEW behavior: Batch size 10 = always keep 10 students being graded at a time
                    //   - When Student 1 finishes, immediately start Student 11 (no waiting)
                    //   - When Student 2 finishes, immediately start Student 12 (no waiting)
                    //   - Maximum resource utilization, no idle containers
                    //
                    // This is achieved using:
                    // - Channel<StudentSolution> as a queue of students waiting to be graded
                    // - Multiple worker tasks (up to MaxParallelStudents) continuously pulling from the queue
                    // - As soon as a worker finishes a student, it immediately pulls the next one
                    // - No batching delays, no Task.WhenAll() blocking
                    
                    _logger.LogInfo($"[Optimization] Using continuous batch processing: {_configuration.MaxParallelStudents} students graded simultaneously at all times");
                    _logger.LogInfo($"[Optimization] When a student finishes, the next student starts immediately (no batch waiting)");
                    _logger.LogInfo($"[Multi-Threading] Using {_configuration.MaxParallelStudents} worker threads across {Environment.ProcessorCount} CPU cores");
                    _logger.LogInfo($"[Parallel Grading] All {_configuration.MaxParallelStudents} students will create containers simultaneously (true parallel execution)");
                    
                    // Create a channel to hold students waiting to be graded
                    // OPTIMIZATION: Set capacity to 2x MaxParallelStudents to reduce producer-consumer coordination overhead
                    // This allows producer to queue work ahead while workers are busy, reducing wait times
                    var channelCapacity = Math.Max(_configuration.MaxParallelStudents * 2, 10);
                    var channel = Channel.CreateBounded<StudentSolution>(new BoundedChannelOptions(channelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait
                    });
                    
                    // Track progress
                    var completedCount = 0;
                    var completedLock = new object();
                    
                    // Producer task: Feed students into the channel
                    var producerTask = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInfo($"[Producer] Starting to feed {studentsToGrade.Count} students into channel");
                            int queuedCount = 0;
                            
                            foreach (var student in studentsToGrade)
                            {
                                // Check for cancellation before adding to channel
                                if (_cancellationTokenSource.Token.IsCancellationRequested)
                                {
                                    _logger.LogInfo($"[Producer] Cancellation requested after queuing {queuedCount}/{studentsToGrade.Count} students");
                                    break;
                                }
                                
                                _logger.LogDebug($"[Producer] Queuing student {queuedCount + 1}/{studentsToGrade.Count}: {student.StudentCode}");
                                await channel.Writer.WriteAsync(student, _cancellationTokenSource.Token);
                                queuedCount++;
                            }
                            
                            _logger.LogInfo($"[Producer] Finished queuing {queuedCount}/{studentsToGrade.Count} students");
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInfo("[Producer] Cancelled while queuing students");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("[Producer] Unexpected error while queuing students", ex);
                        }
                        finally
                        {
                            // CRITICAL: Always complete the channel writer, even if there was an exception
                            // This ensures workers don't wait forever for more students
                            channel.Writer.Complete();
                            _logger.LogInfo("[Producer] Channel writer marked as complete");
                        }
                    }, _cancellationTokenSource.Token);
                    
                    // Consumer tasks: Pull students from channel and grade them
                    var workerTasks = new List<Task>();
                    for (int workerId = 0; workerId < _configuration.MaxParallelStudents; workerId++)
                    {
                        var localWorkerId = workerId;
                        var workerTask = Task.Run(async () =>
                        {
                            _logger.LogInfo($"[Worker-{localWorkerId}] Started and ready to process students");
                            int studentsProcessed = 0;
                            
                            // Each worker continuously pulls students from the queue until empty
                            try
                            {
                                await foreach (var student in channel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
                                {
                                    try
                                    {
                                        // Wait while paused - pass cancellation token for responsive shutdown
                                        while (_isPaused && !_cancellationTokenSource.Token.IsCancellationRequested)
                                        {
                                            await Task.Delay(500, _cancellationTokenSource.Token);
                                        }
                                        
                                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                                        {
                                            _logger.LogInfo($"[Worker-{localWorkerId}] Cancellation requested, stopping after {studentsProcessed} students");
                                            break;
                                        }
                                        
                                        int currentStartIndex;
                                        lock (completedLock)
                                        {
                                            // Index for the student we're about to start (completedCount + 1)
                                            currentStartIndex = completedCount + 1;
                                        }
                                        
                                        _logger.LogInfo($"[Worker-{localWorkerId}] [{currentStartIndex}/{studentsToGrade.Count}] Starting grading for: {student.StudentCode} (Paper {student.PaperNo})");
                                        
                                        // Grade the student (port allocation happens inside via PortAllocator)
                                        // TRUE PARALLEL: No lock - multiple students can create containers simultaneously
                                        await GradeStudentAsync(student, _cancellationTokenSource.Token);
                                        
                                        // Write results after each student
                                        // OPTIMIZATION: Deferred write mechanism batches updates and runs on background thread
                                        // CRITICAL: Lock access to _students collection for thread safety
                                        // Multiple workers calling ToList() simultaneously can cause collection corruption
                                        List<StudentSolution> studentsSnapshot;
                                        lock (_studentsLock)
                                        {
                                            studentsSnapshot = _students.ToList();
                                        }
                                        _resultWriter.WriteStudentsSolutionSummary(studentsSnapshot);
                                        
                                        int currentCompletedIndex;
                                        lock (completedLock)
                                        {
                                            completedCount++;
                                            currentCompletedIndex = completedCount;
                                        }
                                        
                                        studentsProcessed++;
                                        _logger.LogInfo($"[Worker-{localWorkerId}] [{currentCompletedIndex}/{studentsToGrade.Count}] Completed: {student.StudentCode}");
                                        _logger.LogInfo($"[Progress] {currentCompletedIndex}/{studentsToGrade.Count} students completed, {Math.Min(_configuration.MaxParallelStudents, studentsToGrade.Count - currentCompletedIndex)} students currently in progress");
                                        
                                        // Update UI on UI thread
                                        await Dispatcher.InvokeAsync(() => UpdateStatusBar());
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        _logger.LogInfo($"[Worker-{localWorkerId}] Grading cancelled for student {student.StudentCode}");
                                        // Re-throw to exit the worker loop
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        // CRITICAL FIX: Catch ALL exceptions during student grading
                                        // Without this, a single student error would crash the entire worker thread,
                                        // leaving remaining students in the queue unprocessed
                                        _logger.LogError($"[Worker-{localWorkerId}] CRITICAL ERROR while grading {student.StudentCode}: {ex.Message}", ex);
                                        _logger.LogError($"[Worker-{localWorkerId}] Stack trace: {ex.StackTrace}");
                                        
                                        // Ensure student is marked as failed so user knows it wasn't processed
                                        try
                                        {
                                            student.Status = GradingStatus.Failed;
                                            student.StatusMessage = $"Worker crashed: {ex.Message}";
                                            student.EndTime = DateTime.Now;
                                            UpdateStudentInUI(student);
                                            
                                            // CRITICAL: Lock access to _students collection for thread safety
                                            List<StudentSolution> studentsSnapshot;
                                            lock (_studentsLock)
                                            {
                                                studentsSnapshot = _students.ToList();
                                            }
                                            _resultWriter.WriteStudentsSolutionSummary(studentsSnapshot);
                                            
                                            int currentCompletedIndex;
                                            lock (completedLock)
                                            {
                                                completedCount++;
                                                currentCompletedIndex = completedCount;
                                            }
                                            
                                            _logger.LogWarning($"[Worker-{localWorkerId}] Marked {student.StudentCode} as Failed and continuing with next student");
                                        }
                                        catch (Exception cleanupEx)
                                        {
                                            _logger.LogError($"[Worker-{localWorkerId}] Failed to mark student as failed: {cleanupEx.Message}", cleanupEx);
                                        }
                                        
                                        // Continue processing next student - don't let one failure stop the entire worker
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.LogInfo($"[Worker-{localWorkerId}] Cancelled after processing {studentsProcessed} students");
                            }
                            catch (Exception ex)
                            {
                                // Catch any other unexpected errors in the worker loop itself
                                _logger.LogError($"[Worker-{localWorkerId}] Worker thread crashed unexpectedly after processing {studentsProcessed} students", ex);
                            }
                            finally
                            {
                                _logger.LogInfo($"[Worker-{localWorkerId}] Finished. Total students processed: {studentsProcessed}");
                            }
                        }, _cancellationTokenSource.Token);
                        workerTasks.Add(workerTask);
                    }
                    
                    // Wait for producer and all workers to complete
                    try
                    {
                        await producerTask;
                        _logger.LogInfo("[Batch Processing] Producer task completed");
                        
                        await Task.WhenAll(workerTasks);
                        _logger.LogInfo($"[Batch Processing] All {workerTasks.Count} worker tasks completed");
                        _logger.LogInfo($"[Optimization] Continuous batch processing complete: All {studentsToGrade.Count} students graded with maximum efficiency");
                        
                        // CRITICAL VERIFICATION: Check if any students were lost during processing
                        // This detects the bug where students remain in "Not Run" status after grading
                        var lostStudents = studentsToGrade.Where(s => s.Status == GradingStatus.Not_Run).ToList();
                        if (lostStudents.Count > 0)
                        {
                            _logger.LogError($"[CRITICAL BUG DETECTED] {lostStudents.Count} student(s) were queued for grading but never processed!");
                            _logger.LogError("[CRITICAL BUG DETECTED] These students remain in 'Not Run' status:");
                            foreach (var lost in lostStudents)
                            {
                                _logger.LogError($"  - {lost.StudentCode} (Paper {lost.PaperNo})");
                                
                                // Mark these students as Failed with a clear error message
                                lost.Status = GradingStatus.Failed;
                                lost.StatusMessage = "ERROR: Student was queued for grading but worker thread did not process it. This indicates a critical bug in the batch processing system.";
                                lost.EndTime = DateTime.Now;
                                UpdateStudentInUI(lost);
                            }
                            
                            // Write final results with updated statuses
                            // CRITICAL: Lock access to _students collection for thread safety
                            List<StudentSolution> studentsSnapshot;
                            lock (_studentsLock)
                            {
                                studentsSnapshot = _students.ToList();
                            }
                            _resultWriter.WriteStudentsSolutionSummary(studentsSnapshot);
                            
                            _logger.LogError($"[CRITICAL BUG DETECTED] Marked {lostStudents.Count} lost students as Failed");
                            _logger.LogError("[CRITICAL BUG DETECTED] Please report this bug with the log files");
                        }
                        else
                        {
                            _logger.LogInfo("[Verification] All queued students were successfully processed (no lost students detected)");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInfo("[Optimization] Continuous batch processing cancelled");
                    }
                    catch (Exception ex)
                    {
                        // This catches aggregated exceptions from Task.WhenAll
                        _logger.LogError("[Batch Processing] One or more worker tasks encountered errors", ex);
                        
                        // Log individual task exceptions if available
                        if (ex is AggregateException aggEx)
                        {
                            foreach (var innerEx in aggEx.InnerExceptions)
                            {
                                _logger.LogError($"[Batch Processing] Task exception: {innerEx.Message}", innerEx);
                            }
                        }
                    }
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
                UpdateButtonStates();
                
                // CRITICAL: Flush any pending UI updates before finalizing
                // This ensures all student statuses, logs, and stats are visible to user
                _uiUpdateBatcher.Flush();
                
                // CRITICAL: Flush any pending result writes to ensure all data is saved
                _resultWriter.FlushPendingWrites();
                
                // Dispose all Docker containers (including database) when grading session ends
                // Only dispose if not paused (paused sessions may resume)
                if (!_isPaused)
                {
                    _gradingService.DisposeAllContainers(_configuration);
                }
                
                // PORT ALLOCATION REMOVED: No longer using PortAllocator
                // Keeping commented for reference
                // _sharedPortAllocator?.Dispose();
                // _sharedPortAllocator = null;
                
                // Dispose shared GradingMessageLogger when session ends
                // This will export all messages to Excel and close the log file
                _sharedMessageLogger?.LogInfo($"Grading session completed. Total students: {studentsToGrade.Count}");
                _sharedMessageLogger?.Dispose();
                _sharedMessageLogger = null;
                _logger.LogInfo("[Message Logger] Shared GradingMessageLogger disposed and logs exported to Excel");
                
                // LEGACY: Clear shared network monitors (HOST-based monitoring)
                // With sidecar pattern, monitors are Docker containers cleaned up automatically
                /*
                try
                {
                    await SharedNetworkMonitorManager.Instance.ClearAllAsync();
                    _logger.LogInfo("[Shared Network Monitor] All monitors cleared and disposed");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Shared Network Monitor] Error clearing monitors: {ex.Message}");
                }
                */
                
                _logger.LogInfo("Grading session completed");
            }
        }

        /// <summary>
        /// Grades a single student using Docker-based grading.
        /// 
        /// PORT ALLOCATION REMOVED: All students use Code_Container_Internal_Port from environment.xlsx.
        /// Docker containers are isolated, so there's no port conflict between students.
        /// This matches CLI behavior and simplifies configuration.
        /// 
        /// PARALLEL EXECUTION: Multiple students can create containers simultaneously
        /// without any staggering or serialization, allowing true parallel batch grading.
        /// </summary>
        /// <param name="student">Student to grade</param>
        /// <param name="ct">Cancellation token</param>
        private async Task GradeStudentAsync(StudentSolution student, CancellationToken ct)
        {
            // Set logging context with paper number for organized logging (paper/Log_StudentCode_Date)
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            // PORT ALLOCATION REMOVED: No longer check for PortAllocator
            // Port will be read from test kit environment.xlsx configuration
            
            try
            {
                // NOTE: Do NOT change status to InProgress here - let the orchestration service handle it
                // This was causing the filter in StartGradingAsync to skip students
                student.StartTime = DateTime.Now;
                student.ProgressPercent = 0;
                UpdateStudentInUI(student);
                
                // OPTIMIZED: Batch current student display update
                // For batch grading, show "Multiple students..." to avoid UI thrashing
                // For sequential grading, show actual student code
                if (_configuration.MaxParallelStudents > 1)
                {
                    _uiUpdateBatcher.QueueUpdate(() => {
                        runCurrentStudent.Text = "Multiple students...";
                    });
                }
                else
                {
                    _uiUpdateBatcher.QueueUpdate(() => {
                        runCurrentStudent.Text = student.StudentCode;
                    });
                }
                
                // Brief yield to reduce thread contention on UI thread
                await Task.Yield();
                
                _logger.LogInfo($"Starting grading for {student.StudentCode} (Paper {student.PaperNo})");
                
                // Use cached test kit path and config to avoid repeated Excel file reads
                if (!_testKitCache.TryGetValue(student.PaperNo, out var cachedTestKit))
                {
                    // Not in cache (shouldn't happen if LoadStudents ran), load it now
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
                    
                    cachedTestKit = (testKitPath, testKitConfig);
                    _testKitCache[student.PaperNo] = cachedTestKit;
                }
                
                // Check if test kit exists
                if (string.IsNullOrEmpty(cachedTestKit.testKitPath) || cachedTestKit.config == null)
                {
                    student.Status = GradingStatus.Not_Run;
                    student.StatusMessage = $"No test kit for paper {student.PaperNo}";
                    student.EndTime = DateTime.Now;
                    _logger.LogWarning(student.StatusMessage);
                    UpdateStudentInUI(student);
                    return;
                }
                
                student.MaxMark = cachedTestKit.config.TotalMaxMark;
                student.ProgressPercent = 10;
                UpdateStudentInUI(student);
                
                // CRITICAL: Create a student-specific configuration copy with DYNAMICALLY ALLOCATED port
                // Each parallel student needs their own configuration with unique ports to avoid conflicts
                // This ensures each student's network monitor captures traffic on their specific port
                // Internal and external ports MUST match for network monitoring with npcap/libpcap
                //
                // NOTE: We use the dynamically allocated port from PortAllocator instead of the old
                // offset-based approach. This prevents race conditions where ports get reused while
                // still being in use by another student.
                //
                // APPROACH 2: Map Project1/Project2 role configuration directly to ClientProjectName/ServerProjectName
                // This uses the new flexible role indication system instead of relying on legacy properties.
                // The mapping logic ensures the correct project names are used based on configured roles.
                
                // Determine client and server project names from the flexible Project1/Project2 configuration
                string clientProjectName;
                string serverProjectName;
                
                bool hasProject1 = !string.IsNullOrWhiteSpace(_configuration.Project1Name);
                bool hasProject2 = !string.IsNullOrWhiteSpace(_configuration.Project2Name);
                
                if (hasProject1 && hasProject2)
                {
                    // Two projects: Map based on their configured roles
                    clientProjectName = _configuration.Project1IsClient 
                        ? _configuration.Project1Name 
                        : _configuration.Project2Name;
                    serverProjectName = _configuration.Project1IsClient 
                        ? _configuration.Project2Name 
                        : _configuration.Project1Name;
                    
                    _logger.LogInfo($"Two-project configuration: Client={clientProjectName}, Server={serverProjectName}");
                }
                else if (hasProject1 || hasProject2)
                {
                    // Single project: It handles both client and server roles
                    var singleProjectName = hasProject1 ? _configuration.Project1Name : _configuration.Project2Name;
                    clientProjectName = singleProjectName;
                    serverProjectName = singleProjectName;
                    
                    _logger.LogInfo($"Single-project configuration: {singleProjectName} (handles both roles)");
                }
                else
                {
                    // Fallback to legacy properties if Project1/Project2 are not configured
                    // This maintains backward compatibility with older configurations
                    clientProjectName = _configuration.ClientProjectName;
                    serverProjectName = _configuration.ServerProjectName;
                    
                    _logger.LogWarning($"Using legacy project names: Client={clientProjectName}, Server={serverProjectName}");
                }
                
                // PORT ALLOCATION REMOVED: Use Code_Container_Internal_Port from test kit
                // All students use the same internal port (Docker container isolation prevents conflicts)
                int internalPort = cachedTestKit.config.CodeContainerInternalPort;
                _logger.LogInfo($"[{student.StudentCode}] Using unified internal port: {internalPort} (from environment.xlsx - Docker isolated)");
                
                var studentConfig = new GradingConfiguration
                {
                    SubmitFolderPath = _configuration.SubmitFolderPath,
                    TestKitFolderPath = _configuration.TestKitFolderPath,
                    SaveResultFolderPath = _configuration.SaveResultFolderPath,
                    HasClient = _configuration.HasClient,
                    HasServer = _configuration.HasServer,
                    
                    // Use the mapped project names from flexible role configuration
                    ClientProjectName = clientProjectName,
                    ServerProjectName = serverProjectName,
                    
                    // Also copy Project1/Project2 properties for services that might use them
                    Project1Name = _configuration.Project1Name,
                    Project2Name = _configuration.Project2Name,
                    Project1IsClient = _configuration.Project1IsClient,
                    Project2IsClient = _configuration.Project2IsClient,
                    
                    MaxParallelStudents = _configuration.MaxParallelStudents,
                    GradingTimeoutSeconds = _configuration.GradingTimeoutSeconds,
                    DockerNetwork = _configuration.DockerNetwork,
                    StartIndex = _configuration.StartIndex,
                    EndIndex = _configuration.EndIndex,
                    
                    // PORT ALLOCATION REMOVED: Use Code_Container_Internal_Port from environment.xlsx
                    // All students use the same internal port (no allocation needed - Docker isolated)
                    // Docker containers are isolated, so no conflicts between students
                    CodeContainerInternalPort = internalPort,
                    CodeContainerHostPort = internalPort,
                    
                    // Database settings from cached test kit
                    DatabaseImageName = cachedTestKit.config.DatabaseImageName,
                    DatabaseContainerName = cachedTestKit.config.DatabaseContainerName,
                    DatabaseContainerInternalPort = cachedTestKit.config.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = cachedTestKit.config.DatabaseContainerHostPort,
                    DatabaseUsername = cachedTestKit.config.DatabaseUsername,
                    DatabasePassword = cachedTestKit.config.DatabasePassword,
                    
                    // DLL modification fallback setting
                    UseDllModificationFallback = _configuration.UseDllModificationFallback
                };
                
                _logger.LogInfo($"Student config created: Client={clientProjectName}, Server={serverProjectName}");
                _logger.LogInfo($"Using Code_Container_Internal_Port: {internalPort} (no port allocation - matches CLI behavior)");
                _logger.LogInfo($"Max mark from Header.xlsx: {cachedTestKit.config.TotalMaxMark}");
                _logger.LogInfo($"[{student.StudentCode}] DLL Modification Fallback: {(studentConfig.UseDllModificationFallback ? "ENABLED" : "DISABLED")}");
                _logger.LogInfo($"[Parallel Grading] Starting container setup for {student.StudentCode} (no serialization)");
                
                try
                {
                    // Execute grading using the orchestration service - it handles status changes internally
                    // Pass the cancellation token so pause can abort the current grading
                    // PORT ALLOCATION REMOVED: All students use same Code_Container_Internal_Port
                    // Docker container isolation prevents port conflicts between students
                    // TRUE PARALLEL: Containers created simultaneously without any serialization or callbacks
                    // Pass the shared message logger to prevent file access conflicts in parallel grading
                    var sessionState = new GradingSessionState();
                    await _gradingService.StartGradingAsync(
                        new System.Collections.Generic.List<StudentSolution> { student },
                        studentConfig,
                        sessionState,
                        ct,
                        _sharedMessageLogger);
                
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
            }
            finally
            {
                _logger.SetStudentContext(null);
            }
        }

        private async void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning && !_isPaused)
            {
                _isPaused = true;
                // Cancel the current grading operation to abort the student being graded
                _cancellationTokenSource?.Cancel();
                
                // CRITICAL: Immediate UI feedback for pause button (instant response)
                // Use Send priority to update UI immediately without waiting for batch timer
                await Dispatcher.InvokeAsync(() =>
                {
                    btnPause.IsEnabled = false;
                    btnResume.IsEnabled = true;
                    btnStartAll.IsEnabled = true;
                    btnStartSelected.IsEnabled = true;
                }, System.Windows.Threading.DispatcherPriority.Send);
                
                _logger.LogInfo("Grading paused - current student will be aborted and can be resumed");
            }
        }

        private async void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (_isPaused)
            {
                _isPaused = false;
                
                // CRITICAL: Immediate UI feedback for resume button (instant response)
                // Use Send priority to update UI immediately
                await Dispatcher.InvokeAsync(() =>
                {
                    btnPause.IsEnabled = true;
                    btnResume.IsEnabled = false;
                    btnStartAll.IsEnabled = false;
                    btnStartSelected.IsEnabled = false;
                }, System.Windows.Threading.DispatcherPriority.Send);
                
                _logger.LogInfo("Grading resumed");
                
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
            
            // Confirm reset action with user since it deletes result folders
            var result = System.Windows.MessageBox.Show(
                $"This will reset all {_students.Count} student(s) and DELETE their result folders.\n\n" +
                "This ensures a clean re-grade without interference from previous attempts.\n\n" +
                "Are you sure you want to continue?",
                "Confirm Reset All",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.No)
                return;
            
            _logger.LogInfo($"Resetting all {_students.Count} students and deleting result folders...");
            
            lock (_studentsLock)
            {
                foreach (var student in _students)
                {
                    ResetStudent(student);
                }
            }
            
            dgStudents.Items.Refresh();
            UpdateStatusBar();
            
            _logger.LogInfo($"All {_students.Count} student statuses reset and result folders deleted");
            System.Windows.MessageBox.Show(
                $"Reset complete!\n\n{_students.Count} student(s) are ready for re-grading.",
                "Reset Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ResetSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                System.Windows.MessageBox.Show("Cannot reset while grading is in progress.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            List<StudentSolution> selectedStudents;
            lock (_studentsLock)
            {
                selectedStudents = _students.Where(s => s.IsSelected).ToList();
            }
            
            if (selectedStudents.Count == 0)
            {
                System.Windows.MessageBox.Show("No students selected.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Confirm reset action with user since it deletes result folders
            var result = System.Windows.MessageBox.Show(
                $"This will reset {selectedStudents.Count} selected student(s) and DELETE their result folders.\n\n" +
                "This ensures a clean re-grade without interference from previous attempts.\n\n" +
                "Are you sure you want to continue?",
                "Confirm Reset Selected",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.No)
                return;
            
            _logger.LogInfo($"Resetting {selectedStudents.Count} selected students and deleting result folders...");
            
            foreach (var student in selectedStudents)
            {
                ResetStudent(student);
            }
            
            dgStudents.Items.Refresh();
            UpdateStatusBar();
            
            _logger.LogInfo($"{selectedStudents.Count} selected student statuses reset and result folders deleted");
            System.Windows.MessageBox.Show(
                $"Reset complete!\n\n{selectedStudents.Count} student(s) are ready for re-grading.",
                "Reset Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ResetStudent(StudentSolution student)
        {
            student.Status = GradingStatus.Not_Run;
            student.Mark = 0;
            student.StartTime = null;
            student.EndTime = null;
            student.StatusMessage = null;
            student.ProgressPercent = 0;
            
            // COMPREHENSIVE CLEANUP: Delete all result folders for this student
            // This is critical when grading was canceled/paused to prevent interference with re-grading
            // We need to clean up all possible locations where results might be stored
            
            int foldersDeleted = 0;
            
            // 1. Delete paper-organized result folder (current structure)
            // Format: SaveResultFolderPath/{PaperNo}/student/{StudentCode}/
            var paperResultFolder = Path.Combine(_configuration.SaveResultFolderPath, student.PaperNo, "student", student.StudentCode);
            if (Directory.Exists(paperResultFolder))
            {
                try
                {
                    Directory.Delete(paperResultFolder, true);
                    foldersDeleted++;
                    _logger.LogInfo($"Deleted paper-organized result folder for {student.StudentCode} (Paper {student.PaperNo})");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete paper-organized result folder for {student.StudentCode}: {ex.Message}");
                }
            }

            // 2. Delete legacy non-paper-organized result folder (old structure)
            // Format: SaveResultFolderPath/student/{StudentCode}/
            var legacyResultFolder = Path.Combine(_configuration.SaveResultFolderPath, "student", student.StudentCode);
            if (Directory.Exists(legacyResultFolder))
            {
                try
                {
                    Directory.Delete(legacyResultFolder, true);
                    foldersDeleted++;
                    _logger.LogInfo($"Deleted legacy result folder for {student.StudentCode}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to delete legacy result folder for {student.StudentCode}: {ex.Message}");
                }
            }
            
            // 3. Delete student-specific log folders that might contain partial results
            // Format: SaveResultFolderPath/Logs/Log_{StudentCode}_{Date}_Paper{PaperNo}/
            try
            {
                // SECURITY: Validate student code to prevent directory traversal
                // Student codes should not contain path separators or special characters
                if (string.IsNullOrWhiteSpace(student.StudentCode) || 
                    student.StudentCode.Contains("..") || 
                    student.StudentCode.Contains(Path.DirectorySeparatorChar) ||
                    student.StudentCode.Contains(Path.AltDirectorySeparatorChar))
                {
                    _logger.LogWarning($"Invalid student code format: {student.StudentCode}. Skipping log folder cleanup.");
                }
                else
                {
                    var logsFolder = Path.Combine(_configuration.SaveResultFolderPath, "Logs");
                    if (Directory.Exists(logsFolder))
                    {
                        var studentLogPattern = $"Log_{student.StudentCode}_*";
                        var studentLogFolders = Directory.GetDirectories(logsFolder, studentLogPattern);
                    
                        foreach (var logFolder in studentLogFolders)
                        {
                            try
                            {
                                Directory.Delete(logFolder, true);
                                foldersDeleted++;
                                _logger.LogInfo($"Deleted log folder: {Path.GetFileName(logFolder)}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Failed to delete log folder {Path.GetFileName(logFolder)}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to search for student log folders: {ex.Message}");
            }
            
            // 4. Delete any temp/intermediate files for this student
            // These might be created during grading but not cleaned up if canceled
            try
            {
                // SECURITY: Reuse student code validation from above
                if (!string.IsNullOrWhiteSpace(student.StudentCode) && 
                    !student.StudentCode.Contains("..") && 
                    !student.StudentCode.Contains(Path.DirectorySeparatorChar) &&
                    !student.StudentCode.Contains(Path.AltDirectorySeparatorChar))
                {
                    var tempPattern = $"*{student.StudentCode}*.tmp";
                    var tempFiles = Directory.GetFiles(_configuration.SaveResultFolderPath, tempPattern, SearchOption.AllDirectories);
                
                    foreach (var tempFile in tempFiles)
                    {
                        try
                        {
                            File.Delete(tempFile);
                            _logger.LogInfo($"Deleted temp file: {Path.GetFileName(tempFile)}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Failed to delete temp file {Path.GetFileName(tempFile)}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to search for temp files: {ex.Message}");
            }
            
            if (foldersDeleted > 0)
            {
                _logger.LogInfo($"Reset complete for {student.StudentCode}: Deleted {foldersDeleted} folder(s). Student is ready for re-grading.");
            }
            else
            {
                _logger.LogInfo($"Reset complete for {student.StudentCode}: No existing result folders found.");
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
            // OPTIMIZED: Use batching to reduce UI thread contention
            // During parallel grading, this prevents hundreds of redundant button state updates
            _uiUpdateBatcher.QueueUpdate(() =>
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
            // OPTIMIZED: Single-pass iteration through students collection
            // Previously iterated 4-5 times, now only once for better performance
            // CRITICAL: Lock access to ensure thread-safe enumeration during batch grading
            int total, success = 0, failed = 0, notRun = 0;
            DateTime? latestEndTime = null;
            
            lock (_studentsLock)
            {
                total = _students.Count;
                
                foreach (var student in _students)
                {
                    switch (student.Status)
                    {
                        case GradingStatus.Success:
                            success++;
                            break;
                        case GradingStatus.Failed:
                            failed++;
                            break;
                        case GradingStatus.Not_Run:
                            notRun++;
                            break;
                    }
                    
                    // Track latest end time for session duration calculation
                    if (student.EndTime.HasValue && (!latestEndTime.HasValue || student.EndTime.Value > latestEndTime.Value))
                    {
                        latestEndTime = student.EndTime;
                    }
                }
            }
            
            var graded = success + failed;
            
            // Calculate session duration (only when session has started)
            string sessionDuration = "-";
            if (_sessionStartTime.HasValue)
            {
                var endTime = _isRunning ? DateTime.Now : (latestEndTime ?? DateTime.Now);
                var elapsed = endTime - _sessionStartTime.Value;
                
                sessionDuration = elapsed.TotalHours >= 1
                    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
                    : elapsed.TotalMinutes >= 1
                        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                        : $"{elapsed.Seconds}s";
            }
            
            _uiUpdateBatcher.QueueUpdate(() =>
            {
                runTotal.Text = total.ToString();
                runGraded.Text = graded.ToString();
                runPercent.Text = total > 0 ? ((graded * 100) / total).ToString() : "0";
                runSuccess.Text = success.ToString();
                runFailed.Text = failed.ToString();
                runNotRun.Text = notRun.ToString();
                txtSessionDuration.Text = sessionDuration;
            });
        }

        private void UpdateStudentInUI(StudentSolution student)
        {
            // OPTIMIZED: Use Background priority to avoid blocking UI thread
            // DataGrid.Items.Refresh() is expensive with 150+ students
            // Background priority allows user input events to take precedence
            Dispatcher.BeginInvoke(new Action(() =>
            {
                dgStudents.Items.Refresh();
            }), System.Windows.Threading.DispatcherPriority.Background);
            
            // Status bar can still be batched for efficiency
            UpdateStatusBar();
        }

        #region Event Handlers

        private void GradingService_StudentGradingStarted(object? sender, StudentSolution student)
        {
            // OPTIMIZED: Only update when student starts (important milestone)
            // No intermediate progress updates needed - we only care about start/end
            UpdateStudentInUI(student);
            
            // Update student log path display
            if (!string.IsNullOrEmpty(student.StudentCode) && !string.IsNullOrEmpty(student.PaperNo))
            {
                var logsFolder = Path.Combine(_configuration.SaveResultFolderPath, "Logs");
                _currentStudentLogPath = Path.Combine(logsFolder, $"Log_{student.StudentCode}_{DateTime.Now:yyyyMMdd}_Paper{student.PaperNo}");
                
                _uiUpdateBatcher.QueueUpdate(() =>
                {
                    txtStudentLogPath.Text = _currentStudentLogPath;
                });
            }
        }

        private void GradingService_StudentGradingCompleted(object? sender, StudentSolution student)
        {
            // OPTIMIZED: Only update when student completes (important milestone)
            // Time elapsed is calculated on-demand via Duration property, no timer needed
            UpdateStudentInUI(student);
        }

        private void GradingService_SessionStateChanged(object? sender, GradingSessionState state)
        {
            // Update status bar for session-level changes
            UpdateStatusBar();
        }

        #endregion

        private void dgStudents_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {

        }
        
        /// <summary>
        /// Reads the starting port from Environment.xlsx in the test kit folder.
        /// Returns 0 if not found or error occurs (caller should use default port).
        /// </summary>
        private int ReadStartingPortFromEnvironmentXlsx(string testKitPath)
        {
            try
            {
                // Look for Environment.xlsx in the question-specific test kit folder
                var environmentPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (!File.Exists(environmentPath))
                {
                    // Try lowercase as fallback
                    environmentPath = Path.Combine(testKitPath, "environment.xlsx");
                    if (!File.Exists(environmentPath))
                    {
                        _logger.LogWarning($"Environment.xlsx not found at {testKitPath}");
                        return 0;
                    }
                }

                _logger.LogInfo($"Reading port configuration from: {environmentPath}");

                using (var workbook = new ClosedXML.Excel.XLWorkbook(environmentPath))
                {
                    // Look for "Config" sheet which contains port configuration
                    var worksheet = workbook.Worksheet("Config");
                    if (worksheet == null)
                    {
                        _logger.LogWarning($"'Config' sheet not found in Environment.xlsx");
                        return 0;
                    }
                    
                    // Find Code_Container_Host_Port in the Config sheet
                    foreach (var row in worksheet.RowsUsed().Skip(1)) // Skip header row
                    {
                        var keyCell = row.Cell(1).GetString().Trim();
                        var normalizedKey = keyCell.Replace("_", "").ToLowerInvariant();
                        
                        if (normalizedKey == "codecontainerhostport" || normalizedKey == "codecontainerinternalport")
                        {
                            var valueCell = row.Cell(2);
                            int port = 0;
                            
                            if (valueCell.TryGetValue<int>(out var intValue))
                            {
                                port = intValue;
                            }
                            else if (int.TryParse(valueCell.GetString(), out var parsedValue))
                            {
                                port = parsedValue;
                            }
                            
                            if (port > 0)
                            {
                                _logger.LogInfo($"Found starting port {port} in Environment.xlsx (key: {keyCell})");
                                return port;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error reading port from Environment.xlsx: {ex.Message}");
            }
            
            return 0; // Not found or error
        }
    }
}
