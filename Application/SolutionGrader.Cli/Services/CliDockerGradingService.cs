using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EnvironmentBuilder.DockerCommand;
using FileMaster.FileEngine;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Services;

namespace SolutionGrader.Cli.Services
{
    /// <summary>
    /// Docker-based grading orchestrator for the CLI.
    /// 
    /// CRITICAL: This service DELEGATES to the shared DockerGradingService from
    /// Lib/SolutionGrader.Core to ensure IDENTICAL grading behavior between CLI and UI.
    /// 
    /// This orchestrator handles:
    /// 1. Discover students from submit folder
    /// 2. Load test kit mapping from Mapping.xlsx
    /// 3. Delegate grading to shared DockerGradingService (same as UI)
    /// 4. Write summary results
    /// 
    /// The actual grading logic (containers, test execution, network monitoring, cleanup) 
    /// is handled by DockerGradingService which is SHARED with SolutionGrader.UI.
    /// </summary>
    public class CliDockerGradingService
    {
        private readonly DockerCommandExecutor _dockerExecutor;
        
        // OPTIMIZATION: Cache test kit paths to avoid repeated file system lookups
        // Mirrors the UI's _testKitCache optimization
        // Using ConcurrentDictionary for thread-safe access during parallel grading
        private readonly ConcurrentDictionary<string, string> _testKitPathCache = new ConcurrentDictionary<string, string>();
        
        // OPTIMIZATION: Cache starting ports from Environment.xlsx to avoid repeated Excel reads
        // Key: test kit path, Value: starting port
        // Using ConcurrentDictionary for thread-safe access during parallel grading
        private readonly ConcurrentDictionary<string, int> _startingPortCache = new ConcurrentDictionary<string, int>();

        public CliDockerGradingService()
        {
            _dockerExecutor = new DockerCommandExecutor();
        }

        /// <summary>
        /// Execute grading for students based on configuration.
        /// Supports parallel grading and index range selection.
        /// </summary>
        /// <param name="config">Grading configuration</param>
        /// <param name="paperFilter">Optional paper number filter</param>
        /// <param name="studentFilter">Optional student code filter</param>
        /// <returns>Exit code (0 = success, 1 = failure)</returns>
        public async Task<int> ExecuteAsync(CliGradingConfiguration config, string? paperFilter = null, string? studentFilter = null)
        {
            Console.WriteLine("[CLI] Starting Docker grading using SHARED DockerGradingService...");
            Console.WriteLine("[CLI] This ensures IDENTICAL behavior between CLI and UI.");
            Console.WriteLine();
            
            // OPTIMIZATION: Configure ThreadPool for optimal multi-threading performance
            ConfigureThreadPoolForParallelGrading(config.MaxParallelStudents);

            // CRITICAL: Clear all previously allocated ports at the START of a new grading session.
            // This prevents port exhaustion from previous runs while ensuring no port reuse
            // DURING the current session (which could cause race conditions in parallel grading).
            // 
            // The "never reuse" policy applies WITHIN a session - once a port is allocated
            // for this session, it stays allocated until the session ends. This prevents
            // the race condition where Student A finishes and releases port 8001, but the
            // system incorrectly reuses it while Student B is still being graded.
            Console.WriteLine("[CLI] Clearing port allocation from previous sessions...");
            PortAllocator.ClearAllAllocatedPorts();

            // Check if Docker is running
            if (!_dockerExecutor.IsDockerRunning())
            {
                Console.WriteLine("[ERROR] Docker is not running. Please start Docker and try again.");
                return 1;
            }

            // Discover students from submit folder
            var allStudents = DiscoverStudents(config.SubmitFolderPath, config, paperFilter, studentFilter);
            if (allStudents.Count == 0)
            {
                Console.WriteLine("[WARNING] No students found in submit folder.");
                return 0;
            }

            // Apply index range filtering
            var students = ApplyIndexRange(allStudents, config.StartIndex, config.EndIndex);
            if (students.Count == 0)
            {
                Console.WriteLine($"[WARNING] No students in the specified index range [{config.StartIndex}, {config.EndIndex}].");
                return 0;
            }

            Console.WriteLine($"[CLI] Found {allStudents.Count} student(s) total, grading {students.Count} student(s) in index range [{config.StartIndex}, {(config.EndIndex == -1 ? "end" : config.EndIndex.ToString())}].");
            Console.WriteLine($"[CLI] Parallel grading: {config.MaxParallelStudents} student(s) at a time.");
            Console.WriteLine($"[CLI] Port allocation: Ports are allocated once per student and NEVER reused during this session.");
            Console.WriteLine();

            // Create output directory
            Directory.CreateDirectory(config.SaveResultFolderPath);

            // Grade students using parallel or sequential execution
            var results = await GradeStudentsAsync(students, config);

            // Write overall summary
            await WriteOverallSummaryAsync(config.SaveResultFolderPath, results);

            Console.WriteLine();
            Console.WriteLine($"{'=',-60}");
            Console.WriteLine("[CLI] Grading Complete!");
            Console.WriteLine($"Total students: {results.Count}");
            Console.WriteLine($"Passed: {results.Count(r => r.Passed)}");
            Console.WriteLine($"Failed: {results.Count(r => !r.Passed)}");
            Console.WriteLine($"Results saved to: {config.SaveResultFolderPath}");
            Console.WriteLine($"{'=',-60}");

            return results.Any(r => r.Passed) ? 0 : 1;
        }

