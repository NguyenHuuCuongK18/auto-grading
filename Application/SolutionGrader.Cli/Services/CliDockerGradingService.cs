using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EnvironmentBuilder.DockerCommand;

namespace SolutionGrader.Cli.Services
{
    /// <summary>
    /// Docker-based grading service for the CLI.
    /// This service orchestrates the complete Docker grading workflow:
    /// 1. Discover students from submit folder
    /// 2. Load test kit configuration (Environment.xlsx, Header.xlsx)
    /// 3. Setup Docker containers (MSSQL database, server container, client container)
    /// 4. Execute test cases from Detail.xlsx (StartServer, StartClient, Input, etc.)
    /// 5. Capture outputs from containers using stage-based approach
    /// 6. Compare outputs against expected values from Client/Server/Network sheets
    /// 7. Write results in SampleLogging format using ClosedXML
    /// 
    /// This service syncs with the SolutionGrader.UI services but is designed
    /// for cross-platform CLI usage without WPF dependencies.
    /// </summary>
    public class CliDockerGradingService
    {
        // Configuration constants for timing and defaults
        private const int StartupDelayMs = 3000;          // Delay after starting client/server
        private const int InputProcessingDelayMs = 5000;  // Delay after sending input
        private const int AttachReadDelayMs = 2000;       // Delay to allow console output to be captured via attach
        private const string DefaultDatabaseName = "Library";
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly DockerConsoleManager _consoleManager;

        public CliDockerGradingService()
        {
            _dockerExecutor = new DockerCommandExecutor();
            _consoleManager = new DockerConsoleManager();
        }

        /// <summary>
        /// Execute Docker-based grading for students.
        /// </summary>
        /// <param name="config">Grading configuration</param>
        /// <param name="paperNo">Optional paper number filter</param>
        /// <param name="studentCode">Optional student code filter</param>
        /// <returns>Exit code (0 = success)</returns>
        public async Task<int> ExecuteAsync(CliGradingConfiguration config, string? paperNo, string? studentCode)
        {
            Console.WriteLine("[DockerGrade] Starting grading session...");

            // Validate inputs
            if (!Directory.Exists(config.SubmitFolderPath))
            {
                Console.WriteLine($"[ERROR] Submit folder not found: {config.SubmitFolderPath}");
                return 1;
            }

            if (!Directory.Exists(config.TestKitFolderPath))
            {
                Console.WriteLine($"[ERROR] TestKit folder not found: {config.TestKitFolderPath}");
                return 1;
            }

            // Check Docker is running
            if (!_dockerExecutor.IsDockerRunning())
            {
                Console.WriteLine("[ERROR] Docker is not running! Please start Docker.");
                return 1;
            }
            Console.WriteLine("[DockerGrade] Docker is running.");

            // Discover students
            var students = DiscoverStudents(config, paperNo, studentCode);
            Console.WriteLine($"[DockerGrade] Found {students.Count} student(s) to grade.");

            if (students.Count == 0)
            {
                Console.WriteLine("[WARNING] No students found to grade.");
                return 0;
            }

            // Create output folder
            Directory.CreateDirectory(config.SaveResultFolderPath);

            // Grade each student
            var allResults = new List<StudentGradingResult>();
            int gradedCount = 0;
            int passedCount = 0;

            foreach (var student in students)
            {
                gradedCount++;
                Console.WriteLine($"\n{new string('=', 60)}");
                Console.WriteLine($"[{gradedCount}/{students.Count}] Grading student: {student.StudentCode} (Paper {student.PaperNo})");
                Console.WriteLine($"{new string('=', 60)}");

                var result = await GradeStudentAsync(student, config);
                allResults.Add(result);

                if (result.Passed) passedCount++;

                Console.WriteLine($"[Result] {student.StudentCode}: {(result.Passed ? "PASSED" : "FAILED")} - {result.TotalMark:F2}/{result.MaxMark:F2}");
            }

            // Write overall summary
            await WriteStudentsSolutionSummaryAsync(config.SaveResultFolderPath, allResults);

            Console.WriteLine($"\n{new string('=', 60)}");
            Console.WriteLine("[DockerGrade] Grading Complete");
            Console.WriteLine($"{new string('=', 60)}");
            Console.WriteLine($"Results saved to: {config.SaveResultFolderPath}");
            Console.WriteLine($"Total students: {allResults.Count}");
            Console.WriteLine($"Passed: {passedCount}");
            Console.WriteLine($"Failed: {allResults.Count - passedCount}");

            return 0;
        }

