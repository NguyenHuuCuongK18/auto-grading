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
using SolutionGrader.Core.Services;
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
        private StringBuilder _logBuffer = new StringBuilder();
        private int _estimatedLogCapacity = 8192; // Default for unknown student count
        
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
            
            // Initialize batch grading configuration control with default value
            txtMaxParallelStudents.Text = _configuration.MaxParallelStudents.ToString();
            
            // Initialize index selection controls with 1-based defaults
            txtSelectStartIndex.Text = "1";
            txtSelectEndIndex.Text = "-1";
            
            // Load students
            LoadStudents();
            
            // Setup elapsed timer
            _elapsedTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _elapsedTimer.Tick += ElapsedTimer_Tick;
            
            _logger.LogInfo("Grading window initialized");
            _logger.LogInfo($"Batch grading configuration: Number of Solutions={_configuration.MaxParallelStudents}");
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
                cmbPaperSelection.Items.Add("-- Select Paper --");
                cmbPaperSelection.SelectedIndex = 0;
                
                // Get unique paper numbers
                var paperNumbers = students.Select(s => s.PaperNo).Distinct().OrderBy(p => int.TryParse(p, out var n) ? n : 0);
                foreach (var paper in paperNumbers) { cmbPaperSelection.Items.Add($"Paper {paper}"); }
                
                int idx = 1; // 1-based ids for clarity
                foreach (var student in students)
                {
                    // assign 1-based Id
                    student.Id = idx++;
                    
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
            
            // FIXED: Ensure UI updates are marshalled to UI thread and force DataGrid refresh
            // Select all students with this paper number
            int selectedCount = 0;
            foreach (var student in _students.Where(s => s.PaperNo == paperNo))
            {
                student.IsSelected = true;
                selectedCount++;
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
            foreach (var student in _students)
            {
                if (student.IsSelected)
                {
                    student.IsSelected = false;
                    unselectedCount++;
                }
            }
            
            // Step 2: Apply selection to the specified index range
            var studentsInRange = ApplyIndexRange(_students.ToList(), startIndex, endIndex);
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
            
            // FIXED: Enhanced logging and validation to debug selection issues
            _logger.LogInfo($"=== Starting Grading Session ===");
            _logger.LogInfo($"Mode: {(selectedOnly ? "Selected Only" : "All Students")}");
            _logger.LogInfo($"Total students loaded: {_students.Count}");
            _logger.LogInfo($"Students in filtered view: {_filteredStudents.Count}");
            
            if (selectedOnly)
            {
                var selectedStudents = _filteredStudents.Where(s => s.IsSelected).ToList();
                var notSuccessStudents = selectedStudents.Where(s => s.Status != GradingStatus.Success).ToList();
                _logger.LogInfo($"Students with IsSelected=true: {selectedStudents.Count}");
                _logger.LogInfo($"Students with IsSelected=true AND Status!=Success: {notSuccessStudents.Count}");
                
                // Log detailed info about selected students
                foreach (var s in selectedStudents)
                {
                    _logger.LogInfo($"  - Student {s.Id}: {s.StudentCode}, IsSelected={s.IsSelected}, Status={s.Status}");
                }
            }
            
            // Get students to grade based on selection
            var studentsToGrade = selectedOnly
                ? _filteredStudents.Where(s => s.IsSelected && s.Status != GradingStatus.Success).ToList()
                : _filteredStudents.Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused).ToList();
            
            _logger.LogInfo($"Students to grade after filtering: {studentsToGrade.Count}");
            
            if (studentsToGrade.Count == 0)
            {
                var message = selectedOnly 
                    ? "No students to grade.\n\nPossible reasons:\n- No students are selected (check the 'Select' checkboxes)\n- All selected students have already been successfully graded\n\nTip: Use 'Apply' button after entering index range to select students."
                    : "No students to grade.\n\nAll students have been graded or there are no students loaded.";
                    
                _logger.LogWarning(message);
                System.Windows.MessageBox.Show(message, "No Students to Grade", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // CRITICAL: Clear all previously allocated ports at the START of a new grading session.
            // This prevents port exhaustion from previous runs while ensuring no port reuse
            // DURING the current session (which could cause race conditions in parallel grading).
            // 
            // The "never reuse" policy applies WITHIN a session - once a port is allocated
            // for this session, it stays allocated until the session ends. This prevents
            // the race condition where Student A finishes and releases port 8001, but the
            // system incorrectly reuses it while Student B is still being graded.
            _logger.LogInfo("[UI] Clearing port allocation from previous sessions...");
            PortAllocator.ClearAllAllocatedPorts();
            
            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _sessionStartTime = DateTime.Now;
            _elapsedTimer?.Start();
            
            // OPTIMIZATION: Pre-allocate log buffer based on known student count
            // Estimate ~2KB per student (includes test cases, setup, cleanup logs)
            // This reduces memory allocations during grading
            _estimatedLogCapacity = studentsToGrade.Count * 2048;
            _logBuffer = new StringBuilder(_estimatedLogCapacity);
            _logger.LogInfo($"Pre-allocated log buffer capacity: {_estimatedLogCapacity / 1024}KB for {studentsToGrade.Count} students");
            
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
            
            _logger.LogInfo($"Port allocation: Ports are allocated once per student and NEVER reused during this session.");
            
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
                        _resultWriter.WriteStudentsSolutionSummary(_students.ToList());
                        
                        UpdateStatusBar();
                    }
                }
                else
                {
                    // Parallel grading using SemaphoreSlim to limit concurrency
                    // Each student gets a dynamically allocated port via PortAllocator
                    var resultLock = new object();
                    var startupLock = new SemaphoreSlim(1, 1); // OPTIMIZATION: Stagger container startups
                    
                    using (var semaphore = new SemaphoreSlim(_configuration.MaxParallelStudents))
                    {
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
                                
                                // Port allocation is now handled inside GradeStudentAsync using PortAllocator
                                // This ensures thread-safe, unique port allocation that never reuses ports
                                await GradeStudentAsync(student, _cancellationTokenSource.Token, startupLock);
                                
                                // Write results after each student
                                // OPTIMIZATION: Deferred write mechanism batches updates and runs on background thread
                                // No need for lock - the ResultWriter handles thread safety internally
                                _resultWriter.WriteStudentsSolutionSummary(_students.ToList());
                                
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
                
                // CRITICAL: Flush any pending result writes to ensure all data is saved
                _resultWriter.FlushPendingWrites();
                
                // Dispose all Docker containers (including database) when grading session ends
                // Only dispose if not paused (paused sessions may resume)
                if (!_isPaused)
                {
                    _gradingService.DisposeAllContainers(_configuration);
                }
                
                _logger.LogInfo("Grading session completed");
            }
        }

        /// <summary>
        /// Grades a single student using Docker-based grading.
        /// 
        /// Port allocation is handled dynamically using PortAllocator to ensure
        /// thread-safe, unique port allocation that never reuses ports within a session.
        /// This prevents race conditions in parallel grading where ports could be
        /// incorrectly reused while still in use by another student.
        /// </summary>
        /// <param name="student">Student to grade</param>
        /// <param name="ct">Cancellation token</param>
        private async Task GradeStudentAsync(StudentSolution student, CancellationToken ct, SemaphoreSlim startupLock = null)
        {
            // Set logging context with paper number for organized logging (paper/Log_StudentCode_Date)
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
            // Allocate a port dynamically using PortAllocator (thread-safe for parallel grading)
            // This replaces the old portOffset-based approach which could lead to race conditions
            // when students finish at different times and ports get reused incorrectly.
            //
            // CRITICAL: The PortAllocator ensures ports are NEVER reused within a grading session.
            // This prevents the scenario where:
            // - Student A starts with port 8001
            // - Student B starts with port 8000  
            // - Student A finishes and releases port 8001
            // - The system incorrectly reuses port 8000 (still in use by Student B)
            using var portAllocator = new PortAllocator();
            int allocatedPort = portAllocator.AllocatePort();
            
            if (allocatedPort == -1)
            {
                _logger.LogError($"[UI] Failed to allocate port for student {student.StudentCode}");
                student.Status = GradingStatus.Failed;
                student.StatusMessage = "Failed to allocate port for grading";
                student.EndTime = DateTime.Now;
                UpdateStudentInUI(student);
                return;
            }
            
            _logger.LogInfo($"[UI] Allocated port {allocatedPort} for student {student.StudentCode}");
            
            try
            {
                // NOTE: Do NOT change status to InProgress here - let the orchestration service handle it
                // This was causing the filter in StartGradingAsync to skip students
                student.StartTime = DateTime.Now;
                student.ProgressPercent = 0;
                UpdateStudentInUI(student);
                
                // CRITICAL FIX: Update UI element from UI thread to avoid cross-thread access issues
                // Use BeginInvoke (async) instead of Invoke (blocking) to prevent deadlocks when
                // batch size equals student pool size (all students start simultaneously)
                // Show "Multiple students" for batch grading to avoid constant UI thrashing
                // BALANCED: Use Render priority to update current student display
                // This ensures user sees which student is being graded without blocking workers
                if (_configuration.MaxParallelStudents > 1)
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        runCurrentStudent.Text = "Multiple students...";
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        runCurrentStudent.Text = student.StudentCode;
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
                
                // Brief yield to reduce thread contention on UI thread
                await Task.Yield();
                
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
                    
                    // Use dynamically allocated port for DIRECT MAPPING (internal:external)
                    // This ensures each student's client reaches their own server via host.docker.internal
                    // The PortAllocator ensures no port reuse during the grading session
                    CodeContainerInternalPort = allocatedPort,
                    CodeContainerHostPort = allocatedPort,
                    
                    // Database settings from test kit
                    DatabaseImageName = testKitConfig.DatabaseImageName,
                    DatabaseContainerName = testKitConfig.DatabaseContainerName,
                    DatabaseContainerInternalPort = testKitConfig.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = testKitConfig.DatabaseContainerHostPort,
                    DatabaseUsername = testKitConfig.DatabaseUsername,
                    DatabasePassword = testKitConfig.DatabasePassword
                };
                
                _logger.LogInfo($"Student config created: Client={clientProjectName}, Server={serverProjectName}");
                
                _logger.LogInfo($"Using dynamically allocated port: {allocatedPort} (no reuse policy)");
                _logger.LogInfo($"Max mark from Header.xlsx: {testKitConfig.TotalMaxMark}");
                _logger.LogInfo($"Network monitor will capture traffic on host port {allocatedPort}");
                
                // OPTIMIZATION: Stagger container startup to avoid Docker strain
                // Acquire lock before starting containers, release when containers are actually ready
                bool lockAcquired = false;
                if (startupLock != null)
                {
                    await startupLock.WaitAsync(ct);
                    lockAcquired = true;
                    _logger.LogInfo($"[Staggered Startup] Starting container setup for {student.StudentCode}");
                }
                
                // Callback to release lock as soon as containers are ready
                Action? onContainersReady = null;
                if (lockAcquired && startupLock != null)
                {
                    onContainersReady = () =>
                    {
                        startupLock.Release();
                        _logger.LogInfo($"[Staggered Startup] Containers ready for {student.StudentCode}, next student can start");
                    };
                }
                
                // Execute grading using the orchestration service - it handles status changes internally
                // Pass the cancellation token so pause can abort the current grading
                // IMPORTANT: Each student gets their own configuration with unique ports for network monitoring
                var sessionState = new GradingSessionState();
                await _gradingService.StartGradingAsync(
                    new System.Collections.Generic.List<StudentSolution> { student },
                    studentConfig,
                    sessionState,
                    ct,
                    onContainersReady);
                
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
            // BALANCED: Use BeginInvoke with Normal priority for responsive UI without blocking
            // Normal priority ensures UI updates happen promptly while not blocking worker threads
            Dispatcher.BeginInvoke(new Action(() =>
            {
                btnStartAll.IsEnabled = !_isRunning || _isPaused;
                btnStartSelected.IsEnabled = !_isRunning || _isPaused;
                btnPause.IsEnabled = _isRunning && !_isPaused;
                btnResume.IsEnabled = _isPaused;
                btnResetAll.IsEnabled = !_isRunning;
                btnResetSelected.IsEnabled = !_isRunning;
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void UpdateStatusBar()
        {
            // BALANCED: Pre-compute on worker thread, update UI with Normal priority
            // Ensures statistics are visible while maintaining performance
            var total = _students.Count;
            var graded = _students.Count(s => s.Status == GradingStatus.Success || s.Status == GradingStatus.Failed);
            var success = _students.Count(s => s.Status == GradingStatus.Success);
            var failed = _students.Count(s => s.Status == GradingStatus.Failed);
            var notRun = _students.Count(s => s.Status == GradingStatus.Not_Run);
            
            Dispatcher.BeginInvoke(new Action(() =>
            {
                runTotal.Text = total.ToString();
                runGraded.Text = graded.ToString();
                runPercent.Text = total > 0 ? ((graded * 100) / total).ToString() : "0";
                runSuccess.Text = success.ToString();
                runFailed.Text = failed.ToString();
                runNotRun.Text = notRun.ToString();
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void UpdateStudentInUI(StudentSolution student)
        {
            // BALANCED: Use Render priority for DataGrid refresh (visible to user)
            // Render priority ensures user sees updates without blocking workers
            Dispatcher.BeginInvoke(new Action(() =>
            {
                dgStudents.Items.Refresh();
            }), System.Windows.Threading.DispatcherPriority.Render);
            
            // Update status bar with Normal priority
            UpdateStatusBar();
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
            // BALANCED: Use Background priority for logging (less critical than grid updates)
            // Logs update without impacting more important UI elements
            var logLine = $"[{e.Timestamp:HH:mm:ss}] [{e.Level}] {e.Message}\n";
            
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _logBuffer.Append(logLine);
                
                // OPTIMIZATION: Keep log buffer manageable with dynamic threshold
                // Use 2x estimated capacity as max to allow for overhead
                var maxCapacity = Math.Max(50000, _estimatedLogCapacity * 2);
                if (_logBuffer.Length > maxCapacity)
                {
                    // Trim to 80% of max capacity to reduce frequent trimming
                    var targetLength = (int)(maxCapacity * 0.8);
                    var trimmed = _logBuffer.ToString().Substring(_logBuffer.Length - targetLength);
                    _logBuffer.Clear();
                    _logBuffer.Append(trimmed);
                    _logger.LogDebug($"Log buffer trimmed to {targetLength / 1024}KB");
                }
                
                txtLog.Text = _logBuffer.ToString();
                txtLog.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Background);
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

        private void dgStudents_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {

        }
    }
}