        /// <summary>
        /// Apply index range filtering to the student list.
        /// </summary>
        private List<StudentInfo> ApplyIndexRange(List<StudentInfo> students, int startIndex, int endIndex)
        {
            if (startIndex < 0) startIndex = 0;
            if (startIndex >= students.Count) return new List<StudentInfo>();
            
            if (endIndex == -1 || endIndex >= students.Count)
            {
                // Grade from startIndex to end
                return students.Skip(startIndex).ToList();
            }
            else
            {
                // Grade from startIndex to endIndex (inclusive)
                var count = endIndex - startIndex + 1;
                if (count <= 0) return new List<StudentInfo>();
                return students.Skip(startIndex).Take(count).ToList();
            }
        }

        /// <summary>
        /// Grade students either sequentially or in parallel based on configuration.
        /// 
        /// OPTIMIZATION: Uses producer-consumer pattern for TRUE CONTINUOUS BATCH PROCESSING.
        /// - OLD behavior: Batch size 10 = grade 10, wait for all 10 to finish, then start next 10
        /// - NEW behavior: Batch size 10 = always keep 10 students being graded at a time
        ///   - When Student 1 finishes, immediately start Student 11 (no waiting)
        ///   - When Student 2 finishes, immediately start Student 12 (no waiting)
        ///   - Maximum resource utilization, no idle containers
        /// 
        /// This is achieved using:
        /// - Channel<StudentInfo> as a queue of students waiting to be graded
        /// - Multiple worker tasks (up to MaxParallelStudents) continuously pulling from the queue
        /// - As soon as a worker finishes a student, it immediately pulls the next one
        /// - No batching delays, no Task.WhenAll() blocking
        /// 
        /// Each parallel student gets their own:
        /// - Unique container names (with student code suffix)
        /// - Dynamically allocated ports via PortAllocator (thread-safe)
        /// - Own database instance (same container, different database)
        /// - Own network monitor
        /// </summary>
        private async Task<List<StudentGradingResult>> GradeStudentsAsync(List<StudentInfo> students, CliGradingConfiguration config)
        {
            var results = new ConcurrentBag<StudentGradingResult>();
            var studentIndex = 0;

            if (config.MaxParallelStudents <= 1)
            {
                // Sequential grading (original behavior)
                foreach (var student in students)
                {
                    studentIndex++;
                    Console.WriteLine($"\n{'=',-60}");
                    Console.WriteLine($"[{studentIndex}/{students.Count}] Grading student: {student.StudentCode} (Paper {student.PaperNo})");
                    Console.WriteLine($"{'=',-60}");

                    var result = await GradeStudentUsingSharedServiceAsync(student, config, 0);
                    results.Add(result);

                    Console.WriteLine($"[CLI] Result: {(result.Passed ? "PASSED" : "FAILED")} - {result.TotalMark:F2}/{result.MaxMark:F2}");
                }
            }
            else
            {
                // OPTIMIZED: TRUE CONTINUOUS BATCH PROCESSING using producer-consumer pattern
                // This ensures MaxParallelStudents are ALWAYS being graded simultaneously
                // When one finishes, the next one starts immediately (no batch waiting)
                Console.WriteLine($"[Optimization] Using continuous batch processing: {config.MaxParallelStudents} students graded simultaneously at all times");
                Console.WriteLine($"[Optimization] When a student finishes, the next student starts immediately (no batch waiting)");
                Console.WriteLine($"[Multi-Threading] Using {config.MaxParallelStudents} worker threads across {Environment.ProcessorCount} CPU cores");
                
                // Create a channel to hold students waiting to be graded
                // OPTIMIZATION: Set capacity to 2x MaxParallelStudents to reduce producer-consumer coordination overhead
                // This allows producer to queue work ahead while workers are busy, reducing wait times
                var channelCapacity = Math.Max(config.MaxParallelStudents * 2, 10);
                var channel = Channel.CreateBounded<StudentInfo>(new BoundedChannelOptions(channelCapacity)
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
                        foreach (var student in students)
                        {
                            // WriteAsync with cancellation token for responsive shutdown
                            await channel.Writer.WriteAsync(student, CancellationToken.None);
                        }
                    }
                    finally
                    {
                        channel.Writer.Complete();
                    }
                });
                