        /// <summary>
        /// Discover students from the submit folder.
        /// </summary>
        private List<StudentInfo> DiscoverStudents(CliGradingConfiguration config, string? paperNo, string? studentCode)
        {
            var students = new List<StudentInfo>();

            var paperDirs = Directory.GetDirectories(config.SubmitFolderPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .Where(d => string.IsNullOrEmpty(paperNo) || Path.GetFileName(d) == paperNo)
                .OrderBy(d => int.Parse(Path.GetFileName(d)));

            foreach (var paperDir in paperDirs)
            {
                var paper = Path.GetFileName(paperDir);
                var studentDirs = Directory.GetDirectories(paperDir)
                    .Where(d => !Path.GetFileName(d).Contains("."))
                    .Where(d => string.IsNullOrEmpty(studentCode) || 
                               Path.GetFileName(d).Equals(studentCode, StringComparison.OrdinalIgnoreCase));

                foreach (var studentDir in studentDirs)
                {
                    var code = Path.GetFileName(studentDir);
                    var solutionPath = Path.Combine(studentDir, "1", "solution");

                    if (!Directory.Exists(solutionPath))
                    {
                        Console.WriteLine($"[WARNING] Solution folder not found for {code}, skipping.");
                        continue;
                    }

                    var student = new StudentInfo
                    {
                        StudentCode = code,
                        PaperNo = paper,
                        SolutionPath = solutionPath
                    };

                    // Find DLLs
                    if (config.HasServer)
                        student.ServerDllPath = FindDll(solutionPath, config.ServerProjectName, "Q11", "Server");
                    if (config.HasClient)
                        student.ClientDllPath = FindDll(solutionPath, config.ClientProjectName, "Q12", "Client");

                    students.Add(student);
                    Console.WriteLine($"[Discover] {code}: Server={student.ServerDllPath ?? "N/A"}, Client={student.ClientDllPath ?? "N/A"}");
                }
            }

            return students;
        }

        /// <summary>
        /// Find a DLL file in the solution folder.
        /// </summary>
        private string? FindDll(string solutionPath, string projectName, params string[] fallbackFolderNames)
        {
            // Try to find by project name first
            if (!string.IsNullOrEmpty(projectName))
            {
                var files = Directory.GetFiles(solutionPath, $"{projectName}.dll", SearchOption.AllDirectories);
                var mainDll = files.FirstOrDefault(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar));
                if (mainDll != null)
                    return mainDll;
            }

            // Try fallback folder names
            foreach (var folderName in fallbackFolderNames)
            {
                var folderPath = Directory.GetDirectories(solutionPath, folderName + "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (folderPath != null)
                {
                    var dlls = Directory.GetFiles(folderPath, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "runtimes" + Path.DirectorySeparatorChar) &&
                                   !Path.GetFileName(f).StartsWith("Microsoft.") &&
                                   !Path.GetFileName(f).StartsWith("System."))
                        .ToList();

                    if (dlls.Count > 0)
                        return dlls[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Grade a single student.
        /// </summary>
        private async Task<StudentGradingResult> GradeStudentAsync(StudentInfo student, CliGradingConfiguration config)
        {
            var result = new StudentGradingResult
            {
                StudentCode = student.StudentCode,
                PaperNo = student.PaperNo
            };

            var studentResultPath = Path.Combine(config.SaveResultFolderPath, student.PaperNo, "student", student.StudentCode);
            Directory.CreateDirectory(studentResultPath);

            try
            {
                // Get test kit for this paper
                var testKitPath = GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    Console.WriteLine($"[WARNING] No test kit found for paper {student.PaperNo}");
                    result.ErrorMessage = $"No test kit for paper {student.PaperNo}";
                    return result;
                }

                // Load test kit configuration
                var testKitConfig = LoadTestKitConfig(testKitPath);
                result.MaxMark = testKitConfig.TotalMaxMark;

                // Update config with environment settings
                if (testKitConfig.CodeContainerInternalPort > 0)
                    config.CodeContainerInternalPort = testKitConfig.CodeContainerInternalPort;
                if (testKitConfig.CodeContainerHostPort > 0)
                    config.CodeContainerHostPort = testKitConfig.CodeContainerHostPort;

                // Setup containers
                var serverContainer = $"ag-server-{student.StudentCode}";
                var clientContainer = $"ag-client-{student.StudentCode}";

                await SetupContainersAsync(student, config, testKitConfig, serverContainer, clientContainer);

                // Execute test cases
                foreach (var testCase in testKitConfig.TestCases)
                {
                    Console.WriteLine($"\n--- Test Case: {testCase.Name} (max: {testCase.MaxMark} points) ---");

                    var tcResult = await ExecuteTestCaseAsync(student, testCase, testKitConfig, config, serverContainer, clientContainer);
                    result.TestCaseResults.Add(tcResult);

                    // Write test case result files
                    var tcResultPath = Path.Combine(studentResultPath, testCase.Name);
                    Directory.CreateDirectory(tcResultPath);
                    await WriteTestCaseResultAsync(tcResultPath, testCase.Name, tcResult);

                    Console.WriteLine($"[TestCase] {testCase.Name}: {(tcResult.Passed ? "PASSED" : "FAILED")} - {tcResult.EarnedMark:F2}/{tcResult.MaxMark:F2}");

                    // Cleanup between test cases (stop applications, wait for port release)
                    await CleanupBetweenTestCasesAsync(serverContainer, clientContainer, config.CodeContainerHostPort);
                }

                // Calculate totals
                result.TotalMark = result.TestCaseResults.Sum(tc => tc.EarnedMark);
                result.Passed = result.TestCaseResults.Any(tc => tc.Passed);

                // Write overall summary
                await WriteOverallSummaryAsync(studentResultPath, result.TestCaseResults);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Grading failed for {student.StudentCode}: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                // Cleanup containers
                await CleanupContainersAsync($"ag-server-{student.StudentCode}", $"ag-client-{student.StudentCode}");
            }

            return result;
        }

        /// <summary>
        /// Get test kit path for a paper.
        /// </summary>
        private string? GetTestKitForPaper(string testKitFolderPath, string paperNo)
        {
            // Check Mapping.xlsx
            var mappingPath = Path.Combine(testKitFolderPath, "Mapping.xlsx");
            if (File.Exists(mappingPath))
            {
                try
                {
                    using var workbook = new XLWorkbook(mappingPath);
                    var ws = workbook.Worksheet(1);
                    foreach (var row in ws.RowsUsed().Skip(1))
                    {
                        var rowPaper = row.Cell(1).GetValue<string>();
                        if (rowPaper?.Equals(paperNo, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var questionKit = row.Cell(3).GetValue<string>();
                            if (!string.IsNullOrEmpty(questionKit))
                            {
                                var path = Path.Combine(testKitFolderPath, questionKit);
                                if (Directory.Exists(path))
                                    return path;
                            }
                        }
                    }
                }
                catch { }
            }

            // Fallback to convention
            var conventions = new[] { $"Q{paperNo}", $"Paper{paperNo}", paperNo };
            foreach (var conv in conventions)
            {
                var path = Path.Combine(testKitFolderPath, conv);
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "Header.xlsx")))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Load test kit configuration from Excel files.
        /// </summary>
        private TestKitConfig LoadTestKitConfig(string testKitPath)
        {
            var config = new TestKitConfig();

            // Load Environment.xlsx
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (File.Exists(envPath))
            {
                try
                {
                    using var workbook = new XLWorkbook(envPath);
                    if (workbook.TryGetWorksheet("Config", out var ws))
                    {
                        foreach (var row in ws.RowsUsed().Skip(1))
                        {
                            var key = row.Cell(1).GetValue<string>()?.Trim()?.ToLowerInvariant().Replace("_", "");
                            var value = row.Cell(2).GetValue<string>()?.Trim();

                            switch (key)
                            {
                                case "codecontainerinternalport":
                                    if (int.TryParse(value, out var ip)) config.CodeContainerInternalPort = ip;
                                    break;
                                case "codecontainerhostport":
                                    if (int.TryParse(value, out var hp)) config.CodeContainerHostPort = hp;
                                    break;
                                case "codeimagename":
                                    config.CodeImageName = value ?? "";
                                    break;
                                case "dockernetwork":
                                    config.DockerNetwork = value ?? "";
                                    break;
                                case "databasepassword":
                                    config.DatabasePassword = value ?? "";
                                    break;
                                case "defaultdatabasename":
                                    config.DatabaseName = value ?? "Library";
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Error loading Environment.xlsx: {ex.Message}");
                }
            }

            // Load Header.xlsx
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                try
                {
                    using var workbook = new XLWorkbook(headerPath);
                    if (workbook.TryGetWorksheet("QuestionMark", out var markSheet))
                    {
                        foreach (var row in markSheet.RowsUsed().Skip(1))
                        {
                            var tcName = row.Cell(1).GetValue<string>()?.Trim();
                            var mark = row.Cell(2).GetValue<double>();
                            if (!string.IsNullOrEmpty(tcName))
                                config.TestCaseMarks[tcName] = mark;
                        }
                    }

                    if (workbook.TryGetWorksheet("Config", out var configSheet))
                    {
                        foreach (var row in configSheet.RowsUsed().Skip(1))
                        {
                            var key = row.Cell(1).GetValue<string>()?.Trim();
                            var value = row.Cell(2).GetValue<string>()?.Trim();
                            if (key?.Equals("Protocol", StringComparison.OrdinalIgnoreCase) == true)
                                config.Protocol = value ?? "TCP";
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Error loading Header.xlsx: {ex.Message}");
                }
            }

            // Discover test cases
            config.TestCases = Directory.GetDirectories(testKitPath)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "Detail.xlsx")))
                .Select(d => new TestCaseConfig
                {
                    Name = Path.GetFileName(d),
                    Path = d,
                    MaxMark = config.TestCaseMarks.TryGetValue(Path.GetFileName(d), out var m) ? m : 0
                })
                .OrderBy(tc => tc.Name)
                .ToList();

            Console.WriteLine($"[TestKit] Loaded {config.TestCases.Count} test cases, total max mark: {config.TotalMaxMark}");

            return config;
        }

        /// <summary>
        /// Setup Docker containers for student.
        /// </summary>
        private async Task SetupContainersAsync(StudentInfo student, CliGradingConfiguration config, TestKitConfig testKitConfig, string serverContainer, string clientContainer)
        {
            Console.WriteLine("[Docker] Setting up containers...");

            // Remove existing containers
            try
            {
                if (_dockerExecutor.IsContainerExist(serverContainer))
                    _dockerExecutor.RemoveContainer(serverContainer);
                if (_dockerExecutor.IsContainerExist(clientContainer))
                    _dockerExecutor.RemoveContainer(clientContainer);
            }
            catch { }

            await Task.Delay(500);

            // Create network if needed
            try
            {
                _dockerExecutor.CreateNetwork(config.DockerNetwork);
            }
            catch { }

            // Create server container with TTY support for reliable output capture
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                Console.WriteLine($"[Docker] Creating server container with TTY: {serverContainer}");
                var serverBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                {
                    ImageName = testKitConfig.CodeImageName,
                    ContainerName = serverContainer,
                    DockerNetwork = config.DockerNetwork,
                    ContainerPort = config.CodeContainerInternalPort,
                    HostPort = config.CodeContainerHostPort,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        { "DOTNET_RUNNING_IN_CONTAINER", "true" },
                        { "DOTNET_SYSTEM_CONSOLE_UNBUFFERED", "1" }  // Disable output buffering
                    }
                };
                // Use RunContainerWithTty for reliable console output capture via docker attach
                _dockerExecutor.RunContainerWithTty(serverBase);
            }

            // Create client container with TTY support
            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                Console.WriteLine($"[Docker] Creating client container with TTY: {clientContainer}");
                var clientBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                {
                    ImageName = testKitConfig.CodeImageName,
                    ContainerName = clientContainer,
                    DockerNetwork = config.DockerNetwork,
                    ContainerPort = config.CodeContainerInternalPort,
                    HostPort = config.CodeContainerHostPort + 1,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        { "DOTNET_RUNNING_IN_CONTAINER", "true" },
                        { "DOTNET_SYSTEM_CONSOLE_UNBUFFERED", "1" }  // Disable output buffering
                    }
                };
                // Use RunContainerWithTty for reliable console output capture via docker attach
                _dockerExecutor.RunContainerWithTty(clientBase);
            }

            // Copy files to containers
            await CopyFilesToContainersAsync(student, serverContainer, clientContainer);
            
            // Generate appsettings.json files in containers
            // This is CRITICAL - student code reads configuration from appsettings.json
            GenerateAppsettingsInContainers(student, config, testKitConfig, serverContainer, clientContainer);

            Console.WriteLine("[Docker] Containers ready with TTY support for console attachment.");
        }

