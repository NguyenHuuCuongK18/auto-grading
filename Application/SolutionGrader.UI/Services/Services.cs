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
    }

    /// <summary>
    /// Logging service for grading operations.
    /// </summary>
    public class LoggingService : ILoggingService
    {
        private readonly string _logFolder;

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
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;

        public event EventHandler<StudentSolution>? StudentGradingStarted;
        public event EventHandler<StudentSolution>? StudentGradingCompleted;
        public event EventHandler<(StudentSolution Student, string Message)>? StudentProgressUpdated;
        public event EventHandler<GradingSessionState>? SessionStateChanged;

        public GradingOrchestrationService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Grades a single student.
        /// </summary>
        public async Task<bool> GradeStudentAsync(
            StudentSolution student,
            GradingConfiguration config,
            CancellationToken ct)
        {
            try
            {
                student.Status = GradingStatus.InProgress;
                student.StartTime = DateTime.Now;
                StudentGradingStarted?.Invoke(this, student);

                _logger.LogInfo($"Starting grading for {student.StudentCode}");
                StudentProgressUpdated?.Invoke(this, (student, "Initializing..."));

                // TODO: Integrate with GradingService from GradingServices library
                // For now, simulate grading
                await Task.Delay(2000, ct);

                student.Status = GradingStatus.Success;
                student.Score = 1.0;
                student.MaxScore = 1.0;
                student.EndTime = DateTime.Now;
                student.Message = "Grading completed";

                StudentGradingCompleted?.Invoke(this, student);
                return true;
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
            SessionStateChanged?.Invoke(this, GradingSessionState.Running);

            foreach (var student in students)
            {
                if (ct.IsCancellationRequested)
                {
                    SessionStateChanged?.Invoke(this, GradingSessionState.Cancelled);
                    return;
                }

                await GradeStudentAsync(student, config, ct);
            }

            SessionStateChanged?.Invoke(this, GradingSessionState.Completed);
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
    }
}