                // Consumer tasks: Pull students from channel and grade them
                var workerTasks = new List<Task>();
                for (int workerId = 0; workerId < config.MaxParallelStudents; workerId++)
                {
                    var localWorkerId = workerId;
                    var workerTask = Task.Run(async () =>
                    {
                        // Each worker continuously pulls students from the queue until empty
                        await foreach (var student in channel.Reader.ReadAllAsync())
                        {
                            int currentIndex;
                            lock (completedLock)
                            {
                                currentIndex = completedCount + 1;
                            }
                            
                            Console.WriteLine($"\n[Worker-{localWorkerId}] [{currentIndex}/{students.Count}] Starting grading for: {student.StudentCode} (Paper {student.PaperNo})");
                            
                            // Grade the student (port allocation happens inside via PortAllocator)
                            var result = await GradeStudentUsingSharedServiceAsync(student, config, 0);
                            results.Add(result);
                            
                            lock (completedLock)
                            {
                                completedCount++;
                                currentIndex = completedCount;
                            }
                            
                            Console.WriteLine($"[Worker-{localWorkerId}] [{currentIndex}/{students.Count}] Completed: {student.StudentCode} - {(result.Passed ? "PASSED" : "FAILED")} - {result.TotalMark:F2}/{result.MaxMark:F2}");
                            Console.WriteLine($"[Progress] {completedCount}/{students.Count} students completed, {Math.Min(config.MaxParallelStudents, students.Count - completedCount)} students currently in progress");
                        }
                    });
                    workerTasks.Add(workerTask);
                }
                
                // Wait for producer and all workers to complete
                await producerTask;
                await Task.WhenAll(workerTasks);
                
                Console.WriteLine($"\n[Optimization] Continuous batch processing complete: All {students.Count} students graded with maximum efficiency");
            }

            // Convert ConcurrentBag to List and sort by original order
            // OPTIMIZATION: Use dictionary for O(n) lookup instead of O(n²) FindIndex
            // Single-pass conversion and ordering to avoid double list allocation
            var studentIndexMap = students.Select((s, i) => new { s.StudentCode, Index = i })
                .ToDictionary(x => x.StudentCode, x => x.Index);
            
            var resultsList = results
                .OrderBy(r => studentIndexMap.TryGetValue(r.StudentCode, out var idx) ? idx : int.MaxValue)
                .ToList();
            
