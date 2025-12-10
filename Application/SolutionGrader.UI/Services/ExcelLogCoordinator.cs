using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Centralized coordinator for Excel log file operations.
    /// 
    /// PROBLEM: In batch grading, multiple containers/processes finishing at different times
    /// all try to write StudentsSolution.xlsx, causing file overwrites and data loss.
    /// 
    /// SOLUTION: Single coordinator that:
    /// 1. Pre-populates Excel file with all known students at START of grading
    /// 2. Fills in predetermined info: No, StudentCode, Paper, Possible Points, Start Time
    /// 3. BATCHES updates: Collects multiple student updates and writes them in one operation
    /// 4. Uses file locking to ensure thread-safe updates
    /// 5. Maintains in-memory cache for fast lookups
    /// 
    /// OPTIMIZATION: Instead of opening/saving Excel file for EACH student completion,
    /// we batch updates together and write them all at once every 2 seconds or when
    /// forced to flush. This eliminates the "constant override" problem and reduces lag.
    /// </summary>
    public class ExcelLogCoordinator : IDisposable
    {
        private readonly string _baseResultPath;
        private readonly ILoggingService _logger;
        private readonly object _fileLock = new object();
        private readonly ConcurrentDictionary<string, StudentRowInfo> _studentRowMap = new();
        private readonly ConcurrentDictionary<string, PendingUpdate> _pendingUpdates = new();
        private System.Threading.Timer? _batchTimer;
        private bool _isInitialized = false;
        private bool _disposed = false;
        
        /// <summary>
        /// Represents a pending update to be batched.
        /// </summary>
        private class PendingUpdate
        {
            public string StudentCode { get; set; } = "";
            public string PaperNo { get; set; } = "";
            public Action<IXLRow> UpdateAction { get; set; } = null!;
        }

        /// <summary>
        /// Tracks row information for each student in the Excel file.
        /// </summary>
        private class StudentRowInfo
        {
            public int RowNumber { get; set; }
            public string StudentCode { get; set; } = "";
            public string PaperNo { get; set; } = "";
            public double PossiblePoints { get; set; }
        }

        public ExcelLogCoordinator(ILoggingService logger, string baseResultPath)
        {
            _logger = logger;
            _baseResultPath = baseResultPath;

            // Ensure result directory exists
            if (!Directory.Exists(_baseResultPath))
            {
                Directory.CreateDirectory(_baseResultPath);
            }
        }

        /// <summary>
        /// Initializes the Excel file with all students at the START of grading.
        /// Pre-fills: No, StudentCode, Paper, Possible Points (from test kit), Start Time
        /// 
        /// This is called ONCE when grading starts, before any containers are launched.
        /// </summary>
        /// <param name="students">Complete list of students to be graded</param>
        /// <param name="testKitMaxMarks">Dictionary mapping paper number to max possible marks</param>
        public void InitializeExcelFile(List<StudentSolution> students, Dictionary<string, double> testKitMaxMarks)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("ExcelLogCoordinator already initialized, skipping");
                return;
            }

            lock (_fileLock)
            {
                try
                {
                    _logger.LogInfo($"[ExcelLogCoordinator] Initializing StudentsSolution.xlsx with {students.Count} students");

                    var filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");

                    // Create new workbook
                    using var workbook = new XLWorkbook();
                    var worksheet = workbook.Worksheets.Add("Sheet1");

                    // Header row
                    worksheet.Cell(1, 1).Value = "No";
                    worksheet.Cell(1, 2).Value = "StudentCode";
                    worksheet.Cell(1, 3).Value = "ExamPaper";
                    worksheet.Cell(1, 4).Value = "PossiblePoints";
                    worksheet.Cell(1, 5).Value = "EarnedPoints";
                    worksheet.Cell(1, 6).Value = "Status";
                    worksheet.Cell(1, 7).Value = "StartTime";
                    worksheet.Cell(1, 8).Value = "EndTime";
                    worksheet.Cell(1, 9).Value = "Duration";
                    worksheet.Cell(1, 10).Value = "ServerIP";
                    worksheet.Cell(1, 11).Value = "ServerPort";
                    worksheet.Cell(1, 12).Value = "ClientIP";
                    worksheet.Cell(1, 13).Value = "ClientPort";
                    worksheet.Cell(1, 14).Value = "ServerDLL";
                    worksheet.Cell(1, 15).Value = "ClientDLL";
                    worksheet.Cell(1, 16).Value = "DllModUsed";

                    // Style header
                    var headerRow = worksheet.Row(1);
                    headerRow.Style.Font.Bold = true;
                    headerRow.Style.Fill.BackgroundColor = XLColor.LightBlue;

                    // Pre-populate with all students
                    int row = 2;
                    int no = 1;
                    foreach (var student in students.OrderBy(s => int.TryParse(s.PaperNo, out var n) ? n : 0).ThenBy(s => s.StudentCode))
                    {
                        // Get possible points for this paper from test kit
                        var possiblePoints = testKitMaxMarks.TryGetValue(student.PaperNo, out var points) ? points : 0.0;

                        // Pre-fill known information
                        worksheet.Cell(row, 1).Value = no.ToString();
                        worksheet.Cell(row, 2).Value = student.StudentCode;
                        worksheet.Cell(row, 3).Value = student.PaperNo;
                        worksheet.Cell(row, 4).Value = possiblePoints.ToString("0.##");
                        worksheet.Cell(row, 5).Value = "0.00"; // Placeholder for earned points
                        worksheet.Cell(row, 6).Value = "Not Started"; // Initial status
                        worksheet.Cell(row, 7).Value = ""; // Start time (filled when grading starts)
                        worksheet.Cell(row, 8).Value = ""; // End time (filled when grading completes)
                        worksheet.Cell(row, 9).Value = ""; // Duration
                        worksheet.Cell(row, 10).Value = ""; // ServerIP (filled when grading starts)
                        worksheet.Cell(row, 11).Value = ""; // ServerPort (filled when grading starts)
                        worksheet.Cell(row, 12).Value = ""; // ClientIP (filled when grading starts)
                        worksheet.Cell(row, 13).Value = ""; // ClientPort (filled when grading starts)
                        worksheet.Cell(row, 14).Value = ""; // ServerDLL (filled when grading starts)
                        worksheet.Cell(row, 15).Value = ""; // ClientDLL (filled when grading starts)
                        worksheet.Cell(row, 16).Value = ""; // DllModUsed (filled when grading starts)

                        // Track row mapping for quick updates
                        _studentRowMap[GetStudentKey(student.StudentCode, student.PaperNo)] = new StudentRowInfo
                        {
                            RowNumber = row,
                            StudentCode = student.StudentCode,
                            PaperNo = student.PaperNo,
                            PossiblePoints = possiblePoints
                        };

                        row++;
                        no++;
                    }

                    // Auto-fit columns
                    worksheet.Columns().AdjustToContents();

                    // Save to disk
                    workbook.SaveAs(filePath);

                    _isInitialized = true;
                    _logger.LogInfo($"[ExcelLogCoordinator] Initialized StudentsSolution.xlsx with {no - 1} students");
                }
                catch (Exception ex)
                {
                    _logger.LogError("[ExcelLogCoordinator] Failed to initialize Excel file", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Updates a single student's start time when grading begins.
        /// This is called when a student's grading starts (container setup complete).
        /// </summary>
        public void UpdateStudentStarted(string studentCode, string paperNo, DateTime startTime)
        {
            if (!_isInitialized)
            {
                _logger.LogWarning($"[ExcelLogCoordinator] [{studentCode}] Excel not initialized, cannot update start time");
                return;
            }

            UpdateStudentRow(studentCode, paperNo, row =>
            {
                row.Cell(6).Value = "In Progress";
                row.Cell(7).Value = startTime.ToString("yyyy-MM-dd HH:mm:ss");
                row.Style.Fill.BackgroundColor = XLColor.LightYellow;
            });

            _logger.LogInfo($"[ExcelLogCoordinator] [{studentCode}] Updated start time: {startTime:yyyy-MM-dd HH:mm:ss}");
        }

        /// <summary>
        /// Updates a single student's configuration when grading configuration is determined.
        /// This is called after containers are set up and before actual test execution begins.
        /// Records the actual IP addresses, ports, and DLL paths being used for debugging.
        /// </summary>
        public void UpdateStudentConfiguration(
            string studentCode, 
            string paperNo, 
            string serverIP, 
            int serverPort,
            string clientIP,
            int clientPort,
            string serverDllPath,
            string clientDllPath,
            bool dllModUsed)
        {
            if (!_isInitialized)
            {
                _logger.LogWarning($"[ExcelLogCoordinator] [{studentCode}] Excel not initialized, cannot update configuration");
                return;
            }

            UpdateStudentRow(studentCode, paperNo, row =>
            {
                row.Cell(10).Value = serverIP;
                row.Cell(11).Value = serverPort.ToString();
                row.Cell(12).Value = clientIP;
                row.Cell(13).Value = clientPort.ToString();
                row.Cell(14).Value = Path.GetFileName(serverDllPath ?? "N/A");
                row.Cell(15).Value = Path.GetFileName(clientDllPath ?? "N/A");
                row.Cell(16).Value = dllModUsed ? "Yes" : "No";
            });

            _logger.LogInfo($"[ExcelLogCoordinator] [{studentCode}] Updated configuration: Server={serverIP}:{serverPort}, Client={clientIP}:{clientPort}, DllMod={dllModUsed}");
        }

        /// <summary>
        /// Updates a single student's completion info when grading finishes.
        /// This is called when a student's grading completes (container cleaned up).
        /// </summary>
        public void UpdateStudentCompleted(string studentCode, string paperNo, DateTime endTime, double earnedPoints, GradingStatus status)
        {
            if (!_isInitialized)
            {
                _logger.LogWarning($"[ExcelLogCoordinator] [{studentCode}] Excel not initialized, cannot update completion");
                return;
            }

            UpdateStudentRow(studentCode, paperNo, row =>
            {
                var startTimeCell = row.Cell(7);
                var startTimeStr = startTimeCell.GetValue<string>();
                
                // Update earned points and status
                row.Cell(5).Value = earnedPoints.ToString("0.##");
                row.Cell(6).Value = GetStatusDisplay(status);
                row.Cell(8).Value = endTime.ToString("yyyy-MM-dd HH:mm:ss");

                // Calculate duration if start time exists
                if (!string.IsNullOrEmpty(startTimeStr) && DateTime.TryParse(startTimeStr, out var startTime))
                {
                    var duration = endTime - startTime;
                    row.Cell(9).Value = $"{duration.TotalSeconds:F2}s";
                }

                // Color code row based on status
                switch (status)
                {
                    case GradingStatus.Success:
                        row.Style.Fill.BackgroundColor = XLColor.LightGreen;
                        break;
                    case GradingStatus.Failed:
                        row.Style.Fill.BackgroundColor = XLColor.LightPink;
                        break;
                    default:
                        row.Style.Fill.BackgroundColor = XLColor.White;
                        break;
                }
            });

            _logger.LogInfo($"[ExcelLogCoordinator] [{studentCode}] Updated completion: {earnedPoints:F2} points, status: {status}");
        }

        /// <summary>
        /// Internal helper to update a student's row in the Excel file.
        /// Uses file locking to ensure thread-safe updates.
        /// </summary>
        private void UpdateStudentRow(string studentCode, string paperNo, Action<IXLRow> updateAction)
        {
            var key = GetStudentKey(studentCode, paperNo);
            if (!_studentRowMap.TryGetValue(key, out var rowInfo))
            {
                _logger.LogWarning($"[ExcelLogCoordinator] [{studentCode}] Student not found in row map, cannot update");
                return;
            }

            // OPTIMIZED: Queue update for batching instead of immediate write
            // This prevents the "constant override" problem and eliminates lag from
            // opening/saving the Excel file for every single student completion
            _pendingUpdates[key] = new PendingUpdate
            {
                StudentCode = studentCode,
                PaperNo = paperNo,
                UpdateAction = updateAction
            };
            
            // Start or reset batch timer (write after 2 seconds of inactivity)
            var oldTimer = Interlocked.Exchange(ref _batchTimer, 
                new System.Threading.Timer(_ => ProcessPendingUpdates(), null, 2000, Timeout.Infinite));
            oldTimer?.Dispose();
        }
        
        /// <summary>
        /// Processes all pending updates in a single batch operation.
        /// This opens the Excel file ONCE, applies ALL pending updates, then saves ONCE.
        /// Much more efficient than opening/saving for each individual student.
        /// </summary>
        private void ProcessPendingUpdates()
        {
            if (_pendingUpdates.IsEmpty) return;
            
            // Get all pending updates
            var updates = _pendingUpdates.ToArray();
            _pendingUpdates.Clear();
            
            if (updates.Length == 0) return;

            lock (_fileLock)
            {
                try
                {
                    var filePath = Path.Combine(_baseResultPath, "StudentsSolution.xlsx");
                    
                    _logger.LogInfo($"[ExcelLogCoordinator] Processing batch update for {updates.Length} student(s)");
                    
                    // Open existing workbook (with retry for file locks)
                    XLWorkbook workbook = null;
                    int retries = 3;
                    while (retries > 0)
                    {
                        try
                        {
                            workbook = new XLWorkbook(filePath);
                            break;
                        }
                        catch (IOException)
                        {
                            retries--;
                            if (retries > 0)
                            {
                                Thread.Sleep(100);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }

                    using (workbook)
                    {
                        var worksheet = workbook.Worksheet("Sheet1");

                        // Apply ALL pending updates in one pass
                        foreach (var update in updates)
                        {
                            var key = GetStudentKey(update.Value.StudentCode, update.Value.PaperNo);
                            if (_studentRowMap.TryGetValue(key, out var rowInfo))
                            {
                                var row = worksheet.Row(rowInfo.RowNumber);
                                update.Value.UpdateAction(row);
                            }
                        }

                        // Save back to disk ONCE for all updates
                        workbook.SaveAs(filePath);
                    }
                    
                    _logger.LogInfo($"[ExcelLogCoordinator] Batch update complete: {updates.Length} student(s) updated");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[ExcelLogCoordinator] Failed to process batch updates", ex);
                }
            }
        }
        
        /// <summary>
        /// Forces immediate processing of all pending updates.
        /// Call this when grading completes to ensure all data is written.
        /// </summary>
        public void Flush()
        {
            lock (_fileLock)
            {
                _batchTimer?.Dispose();
                _batchTimer = null;
            }
            
            ProcessPendingUpdates();
        }

        /// <summary>
        /// Gets a unique key for student lookup in the row map.
        /// </summary>
        private static string GetStudentKey(string studentCode, string paperNo)
        {
            return $"{paperNo}_{studentCode}";
        }

        /// <summary>
        /// Converts GradingStatus enum to display string.
        /// </summary>
        private static string GetStatusDisplay(GradingStatus status)
        {
            return status switch
            {
                GradingStatus.Not_Run => "Not Started",
                GradingStatus.InProgress => "In Progress",
                GradingStatus.Success => "Success",
                GradingStatus.Failed => "Failed",
                GradingStatus.Paused => "Paused",
                GradingStatus.Disposed => "Disposed",
                _ => status.ToString()
            };
        }

        /// <summary>
        /// Flushes any pending changes and closes the coordinator.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _logger.LogInfo("[ExcelLogCoordinator] Disposing coordinator - flushing pending updates");
            
            // Flush any pending updates before disposing
            Flush();
            
            _disposed = true;
        }
    }
}
