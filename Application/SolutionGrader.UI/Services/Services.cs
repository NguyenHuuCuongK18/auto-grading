using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Interface for logging service.
    /// </summary>
    public interface ILoggingService
    {
        event EventHandler<LogEventArgs>? LogAdded;
        void Log(string message, LogLevel level = LogLevel.Info);
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(string message, Exception ex);
    }

    /// <summary>
    /// Logging service for grading operations.
    /// </summary>
    public class LoggingService : ILoggingService, IDisposable
    {
        private readonly string _logFolder;
        private string? _currentStudentContext;
        private string? _currentPaperContext;

        public event EventHandler<LogEventArgs>? LogAdded;

        public LoggingService(string logFolder)
        {
            _logFolder = logFolder;
            Directory.CreateDirectory(_logFolder);
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            var args = new LogEventArgs(message, level);
            Console.WriteLine($"[{level}] {message}");
            LogAdded?.Invoke(this, args);
        }

        public void LogInfo(string message) => Log(message, LogLevel.Info);
        public void LogWarning(string message) => Log(message, LogLevel.Warning);
        public void LogError(string message) => Log(message, LogLevel.Error);
        
        /// <summary>
        /// Logs an error with exception details.
        /// </summary>
        public void LogError(string message, Exception ex)
        {
            Log($"{message}: {ex.Message}", LogLevel.Error);
        }

        /// <summary>
        /// Sets the current student context for logging.
        /// </summary>
        public void SetStudentContext(string? studentCode)
        {
            _currentStudentContext = studentCode;
        }

        /// <summary>
        /// Sets the current student and paper context for logging.
        /// </summary>
        public void SetStudentContext(string? studentCode, string? paperNo)
        {
            _currentStudentContext = studentCode;
            _currentPaperContext = paperNo;
        }

        /// <summary>
        /// Disposes the logging service.
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose currently
        }
    }

    /// <summary>
    /// Service for discovering student submissions.
    /// </summary>
    public class StudentDiscoveryService
    {
        private readonly ILoggingService _logger;

        public StudentDiscoveryService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Discovers students from the submit folder.
        /// Expected structure: Submit/{PaperNo}/{StudentCode}/1/solution
        /// </summary>
        public List<StudentSolution> DiscoverStudents(string submitFolder)
        {
            var students = new List<StudentSolution>();

            if (!Directory.Exists(submitFolder))
            {
                _logger.LogWarning($"Submit folder not found: {submitFolder}");
                return students;
            }

            foreach (var paperDir in Directory.GetDirectories(submitFolder))
            {
                var paperNo = Path.GetFileName(paperDir);
                
                foreach (var studentDir in Directory.GetDirectories(paperDir))
                {
                    var studentCode = Path.GetFileName(studentDir);
                    var solutionPath = Path.Combine(studentDir, "1", "solution");

                    if (Directory.Exists(solutionPath))
                    {
                        students.Add(new StudentSolution
                        {
                            StudentCode = studentCode,
                            PaperNo = paperNo,
                            SolutionPath = solutionPath,
                            Status = GradingStatus.Not_Run
                        });
                    }
                }
            }

            _logger.LogInfo($"Discovered {students.Count} student submissions");
            return students;
        }

        /// <summary>
        /// Discovers students from the submit folder for a specific paper.
        /// </summary>
        public List<StudentSolution> DiscoverStudents(string submitFolder, string paperNo)
        {
            var allStudents = DiscoverStudents(submitFolder);
            return allStudents.Where(s => s.PaperNo == paperNo).ToList();
        }

        /// <summary>
        /// Discovers students using the configuration (for compatibility with UI code).
        /// </summary>
        public List<StudentSolution> DiscoverStudents(string submitFolder, GradingConfiguration config)
        {
            return DiscoverStudents(submitFolder);
        }
    }

    /// <summary>
    /// Service for discovering test kits.
    /// </summary>
    public class TestKitDiscoveryService
    {
        private readonly ILoggingService _logger;

        public TestKitDiscoveryService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Discovers test kits from the testkit folder.
        /// </summary>
        public List<TestKitInfo> DiscoverTestKits(string testKitFolder)
        {
            var testKits = new List<TestKitInfo>();

            if (!Directory.Exists(testKitFolder))
            {
                _logger.LogWarning($"TestKit folder not found: {testKitFolder}");
                return testKits;
            }

            foreach (var tkDir in Directory.GetDirectories(testKitFolder))
            {
                var name = Path.GetFileName(tkDir);
                var testCases = new List<string>();

                // Find test cases (TC1, TC2, etc.)
                foreach (var tcDir in Directory.GetDirectories(tkDir))
                {
                    var tcName = Path.GetFileName(tcDir);
                    if (tcName.StartsWith("TC", StringComparison.OrdinalIgnoreCase))
                    {
                        // Verify Detail.xlsx exists
                        if (File.Exists(Path.Combine(tcDir, "Detail.xlsx")))
                        {
                            testCases.Add(tcName);
                        }
                    }
                }

                if (testCases.Count > 0)
                {
                    testKits.Add(new TestKitInfo
                    {
                        Name = name,
                        Path = tkDir,
                        TestCases = testCases.OrderBy(tc => tc).ToList()
                    });
                }
            }

            _logger.LogInfo($"Discovered {testKits.Count} test kits");
            return testKits;
        }

        /// <summary>
        /// Gets the test kit path for a given paper number using dictionary mapping.
        /// </summary>
        public string? GetTestKitForPaper(string paperNo, Dictionary<string, string> mapping)
        {
            if (mapping.TryGetValue(paperNo, out var testKitName))
            {
                return testKitName;
            }
            return null;
        }

        /// <summary>
        /// Gets the test kit path for a given paper by searching the test kit folder.
        /// First checks the PaperToTestKitMapping, then falls back to folder conventions.
        /// Returns the full path to the matching question folder.
        /// </summary>
        public string? GetTestKitForPaper(string testKitFolder, string paperNo)
        {
            return GetTestKitForPaper(testKitFolder, paperNo, new Dictionary<string, string>());
        }

        /// <summary>
        /// Gets the test kit path for a given paper by first checking the mapping dictionary,
        /// then falling back to folder conventions (Q{n}, Paper{n}).
        /// Returns the full path to the matching question folder.
        /// </summary>
        public string? GetTestKitForPaper(string testKitFolder, string paperNo, Dictionary<string, string> mapping)
        {
            if (!Directory.Exists(testKitFolder))
            {
                return null;
            }

            // First, try to use the mapping from Mapping.xlsx
            if (mapping != null && mapping.TryGetValue(paperNo, out var testKitName))
            {
                var mappedPath = Path.Combine(testKitFolder, testKitName);
                if (Directory.Exists(mappedPath))
                {
                    _logger.LogInfo($"Found testkit for paper {paperNo} from mapping: {mappedPath}");
                    return mappedPath;
                }
            }

            // Try Q{paperNo} convention (Q1, Q2, etc.)
            var testKitPath = Path.Combine(testKitFolder, $"Q{paperNo}");
            if (Directory.Exists(testKitPath))
            {
                _logger.LogInfo($"Found testkit for paper {paperNo} using Q{paperNo} convention: {testKitPath}");
                return testKitPath;
            }

            // Try Paper{paperNo} convention
            testKitPath = Path.Combine(testKitFolder, $"Paper{paperNo}");
            if (Directory.Exists(testKitPath))
            {
                _logger.LogInfo($"Found testkit for paper {paperNo} using Paper{paperNo} convention: {testKitPath}");
                return testKitPath;
            }

            _logger.LogWarning($"No testkit found for paper {paperNo}");
            return null;
        }

        /// <summary>
        /// Loads the paper-to-testkit mapping from Mapping.xlsx.
        /// Format: PaperNo | Question | QuestionKit
        /// Example: 1 | Q1 | Q1
        /// Returns a dictionary mapping paper number (string) to testkit folder name (string).
        /// </summary>
        public Dictionary<string, string> LoadMappingFromExcel(string testKitFolder)
        {
            var mapping = new Dictionary<string, string>();
            var mappingPath = Path.Combine(testKitFolder, "Mapping.xlsx");

            if (!File.Exists(mappingPath))
            {
                _logger.LogWarning($"Mapping.xlsx not found: {mappingPath}");
                return mapping;
            }

            try
            {
                using var wb = new XLWorkbook(mappingPath);
                var ws = wb.Worksheet(1);

                // Skip header row
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    // Get the raw cell value and convert to string to handle both numeric and text values
                    var paperNoCell = row.Cell(1);
                    var questionKitCell = row.Cell(3); // Column 3 is QuestionKit
                    
                    // Convert to string, handling numeric values
                    var paperNo = paperNoCell.GetString().Trim();
                    var questionKit = questionKitCell.GetString().Trim();

                    if (!string.IsNullOrWhiteSpace(paperNo) && !string.IsNullOrWhiteSpace(questionKit))
                    {
                        mapping[paperNo] = questionKit;
                        _logger.LogInfo($"Loaded mapping: Paper {paperNo} -> {questionKit}");
                    }
                }

                _logger.LogInfo($"Loaded {mapping.Count} paper-to-testkit mappings from Mapping.xlsx");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to read Mapping.xlsx: {ex.Message}");
            }

            return mapping;
        }

        /// <summary>
        /// Validates that all discovered papers have corresponding testkits.
        /// Returns a list of papers that have no mapping.
        /// </summary>
        public List<string> ValidateMappings(string testKitFolder, Dictionary<string, string> mapping, IEnumerable<string> paperNumbers)
        {
            var unmappedPapers = new List<string>();

            foreach (var paperNo in paperNumbers.Distinct())
            {
                var testKitPath = GetTestKitForPaper(testKitFolder, paperNo, mapping);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    unmappedPapers.Add(paperNo);
                }
            }

            return unmappedPapers;
        }
    }

    /// <summary>
    /// Configuration data loaded from testkit Header.xlsx.
    /// </summary>
    public class TestKitConfig
    {
        public Dictionary<string, double> PointAllocation { get; set; } = new();
        public double TotalMaxMark { get; set; }
        public int CodeContainerInternalPort { get; set; } = 5000;
        public int CodeContainerHostPort { get; set; } = 5000;
        public string DatabaseImageName { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";
        public string DatabaseContainerName { get; set; } = "ag-database";
        public int DatabaseContainerInternalPort { get; set; } = 1433;
        public int DatabaseContainerHostPort { get; set; } = 1433;
        public string DatabaseUsername { get; set; } = "sa";
        public string DatabasePassword { get; set; } = "YourStrong@Passw0rd";
    }

    /// <summary>
    /// Service for reading test kit configuration.
    /// </summary>
    public class TestKitConfigService
    {
        private readonly ILoggingService _logger;

        public TestKitConfigService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads test kit configuration from Header.xlsx.
        /// </summary>
        public TestKitConfig LoadTestKitConfig(string testKitPath)
        {
            var config = new TestKitConfig();
            var allocation = ReadPointAllocation(testKitPath);
            config.PointAllocation = allocation;
            config.TotalMaxMark = allocation.Values.Sum();
            return config;
        }

        /// <summary>
        /// Reads Header.xlsx to get point allocation.
        /// </summary>
        public Dictionary<string, double> ReadPointAllocation(string testKitPath)
        {
            var allocation = new Dictionary<string, double>();
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");

            if (!File.Exists(headerPath))
            {
                _logger.LogWarning($"Header.xlsx not found: {headerPath}");
                return allocation;
            }

            try
            {
                using var wb = new XLWorkbook(headerPath);
                var ws = wb.Worksheet(1);
                
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var tcName = row.Cell(1).GetString();
                    if (double.TryParse(row.Cell(2).GetString(), out var mark))
                    {
                        allocation[tcName] = mark;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to read Header.xlsx: {ex.Message}");
            }

            return allocation;
        }
    }

    /// <summary>
    /// Service that orchestrates grading operations.
    /// Integrates with GradingService for Docker-based grading.
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;
        private GradingServices.GradingService? _gradingService;

        public event EventHandler<StudentSolution>? StudentGradingStarted;
        public event EventHandler<StudentSolution>? StudentGradingCompleted;
        public event EventHandler<StudentSolution>? StudentProgressUpdated;
        public event EventHandler<GradingSessionState>? SessionStateChanged;

        public GradingOrchestrationService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Initializes the grading service with the configuration.
        /// </summary>
        public void Initialize(GradingConfiguration config)
        {
            try
            {
                // Create the Docker container service
                var containerService = new GradingServices.DockerContainerService(
                    networkName: "ag-network",
                    serverPort: 5000
                );

                // Create the network monitor (may fail if no sudo)
                NetworkMonitor.NetworkMonitorService? networkMonitor = null;
                try
                {
                    networkMonitor = new NetworkMonitor.NetworkMonitorService();
                    _logger.LogInfo("Network monitor initialized");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Network monitor initialization failed: {ex.Message}. Network checks will be skipped.");
                }

                // Create the grading service
                _gradingService = new GradingServices.GradingService(
                    containerService,
                    networkMonitor
                );

                _logger.LogInfo("Grading service initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize grading service: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Grades a single student using the GradingService from Lib.
        /// </summary>
        public async Task<bool> GradeStudentAsync(
            StudentSolution student,
            GradingConfiguration config,
            CancellationToken ct)
        {
            if (_gradingService == null)
            {
                Initialize(config);
            }

            try
            {
                student.Status = GradingStatus.InProgress;
                student.StartTime = DateTime.Now;
                StudentGradingStarted?.Invoke(this, student);

                _logger.LogInfo($"Starting grading for {student.StudentCode}");

                // Create progress reporter
                var progress = new Progress<string>(msg =>
                {
                    _logger.LogInfo(msg);
                    StudentProgressUpdated?.Invoke(this, student);
                });

                // Create grading configuration for the Lib service
                var gradingConfig = new GradingServices.GradingConfiguration
                {
                    SubmitFolderPath = config.SubmitFolderPath,
                    TestKitFolderPath = config.TestKitFolderPath,
                    SaveResultFolderPath = config.SaveResultFolderPath,
                    HasClient = config.HasClient,
                    HasServer = config.HasServer,
                    ClientProjectName = config.ClientProjectName,
                    ServerProjectName = config.ServerProjectName,
                    ServerPort = config.ServerPort,
                    PaperToTestKitMapping = config.PaperToTestKitMapping
                };

                // Call the Lib GradingService (5 args: studentCode, paperNo, config, progress, ct)
                var result = await _gradingService!.GradeStudentAsync(
                    student.StudentCode,
                    student.PaperNo,
                    gradingConfig,
                    progress,
                    ct
                );

                // Update student result from Lib result
                student.Status = result.Success ? GradingStatus.Success : GradingStatus.Failed;
                student.Score = result.TotalPointsAwarded;
                student.MaxScore = result.TotalPointsPossible;
                student.EndTime = DateTime.Now;
                student.Message = result.ErrorMessage ?? (result.Success ? "All tests passed" : "Some tests failed");

                StudentGradingCompleted?.Invoke(this, student);
                return result.Success;
            }
            catch (OperationCanceledException)
            {
                student.Status = GradingStatus.Paused;
                student.Message = "Grading cancelled";
                return false;
            }
            catch (Exception ex)
            {
                student.Status = GradingStatus.Failed;
                student.Message = ex.Message;
                student.EndTime = DateTime.Now;
                _logger.LogError($"Grading failed for {student.StudentCode}: {ex.Message}");
                StudentGradingCompleted?.Invoke(this, student);
                return false;
            }
        }

        /// <summary>
        /// Grades multiple students.
        /// </summary>
        public async Task GradeStudentsAsync(
            IEnumerable<StudentSolution> students,
            GradingConfiguration config,
            CancellationToken ct)
        {
            // Initialize grading service once
            if (_gradingService == null)
            {
                Initialize(config);
            }

            SessionStateChanged?.Invoke(this, new GradingSessionState { IsRunning = true });

            foreach (var student in students)
            {
                if (ct.IsCancellationRequested)
                {
                    SessionStateChanged?.Invoke(this, new GradingSessionState { IsRunning = false });
                    return;
                }

                await GradeStudentAsync(student, config, ct);
            }

            SessionStateChanged?.Invoke(this, new GradingSessionState { IsRunning = false });
        }

        /// <summary>
        /// Starts grading for the given students (compatible with existing UI code).
        /// </summary>
        public async Task StartGradingAsync(
            List<StudentSolution> students,
            GradingConfiguration config,
            GradingSessionState sessionState)
        {
            sessionState.IsRunning = true;
            using var cts = new CancellationTokenSource();
            await GradeStudentsAsync(students, config, cts.Token);
            sessionState.IsRunning = false;
        }

        /// <summary>
        /// Pauses the current grading session.
        /// </summary>
        public void PauseGrading()
        {
            _logger.LogInfo("Grading paused");
        }

        /// <summary>
        /// Pauses the current grading session with state update.
        /// </summary>
        public void PauseGrading(GradingSessionState sessionState)
        {
            sessionState.IsPaused = true;
            _logger.LogInfo("Grading paused");
        }

        /// <summary>
        /// Resumes the current grading session.
        /// </summary>
        public void ResumeGrading()
        {
            _logger.LogInfo("Grading resumed");
        }

        /// <summary>
        /// Resumes the current grading session with state update.
        /// </summary>
        public void ResumeGrading(GradingSessionState sessionState)
        {
            sessionState.IsPaused = false;
            _logger.LogInfo("Grading resumed");
        }

        /// <summary>
        /// Resets all student statuses to Not_Run.
        /// </summary>
        public void ResetAllStatuses(IEnumerable<StudentSolution> students)
        {
            foreach (var student in students)
            {
                student.Status = GradingStatus.Not_Run;
                student.Score = 0;
                student.MaxScore = 0;
                student.Message = string.Empty;
                student.ProgressPercent = 0;
            }
            _logger.LogInfo("All student statuses reset");
        }

        /// <summary>
        /// Resets all student statuses with session state update.
        /// </summary>
        public void ResetAllStatuses(IEnumerable<StudentSolution> students, GradingSessionState sessionState)
        {
            ResetAllStatuses(students);
            sessionState.Reset();
        }

        /// <summary>
        /// Disposes a student's grading resources.
        /// </summary>
        public void DisposeStudent(StudentSolution student)
        {
            student.Status = GradingStatus.Disposed;
            _logger.LogInfo($"Disposed student {student.StudentCode}");
        }

        /// <summary>
        /// Discovers students from the configuration.
        /// </summary>
        public List<StudentSolution> DiscoverStudents(GradingConfiguration config)
        {
            var discoveryService = new StudentDiscoveryService(_logger);
            return discoveryService.DiscoverStudents(config.SubmitFolderPath);
        }
    }

    /// <summary>
    /// Service for writing grading results.
    /// </summary>
    public class ResultWriterService
    {
        private readonly ILoggingService _logger;
        private readonly string _outputFolder;

        public ResultWriterService(ILoggingService logger, string outputFolder)
        {
            _logger = logger;
            _outputFolder = outputFolder;
            Directory.CreateDirectory(_outputFolder);
        }

        /// <summary>
        /// Writes overall summary for all students.
        /// </summary>
        public void WriteStudentsSolution(IEnumerable<StudentSolution> students, string paperNo)
        {
            try
            {
                var filePath = Path.Combine(_outputFolder, paperNo, "StudentsSolution.xlsx");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Sheet1");

                // Header
                ws.Cell(1, 1).Value = "No";
                ws.Cell(1, 2).Value = "StudentCode";
                ws.Cell(1, 3).Value = "ExamPaper";
                ws.Cell(1, 4).Value = "Status";
                ws.Cell(1, 5).Value = "FinalResult";
                ws.Cell(1, 6).Value = "StartDate";
                ws.Cell(1, 7).Value = "EndDate";
                ws.Row(1).Style.Font.Bold = true;

                int row = 2;
                foreach (var student in students)
                {
                    ws.Cell(row, 1).Value = row - 1;
                    ws.Cell(row, 2).Value = student.StudentCode;
                    ws.Cell(row, 3).Value = student.PaperNo;
                    ws.Cell(row, 4).Value = student.Status.ToString();
                    ws.Cell(row, 5).Value = student.Score;
                    ws.Cell(row, 6).Value = student.StartTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";
                    ws.Cell(row, 7).Value = student.EndTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";
                    row++;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(filePath);

                _logger.LogInfo($"Written StudentsSolution.xlsx to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write StudentsSolution.xlsx: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes summary for all students (single output file for all papers).
        /// </summary>
        public void WriteStudentsSolutionSummary(IEnumerable<StudentSolution> students)
        {
            try
            {
                var filePath = Path.Combine(_outputFolder, "StudentsSolution.xlsx");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                using var wb = new XLWorkbook();
                var ws = wb.AddWorksheet("Sheet1");

                // Header
                ws.Cell(1, 1).Value = "No";
                ws.Cell(1, 2).Value = "StudentCode";
                ws.Cell(1, 3).Value = "ExamPaper";
                ws.Cell(1, 4).Value = "Status";
                ws.Cell(1, 5).Value = "FinalResult";
                ws.Cell(1, 6).Value = "StartDate";
                ws.Cell(1, 7).Value = "EndDate";
                ws.Row(1).Style.Font.Bold = true;

                int row = 2;
                foreach (var student in students)
                {
                    ws.Cell(row, 1).Value = row - 1;
                    ws.Cell(row, 2).Value = student.StudentCode;
                    ws.Cell(row, 3).Value = student.PaperNo;
                    ws.Cell(row, 4).Value = student.Status.ToString();
                    ws.Cell(row, 5).Value = student.Score;
                    ws.Cell(row, 6).Value = student.StartTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";
                    ws.Cell(row, 7).Value = student.EndTime?.ToString("dd-MM-yyyy HH:mm:ss") ?? "";
                    row++;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(filePath);

                _logger.LogInfo($"Written StudentsSolution.xlsx to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write StudentsSolution.xlsx: {ex.Message}");
            }
        }
    }
}