            return resultsList;
        }

        /// <summary>
        /// Grade a single student using the SHARED DockerGradingService.
        /// This ensures identical grading logic between CLI and UI.
        /// </summary>
        /// <param name="portOffset">Port offset for parallel grading (0 for sequential) - DEPRECATED, now uses PortAllocator</param>
        private async Task<StudentGradingResult> GradeStudentUsingSharedServiceAsync(StudentInfo student, CliGradingConfiguration config, int portOffset)
        {
            var result = new StudentGradingResult
            {
                StudentCode = student.StudentCode,
                PaperNo = student.PaperNo
            };

            try
            {
                // OPTIMIZATION: Use cached test kit path to avoid repeated file system lookups
                // This mirrors the UI's test kit caching optimization
                if (!_testKitPathCache.TryGetValue(student.PaperNo, out var testKitPath))
                {
                    testKitPath = GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                    if (!string.IsNullOrEmpty(testKitPath))
                    {
                        _testKitPathCache[student.PaperNo] = testKitPath;
                    }
                }
                
                if (string.IsNullOrEmpty(testKitPath))
                {
                    Console.WriteLine($"[WARNING] No test kit found for paper {student.PaperNo}");
                    result.ErrorMessage = $"No test kit for paper {student.PaperNo}";
                    return result;
                }

                // LAZY EXTRACTION: Extract zip file only when grading this student (not during discovery)
                // This prevents extracting ALL students when we only need to grade a subset
                var questionFolder = Path.GetDirectoryName(student.SolutionPath)!;
                bool solutionReady = SharedDiscoveryServices.EnsureSolutionExtracted(
                    questionFolder, 
                    msg => Console.WriteLine($"[{student.StudentCode}] {msg}")
                );
                
                if (!solutionReady)
                {
                    Console.WriteLine($"[ERROR] Failed to extract or find solution for {student.StudentCode}");
                    result.ErrorMessage = "Failed to extract or find solution";
                    return result;
                }
                
                // NOW find DLLs after ensuring solution is extracted
                student.ServerDllPath = FindDll(student.SolutionPath, config.ServerProjectName);
                student.ClientDllPath = FindDll(student.SolutionPath, config.ClientProjectName);
                
                // Validate at least one component exists
                if (string.IsNullOrEmpty(student.ServerDllPath) && string.IsNullOrEmpty(student.ClientDllPath))
                {
                    Console.WriteLine($"[ERROR] No DLLs found for {student.StudentCode} after extraction");
                    result.ErrorMessage = "No DLLs found in solution";
                    return result;
                }
                
                // Log warnings for missing expected DLLs (only errors, not successes)
                if (student.ServerDllPath == null && !string.IsNullOrEmpty(config.ServerProjectName))
                {
                    Console.WriteLine($"[WARNING] Student {student.StudentCode} - Expected server DLL '{config.ServerProjectName}.dll' not found in solution folder");
                }
                if (student.ClientDllPath == null && !string.IsNullOrEmpty(config.ClientProjectName))
                {
                    Console.WriteLine($"[WARNING] Student {student.StudentCode} - Expected client DLL '{config.ClientProjectName}.dll' not found in solution folder");
                }

                // OPTIMIZATION: Cache starting port from Environment.xlsx to avoid repeated Excel reads
                // This is a significant performance improvement for batch grading of students with the same paper
                if (!_startingPortCache.TryGetValue(testKitPath, out var startingPortFromEnv))
                {
                    // CRITICAL: Read the starting port from test kit's Environment.xlsx BEFORE creating PortAllocator
                    // This ensures PortAllocator starts from the correct base port specified in the test kit,
                    // not the hardcoded default of 8000.
                    //
                    // For example:
                    // - If Environment.xlsx specifies port 4001, PortAllocator allocates: 4001, 4002, 4003, ...
                    // - If Environment.xlsx specifies port 8000, PortAllocator allocates: 8000, 8001, 8002, ...
                    // - If Environment.xlsx is missing or doesn't specify port, PortAllocator defaults to: 8000, 8001, 8002, ...
                    //
                    // This ensures consistency between:
                    // 1. Container port binding (uses allocated port)
                    // 2. DLL modification (uses allocated port via DockerGradingConfig)
                    // 3. Network monitoring (uses allocated port via DockerGradingConfig)
                    startingPortFromEnv = ReadStartingPortFromEnvironmentXlsx(testKitPath);
                    _startingPortCache[testKitPath] = startingPortFromEnv;
                }
                
                // CRITICAL: Allocate a port dynamically using PortAllocator (thread-safe for parallel grading)
                // NEVER RE-USE POLICY: Each student gets the next sequential port (N, N+1, N+2, ...)
                // Once allocated, a port is NEVER recycled during or between sessions (unless manually cleared)
                // This prevents race conditions where a port could be reused while still in use by another student
                //
                // Pass the starting port from Environment.xlsx to ensure consistent port allocation
                // Based on test-grader reference: https://github.com/NguyenHuuCuongK18/test-grader.git
                using var portAllocator = new PortAllocator(startingPortFromEnv);
                int allocatedPort = portAllocator.AllocatePort();
                
                if (allocatedPort == -1)
                {
                    Console.WriteLine($"[ERROR] Failed to allocate port for student {student.StudentCode}");
                    result.ErrorMessage = "Failed to allocate port for grading";
                    return result;
                }

                // Create student result path - simplified to: {saveResultFolder}/{studentCode}
                // This matches the UI's simplified structure
                var studentResultPath = Path.Combine(config.SaveResultFolderPath, student.StudentCode);
                Directory.CreateDirectory(studentResultPath);

                // Build DockerGradingConfig with DYNAMICALLY ALLOCATED port
                // Internal and external ports MUST MATCH for direct mapping (critical for network monitoring)
                // Example: Student 1 gets port 8000 (8000:8000), Student 2 gets port 8001 (8001:8001)
                var dockerConfig = new DockerGradingConfig
                {
                    HasClient = config.HasClient,
                    HasServer = config.HasServer,
                    ClientProjectName = config.ClientProjectName,
                    ServerProjectName = config.ServerProjectName,
                    // Use dynamically allocated port for DIRECT MAPPING (internal:external)
                    // This ensures each student's client reaches their own server via host.docker.internal
                    CodeContainerInternalPort = allocatedPort,
                    CodeContainerHostPort = allocatedPort,
                    DockerNetwork = config.DockerNetwork,
                    DatabaseImageName = config.DatabaseImageName,
                    // Use same database container name for all students (shared container, different database instances)
                    DatabaseContainerName = config.DatabaseContainerName,
                    DatabaseContainerInternalPort = config.DatabaseContainerInternalPort,
                    DatabaseContainerHostPort = config.DatabaseContainerHostPort,
                    DatabaseUsername = config.DatabaseUsername,
                    DatabasePassword = config.DatabasePassword,
                    GradingTimeoutSeconds = config.GradingTimeoutSeconds,
                    TestCaseTimeoutSeconds = config.TestCaseTimeoutSeconds,
                    
                    // CRITICAL: Enable DLL modification for batch grading
                    // This patches hardcoded ports (4000, 5000, etc.) to allocated port (8000, 8001, 8002)
                    // Without this, students' hardcoded ports won't match container exposed ports
                    UseDllModificationFallback = config.UseDllModificationFallback
                };

                Console.WriteLine($"[{student.StudentCode}] Using dynamically allocated port: {allocatedPort}");

                // Create the SHARED services (same as SolutionGrader.UI)
                IRunContext runContext = new RunContext();
                INetworkMonitorService networkMonitor = new NetworkMonitorService(runContext);

                // Create the SHARED DockerGradingService
                var dockerGradingService = new DockerGradingService(networkMonitor, runContext);

                // Subscribe to progress events
                dockerGradingService.ProgressUpdated += (sender, args) =>
                    Console.WriteLine($"  [{student.StudentCode}] {args.Message}");

                // Reset database for this student (ensures clean state)
                // For parallel grading, this creates a separate database instance in the shared container
                await dockerGradingService.ResetDatabaseForNewStudentAsync(dockerConfig);

                // Grade the student using the SHARED service
                var dockerResult = await dockerGradingService.GradeStudentAsync(
                    dockerConfig,
                    testKitPath,
                    studentResultPath,
                    student.ServerDllPath,
                    student.ClientDllPath,
                    student.StudentCode,
                    CancellationToken.None);

                // Convert DockerGradingResult to StudentGradingResult
                result.TotalMark = dockerResult.TotalMark;
                result.MaxMark = dockerResult.MaxMark;
                result.Passed = dockerResult.Passed;
                result.ErrorMessage = dockerResult.ErrorMessage;

                // Convert test case results
                foreach (var tcResult in dockerResult.TestCaseResults)
                {
                    result.TestCaseResults.Add(new TestCaseResult
                    {
                        TestCaseName = tcResult.TestCaseName,
                        EarnedMark = tcResult.EarnedMark,
                        MaxMark = tcResult.MaxMark,
                        Passed = tcResult.Passed,
                        ErrorMessage = tcResult.ErrorMessage
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Grading failed for {student.StudentCode}: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        #region Student Discovery

        /// <summary>
        /// Discover students from the submit folder.
        /// </summary>
        private List<StudentInfo> DiscoverStudents(string submitPath, CliGradingConfiguration config, string? paperFilter, string? studentFilter)
        {
            var students = new List<StudentInfo>();

            if (!Directory.Exists(submitPath))
            {
                Console.WriteLine($"[ERROR] Submit folder not found: {submitPath}");
                return students;
            }

            // Get paper folders (numbered folders like "1", "2", etc.)
            var paperDirs = Directory.GetDirectories(submitPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .OrderBy(d => int.Parse(Path.GetFileName(d)!));

            foreach (var paperDir in paperDirs)
            {
                var paperNo = Path.GetFileName(paperDir);
                if (!string.IsNullOrEmpty(paperFilter) && paperNo != paperFilter)
                    continue;

                // Get student folders
                var studentDirs = Directory.GetDirectories(paperDir)
                    .Where(d => !Path.GetFileName(d)!.Contains("."))
                    .OrderBy(d => d);

                foreach (var studentDir in studentDirs)
                {
                    var studentCode = Path.GetFileName(studentDir);
                    if (!string.IsNullOrEmpty(studentFilter) && studentCode != studentFilter)
                        continue;

                    // OPTIMIZED: Don't extract during discovery - only check if solution exists or zip exists
                    // This prevents extracting ALL students when we only need to grade a subset (based on index range)
                    // Extraction will happen lazily when grading each student (in GradeStudentUsingSharedServiceAsync)
                    
                    var questionFolder = Path.Combine(studentDir, "1");
                    if (!Directory.Exists(questionFolder))
                    {
                        Console.WriteLine($"[WARNING] No question folder for {studentCode}");
                        continue;
                    }
                    
                    var solutionPath = Path.Combine(questionFolder, "solution");
                    bool hasSolutionFolder = Directory.Exists(solutionPath);
                    bool hasZipFile = Directory.GetFiles(questionFolder, "*.zip").Length > 0;
                    
                    if (!hasSolutionFolder && !hasZipFile)
                    {
                        Console.WriteLine($"[WARNING] No solution folder and no zip file for {studentCode}");
                        continue;
                    }
                    
                    // Don't find DLLs during discovery - this would require extraction for all students
                    // DLLs will be found during grading when solution is ensured to be extracted
                    students.Add(new StudentInfo
                    {
                        StudentCode = studentCode!,
                        PaperNo = paperNo!,
                        SolutionPath = solutionPath,
                        ServerDllPath = null,  // Will be found during grading after extraction
                        ClientDllPath = null   // Will be found during grading after extraction
                    });
                }
            }

            return students;
        }

        /// <summary>
        /// Find a DLL file for a given project name.
        /// </summary>
        private string? FindDll(string solutionPath, string projectName)
        {
            // Common folder patterns
            var patterns = new[]
            {
                $"{projectName}*",
                $"Q*_{projectName}*",
                $"*{projectName}*"
            };

            foreach (var pattern in patterns)
            {
                var folders = Directory.GetDirectories(solutionPath, pattern, SearchOption.TopDirectoryOnly);
                foreach (var folder in folders)
                {
                    // Look for the main DLL
                    var dllPath = Path.Combine(folder, $"{projectName}.dll");
                    if (File.Exists(dllPath))
                        return dllPath;

                    // Search recursively but skip runtimes folder
                    var dlls = Directory.GetFiles(folder, $"{projectName}.dll", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                        .ToList();

                    if (dlls.Count > 0)
                        return dlls[0];
                }
            }

            // Try Q11/Q12 patterns (common exam patterns)
            var qFolder = projectName.Replace("Project", "Q");
            var qFolders = Directory.GetDirectories(solutionPath, $"{qFolder}*", SearchOption.TopDirectoryOnly);
            foreach (var folder in qFolders)
            {
                var dlls = Directory.GetFiles(folder, $"{projectName}.dll", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar))
                    .ToList();

                if (dlls.Count > 0)
                    return dlls[0];
            }

            return null;
        }

        #endregion

        #region Test Kit Loading

        /// <summary>
        /// Get the test kit path for a specific paper using Mapping.xlsx.
        /// </summary>
        private string? GetTestKitForPaper(string testKitRoot, string paperNo)
        {
            // Try to find mapping
            var mappingPath = Path.Combine(testKitRoot, "Mapping.xlsx");
            if (File.Exists(mappingPath))
            {
                using var wb = new XLWorkbook(mappingPath);
                var ws = wb.Worksheet(1);

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var paper = row.Cell(1).GetValue<string>();
                    var question = row.Cell(2).GetValue<string>();

                    if (paper == paperNo && !string.IsNullOrEmpty(question))
                    {
                        var questionPath = Path.Combine(testKitRoot, question);
                        if (Directory.Exists(questionPath))
                            return questionPath;
                    }
                }
            }

            // Fallback: try direct folder matching
            var directPath = Path.Combine(testKitRoot, paperNo);
            if (Directory.Exists(directPath))
                return directPath;

            // Try Q1, Q2, etc.
            var qPath = Path.Combine(testKitRoot, $"Q{paperNo}");
            if (Directory.Exists(qPath))
                return qPath;

            return null;
        }

        #endregion

        #region Multi-Threading Optimization

        /// <summary>
        /// Configure .NET ThreadPool for optimal parallel grading performance.
        /// 
        /// Auto-grading is I/O-bound (Docker operations, file I/O, network monitoring),
        /// so we configure ThreadPool to handle higher concurrency than CPU count.
        /// 
        /// Benefits:
        /// - Reduces thread creation latency (pre-allocates minimum threads)
        /// - Allows higher concurrency for I/O-bound workload
        /// - Improves throughput for parallel student grading
        /// </summary>
        /// <param name="maxParallelStudents">Maximum students to grade in parallel</param>
        private void ConfigureThreadPoolForParallelGrading(int maxParallelStudents)
        {
            // Get current ThreadPool settings
            ThreadPool.GetMinThreads(out int currentMinWorker, out int currentMinIO);
            ThreadPool.GetMaxThreads(out int currentMaxWorker, out int currentMaxIO);
            
            // OPTIMIZATION: Set minimum threads to reduce spin-up latency
            // Auto-grading is I/O-bound, so we need more threads than CPU cores
            // Formula: Max(maxParallelStudents * 2, CPU_cores * 2)
            // This ensures threads are pre-created and ready for work
            int minWorkerThreads = Math.Max(
                maxParallelStudents * 2,
                Environment.ProcessorCount * 2
            );
            int minIOThreads = Math.Max(
                maxParallelStudents * 2,
                Environment.ProcessorCount * 2
            );
            
            // OPTIMIZATION: Set maximum threads for I/O-bound workload
            // Allow up to 4x parallelism for async I/O operations (Docker, files, network)
            int maxWorkerThreads = Math.Max(
                maxParallelStudents * 4,
                Environment.ProcessorCount * 4
            );
            int maxIOThreads = Math.Max(
                maxParallelStudents * 4,
                Environment.ProcessorCount * 4
            );
            
            // Apply ThreadPool configuration
            ThreadPool.SetMinThreads(minWorkerThreads, minIOThreads);
            ThreadPool.SetMaxThreads(maxWorkerThreads, maxIOThreads);
            
            // Log configuration
            Console.WriteLine($"[ThreadPool Configuration]");
            Console.WriteLine($"  CPU Cores: {Environment.ProcessorCount}");
            Console.WriteLine($"  MaxParallelStudents: {maxParallelStudents}");
            Console.WriteLine($"  Parallelism Ratio: {maxParallelStudents / (double)Environment.ProcessorCount:F2}x CPU cores");
            Console.WriteLine($"  Worker Threads: Min={minWorkerThreads} (was {currentMinWorker}), Max={maxWorkerThreads} (was {currentMaxWorker})");
            Console.WriteLine($"  I/O Threads: Min={minIOThreads} (was {currentMinIO}), Max={maxIOThreads} (was {currentMaxIO})");
            Console.WriteLine($"[ThreadPool] Configured for optimal I/O-bound workload performance");
        }

        #endregion

        #region Result Writing

        /// <summary>
        /// Write overall grading summary.
        /// </summary>
        private async Task WriteOverallSummaryAsync(string resultPath, List<StudentGradingResult> results)
        {
            var summaryPath = Path.Combine(resultPath, "StudentsSolution.xlsx");
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Summary");

            // Headers
            ws.Cell(1, 1).Value = "StudentCode";
            ws.Cell(1, 2).Value = "Paper";
            ws.Cell(1, 3).Value = "Status";
            ws.Cell(1, 4).Value = "TotalMark";
            ws.Cell(1, 5).Value = "MaxMark";
            ws.Cell(1, 6).Value = "Error";
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var result in results)
            {
                ws.Cell(row, 1).Value = result.StudentCode;
                ws.Cell(row, 2).Value = result.PaperNo;
                ws.Cell(row, 3).Value = result.Passed ? "PASSED" : "FAILED";
                ws.Cell(row, 4).Value = result.TotalMark;
                ws.Cell(row, 5).Value = result.MaxMark;
                ws.Cell(row, 6).Value = result.ErrorMessage ?? "";
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(summaryPath);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Reads the starting port for PortAllocator from the test kit's Environment.xlsx file.
        /// This ensures that port allocation starts from the correct base port specified in the test kit.
        /// 
        /// Port Configuration Priority:
        /// 1. Code_Container_Host_Port from Environment.xlsx (preferred)
        /// 2. Code_Container_Internal_Port from Environment.xlsx (fallback)
        /// 3. Default 8000 if not found
        /// 
        /// This method ensures consistency between:
        /// - Port used for container creation (via PortAllocator)
        /// - Port used for DLL modification (via DockerGradingConfig)
        /// - Port used for network monitoring (via DockerGradingConfig)
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder (e.g., TestKit/Q12)</param>
        /// <returns>Starting port for PortAllocator, or 0 if not found (will use PortAllocator default 8000)</returns>
        private int ReadStartingPortFromEnvironmentXlsx(string testKitPath)
        {
            try
            {
                // Look for Environment.xlsx in the test kit folder
                var environmentPath = Path.Combine(testKitPath, "Environment.xlsx");
                if (!File.Exists(environmentPath))
                {
                    // Try lowercase as fallback
                    environmentPath = Path.Combine(testKitPath, "environment.xlsx");
                    if (!File.Exists(environmentPath))
                    {
                        Console.WriteLine($"[Port Config] Environment.xlsx not found at {testKitPath}. PortAllocator will use default 8000.");
                        return 0;
                    }
                }

                Console.WriteLine($"[Port Config] Reading starting port from Environment.xlsx: {environmentPath}");

                using (var workbook = new XLWorkbook(environmentPath))
                {
                    // Look for "Config" sheet which contains port configuration
                    var worksheet = workbook.Worksheet("Config");
                    if (worksheet == null)
                    {
                        Console.WriteLine($"[Port Config] 'Config' sheet not found in Environment.xlsx at {environmentPath}");
                        return 0;
                    }
                    
                    // Read port configuration from Config sheet (column 1 = Key, column 2 = Value)
                    // Try Code_Container_Host_Port first, then Code_Container_Internal_Port as fallback
                    foreach (var row in worksheet.RowsUsed().Skip(1)) // Skip header row
                    {
                        var keyCell = row.Cell(1).GetValue<string>()?.Trim() ?? "";
                        
                        // Normalize key by removing underscores and making lowercase for comparison
                        var normalizedKey = keyCell.Replace("_", "").ToLowerInvariant();
                        
                        // Prefer Code_Container_Host_Port (external port for host exposure)
                        // This is what containers will actually bind to on the host
                        if (normalizedKey == "codecontainerhostport" || normalizedKey == "codecontainerinternalport")
                        {
                            var valueCell = row.Cell(2);
                            int port = 0;
                            
                            // Try to get as integer first
                            if (valueCell.TryGetValue<int>(out var intValue))
                            {
                                port = intValue;
                            }
                            else
                            {
                                // Fallback to string parsing
                                var valueStr = valueCell.GetValue<string>()?.Trim() ?? "";
                                int.TryParse(valueStr, out port);
                            }
                            
                            if (port > 0 && port <= 65535)
                            {
                                Console.WriteLine($"[Port Config] Successfully read {keyCell}={port} from Environment.xlsx.");
                                Console.WriteLine($"[Port Config] PortAllocator will start from port {port} and allocate sequentially (N, N+1, N+2, ...)");
                                return port;
                            }
                        }
                    }
                    
                    Console.WriteLine($"[Port Config] Code_Container_Host_Port or Code_Container_Internal_Port not found in Environment.xlsx at {environmentPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Port Config] Error reading starting port from Environment.xlsx: {ex.Message}");
            }

            return 0;  // Will use PortAllocator default (8000)
        }

        #endregion
    }

    #region Model Classes

    /// <summary>
    /// Information about a student to be graded.
    /// </summary>
    public class StudentInfo
    {
        public string StudentCode { get; set; } = "";
        public string PaperNo { get; set; } = "";
        public string SolutionPath { get; set; } = "";
        public string? ServerDllPath { get; set; }
        public string? ClientDllPath { get; set; }
    }

    /// <summary>
    /// Result of grading a single student.
    /// </summary>
    public class StudentGradingResult
    {
        public string StudentCode { get; set; } = "";
        public string PaperNo { get; set; } = "";
        public double TotalMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new();
    }

    /// <summary>
    /// Result of a single test case (simplified for CLI).
    /// The detailed results are written by DockerGradingService.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = "";
        public double EarnedMark { get; set; }
        public double MaxMark { get; set; }
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
