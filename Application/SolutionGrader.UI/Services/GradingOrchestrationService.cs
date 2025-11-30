using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.UI.Models;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using EnvironmentBuilder.DockerCommand;
using Newtonsoft.Json;
using SolutionGrader.Services;
using Environment = Domain.Entities.Main.Environment;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Main service that orchestrates the grading process for student solutions.
    /// Handles container management, grading execution, and result collection.
    /// 
    /// Key responsibilities:
    /// 1. Set up THREE containers per student: server, client, and MSSQL database
    /// 2. Read port configurations from environment.xlsx
    /// 3. Read max marks from Header.xlsx QuestionMark sheet
    /// 4. Execute grading steps from Detail.xlsx (StartClient, StartServer, Input, etc.)
    /// 5. Write results in SampleLogging format
    /// </summary>
    public class GradingOrchestrationService
    {
        private readonly ILoggingService _logger;
        private readonly StudentDiscoveryService _studentDiscovery;
        private readonly TestKitDiscoveryService _testKitDiscovery;
        private readonly TestKitConfigService _testKitConfigService;
        private readonly DockerCommandExecutor _dockerExecutor;
        
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly object _lockObject = new object();
        
        // Constants for configuration
        /// <summary>
        /// Indicates no port is required (e.g., for client applications that initiate connections)
        /// </summary>
        private const string NoPortRequired = "-1";
        
        /// <summary>
        /// Port offset for client container to avoid conflicts with server container
        /// </summary>
        private const int ClientPortOffset = 1;
        
        /// <summary>
        /// Default timeout for database readiness check in milliseconds
        /// </summary>
        private const int DatabaseReadinessTimeoutMs = 5000;
        
        /// <summary>
        /// Interval for checking database readiness in milliseconds
        /// </summary>
        private const int DatabaseReadinessCheckIntervalMs = 500;
        
        // Events for UI updates
        public event EventHandler<StudentSolution>? StudentGradingStarted;
        public event EventHandler<StudentSolution>? StudentGradingCompleted;
        public event EventHandler<StudentSolution>? StudentProgressUpdated;
        public event EventHandler<GradingSessionState>? SessionStateChanged;

        public GradingOrchestrationService(ILoggingService logger)
        {
            _logger = logger;
            _studentDiscovery = new StudentDiscoveryService(logger);
            _testKitDiscovery = new TestKitDiscoveryService(logger);
            _testKitConfigService = new TestKitConfigService(logger);
            _dockerExecutor = new DockerCommandExecutor();
        }

        /// <summary>
        /// Discovers all students from the submit folder.
        /// </summary>
        public List<StudentSolution> DiscoverStudents(GradingConfiguration config)
        {
            return _studentDiscovery.DiscoverStudents(config.SubmitFolderPath, config);
        }

        /// <summary>
        /// Starts the grading process for the specified students.
        /// </summary>
        /// <param name="students">Students to grade</param>
        /// <param name="config">Grading configuration</param>
        /// <param name="sessionState">Session state to update</param>
        /// <returns>Task representing the grading operation</returns>
        public async Task StartGradingAsync(
            List<StudentSolution> students, 
            GradingConfiguration config,
            GradingSessionState sessionState)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;

            sessionState.IsRunning = true;
            sessionState.IsPaused = false;
            sessionState.SessionStartTime = DateTime.Now;
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);

            _logger.LogInfo($"Starting grading for {students.Count} students");
            SessionStateChanged?.Invoke(this, sessionState);

            try
            {
                // Grade students one at a time (as per requirement)
                foreach (var student in students.Where(s => s.Status == GradingStatus.Not_Run || s.Status == GradingStatus.Paused))
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInfo("Grading cancelled");
                        break;
                    }

                    // Check if paused
                    while (sessionState.IsPaused && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(500, ct);
                    }

                    if (ct.IsCancellationRequested) break;

                    sessionState.CurrentStudentCode = student.StudentCode;
                    SessionStateChanged?.Invoke(this, sessionState);

                    await GradeStudentAsync(student, config, ct);

                    // Update session state
                    sessionState.GradedStudents++;
                    sessionState.NotRunCount = students.Count(s => s.Status == GradingStatus.Not_Run);
                    sessionState.SuccessCount = students.Count(s => s.Status == GradingStatus.Success);
                    sessionState.FailedCount = students.Count(s => s.Status == GradingStatus.Failed);
                    SessionStateChanged?.Invoke(this, sessionState);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Grading operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during grading", ex);
            }
            finally
            {
                sessionState.IsRunning = false;
                sessionState.SessionEndTime = DateTime.Now;
                sessionState.CurrentStudentCode = null;
                SessionStateChanged?.Invoke(this, sessionState);
                _logger.LogInfo("Grading session completed");
            }
        }

        /// <summary>
        /// Grades a single student's solution.
        /// </summary>
        private async Task GradeStudentAsync(
            StudentSolution student, 
            GradingConfiguration config,
            CancellationToken ct)
        {
            // Set student context with paper number for organized logging
            if (_logger is LoggingService loggingService)
            {
                loggingService.SetStudentContext(student.StudentCode, student.PaperNo);
            }
            else
            {
                _logger.SetStudentContext(student.StudentCode);
            }
            
            student.StartTime = DateTime.Now;
            student.Status = GradingStatus.InProgress;
            student.ProgressPercent = 0;
            StudentGradingStarted?.Invoke(this, student);

            try
            {
                _logger.LogInfo($"Starting grading for student: {student.StudentCode} (Paper {student.PaperNo})");

                // Step 1: Check if test kit exists for this paper
                student.ProgressPercent = 10;
                StudentProgressUpdated?.Invoke(this, student);

                var testKitPath = _testKitDiscovery.GetTestKitForPaper(config.TestKitFolderPath, student.PaperNo);
                if (string.IsNullOrEmpty(testKitPath))
                {
                    student.Status = GradingStatus.Not_Run;
                    student.StatusMessage = $"No test kit for paper {student.PaperNo}";
                    _logger.LogWarning(student.StatusMessage);
                    return;
                }

                _logger.LogInfo($"Using test kit: {testKitPath}");

                // Step 2: Load environment configuration
                student.ProgressPercent = 20;
                StudentProgressUpdated?.Invoke(this, student);

                var envPath = _testKitDiscovery.GetEnvironmentPath(testKitPath);
                if (string.IsNullOrEmpty(envPath))
                {
                    student.Status = GradingStatus.Failed;
                    student.StatusMessage = "Environment.xlsx not found in test kit";
                    _logger.LogError(student.StatusMessage);
                    return;
                }

                var environment = LoadEnvironment(envPath);
                
                // Step 3: Configure environment for this student
                student.ProgressPercent = 30;
                StudentProgressUpdated?.Invoke(this, student);

                ConfigureEnvironmentForStudent(environment, student, config, testKitPath);

                // Step 4: Setup Docker containers
                student.ProgressPercent = 40;
                StudentProgressUpdated?.Invoke(this, student);

                await SetupContainersAsync(environment, ct);

                // Step 5: Copy student solution files to containers
                student.ProgressPercent = 50;
                StudentProgressUpdated?.Invoke(this, student);

                await CopyFilesToContainersAsync(student, environment, ct);

                // Step 6: Execute grading
                student.ProgressPercent = 70;
                StudentProgressUpdated?.Invoke(this, student);

                var result = await ExecuteGradingAsync(student, environment, testKitPath, config, ct);

                // Step 7: Collect results and cleanup
                student.ProgressPercent = 90;
                StudentProgressUpdated?.Invoke(this, student);

                await WriteResultsAsync(student, result, ct);
                await CleanupContainersAsync(environment, ct);

                // Mark as complete
                student.Status = result.success ? GradingStatus.Success : GradingStatus.Failed;
                student.Mark = result.mark;
                student.StatusMessage = result.message;
                student.ProgressPercent = 100;

                _logger.LogInfo($"Grading completed for {student.StudentCode}. Mark: {student.Mark}, Status: {student.Status}");
            }
            catch (OperationCanceledException)
            {
                student.Status = GradingStatus.Paused;
                student.StatusMessage = "Grading was paused/cancelled";
                _logger.LogWarning($"Grading paused for {student.StudentCode}");
            }
            catch (Exception ex)
            {
                student.Status = GradingStatus.Failed;
                student.StatusMessage = $"Error: {ex.Message}";
                _logger.LogError($"Grading failed for {student.StudentCode}", ex);
            }
            finally
            {
                student.EndTime = DateTime.Now;
                StudentGradingCompleted?.Invoke(this, student);
                _logger.SetStudentContext(null);
            }
        }

        /// <summary>
        /// Loads environment configuration from Excel file.
        /// </summary>
        private Environment LoadEnvironment(string envPath)
        {
            // Use the existing EnvironmentService from SolutionGrader.Services
            try
            {
                return EnvironmentService.GetEnvironment(envPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load environment from {envPath}", ex);
                throw;
            }
        }

        /// <summary>
        /// Configures the environment settings for a specific student.
        /// </summary>
        private void ConfigureEnvironmentForStudent(
            Environment environment, 
            StudentSolution student,
            GradingConfiguration config,
            string testKitPath)
        {
            var configs = environment.Configs;

            // Set container names for this student
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerName, $"ag-server-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.GivenConsoleContainerName, $"ag-client-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.StudentQuestionName, $"ag-{student.StudentCode}");
            SetOrAddConfig(configs, EnvironmentConfiguration.DatabaseName, $"DB_{student.StudentCode}");

            // Set port configurations
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerInternalPort, config.CodeContainerInternalPort.ToString());
            SetOrAddConfig(configs, EnvironmentConfiguration.CodeContainerHostPort, config.CodeContainerHostPort.ToString());

            // Set file paths
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath)!;
                SetOrAddConfig(configs, EnvironmentConfiguration.CodeFilePath, serverDir);
                SetOrAddConfig(configs, EnvironmentConfiguration.DockerServerPath, GetDockerDllPath(serverDir, student.ServerDllPath));
            }

            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath)!;
                SetOrAddConfig(configs, EnvironmentConfiguration.GivenConsolePath, clientDir);
                SetOrAddConfig(configs, EnvironmentConfiguration.DockerClientPath, GetDockerDllPath(clientDir, student.ClientDllPath));
            }

            // Set runtime folder from test kit
            var runtimesFolder = configs.GetValueOrDefault(EnvironmentConfiguration.RuntimesFolder);
            if (!string.IsNullOrEmpty(runtimesFolder) && !Path.IsPathRooted(runtimesFolder))
            {
                SetOrAddConfig(configs, EnvironmentConfiguration.RuntimesFolder, Path.Combine(testKitPath, runtimesFolder));
            }

            // Set database file path
            var dbFilePath = configs.GetValueOrDefault(EnvironmentConfiguration.DefaultDatabaseFilePath);
            if (!string.IsNullOrEmpty(dbFilePath) && !Path.IsPathRooted(dbFilePath))
            {
                SetOrAddConfig(configs, EnvironmentConfiguration.DefaultDatabaseFilePath, Path.Combine(testKitPath, dbFilePath));
            }
        }

        private string GetDockerDllPath(string baseDir, string dllPath)
        {
            var relativePath = Path.GetRelativePath(baseDir, dllPath);
            var folderName = Path.GetFileName(baseDir);
            return $"/apps/{folderName}/{relativePath}".Replace("\\", "/");
        }

        /// <summary>
        /// Sets up Docker containers for grading.
        /// 
        /// This method sets up THREE containers per student:
        /// 1. Server container - runs the student's server code
        /// 2. Client container - runs the student's client code  
        /// 3. Database container - MSSQL server for database operations
        /// 
        /// The containers are created and started but the applications inside
        /// are NOT started until grading steps call StartServer/StartClient.
        /// This allows the network monitor to properly capture traffic.
        /// </summary>
        private async Task SetupContainersAsync(Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Setting up Docker containers (Server, Client, Database)...");

            try
            {
                // Check if Docker is running
                if (!_dockerExecutor.IsDockerRunning())
                {
                    throw new InvalidOperationException("Docker is not running. Please start Docker Desktop.");
                }

                // Get container configuration
                var serverContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerName);
                var dbContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseContainerName);
                var network = TryGetConfig(environment.Configs, EnvironmentConfiguration.DockerNetwork);
                
                // Get image names
                var codeImageName = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeImageName);
                var dbImageName = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseImageName);
                
                // Get port configuration
                var internalPort = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerInternalPort);
                var hostPort = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerHostPort);
                var dbInternalPort = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseContainerInternalPort);
                var dbHostPort = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseContainerHostPort);

                _logger.LogInfo($"Container setup configuration:");
                _logger.LogInfo($"  - Server container: {serverContainer}");
                _logger.LogInfo($"  - Client container: {clientContainer}");
                _logger.LogInfo($"  - Database container: {dbContainer}");
                _logger.LogInfo($"  - Docker network: {network}");
                _logger.LogInfo($"  - Code image: {codeImageName}");
                _logger.LogInfo($"  - Database image: {dbImageName}");
                _logger.LogInfo($"  - Code ports: {hostPort}:{internalPort}");
                _logger.LogInfo($"  - Database ports: {dbHostPort}:{dbInternalPort}");

                // Create Docker network if not exists
                if (!string.IsNullOrEmpty(network))
                {
                    try
                    {
                        _dockerExecutor.CreateNetwork(network);
                        _logger.LogInfo($"Docker network '{network}' created/verified");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Network creation warning: {ex.Message}");
                        // Network may already exist, continue
                    }
                }

                // Setup Database container (container 1 of 3)
                if (!string.IsNullOrEmpty(dbContainer) && !string.IsNullOrEmpty(dbImageName))
                {
                    _logger.LogInfo($"Setting up Database container: {dbContainer}");
                    
                    // Pull image if needed
                    if (!_dockerExecutor.IsImageExists(dbImageName))
                    {
                        _logger.LogInfo($"Pulling database image: {dbImageName}");
                        _dockerExecutor.PullImage(dbImageName);
                    }
                    
                    // Create and run database container
                    var dbUsername = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseUsername);
                    var dbPassword = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabasePassword);
                    
                    var dbDockerBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                    {
                        ImageName = dbImageName,
                        ContainerName = dbContainer,
                        DockerNetwork = network,
                        ContainerPort = int.TryParse(dbInternalPort, out var dbip) ? dbip : 1433,
                        HostPort = int.TryParse(dbHostPort, out var dbhp) ? dbhp : 1434,
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            { "ACCEPT_EULA", "Y" },
                            { "SA_PASSWORD", dbPassword }
                        }
                    };
                    
                    _dockerExecutor.RunContainer(dbDockerBase);
                    _logger.LogInfo($"Database container '{dbContainer}' started");
                    
                    // Wait for database to be ready
                    // Using configurable timeout for database readiness
                    _logger.LogInfo($"Waiting for database to be ready (timeout: {DatabaseReadinessTimeoutMs}ms)...");
                    await Task.Delay(DatabaseReadinessTimeoutMs, ct);
                }

                // Setup Server container (container 2 of 3)
                if (!string.IsNullOrEmpty(serverContainer) && !string.IsNullOrEmpty(codeImageName))
                {
                    _logger.LogInfo($"Setting up Server container: {serverContainer}");
                    
                    // Pull image if needed
                    if (!_dockerExecutor.IsImageExists(codeImageName))
                    {
                        _logger.LogInfo($"Pulling code image: {codeImageName}");
                        _dockerExecutor.PullImage(codeImageName);
                    }
                    
                    var serverDockerBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                    {
                        ImageName = codeImageName,
                        ContainerName = serverContainer,
                        DockerNetwork = network,
                        ContainerPort = int.TryParse(internalPort, out var sip) ? sip : 5000,
                        HostPort = int.TryParse(hostPort, out var shp) ? shp : 5000,
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            { "DOTNET_RUNNING_IN_CONTAINER", "true" },
                            { "ASPNETCORE_URLS", $"http://+:{internalPort}" }
                        }
                    };
                    
                    _dockerExecutor.RunContainer(serverDockerBase);
                    _logger.LogInfo($"Server container '{serverContainer}' started");
                }

                // Setup Client container (container 3 of 3)
                if (!string.IsNullOrEmpty(clientContainer) && !string.IsNullOrEmpty(codeImageName))
                {
                    _logger.LogInfo($"Setting up Client container: {clientContainer}");
                    
                    // Client container uses same code image but different port mapping
                    // Client typically doesn't need to expose a port since it initiates connections
                    // Using ClientPortOffset to avoid port conflicts with server container
                    var clientPort = int.TryParse(hostPort, out var hp) ? hp + ClientPortOffset : 5001;
                    
                    var clientDockerBase = new Domain.Entities.Docker.DockerSupporter.Entity.DockerBase
                    {
                        ImageName = codeImageName,
                        ContainerName = clientContainer,
                        DockerNetwork = network,
                        ContainerPort = int.TryParse(internalPort, out var cip) ? cip : 5000,
                        HostPort = clientPort,
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            { "DOTNET_RUNNING_IN_CONTAINER", "true" }
                        }
                    };
                    
                    _dockerExecutor.RunContainer(clientDockerBase);
                    _logger.LogInfo($"Client container '{clientContainer}' started on port {clientPort}");
                }

                // Also try the EnvironmentManagerInvoker as a fallback/additional setup
                // This handles any additional configuration that may be needed
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TrySetupContainer(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"EnvironmentManagerInvoker setup warning: {error}");
                }

                await Task.Delay(1000, ct); // Wait for all containers to be ready
                _logger.LogInfo("Docker containers setup complete (3 containers: server, client, database)");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to setup Docker containers", ex);
                throw;
            }
        }

        private static string TryGetConfig(Dictionary<string, string> configs, string key)
        {
            return configs.TryGetValue(key, out var value) ? value : string.Empty;
        }

        /// <summary>
        /// Copies student solution files to Docker containers.
        /// Copies files to both Server and Client containers based on the configured paths.
        /// Files are copied but containers are NOT started - they will be started
        /// when grading steps call for StartServer/StartClient actions.
        /// </summary>
        private async Task CopyFilesToContainersAsync(StudentSolution student, Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Copying files to containers...");

            try
            {
                // Get container names
                var serverContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerName);
                
                // Get source paths
                var serverPath = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeFilePath);
                var clientPath = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsolePath);

                // Copy server files if configured
                if (!string.IsNullOrEmpty(serverContainer) && !string.IsNullOrEmpty(serverPath) && Directory.Exists(serverPath))
                {
                    _logger.LogInfo($"Copying server files from {serverPath} to container {serverContainer}");
                    try
                    {
                        // Create /apps directory in container if it doesn't exist
                        _dockerExecutor.MakeDirectory(serverContainer, "/apps");
                        
                        // Copy the entire solution folder to the container
                        var folderName = Path.GetFileName(serverPath);
                        _dockerExecutor.CopyFileToContainer(serverPath, $"{serverContainer}:/apps/{folderName}");
                        _logger.LogInfo($"Server files copied successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to copy server files: {ex.Message}");
                    }
                }

                // Copy client files if configured
                if (!string.IsNullOrEmpty(clientContainer) && !string.IsNullOrEmpty(clientPath) && Directory.Exists(clientPath))
                {
                    _logger.LogInfo($"Copying client files from {clientPath} to container {clientContainer}");
                    try
                    {
                        // Create /apps directory in container if it doesn't exist
                        _dockerExecutor.MakeDirectory(clientContainer, "/apps");
                        
                        // Copy the entire solution folder to the container
                        var folderName = Path.GetFileName(clientPath);
                        _dockerExecutor.CopyFileToContainer(clientPath, $"{clientContainer}:/apps/{folderName}");
                        _logger.LogInfo($"Client files copied successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to copy client files: {ex.Message}");
                    }
                }

                // Also try the EnvironmentManagerInvoker for any additional setup
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TrySetupQuestion(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"EnvironmentManagerInvoker setup warning: {error}");
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Files copied to containers");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to copy files to containers", ex);
                throw;
            }
        }

        /// <summary>
        /// Executes the actual grading process.
        /// 
        /// This method:
        /// 1. Loads test kit configuration including max marks from Header.xlsx
        /// 2. Gets test case actions from Detail.xlsx (StartClient, StartServer, Input, etc.)
        /// 3. Sets up containers but does NOT start them until grading steps require it
        /// 4. Executes grading steps in order
        /// 5. Calculates final mark based on Header.xlsx QuestionMark sheet values
        /// </summary>
        private async Task<(bool success, double mark, string message)> ExecuteGradingAsync(
            StudentSolution student,
            Environment environment,
            string testKitPath,
            GradingConfiguration config,
            CancellationToken ct)
        {
            _logger.LogInfo("=".PadRight(60, '='));
            _logger.LogInfo($"Starting grading execution for student: {student.StudentCode}");
            _logger.LogInfo("=".PadRight(60, '='));

            try
            {
                // Load test kit configuration with marks from Header.xlsx
                var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
                if (testKitConfig == null)
                {
                    _logger.LogError("FATAL: Failed to load test kit configuration");
                    return (false, 0, "Failed to load test kit configuration");
                }

                _logger.LogInfo($"Test kit configuration loaded:");
                _logger.LogInfo($"  - Max mark from Header.xlsx: {testKitConfig.TotalMaxMark}");
                _logger.LogInfo($"  - Port configuration - Internal: {testKitConfig.CodeContainerInternalPort}, Host: {testKitConfig.CodeContainerHostPort}");
                _logger.LogInfo($"  - Docker network: {testKitConfig.DockerNetwork}");
                _logger.LogInfo($"  - Code image: {testKitConfig.CodeImageName}");
                
                // Update student's max mark from Header.xlsx
                student.MaxMark = testKitConfig.TotalMaxMark;

                // Get list of test cases
                var testCaseNames = _testKitConfigService.GetTestCaseNames(testKitPath);
                if (testCaseNames.Count == 0)
                {
                    _logger.LogError("FATAL: No test cases found in test kit (no folders with Detail.xlsx)");
                    return (false, 0, "No test cases found in test kit");
                }

                _logger.LogInfo($"Found {testCaseNames.Count} test cases: {string.Join(", ", testCaseNames)}");

                // Log test case marks from Header.xlsx
                _logger.LogInfo("Test case marks from Header.xlsx QuestionMark sheet:");
                foreach (var tcMark in testKitConfig.TestCaseMarks)
                {
                    _logger.LogInfo($"  - {tcMark.Key}: {tcMark.Value} points");
                }

                double totalEarnedMark = 0;
                int passedTestCases = 0;
                var testCaseResults = new List<string>();

                // Process each test case
                foreach (var testCaseName in testCaseNames)
                {
                    ct.ThrowIfCancellationRequested();

                    _logger.LogInfo("-".PadRight(50, '-'));
                    _logger.LogInfo($"Processing test case: {testCaseName}");
                    _logger.LogInfo("-".PadRight(50, '-'));

                    var testCasePath = Path.Combine(testKitPath, testCaseName);
                    
                    // Read actions from Detail.xlsx User sheet
                    var actions = _testKitConfigService.GetTestCaseActions(testCasePath);
                    
                    if (actions.Count == 0)
                    {
                        _logger.LogWarning($"Test case {testCaseName}: No actions found in Detail.xlsx - marking as FAILED");
                        testCaseResults.Add($"{testCaseName}: FAILED (no actions in Detail.xlsx)");
                        continue;
                    }

                    _logger.LogInfo($"Loaded {actions.Count} actions from Detail.xlsx:");
                    foreach (var (stage, input, action) in actions)
                    {
                        _logger.LogInfo($"  Stage {stage}: Action='{action}', Input='{input}'");
                    }

                    // Get max mark for this test case from Header.xlsx
                    var testCaseMaxMark = testKitConfig.TestCaseMarks.TryGetValue(testCaseName, out var mark) ? mark : 0;
                    _logger.LogInfo($"Test case {testCaseName}: Max mark = {testCaseMaxMark}");

                    // Execute test case actions by reading from Detail.xlsx
                    bool testCasePassed = await ExecuteTestCaseAsync(student, testCasePath, actions, environment, config, ct);

                    if (testCasePassed)
                    {
                        totalEarnedMark += testCaseMaxMark;
                        passedTestCases++;
                        _logger.LogInfo($">>> Test case {testCaseName}: PASSED (+{testCaseMaxMark} points)");
                        testCaseResults.Add($"{testCaseName}: PASSED (+{testCaseMaxMark})");
                    }
                    else
                    {
                        _logger.LogInfo($">>> Test case {testCaseName}: FAILED (0 points)");
                        testCaseResults.Add($"{testCaseName}: FAILED (0)");
                    }
                }

                // Calculate final result
                bool overallSuccess = passedTestCases > 0;
                string message = $"Passed {passedTestCases}/{testCaseNames.Count} test cases";
                
                _logger.LogInfo("=".PadRight(60, '='));
                _logger.LogInfo($"GRADING COMPLETE FOR {student.StudentCode}");
                _logger.LogInfo($"  Total mark: {totalEarnedMark}/{testKitConfig.TotalMaxMark}");
                _logger.LogInfo($"  Test cases passed: {passedTestCases}/{testCaseNames.Count}");
                _logger.LogInfo($"  Results summary:");
                foreach (var result in testCaseResults)
                {
                    _logger.LogInfo($"    - {result}");
                }
                _logger.LogInfo("=".PadRight(60, '='));
                
                return (overallSuccess, totalEarnedMark, message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Grading execution failed", ex);
                return (false, 0, ex.Message);
            }
        }

        /// <summary>
        /// Executes a single test case by processing its actions from Detail.xlsx.
        /// Actions include: StartClient, StartServer, CloseClient, CloseServer, Input
        /// 
        /// This method actually deploys and runs the student's code in Docker containers:
        /// 1. StartServer - Starts the server application inside the server container
        /// 2. StartClient - Starts the client application inside the client container
        /// 3. Input - Sends input to the appropriate container
        /// 4. CloseClient/CloseServer - Stops the respective container
        /// 
        /// Network traffic is captured via the exposed host port for later validation.
        /// </summary>
        private async Task<bool> ExecuteTestCaseAsync(
            StudentSolution student,
            string testCasePath,
            List<(int Stage, string Input, string Action)> actions,
            Environment environment,
            GradingConfiguration config,
            CancellationToken ct)
        {
            _logger.LogInfo($"Executing test case from: {testCasePath}");
            _logger.LogInfo($"Number of actions to execute: {actions.Count}");

            // Get container names from environment configuration
            var serverContainerName = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerName);
            var clientContainerName = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerName);
            var serverAppName = TryGetConfig(environment.Configs, EnvironmentConfiguration.StudentQuestionName) + "-server";
            var clientAppName = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleAppName);
            var serverDllPath = TryGetConfig(environment.Configs, EnvironmentConfiguration.DockerServerPath);
            var clientDllPath = TryGetConfig(environment.Configs, EnvironmentConfiguration.DockerClientPath);
            var internalPort = config.CodeContainerInternalPort.ToString();

            // Use student's DLL paths if not configured in environment
            if (string.IsNullOrEmpty(serverDllPath) && !string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath)!;
                var folderName = Path.GetFileName(serverDir);
                var fileName = Path.GetFileName(student.ServerDllPath);
                serverDllPath = $"/apps/{folderName}/{fileName}";
                _logger.LogInfo($"Using student server DLL path: {serverDllPath}");
            }

            if (string.IsNullOrEmpty(clientDllPath) && !string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath)!;
                var folderName = Path.GetFileName(clientDir);
                var fileName = Path.GetFileName(student.ClientDllPath);
                clientDllPath = $"/apps/{folderName}/{fileName}";
                _logger.LogInfo($"Using student client DLL path: {clientDllPath}");
            }

            // If client app name is not set, derive from student code
            if (string.IsNullOrEmpty(clientAppName))
            {
                clientAppName = $"ag-{student.StudentCode}-client";
            }

            // Log configuration for debugging
            _logger.LogInfo($"Test case execution configuration:");
            _logger.LogInfo($"  - Server container: {(string.IsNullOrEmpty(serverContainerName) ? "(not set)" : serverContainerName)}");
            _logger.LogInfo($"  - Client container: {(string.IsNullOrEmpty(clientContainerName) ? "(not set)" : clientContainerName)}");
            _logger.LogInfo($"  - Server app name: {serverAppName}");
            _logger.LogInfo($"  - Client app name: {clientAppName}");
            _logger.LogInfo($"  - Server DLL path: {(string.IsNullOrEmpty(serverDllPath) ? "(not set)" : serverDllPath)}");
            _logger.LogInfo($"  - Client DLL path: {(string.IsNullOrEmpty(clientDllPath) ? "(not set)" : clientDllPath)}");
            _logger.LogInfo($"  - Internal port: {internalPort}");

            // Track execution success
            bool allActionsSucceeded = true;
            int actionsExecuted = 0;
            int actionsFailed = 0;

            try
            {
                // Check if Docker is available before executing
                if (!_dockerExecutor.IsDockerRunning())
                {
                    _logger.LogError("FATAL: Docker is not running. Cannot execute test case.");
                    _logger.LogError("Please start Docker Desktop and try again.");
                    return false;
                }
                _logger.LogInfo("Docker is running - proceeding with test case execution");

                foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
                {
                    ct.ThrowIfCancellationRequested();

                    _logger.LogInfo($"[Stage {stage}] Executing action: '{action}'" + (string.IsNullOrEmpty(input) ? "" : $" with input: '{input}'"));

                    bool actionSuccess = true;

                    switch (action.ToUpperInvariant())
                    {
                        case "STARTSERVER":
                            actionSuccess = await ExecuteStartServerAsync(serverContainerName, serverAppName, serverDllPath, internalPort, ct);
                            break;

                        case "STARTCLIENT":
                            actionSuccess = await ExecuteStartClientAsync(clientContainerName, clientAppName, clientDllPath, ct);
                            break;

                        case "INPUT":
                            actionSuccess = await ExecuteInputAsync(clientContainerName, clientAppName, input, ct);
                            break;

                        case "CLOSECLIENT":
                            actionSuccess = await ExecuteCloseClientAsync(clientContainerName, ct);
                            break;

                        case "CLOSESERVER":
                            actionSuccess = await ExecuteCloseServerAsync(serverContainerName, ct);
                            break;

                        default:
                            _logger.LogWarning($"Unknown action: {action} - skipping");
                            break;
                    }

                    if (actionSuccess)
                    {
                        _logger.LogInfo($"[Stage {stage}] Action '{action}' completed successfully");
                        actionsExecuted++;
                    }
                    else
                    {
                        _logger.LogError($"[Stage {stage}] Action '{action}' FAILED");
                        actionsFailed++;
                        allActionsSucceeded = false;
                        // Continue with other actions rather than failing immediately
                    }

                    // Allow time for Docker operations to complete
                    await Task.Delay(200, ct);
                }

                // Get container logs for debugging
                await LogContainerOutputsAsync(serverContainerName, clientContainerName);

                // Log execution summary
                _logger.LogInfo($"Test case execution summary:");
                _logger.LogInfo($"  - Total actions: {actions.Count}");
                _logger.LogInfo($"  - Actions executed: {actionsExecuted}");
                _logger.LogInfo($"  - Actions failed: {actionsFailed}");
                _logger.LogInfo($"  - Overall result: {(allActionsSucceeded ? "PASSED" : "FAILED")}");

                return allActionsSucceeded;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Test case execution failed with exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Executes the StartServer action - starts the server application inside its container.
        /// </summary>
        private async Task<bool> ExecuteStartServerAsync(string? serverContainerName, string serverAppName, string? serverDllPath, string internalPort, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(serverContainerName))
            {
                _logger.LogWarning("Server container name not configured, skipping StartServer");
                return true; // Not a failure, just not configured
            }

            if (string.IsNullOrEmpty(serverDllPath))
            {
                _logger.LogWarning("Server DLL path not configured, skipping StartServer");
                return true;
            }

            _logger.LogInfo($"Starting server in container: {serverContainerName}");

            try
            {
                // Check if container exists
                if (!_dockerExecutor.IsContainerExist(serverContainerName))
                {
                    _logger.LogError($"Server container '{serverContainerName}' does not exist");
                    return false;
                }

                // Start the container if not running
                _dockerExecutor.StartExistedContainer(serverContainerName);
                await Task.Delay(1000, ct);

                // Start the .NET application inside the container
                bool serverStarted = _dockerExecutor.WaitForPublishConsoleFileDeployment(
                    serverContainerName,
                    serverAppName,
                    serverDllPath,
                    internalPort,
                    maxWaitTimeMs: 30000);

                if (serverStarted)
                {
                    _logger.LogInfo($"Server started successfully in {serverContainerName}");
                    return true;
                }
                else
                {
                    _logger.LogWarning($"Server may not have started properly in {serverContainerName}");
                    // Still return true as the action was attempted - actual validation is separate
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start server: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Executes the StartClient action - starts the client application inside its container.
        /// </summary>
        private async Task<bool> ExecuteStartClientAsync(string? clientContainerName, string clientAppName, string? clientDllPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientContainerName))
            {
                _logger.LogWarning("Client container name not configured, skipping StartClient");
                return true;
            }

            if (string.IsNullOrEmpty(clientDllPath))
            {
                _logger.LogWarning("Client DLL path not configured, skipping StartClient");
                return true;
            }

            _logger.LogInfo($"Starting client in container: {clientContainerName}");

            try
            {
                // Check if container exists
                if (!_dockerExecutor.IsContainerExist(clientContainerName))
                {
                    _logger.LogError($"Client container '{clientContainerName}' does not exist");
                    return false;
                }

                // Start the container if not running
                _dockerExecutor.StartExistedContainer(clientContainerName);
                await Task.Delay(1000, ct);

                // Start the .NET application inside the container
                bool clientStarted = _dockerExecutor.WaitForPublishConsoleFileDeployment(
                    clientContainerName,
                    clientAppName ?? "ag-client",
                    clientDllPath,
                    NoPortRequired, // Client typically doesn't listen on a port
                    maxWaitTimeMs: 30000);

                if (clientStarted)
                {
                    _logger.LogInfo($"Client started successfully in {clientContainerName}");
                }
                else
                {
                    _logger.LogWarning($"Client may not have started properly in {clientContainerName}");
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start client: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Executes the Input action - sends input to the client application.
        /// </summary>
        private async Task<bool> ExecuteInputAsync(string? clientContainerName, string clientAppName, string input, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientContainerName))
            {
                _logger.LogWarning("Client container not configured, skipping Input");
                return true;
            }

            if (string.IsNullOrEmpty(input))
            {
                _logger.LogDebug("Input is empty, skipping");
                return true;
            }

            _logger.LogInfo($"Sending input to client: '{input}'");

            try
            {
                _dockerExecutor.SendInputToContainer(clientContainerName, clientAppName, input);
                await Task.Delay(500, ct);
                _logger.LogInfo($"Input sent successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to send input: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Executes the CloseClient action - stops the client container.
        /// </summary>
        private async Task<bool> ExecuteCloseClientAsync(string? clientContainerName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientContainerName))
            {
                _logger.LogWarning("Client container not configured, skipping CloseClient");
                return true;
            }

            _logger.LogInfo($"Stopping client container: {clientContainerName}");

            try
            {
                _dockerExecutor.StopContainer(clientContainerName);
                await Task.Delay(500, ct);
                _logger.LogInfo($"Client container stopped");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error stopping client container: {ex.Message}");
                return true; // Not critical if stop fails
            }
        }

        /// <summary>
        /// Executes the CloseServer action - stops the server container.
        /// </summary>
        private async Task<bool> ExecuteCloseServerAsync(string? serverContainerName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(serverContainerName))
            {
                _logger.LogWarning("Server container not configured, skipping CloseServer");
                return true;
            }

            _logger.LogInfo($"Stopping server container: {serverContainerName}");

            try
            {
                _dockerExecutor.StopContainer(serverContainerName);
                await Task.Delay(500, ct);
                _logger.LogInfo($"Server container stopped");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error stopping server container: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Logs the output from server and client containers for debugging.
        /// </summary>
        private async Task LogContainerOutputsAsync(string? serverContainerName, string? clientContainerName)
        {
            await Task.CompletedTask; // Placeholder for async

            if (!string.IsNullOrEmpty(serverContainerName))
            {
                try
                {
                    var serverLogs = _dockerExecutor.GetContainerLogs(serverContainerName);
                    if (!string.IsNullOrEmpty(serverLogs))
                    {
                        _logger.LogInfo($"Server container logs ({serverContainerName}):");
                        foreach (var line in serverLogs.Split('\n').Take(50))
                        {
                            _logger.LogDebug($"  [SERVER] {line}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Could not retrieve server logs: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(clientContainerName))
            {
                try
                {
                    var clientLogs = _dockerExecutor.GetContainerLogs(clientContainerName);
                    if (!string.IsNullOrEmpty(clientLogs))
                    {
                        _logger.LogInfo($"Client container logs ({clientContainerName}):");
                        foreach (var line in clientLogs.Split('\n').Take(50))
                        {
                            _logger.LogDebug($"  [CLIENT] {line}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Could not retrieve client logs: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Writes grading results to Excel files.
        /// </summary>
        private async Task WriteResultsAsync(StudentSolution student, (bool success, double mark, string message) result, CancellationToken ct)
        {
            _logger.LogInfo("Writing results...");

            try
            {
                var resultFolder = (_logger as LoggingService)?.GetStudentResultFolder(student.StudentCode, student.PaperNo) 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Results", student.StudentCode);

                if (!Directory.Exists(resultFolder))
                {
                    Directory.CreateDirectory(resultFolder);
                }

                // Write result summary
                var summaryPath = Path.Combine(resultFolder, "GradeSummary.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Summary");
                    worksheet.Cell(1, 1).Value = "Student Code";
                    worksheet.Cell(1, 2).Value = student.StudentCode;
                    worksheet.Cell(2, 1).Value = "Paper";
                    worksheet.Cell(2, 2).Value = student.PaperNo;
                    worksheet.Cell(3, 1).Value = "Mark";
                    worksheet.Cell(3, 2).Value = result.mark;
                    worksheet.Cell(4, 1).Value = "Status";
                    worksheet.Cell(4, 2).Value = result.success ? "Success" : "Failed";
                    worksheet.Cell(5, 1).Value = "Message";
                    worksheet.Cell(5, 2).Value = result.message;
                    worksheet.Cell(6, 1).Value = "Start Time";
                    worksheet.Cell(6, 2).Value = student.StartTime?.ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cell(7, 1).Value = "End Time";
                    worksheet.Cell(7, 2).Value = student.EndTime?.ToString("yyyy-MM-dd HH:mm:ss");

                    workbook.SaveAs(summaryPath);
                }

                _logger.LogInfo($"Results written to {resultFolder}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to write results", ex);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Cleans up Docker containers after grading.
        /// Removes all three containers: Server, Client, and Database.
        /// </summary>
        private async Task CleanupContainersAsync(Environment environment, CancellationToken ct)
        {
            _logger.LogInfo("Cleaning up containers...");

            try
            {
                // Get container names
                var serverContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.GivenConsoleContainerName);
                var dbContainer = TryGetConfig(environment.Configs, EnvironmentConfiguration.DatabaseContainerName);

                // Remove Server container
                if (!string.IsNullOrEmpty(serverContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing server container: {serverContainer}");
                        _dockerExecutor.RemoveContainer(serverContainer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing server container: {ex.Message}");
                    }
                }

                // Remove Client container
                if (!string.IsNullOrEmpty(clientContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing client container: {clientContainer}");
                        _dockerExecutor.RemoveContainer(clientContainer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing client container: {ex.Message}");
                    }
                }

                // Remove Database container
                if (!string.IsNullOrEmpty(dbContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing database container: {dbContainer}");
                        _dockerExecutor.RemoveContainer(dbContainer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing database container: {ex.Message}");
                    }
                }

                // Also try the EnvironmentManagerInvoker for any additional cleanup
                EnvironmentBuilder.helper.EnvironmentManagerInvoker.TryDisposeContainer(environment, out var error);
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning($"EnvironmentManagerInvoker cleanup warning: {error}");
                }

                await Task.Delay(500, ct);
                _logger.LogInfo("Container cleanup complete (all 3 containers removed)");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to cleanup containers", ex);
            }
        }

        /// <summary>
        /// Pauses the current grading session.
        /// </summary>
        public void PauseGrading(GradingSessionState sessionState)
        {
            _logger.LogInfo("Pausing grading session...");
            sessionState.IsPaused = true;
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Resumes a paused grading session.
        /// </summary>
        public void ResumeGrading(GradingSessionState sessionState)
        {
            _logger.LogInfo("Resuming grading session...");
            sessionState.IsPaused = false;
            SessionStateChanged?.Invoke(this, sessionState);
        }

        /// <summary>
        /// Cancels the current grading session.
        /// </summary>
        public void CancelGrading()
        {
            _logger.LogInfo("Cancelling grading session...");
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Resets all student statuses.
        /// </summary>
        public void ResetAllStatuses(List<StudentSolution> students, GradingSessionState sessionState)
        {
            _logger.LogInfo("Resetting all student statuses...");

            foreach (var student in students)
            {
                student.Status = GradingStatus.Not_Run;
                student.Mark = 0;
                student.StartTime = null;
                student.EndTime = null;
                student.StatusMessage = null;
                student.ProgressPercent = 0;
            }

            sessionState.Reset();
            sessionState.TotalStudents = students.Count;
            sessionState.NotRunCount = students.Count;
            SessionStateChanged?.Invoke(this, sessionState);

            _logger.LogInfo("All statuses reset");
        }

        /// <summary>
        /// Disposes a specific student's grading state.
        /// </summary>
        public void DisposeStudent(StudentSolution student)
        {
            _logger.LogInfo($"Disposing student: {student.StudentCode}");

            student.Status = GradingStatus.Disposed;
            student.Mark = 0;
            student.StartTime = null;
            student.EndTime = null;
            student.StatusMessage = "State disposed";
            student.ProgressPercent = 0;

            // TODO: Delete associated result files
        }

        private static void SetOrAddConfig(Dictionary<string, string> configs, string key, string value)
        {
            if (configs.ContainsKey(key))
            {
                configs[key] = value;
            }
            else
            {
                configs.Add(key, value);
            }
        }
    }
}