        /// <summary>
        /// Copy student files to containers.
        /// </summary>
        private async Task CopyFilesToContainersAsync(StudentInfo student, string serverContainer, string clientContainer)
        {
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    try
                    {
                        _dockerExecutor.MakeDirectory(serverContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(serverDir, $"{serverContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied server files to {serverContainer}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to copy server files: {ex.Message}");
                    }
                }
            }

            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath);
                if (clientDir != null)
                {
                    var folderName = Path.GetFileName(clientDir);
                    try
                    {
                        _dockerExecutor.MakeDirectory(clientContainer, "/apps");
                        _dockerExecutor.CopyFileToContainer(clientDir, $"{clientContainer}:/apps/{folderName}");
                        Console.WriteLine($"[Docker] Copied client files to {clientContainer}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to copy client files: {ex.Message}");
                    }
                }
            }

            await Task.Delay(500);
        }

        /// <summary>
        /// Generate appsettings.json files for server and client in Docker containers.
        /// This is CRITICAL - the student code reads configuration from appsettings.json.
        /// </summary>
        private void GenerateAppsettingsInContainers(
            StudentInfo student, 
            CliGradingConfiguration config, 
            TestKitConfig testKitConfig,
            string serverContainer, 
            string clientContainer)
        {
            Console.WriteLine("[Appsettings] Generating configuration files...");
            
            // Build connection string for database
            var connectionString = BuildConnectionString(config, testKitConfig);
            
            // For Docker networking, client connects to server container by name
            // Server listens on 0.0.0.0 to accept connections from other containers
            var serverIpAddress = "0.0.0.0";
            var clientIpAddress = serverContainer; // Client connects to server container by DNS name
            var port = config.CodeContainerInternalPort.ToString();
            
            // Generate server appsettings.json
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath);
                if (serverDir != null)
                {
                    var folderName = Path.GetFileName(serverDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    
                    var serverConfig = $@"{{
  ""ConnectionStrings"": {{
    ""MyCnn"": ""{connectionString}""
  }},
  ""IpAddress"": ""{serverIpAddress}"",
  ""Port"": ""{port}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        // Write to temp file then copy to container
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_server_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, serverConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{serverContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Generated server config: IP={serverIpAddress}, Port={port}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to generate server appsettings: {ex.Message}");
                    }
                    finally
                    {
                        // Ensure temp file is cleaned up
                        if (tempFile != null && File.Exists(tempFile))
                            try { File.Delete(tempFile); } catch { }
                    }
                }
            }
            
            // Generate client appsettings.json
            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath);
                if (clientDir != null)
                {
                    var folderName = Path.GetFileName(clientDir);
                    var containerPath = $"/apps/{folderName}/appsettings.json";
                    
                    var clientConfig = $@"{{
  ""IpAddress"": ""{clientIpAddress}"",
  ""Port"": ""{port}""
}}";
                    
                    string? tempFile = null;
                    try
                    {
                        tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_client_{Guid.NewGuid()}.json");
                        File.WriteAllText(tempFile, clientConfig);
                        _dockerExecutor.CopyFileToContainer(tempFile, $"{clientContainer}:{containerPath}");
                        Console.WriteLine($"[Appsettings] Generated client config: IP={clientIpAddress}, Port={port}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to generate client appsettings: {ex.Message}");
                    }
                    finally
                    {
                        // Ensure temp file is cleaned up
                        if (tempFile != null && File.Exists(tempFile))
                            try { File.Delete(tempFile); } catch { }
                    }
                }
            }
        }
        
        /// <summary>
        /// Build database connection string from configuration.
        /// </summary>
        private string BuildConnectionString(CliGradingConfiguration config, TestKitConfig testKitConfig)
        {
            // For Docker, connect to database container by name or localhost with mapped port
            var server = $"localhost,{config.DatabaseContainerHostPort}";
            var database = testKitConfig.DatabaseName ?? DefaultDatabaseName;
            var username = config.DatabaseUsername ?? "sa";
            var password = config.DatabasePassword ?? DefaultDatabasePassword;
            
            return $"server={server};database={database};uid={username};pwd={password};TrustServerCertificate=true";
        }

        /// <summary>
        /// Execute a single test case.
        /// </summary>
        private async Task<TestCaseResult> ExecuteTestCaseAsync(
            StudentInfo student, TestCaseConfig testCase, TestKitConfig testKitConfig,
            CliGradingConfiguration config, string serverContainer, string clientContainer)
        {
            var result = new TestCaseResult
            {
                TestCaseName = testCase.Name,
                MaxMark = testCase.MaxMark
            };

            try
            {
                // Read Detail.xlsx
                var detailPath = Path.Combine(testCase.Path, "Detail.xlsx");
                var actions = ReadActions(detailPath);
                var expectedOutputs = ReadExpectedOutputs(detailPath);

                Console.WriteLine($"[TestCase] Loaded {actions.Count} actions, {expectedOutputs.Count} expected outputs");

                // Execute actions and capture outputs
                var (clientOutputs, serverOutputs) = await ExecuteActionsAsync(
                    student, actions, config, testKitConfig, serverContainer, clientContainer);

                // Compare outputs
                var (earnedMark, passed, comparisons) = CompareOutputs(expectedOutputs, clientOutputs, serverOutputs, testCase.MaxMark);

                result.EarnedMark = earnedMark;
                result.Passed = passed;
                result.Actions = actions.Select(a => new ActionInfo { Stage = a.Stage, Input = a.Input, ActionType = a.Action }).ToList();
                result.ClientComparisons = comparisons.Where(c => c.Source == "Client").ToList();
                result.ServerComparisons = comparisons.Where(c => c.Source == "Server").ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Test case {testCase.Name} failed: {ex.Message}");
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Read actions from Detail.xlsx User sheet.
        /// </summary>
        private List<(int Stage, string Input, string Action)> ReadActions(string detailPath)
        {
            var actions = new List<(int Stage, string Input, string Action)>();
            try
            {
                using var workbook = new XLWorkbook(detailPath);
                if (workbook.TryGetWorksheet("User", out var ws))
                {
                    foreach (var row in ws.RowsUsed().Skip(1))
                    {
                        var stageStr = row.Cell(1).GetValue<string>();
                        var input = row.Cell(2).GetValue<string>() ?? "";
                        var action = row.Cell(3).GetValue<string>() ?? "";

                        if (int.TryParse(stageStr, out var stage) && !string.IsNullOrEmpty(action))
                            actions.Add((stage, input, action));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Error reading actions from Detail.xlsx: {ex.Message}");
            }
            return actions;
        }

        /// <summary>
        /// Read expected outputs from Detail.xlsx Client/Server sheets.
        /// </summary>
        private Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
        {
            var outputs = new Dictionary<int, (string? ClientConsole, string? ServerConsole)>();
            try
            {
                using var workbook = new XLWorkbook(detailPath);

                // Read Client sheet
                if (workbook.TryGetWorksheet("Client", out var clientWs))
                {
                    foreach (var row in clientWs.RowsUsed().Skip(1))
                    {
                        var stageStr = row.Cell(1).GetValue<string>();
                        var console = row.Cell(2).GetValue<string>();

                        if (int.TryParse(stageStr, out var stage))
                        {
                            if (!outputs.ContainsKey(stage))
                                outputs[stage] = (null, null);
                            var current = outputs[stage];
                            outputs[stage] = (console, current.ServerConsole);
                        }
                    }
                }

                // Read Server sheet
                if (workbook.TryGetWorksheet("Server", out var serverWs))
                {
                    foreach (var row in serverWs.RowsUsed().Skip(1))
                    {
                        var stageStr = row.Cell(1).GetValue<string>();
                        var console = row.Cell(2).GetValue<string>();

                        if (int.TryParse(stageStr, out var stage))
                        {
                            if (!outputs.ContainsKey(stage))
                                outputs[stage] = (null, null);
                            var current = outputs[stage];
                            outputs[stage] = (current.ClientConsole, console);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Error reading expected outputs from Detail.xlsx: {ex.Message}");
            }
            return outputs;
        }

        /// <summary>
        /// Execute actions and capture outputs using docker attach for reliable console output.
        /// 
        /// This method uses docker attach instead of docker logs to solve the buffering issue.
        /// The approach:
        /// 1. When starting a container app, create a DockerConsoleAttachment that attaches to the container
        /// 2. The attachment reads console output in real-time via docker attach --sig-proxy=false
        /// 3. Stage markers track output per stage for comparison
        /// </summary>
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsAsync(
            StudentInfo student, List<(int Stage, string Input, string Action)> actions,
            CliGradingConfiguration config, TestKitConfig testKitConfig,
            string serverContainer, string clientContainer)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();
            
            // Console attachments for reliable output capture
            DockerConsoleAttachment? clientAttachment = null;
            DockerConsoleAttachment? serverAttachment = null;
            
            // Track output baselines for calculating "new" output per stage
            int clientBaseline = 0;
            int serverBaseline = 0;

            try
            {
                foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
                {
                    Console.WriteLine($"  [Stage {stage}] {action}" + (string.IsNullOrEmpty(input) ? "" : $" input='{input}'"));

                    switch (action.ToUpperInvariant())
                    {
                        case "STARTSERVER":
                            if (!string.IsNullOrEmpty(student.ServerDllPath))
                            {
                                var serverDirPath = Path.GetDirectoryName(student.ServerDllPath);
                                if (serverDirPath != null)
                                {
                                    var serverDir = Path.GetFileName(serverDirPath);
                                    var serverDll = Path.GetFileName(student.ServerDllPath);
                                    var dockerPath = $"/apps/{serverDir}/{serverDll}";

                                    // Start the application inside the container
                                    _dockerExecutor.WaitForPublishConsoleFileDeployment(
                                        serverContainer, serverContainer, dockerPath,
                                        config.CodeContainerInternalPort.ToString(), 30000);

                                    // Create and start console attachment for server
                                    serverAttachment = _consoleManager.CreateAttachment(serverContainer, $"Server-{serverContainer}");
                                    serverAttachment.StartAttachment();
                                    serverAttachment.StartStage(stage);

                                    // Wait for application to start and output to be captured
                                    await Task.Delay(StartupDelayMs);
                                    
                                    // Get output using attachment (preferred) or fall back to application log
                                    string newOutput;
                                    if (serverAttachment.IsRunning && serverAttachment.OutputLength > 0)
                                    {
                                        newOutput = serverAttachment.GetNewOutputSince(serverBaseline);
                                        serverBaseline = serverAttachment.OutputLength;
                                    }
                                    else
                                    {
                                        // Use application log file for reliable output capture
                                        var output = _dockerExecutor.GetApplicationLog(serverContainer, serverContainer) ?? "";
                                        newOutput = output.Length > serverBaseline ? output.Substring(serverBaseline) : output;
                                        if (!string.IsNullOrEmpty(newOutput))
                                        {
                                            serverBaseline = output.Length;
                                        }
                                    }
                                    
                                    serverOutputs[stage] = newOutput;
                                    Console.WriteLine($"    Server started, output: {newOutput.Length} chars (attach: {serverAttachment?.IsRunning ?? false})");
                                }
                            }
                            break;

                        case "STARTCLIENT":
                            if (!string.IsNullOrEmpty(student.ClientDllPath))
                            {
                                var clientDirPath = Path.GetDirectoryName(student.ClientDllPath);
                                if (clientDirPath != null)
                                {
                                    var clientDir = Path.GetFileName(clientDirPath);
                                    var clientDll = Path.GetFileName(student.ClientDllPath);
                                    var dockerPath = $"/apps/{clientDir}/{clientDll}";

                                    // Start the application inside the container
                                    _dockerExecutor.WaitForPublishConsoleFileDeployment(
                                        clientContainer, clientContainer, dockerPath, "-1", 30000);

                                    // Create and start console attachment for client
                                    clientAttachment = _consoleManager.CreateAttachment(clientContainer, $"Client-{clientContainer}");
                                    clientAttachment.StartAttachment();
                                    clientAttachment.StartStage(stage);

                                    // Wait for application to start and output to be captured
                                    await Task.Delay(StartupDelayMs);
                                    
                                    // Get output using attachment (preferred) or fall back to application log file
                                    // Retry if output is empty (timing issue on first test case)
                                    // Use more retries with longer waits for the first test case
                                    string newOutput = "";
                                    int retryCount = 0;
                                    const int maxRetries = 5;  // Increased retries for first test case
                                    const int retryDelayMs = 2000;  // 2 second delay between retries
                                    
                                    while (string.IsNullOrEmpty(newOutput) && retryCount < maxRetries)
                                    {
                                        if (clientAttachment.IsRunning && clientAttachment.OutputLength > 0)
                                        {
                                            newOutput = clientAttachment.GetNewOutputSince(clientBaseline);
                                            clientBaseline = clientAttachment.OutputLength;
                                        }
                                        else
                                        {
                                            // Use application log file for reliable output capture
                                            var output = _dockerExecutor.GetApplicationLog(clientContainer, clientContainer) ?? "";
                                            newOutput = output.Length > clientBaseline ? output.Substring(clientBaseline) : output;
                                            if (!string.IsNullOrEmpty(newOutput))
                                            {
                                                clientBaseline = output.Length;
                                            }
                                        }
                                        
                                        if (string.IsNullOrEmpty(newOutput) && retryCount < maxRetries - 1)
                                        {
                                            Console.WriteLine($"    Waiting for client output... (retry {retryCount + 1}/{maxRetries})");
                                            await Task.Delay(retryDelayMs);
                                        }
                                        retryCount++;
                                    }
                                    
                                    clientOutputs[stage] = newOutput;
                                    Console.WriteLine($"    Client started, output: {newOutput.Length} chars (attach: {clientAttachment?.IsRunning ?? false})");
                                }
                            }
                            break;

                        case "INPUT":
                            if (!string.IsNullOrEmpty(input))
                            {
                                // Mark new stage for output capture
                                clientAttachment?.StartStage(stage);
                                serverAttachment?.StartStage(stage);
                                
                                // Send input to client container
                                _dockerExecutor.SendInputToContainer(clientContainer, clientContainer, input);
                                
                                // Wait for the input to be processed and response to be captured
                                // This delay allows:
                                // 1. Client to send request to server
                                // 2. Server to process and respond
                                // 3. Client to receive and display response
                                // 4. Console output to be captured
                                await Task.Delay(InputProcessingDelayMs);

                                // Capture client output from application log
                                string newClientOutput;
                                if (clientAttachment != null && clientAttachment.IsRunning)
                                {
                                    newClientOutput = clientAttachment.GetNewOutputSince(clientBaseline);
                                    clientBaseline = clientAttachment.OutputLength;
                                }
                                else
                                {
                                    var output = _dockerExecutor.GetApplicationLog(clientContainer, clientContainer) ?? "";
                                    newClientOutput = output.Length > clientBaseline ? output.Substring(clientBaseline) : "";
                                    if (!string.IsNullOrEmpty(newClientOutput))
                                    {
                                        clientBaseline = output.Length;
                                    }
                                }

                                // Capture server output from application log
                                string newServerOutput;
                                if (serverAttachment != null && serverAttachment.IsRunning)
                                {
                                    newServerOutput = serverAttachment.GetNewOutputSince(serverBaseline);
                                    serverBaseline = serverAttachment.OutputLength;
                                }
                                else
                                {
                                    var output = _dockerExecutor.GetApplicationLog(serverContainer, serverContainer) ?? "";
                                    newServerOutput = output.Length > serverBaseline ? output.Substring(serverBaseline) : "";
                                    if (!string.IsNullOrEmpty(newServerOutput))
                                    {
                                        serverBaseline = output.Length;
                                    }
                                }

                                clientOutputs[stage] = newClientOutput;
                                serverOutputs[stage] = newServerOutput;

                                Console.WriteLine($"    Input sent, client: {newClientOutput.Length} chars, server: {newServerOutput.Length} chars");
                            }
                            break;

                        case "CLOSECLIENT":
                            // Stop client attachment before stopping container
                            if (clientAttachment != null)
                            {
                                clientAttachment.StopAttachment();
                                _consoleManager.RemoveAttachment(clientContainer);
                                clientAttachment = null;
                            }
                            try { _dockerExecutor.StopContainer(clientContainer); } catch { }
                            clientBaseline = 0;
                            break;

                        case "CLOSESERVER":
                            // Stop server attachment before stopping container
                            if (serverAttachment != null)
                            {
                                serverAttachment.StopAttachment();
                                _consoleManager.RemoveAttachment(serverContainer);
                                serverAttachment = null;
                            }
                            try { _dockerExecutor.StopContainer(serverContainer); } catch { }
                            serverBaseline = 0;
                            break;
                    }

                    await Task.Delay(200);
                }
            }
            finally
            {
                // Clean up attachments
                if (clientAttachment != null)
                {
                    _consoleManager.RemoveAttachment(clientContainer);
                }
                if (serverAttachment != null)
                {
                    _consoleManager.RemoveAttachment(serverContainer);
                }
            }

            return (clientOutputs, serverOutputs);
        }

        /// <summary>
        /// Compare outputs and calculate points using ALL-OR-NOTHING policy.
        /// </summary>
        private (double earnedMark, bool passed, List<ComparisonInfo> comparisons) CompareOutputs(
            Dictionary<int, (string? ClientConsole, string? ServerConsole)> expectedOutputs,
            Dictionary<int, string> clientOutputs,
            Dictionary<int, string> serverOutputs,
            double maxMark)
        {
            var comparisons = new List<ComparisonInfo>();
            int total = 0;
            int passed = 0;

            foreach (var (stage, expected) in expectedOutputs)
            {
                if (!string.IsNullOrEmpty(expected.ClientConsole))
                {
                    total++;
                    var actual = clientOutputs.TryGetValue(stage, out var c) ? c : "";
                    var match = CompareText(expected.ClientConsole, actual);
                    if (match) passed++;

                    comparisons.Add(new ComparisonInfo
                    {
                        Source = "Client",
                        Stage = stage,
                        Expected = expected.ClientConsole,
                        Actual = actual,
                        Passed = match,
                        Message = match ? "Text comparison passed" : "Text comparison failed"
                    });

                    Console.WriteLine($"    [Stage {stage}] Client: {(match ? "PASS" : "FAIL")}");
                }

                if (!string.IsNullOrEmpty(expected.ServerConsole))
                {
                    total++;
                    var actual = serverOutputs.TryGetValue(stage, out var s) ? s : "";
                    var match = CompareText(expected.ServerConsole, actual);
                    if (match) passed++;

                    comparisons.Add(new ComparisonInfo
                    {
                        Source = "Server",
                        Stage = stage,
                        Expected = expected.ServerConsole,
                        Actual = actual,
                        Passed = match,
                        Message = match ? "Text comparison passed" : "Text comparison failed"
                    });

                    Console.WriteLine($"    [Stage {stage}] Server: {(match ? "PASS" : "FAIL")}");
                }
            }

            // ALL-OR-NOTHING policy
            bool allPassed = passed == total && total > 0;
            double earnedMark = allPassed ? maxMark : 0;

            Console.WriteLine($"  [Summary] {passed}/{total} comparisons passed, earned: {earnedMark:F2}/{maxMark:F2}");

            return (earnedMark, allPassed, comparisons);
        }

        /// <summary>
        /// Compare expected and actual text (normalized).
        /// </summary>
        private bool CompareText(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected))
                return true;

            var normalizedExpected = NormalizeText(expected);
            var normalizedActual = NormalizeText(actual ?? "");

            return normalizedActual.Contains(normalizedExpected);
        }

        private string NormalizeText(string text)
        {
            return text.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// <summary>
        /// Cleanup between test cases - stops applications and console attachments.
        /// </summary>
        private async Task CleanupBetweenTestCasesAsync(string serverContainer, string clientContainer, int hostPort)
        {
            Console.WriteLine("[Cleanup] Stopping applications between test cases...");

            // Remove any console attachments first
            _consoleManager.RemoveAttachment(serverContainer);
            _consoleManager.RemoveAttachment(clientContainer);

            // Kill dotnet processes in containers using ExecDockerCommand
            try
            {
                _dockerExecutor.ExecDockerCommand($"{serverContainer} pkill -f dotnet", 5000);
            }
            catch { }

            try
            {
                _dockerExecutor.ExecDockerCommand($"{clientContainer} pkill -f dotnet", 5000);
            }
            catch { }

            // Wait for port release
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalSeconds < 10)
            {
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, hostPort);
                    listener.Start();
                    listener.Stop();
                    Console.WriteLine($"[Cleanup] Port {hostPort} is available.");
                    break;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        }

        /// <summary>
        /// Cleanup containers after grading - removes containers and all attachments.
        /// </summary>
        private async Task CleanupContainersAsync(string serverContainer, string clientContainer)
        {
            Console.WriteLine("[Cleanup] Removing containers...");

            // Remove all console attachments
            _consoleManager.RemoveAllAttachments();

            try { _dockerExecutor.RemoveContainer(serverContainer); } catch { }
            try { _dockerExecutor.RemoveContainer(clientContainer); } catch { }

            await Task.Delay(200);
        }

        /// <summary>
        /// Write test case result files in SampleLogging format.
        /// </summary>
        private async Task WriteTestCaseResultAsync(string tcResultPath, string tcName, TestCaseResult tcResult)
        {
            // Write GradeDetail.xlsx
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using (var workbook = new XLWorkbook())
            {
                // User sheet
                var userWs = workbook.Worksheets.Add("User");
                userWs.Cell(1, 1).Value = "Stage";
                userWs.Cell(1, 2).Value = "Input";
                userWs.Cell(1, 3).Value = "Action";
                userWs.Row(1).Style.Font.Bold = true;
                int row = 2;
                foreach (var action in tcResult.Actions)
                {
                    userWs.Cell(row, 1).Value = action.Stage;
                    userWs.Cell(row, 2).Value = action.Input ?? "";
                    userWs.Cell(row, 3).Value = action.ActionType;
                    row++;
                }
                userWs.Columns().AdjustToContents();

                // Client sheet
                var clientWs = workbook.Worksheets.Add("Client");
                clientWs.Cell(1, 1).Value = "Stage";
                clientWs.Cell(1, 2).Value = "Console";
                clientWs.Cell(1, 6).Value = "Result";
                clientWs.Cell(1, 9).Value = "PointsAwarded";
                clientWs.Cell(1, 10).Value = "PointsPossible";
                clientWs.Cell(1, 13).Value = "Message";
                clientWs.Cell(1, 19).Value = "ClientStdout";
                clientWs.Row(1).Style.Font.Bold = true;
                row = 2;
                foreach (var comp in tcResult.ClientComparisons)
                {
                    clientWs.Cell(row, 1).Value = comp.Stage;
                    clientWs.Cell(row, 2).Value = comp.Expected ?? "";
                    clientWs.Cell(row, 6).Value = comp.Passed ? "PASS" : "FAIL";
                    clientWs.Cell(row, 9).Value = comp.PointsAwarded;
                    clientWs.Cell(row, 10).Value = comp.PointsPossible;
                    clientWs.Cell(row, 13).Value = comp.Message ?? "";
                    clientWs.Cell(row, 19).Value = comp.Actual ?? "";
                    row++;
                }
                clientWs.Columns().AdjustToContents();

                // Server sheet
                var serverWs = workbook.Worksheets.Add("Server");
                serverWs.Cell(1, 1).Value = "Stage";
                serverWs.Cell(1, 2).Value = "Console";
                serverWs.Cell(1, 6).Value = "Result";
                serverWs.Cell(1, 9).Value = "PointsAwarded";
                serverWs.Cell(1, 10).Value = "PointsPossible";
                serverWs.Cell(1, 13).Value = "Message";
                serverWs.Cell(1, 19).Value = "ServerStdout";
                serverWs.Row(1).Style.Font.Bold = true;
                row = 2;
                foreach (var comp in tcResult.ServerComparisons)
                {
                    serverWs.Cell(row, 1).Value = comp.Stage;
                    serverWs.Cell(row, 2).Value = comp.Expected ?? "";
                    serverWs.Cell(row, 6).Value = comp.Passed ? "PASS" : "FAIL";
                    serverWs.Cell(row, 9).Value = comp.PointsAwarded;
                    serverWs.Cell(row, 10).Value = comp.PointsPossible;
                    serverWs.Cell(row, 13).Value = comp.Message ?? "";
                    serverWs.Cell(row, 19).Value = comp.Actual ?? "";
                    row++;
                }
                serverWs.Columns().AdjustToContents();

                // Database sheet (placeholder)
                workbook.Worksheets.Add("Database");

                // Network sheet (placeholder)
                var netWs = workbook.Worksheets.Add("Network");
                netWs.Cell(1, 1).Value = "Stage";
                netWs.Cell(1, 6).Value = "Flags";
                netWs.Cell(1, 16).Value = "NetworkResult";
                netWs.Row(1).Style.Font.Bold = true;

                workbook.SaveAs(detailPath);
            }

            // Write TC_Result.xlsx
            var resultPath = Path.Combine(tcResultPath, $"{tcName}_Result.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var ws = workbook.Worksheets.Add("Result");
                ws.Cell(1, 1).Value = "StepId";
                ws.Cell(1, 2).Value = "Stage";
                ws.Cell(1, 3).Value = "Action";
                ws.Cell(1, 4).Value = "Passed";
                ws.Cell(1, 5).Value = "Message";
                ws.Row(1).Style.Font.Bold = true;

                int row = 2;
                foreach (var action in tcResult.Actions)
                {
                    ws.Cell(row, 1).Value = $"USER-{action.ActionType}-{action.Stage}";
                    ws.Cell(row, 2).Value = action.Stage;
                    ws.Cell(row, 3).Value = action.ActionType;
                    ws.Cell(row, 4).Value = true;
                    ws.Cell(row, 5).Value = $"{action.ActionType} executed";
                    row++;
                }
                ws.Columns().AdjustToContents();

                workbook.SaveAs(resultPath);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Write overall summary for a student.
        /// </summary>
        private async Task WriteOverallSummaryAsync(string studentResultPath, List<TestCaseResult> testCaseResults)
        {
            var summaryPath = Path.Combine(studentResultPath, "OverallSummary.xlsx");
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Summary");

            ws.Cell(1, 1).Value = "TestCase";
            ws.Cell(1, 2).Value = "Passed";
            ws.Cell(1, 3).Value = "PointsAwarded";
            ws.Cell(1, 4).Value = "PointsPossible";
            ws.Cell(1, 5).Value = "ErrorNotes";
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var tcResult in testCaseResults)
            {
                ws.Cell(row, 1).Value = tcResult.TestCaseName;
                ws.Cell(row, 2).Value = tcResult.Passed ? "PASS" : "FAIL";
                ws.Cell(row, 3).Value = tcResult.EarnedMark;
                ws.Cell(row, 4).Value = tcResult.MaxMark;
                ws.Cell(row, 5).Value = tcResult.ErrorMessage ?? "";
                row++;
            }

            ws.Columns().AdjustToContents();
            workbook.SaveAs(summaryPath);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Write StudentsSolution.xlsx summary for all students.
        /// </summary>
        private async Task WriteStudentsSolutionSummaryAsync(string resultPath, List<StudentGradingResult> results)
        {
            var paperGroups = results.GroupBy(r => r.PaperNo);
            foreach (var paperGroup in paperGroups)
            {
                var paperPath = Path.Combine(resultPath, paperGroup.Key);
                Directory.CreateDirectory(paperPath);

                var summaryPath = Path.Combine(paperPath, "StudentsSolution.xlsx");
                using var workbook = new XLWorkbook();
                var ws = workbook.Worksheets.Add("Summary");

                ws.Cell(1, 1).Value = "StudentCode";
                ws.Cell(1, 2).Value = "Paper";
                ws.Cell(1, 3).Value = "TotalMark";
                ws.Cell(1, 4).Value = "MaxMark";
                ws.Cell(1, 5).Value = "Percentage";
                ws.Cell(1, 6).Value = "Status";
                ws.Cell(1, 7).Value = "Notes";
                ws.Row(1).Style.Font.Bold = true;

                int row = 2;
                foreach (var student in paperGroup)
                {
                    ws.Cell(row, 1).Value = student.StudentCode;
                    ws.Cell(row, 2).Value = student.PaperNo;
                    ws.Cell(row, 3).Value = student.TotalMark;
                    ws.Cell(row, 4).Value = student.MaxMark;
                    ws.Cell(row, 5).Value = student.MaxMark > 0 ? (student.TotalMark / student.MaxMark * 100) : 0;
                    ws.Cell(row, 6).Value = student.Passed ? "PASSED" : "FAILED";
                    ws.Cell(row, 7).Value = student.ErrorMessage ?? "";
                    row++;
                }

                ws.Columns().AdjustToContents();
                workbook.SaveAs(summaryPath);

                Console.WriteLine($"[Output] StudentsSolution.xlsx written to {summaryPath}");
            }

            await Task.CompletedTask;
        }

        #region Model Classes

        private class StudentInfo
        {
            public string StudentCode { get; set; } = "";
            public string PaperNo { get; set; } = "";
            public string SolutionPath { get; set; } = "";
            public string? ServerDllPath { get; set; }
            public string? ClientDllPath { get; set; }
        }

        private class TestKitConfig
        {
            public int CodeContainerInternalPort { get; set; } = 8000;
            public int CodeContainerHostPort { get; set; } = 8000;
            public string CodeImageName { get; set; } = "fptuxaes/aes-dotnet8-console:latest";
            public string DockerNetwork { get; set; } = "auto-grading-network";
            public string DatabaseName { get; set; } = "Library";
            public string DatabasePassword { get; set; } = "";
            public string Protocol { get; set; } = "TCP";
            public Dictionary<string, double> TestCaseMarks { get; set; } = new();
            public List<TestCaseConfig> TestCases { get; set; } = new();
            public double TotalMaxMark => TestCases.Sum(tc => tc.MaxMark);
        }

        private class TestCaseConfig
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public double MaxMark { get; set; }
        }

        private class StudentGradingResult
        {
            public string StudentCode { get; set; } = "";
            public string PaperNo { get; set; } = "";
            public double TotalMark { get; set; }
            public double MaxMark { get; set; }
            public bool Passed { get; set; }
            public string? ErrorMessage { get; set; }
            public List<TestCaseResult> TestCaseResults { get; set; } = new();
        }

        private class TestCaseResult
        {
            public string TestCaseName { get; set; } = "";
            public double EarnedMark { get; set; }
            public double MaxMark { get; set; }
            public bool Passed { get; set; }
            public string? ErrorMessage { get; set; }
            public List<ActionInfo> Actions { get; set; } = new();
            public List<ComparisonInfo> ClientComparisons { get; set; } = new();
            public List<ComparisonInfo> ServerComparisons { get; set; } = new();
        }

        private class ActionInfo
        {
            public int Stage { get; set; }
            public string? Input { get; set; }
            public string ActionType { get; set; } = "";
        }

        private class ComparisonInfo
        {
            public string Source { get; set; } = "";
            public int Stage { get; set; }
            public string? Expected { get; set; }
            public string? Actual { get; set; }
            public bool Passed { get; set; }
            public double PointsAwarded { get; set; }
            public double PointsPossible { get; set; }
            public string? Message { get; set; }
        }

        #endregion
    }
}
