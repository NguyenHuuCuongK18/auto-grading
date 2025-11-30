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
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using Environment = Domain.Entities.Main.Environment;
using EnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Main service that orchestrates the grading process for student solutions.
    /// Uses DOCKER containers for executing and grading student code.
    /// 
    /// Key responsibilities:
    /// 1. Discover student solutions from submit folder
    /// 2. Match students with test kits by paper number
    /// 3. Execute grading in Docker containers (server, client, database)
    /// 4. Read test steps from Detail.xlsx and execute them (StartServer, StartClient, Input)
    /// 5. Compare outputs against expected values from Client/Server/Network sheets
    /// 6. Calculate points and write results in SampleLogging format
    /// 
    /// Port Configuration:
    /// - Code_Container_Internal_Port: The port the app listens on inside the container
    /// - Code_Container_Host_Port: The port exposed to host for network monitoring
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
        /// Default timeout for database readiness check in milliseconds.
        /// Reduced from 5000ms to 2000ms for faster startup.
        /// </summary>
        private const int DatabaseReadinessTimeoutMs = 2000;
        
        /// <summary>
        /// Interval for checking database readiness in milliseconds
        /// </summary>
        private const int DatabaseReadinessCheckIntervalMs = 500;
        
        /// <summary>
        /// Threshold for determining if a test case passed (50% of max points)
        /// </summary>
        private const double PassThreshold = 0.5;
        
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
            // Set student context with paper number for organized logging (paper/Log_StudentCode_Date)
            _logger.SetStudentContext(student.StudentCode, student.PaperNo);
            
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
        /// Handles fallback to Meta/Given folder when student only provides client or server.
        /// </summary>
        private void ConfigureEnvironmentForStudent(
            Environment environment, 
            StudentSolution student,
            GradingConfiguration config,
            string testKitPath)
        {
            var configs = environment.Configs;

            // Set container names for this student
            SetOrAddConfig(configs, EnvConfig.CodeContainerName, $"ag-server-{student.StudentCode}");
            SetOrAddConfig(configs, EnvConfig.GivenConsoleContainerName, $"ag-client-{student.StudentCode}");
            SetOrAddConfig(configs, EnvConfig.StudentQuestionName, $"ag-{student.StudentCode}");
            SetOrAddConfig(configs, EnvConfig.DatabaseName, $"DB_{student.StudentCode}");

            // Set port configurations
            SetOrAddConfig(configs, EnvConfig.CodeContainerInternalPort, config.CodeContainerInternalPort.ToString());
            SetOrAddConfig(configs, EnvConfig.CodeContainerHostPort, config.CodeContainerHostPort.ToString());

            // Set file paths - with fallback to Meta/Given folder for missing components
            var metaPath = Path.Combine(testKitPath, "Meta", "Given");
            
            // Handle Server path
            if (!string.IsNullOrEmpty(student.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(student.ServerDllPath)!;
                SetOrAddConfig(configs, EnvConfig.CodeFilePath, serverDir);
                SetOrAddConfig(configs, EnvConfig.DockerServerPath, GetDockerDllPath(serverDir, student.ServerDllPath));
                _logger.LogInfo($"Using student's Server DLL: {student.ServerDllPath}");
            }
            else if (config.HasServer)
            {
                // Student should provide server but didn't - try Meta/Given/Server
                var metaServerPath = Path.Combine(metaPath, "Server");
                if (Directory.Exists(metaServerPath))
                {
                    var metaServerDll = FindDllInDirectory(metaServerPath, config.ServerProjectName);
                    if (metaServerDll != null)
                    {
                        SetOrAddConfig(configs, EnvConfig.CodeFilePath, metaServerPath);
                        SetOrAddConfig(configs, EnvConfig.DockerServerPath, GetDockerDllPath(metaServerPath, metaServerDll));
                        _logger.LogInfo($"Using Meta/Given Server DLL: {metaServerDll}");
                    }
                    else
                    {
                        _logger.LogWarning($"No Server DLL found in Meta/Given/Server for project '{config.ServerProjectName}'");
                    }
                }
            }

            // Handle Client path
            if (!string.IsNullOrEmpty(student.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(student.ClientDllPath)!;
                SetOrAddConfig(configs, EnvConfig.GivenConsolePath, clientDir);
                SetOrAddConfig(configs, EnvConfig.DockerClientPath, GetDockerDllPath(clientDir, student.ClientDllPath));
                _logger.LogInfo($"Using student's Client DLL: {student.ClientDllPath}");
            }
            else if (config.HasClient)
            {
                // Student should provide client but didn't - try Meta/Given/Client
                var metaClientPath = Path.Combine(metaPath, "Client");
                if (Directory.Exists(metaClientPath))
                {
                    var metaClientDll = FindDllInDirectory(metaClientPath, config.ClientProjectName);
                    if (metaClientDll != null)
                    {
                        SetOrAddConfig(configs, EnvConfig.GivenConsolePath, metaClientPath);
                        SetOrAddConfig(configs, EnvConfig.DockerClientPath, GetDockerDllPath(metaClientPath, metaClientDll));
                        _logger.LogInfo($"Using Meta/Given Client DLL: {metaClientDll}");
                    }
                    else
                    {
                        _logger.LogWarning($"No Client DLL found in Meta/Given/Client for project '{config.ClientProjectName}'");
                    }
                }
            }

            // Set runtime folder from test kit
            var runtimesFolder = configs.GetValueOrDefault(EnvConfig.RuntimesFolder);
            if (!string.IsNullOrEmpty(runtimesFolder) && !Path.IsPathRooted(runtimesFolder))
            {
                SetOrAddConfig(configs, EnvConfig.RuntimesFolder, Path.Combine(testKitPath, runtimesFolder));
            }

            // Set database file path
            var dbFilePath = configs.GetValueOrDefault(EnvConfig.DefaultDatabaseFilePath);
            if (!string.IsNullOrEmpty(dbFilePath) && !Path.IsPathRooted(dbFilePath))
            {
                SetOrAddConfig(configs, EnvConfig.DefaultDatabaseFilePath, Path.Combine(testKitPath, dbFilePath));
            }
        }

        /// <summary>
        /// Finds a DLL file in a directory by project name.
        /// </summary>
        private string? FindDllInDirectory(string directory, string? projectName)
        {
            if (!Directory.Exists(directory)) return null;

            // First try to find by project name if provided
            if (!string.IsNullOrEmpty(projectName))
            {
                var exactMatch = Path.Combine(directory, $"{projectName}.dll");
                if (File.Exists(exactMatch))
                    return exactMatch;
            }

            // Otherwise find first .dll file (excluding common framework DLLs)
            var dllFiles = Directory.GetFiles(directory, "*.dll")
                .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") &&
                           !Path.GetFileName(f).StartsWith("System."))
                .ToArray();

            return dllFiles.FirstOrDefault();
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
                var serverContainer = TryGetConfig(environment.Configs, EnvConfig.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleContainerName);
                var dbContainer = TryGetConfig(environment.Configs, EnvConfig.DatabaseContainerName);
                var network = TryGetConfig(environment.Configs, EnvConfig.DockerNetwork);
                
                // Get image names
                var codeImageName = TryGetConfig(environment.Configs, EnvConfig.CodeImageName);
                var dbImageName = TryGetConfig(environment.Configs, EnvConfig.DatabaseImageName);
                
                // Get port configuration
                var internalPort = TryGetConfig(environment.Configs, EnvConfig.CodeContainerInternalPort);
                var hostPort = TryGetConfig(environment.Configs, EnvConfig.CodeContainerHostPort);
                var dbInternalPort = TryGetConfig(environment.Configs, EnvConfig.DatabaseContainerInternalPort);
                var dbHostPort = TryGetConfig(environment.Configs, EnvConfig.DatabaseContainerHostPort);

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
                    var dbUsername = TryGetConfig(environment.Configs, EnvConfig.DatabaseUsername);
                    var dbPassword = TryGetConfig(environment.Configs, EnvConfig.DatabasePassword);
                    
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

                // NOTE: Removed duplicate EnvironmentManagerInvoker.TrySetupContainer call
                // We already created all containers above - the invoker call was redundant and caused ~5s delay

                await Task.Delay(500, ct); // Brief wait for all containers to be ready
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
                var serverContainer = TryGetConfig(environment.Configs, EnvConfig.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleContainerName);
                
                // Get source paths
                var serverPath = TryGetConfig(environment.Configs, EnvConfig.CodeFilePath);
                var clientPath = TryGetConfig(environment.Configs, EnvConfig.GivenConsolePath);

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

                // NOTE: Removed duplicate EnvironmentManagerInvoker.TrySetupQuestion call
                // We already copied files above - the invoker call was redundant

                await Task.Delay(200, ct); // Brief wait for filesystem sync
                _logger.LogInfo("Files copied to containers");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to copy files to containers", ex);
                throw;
            }
        }

        /// <summary>
        /// Executes the actual grading process using DOCKER containers.
        /// 
        /// This method orchestrates the Docker-based grading process:
        /// 1. Checks Docker availability
        /// 2. Loads test kit configuration
        /// 3. Sets up Docker containers (server, client, database)
        /// 4. Copies student solution files to containers
        /// 5. Delegates test case execution to DockerTestCaseExecutor
        /// 6. Cleans up containers after grading
        /// 7. Writes results
        /// 
        /// Test case execution (reading Detail.xlsx, executing actions, comparing outputs)
        /// is handled by DockerTestCaseExecutor for better separation of concerns.
        /// </summary>
        private async Task<(bool success, double mark, string message)> ExecuteGradingAsync(
            StudentSolution student,
            Environment environment,
            string testKitPath,
            GradingConfiguration config,
            CancellationToken ct)
        {
            _logger.LogInfo("=".PadRight(60, '='));
            _logger.LogInfo($"Starting DOCKER-BASED grading for student: {student.StudentCode}");
            _logger.LogInfo("=".PadRight(60, '='));

            var dockerGrading = new DockerGradingService(_logger);

            try
            {
                // Step 1: Check Docker availability (containers should already be set up by GradeStudentAsync)
                if (!dockerGrading.IsDockerAvailable())
                {
                    _logger.LogError("FATAL: Docker is not running. Cannot execute grading.");
                    return (false, 0, "Docker is not running. Please start Docker Desktop.");
                }
                _logger.LogInfo("Docker is available and running");

                // Step 2: Load test kit configuration
                var testKitConfig = _testKitConfigService.LoadTestKitConfig(testKitPath);
                if (testKitConfig == null)
                {
                    _logger.LogError("FATAL: Failed to load test kit configuration");
                    return (false, 0, "Failed to load test kit configuration");
                }

                _logger.LogInfo($"Test kit configuration loaded:");
                _logger.LogInfo($"  - Max mark from Header.xlsx: {testKitConfig.TotalMaxMark}");
                _logger.LogInfo($"  - Port: {testKitConfig.CodeContainerHostPort}");
                _logger.LogInfo($"  - Protocol: {testKitConfig.Protocol}");
                student.MaxMark = testKitConfig.TotalMaxMark;

                // Step 3: Create result directory
                var studentResultRoot = _logger.GetStudentResultFolder(student.StudentCode, student.PaperNo);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var resultRoot = Path.Combine(studentResultRoot, $"GradeResult_{timestamp}");
                if (!Directory.Exists(resultRoot))
                {
                    Directory.CreateDirectory(resultRoot);
                }
                _logger.LogInfo($"Results will be saved to: {resultRoot}");

                // NOTE: Docker containers are already set up by GradeStudentAsync
                // We skip duplicate SetupContainersAsync and CopyFilesToContainersAsync calls here

                // Step 4: Execute test cases using DockerTestCaseExecutor
                _logger.LogInfo("=== Executing test cases ===");
                var testCaseExecutor = new DockerTestCaseExecutor(_logger, _testKitConfigService, dockerGrading);
                var results = await testCaseExecutor.ExecuteAllTestCasesAsync(
                    environment, testKitPath, testKitConfig, resultRoot, ct);

                // NOTE: Container cleanup is handled by GradeStudentAsync after this method returns

                // Step 5: Write overall summary
                await WriteOverallSummaryAsync(resultRoot, results.TestCaseResults, results.TotalEarnedMark, testKitConfig.TotalMaxMark, ct);

                // Final result
                bool success = results.PassedTestCases > 0;
                string message = $"Passed {results.PassedTestCases}/{results.TotalTestCases} test cases";
                
                _logger.LogInfo("=".PadRight(60, '='));
                _logger.LogInfo($"GRADING COMPLETE FOR {student.StudentCode}");
                _logger.LogInfo($"  Total mark: {results.TotalEarnedMark}/{testKitConfig.TotalMaxMark}");
                _logger.LogInfo($"  Test cases passed: {results.PassedTestCases}/{results.TotalTestCases}");
                foreach (var result in results.TestCaseResults)
                {
                    _logger.LogInfo($"    - {result}");
                }
                _logger.LogInfo("=".PadRight(60, '='));

                return (success, results.TotalEarnedMark, message);
            }
            catch (OperationCanceledException)
            {
                // NOTE: Container cleanup handled by GradeStudentAsync
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Docker grading execution failed", ex);
                // NOTE: Container cleanup handled by GradeStudentAsync
                return (false, 0, ex.Message);
            }
        }

        /// <summary>
        /// Writes overall grading summary to Excel file.
        /// </summary>
        private async Task WriteOverallSummaryAsync(
            string resultRoot, List<string> testCaseResults, 
            double totalMark, double maxMark, CancellationToken ct)
        {
            try
            {
                var summaryPath = Path.Combine(resultRoot, "OverallSummary.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var ws = workbook.Worksheets.Add("Summary");
                    ws.Cell(1, 1).Value = "TestCase";
                    ws.Cell(1, 2).Value = "Result";
                    ws.Row(1).Style.Font.Bold = true;

                    int row = 2;
                    foreach (var result in testCaseResults)
                    {
                        var parts = result.Split(':');
                        ws.Cell(row, 1).Value = parts[0].Trim();
                        ws.Cell(row, 2).Value = parts.Length > 1 ? parts[1].Trim() : "";
                        row++;
                    }

                    // Add total row
                    row++;
                    ws.Cell(row, 1).Value = "TOTAL";
                    ws.Cell(row, 2).Value = $"{totalMark:F2} / {maxMark:F2}";
                    ws.Row(row).Style.Font.Bold = true;

                    ws.Columns().AdjustToContents();
                    workbook.SaveAs(summaryPath);
                }
                _logger.LogInfo($"Overall summary written to {summaryPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write overall summary: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Finds the student's executable file from the solution path.
        /// Looks for .exe files in publish/bin folders.
        /// </summary>
        private string? FindStudentExecutable(string? dllPath, string? projectName)
        {
            if (string.IsNullOrEmpty(dllPath))
                return null;

            // If it's already an .exe file, return it
            if (dllPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(dllPath))
                return dllPath;

            // Try to find .exe in the same directory as the .dll
            var directory = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            // Look for project-named .exe
            if (!string.IsNullOrEmpty(projectName))
            {
                var projectExe = Path.Combine(directory, $"{projectName}.exe");
                if (File.Exists(projectExe))
                    return projectExe;
            }

            // Look for any .exe file in the directory
            var exeFiles = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            if (exeFiles.Length > 0)
                return exeFiles[0];

            // Try converting .dll path to .exe path
            var exePath = Path.ChangeExtension(dllPath, ".exe");
            if (File.Exists(exePath))
                return exePath;

            return null;
        }

        /// <summary>
        /// Parses grading results from the output directory.
        /// Reads the generated result files and calculates total mark.
        /// </summary>
        private (double totalMark, string message) ParseGradingResults(string resultRoot, TestKitConfigService.TestKitConfig testKitConfig)
        {
            double totalMark = 0;
            var messages = new List<string>();

            try
            {
                if (!Directory.Exists(resultRoot))
                {
                    return (0, "Result directory not found");
                }

                // Look for test case result folders
                var testCaseFolders = Directory.GetDirectories(resultRoot);
                
                foreach (var tcFolder in testCaseFolders)
                {
                    var tcName = Path.GetFileName(tcFolder);
                    
                    // Try to find result files
                    var summaryFile = Path.Combine(tcFolder, "Summary.xlsx");
                    var detailFile = Path.Combine(tcFolder, "GradeDetail.xlsx");

                    // Check for Mark file or parse from detail
                    if (testKitConfig.TestCaseMarks.TryGetValue(tcName, out var maxMark))
                    {
                        // For now, check if any result files exist to determine pass/fail
                        // In a full implementation, we would parse the Excel files
                        bool passed = File.Exists(summaryFile) || File.Exists(detailFile) ||
                                     Directory.GetFiles(tcFolder, "*.xlsx").Length > 0;
                        
                        if (passed)
                        {
                            // Try to parse actual result - for now assume full marks if files exist
                            // A proper implementation would read the comparison results
                            totalMark += maxMark;
                            messages.Add($"{tcName}: PASSED (+{maxMark})");
                        }
                        else
                        {
                            messages.Add($"{tcName}: FAILED (0)");
                        }
                    }
                }

                if (messages.Count == 0)
                {
                    return (0, "No test case results found");
                }

                return (totalMark, string.Join(", ", messages));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error parsing results: {ex.Message}");
                return (0, $"Error parsing results: {ex.Message}");
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
            var serverContainerName = TryGetConfig(environment.Configs, EnvConfig.CodeContainerName);
            var clientContainerName = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleContainerName);
            var serverAppName = TryGetConfig(environment.Configs, EnvConfig.StudentQuestionName) + "-server";
            var clientAppName = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleAppName);
            var serverDllPath = TryGetConfig(environment.Configs, EnvConfig.DockerServerPath);
            var clientDllPath = TryGetConfig(environment.Configs, EnvConfig.DockerClientPath);
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
                    _logger.LogWarning($"Server may not have started properly in {serverContainerName} - execution will continue but results may be affected");
                    // Return false to indicate uncertain state - let the test case fail if server is truly not working
                    return false;
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
                    return true;
                }
                else
                {
                    _logger.LogWarning($"Client may not have started properly in {clientContainerName} - execution will continue but results may be affected");
                    return false;
                }
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
                _logger.LogInfo("Container stop failure is non-critical, continuing...");
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
                _logger.LogInfo("Container stop failure is non-critical, continuing...");
                return true; // Not critical if stop fails
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

        #region Test Case Result Structures
        /// <summary>
        /// Represents the result of executing and comparing a test case.
        /// </summary>
        public class TestCaseExecutionResult
        {
            public bool passed { get; set; }
            public double earnedPoints { get; set; }
            public double maxPoints { get; set; }
            public string? errorMessage { get; set; }
            public List<StepComparisonResult> stepResults { get; set; } = new List<StepComparisonResult>();
        }

        /// <summary>
        /// Represents the result of a single step comparison.
        /// </summary>
        public class StepComparisonResult
        {
            public string stepId { get; set; } = "";
            public int stage { get; set; }
            public string action { get; set; } = "";
            public bool passed { get; set; }
            public string result { get; set; } = "PASS";
            public string? errorCode { get; set; }
            public double pointsAwarded { get; set; }
            public double pointsPossible { get; set; }
            public double durationMs { get; set; }
            public string? message { get; set; }
            public string? expectedOutput { get; set; }
            public string? actualOutput { get; set; }
        }
        #endregion

        /// <summary>
        /// Executes a test case and compares the outputs with expected values.
        /// This is the main grading method that:
        /// 1. Executes actions (StartClient, StartServer, Input)
        /// 2. Captures actual console outputs
        /// 3. Compares against expected outputs from Detail.xlsx
        /// 4. Calculates points based on comparison
        /// </summary>
        private async Task<TestCaseExecutionResult> ExecuteAndCompareTestCaseAsync(
            StudentSolution student,
            string testCaseName,
            string testCasePath,
            List<(int Stage, string Input, string Action)> actions,
            Dictionary<int, TestKitConfigService.ExpectedOutput> expectedOutputs,
            double maxPoints,
            Environment environment,
            GradingConfiguration config,
            CancellationToken ct)
        {
            var result = new TestCaseExecutionResult
            {
                maxPoints = maxPoints
            };

            _logger.LogInfo($"=== Executing and Comparing Test Case: {testCaseName} ===");
            _logger.LogInfo($"Max points available: {maxPoints}");

            // Get container names from environment configuration
            var serverContainerName = TryGetConfig(environment.Configs, EnvConfig.CodeContainerName);
            var clientContainerName = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleContainerName);

            // Calculate points per comparison (divide among all expected outputs)
            int totalComparisons = expectedOutputs.Sum(eo => 
                (string.IsNullOrEmpty(eo.Value.ClientConsole) ? 0 : 1) + 
                (string.IsNullOrEmpty(eo.Value.ServerConsole) ? 0 : 1));
            double pointsPerComparison = totalComparisons > 0 ? maxPoints / totalComparisons : 0;

            _logger.LogInfo($"Total comparisons to perform: {totalComparisons}");
            _logger.LogInfo($"Points per comparison: {pointsPerComparison:F2}");

            var startTime = DateTime.Now;
            var actualClientOutputs = new Dictionary<int, string>();
            var actualServerOutputs = new Dictionary<int, string>();

            try
            {
                // Check if Docker is available
                if (!_dockerExecutor.IsDockerRunning())
                {
                    _logger.LogError("FATAL: Docker is not running. Cannot execute test case.");
                    result.passed = false;
                    result.errorMessage = "Docker is not running";
                    result.stepResults.Add(new StepComparisonResult
                    {
                        stepId = "DOCKER-CHECK",
                        stage = 0,
                        action = "DOCKER_CHECK",
                        passed = false,
                        result = "FAIL",
                        message = "Docker is not running. Please start Docker Desktop."
                    });
                    return result;
                }

                // Execute each action and capture outputs
                foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
                {
                    ct.ThrowIfCancellationRequested();
                    var stepStart = DateTime.Now;

                    _logger.LogInfo($"[Stage {stage}] Executing action: '{action}'" + 
                        (string.IsNullOrEmpty(input) ? "" : $" with input: '{input}'"));

                    var stepResult = new StepComparisonResult
                    {
                        stepId = $"USER-{action.ToUpper()}-{stage}",
                        stage = stage,
                        action = action.ToUpper()
                    };

                    bool actionSuccess = true;

                    switch (action.ToUpperInvariant())
                    {
                        case "STARTSERVER":
                            actionSuccess = await ExecuteStartServerAsync(
                                serverContainerName,
                                TryGetConfig(environment.Configs, EnvConfig.StudentQuestionName) + "-server",
                                TryGetConfig(environment.Configs, EnvConfig.DockerServerPath) ?? 
                                    (student.ServerDllPath != null ? $"/apps/{Path.GetFileName(Path.GetDirectoryName(student.ServerDllPath))}/{Path.GetFileName(student.ServerDllPath)}" : null),
                                config.CodeContainerInternalPort.ToString(),
                                ct);
                            
                            // Wait for server to start and capture output
                            await Task.Delay(1000, ct);
                            if (!string.IsNullOrEmpty(serverContainerName))
                            {
                                actualServerOutputs[stage] = _dockerExecutor.GetContainerLogs(serverContainerName) ?? "";
                            }
                            break;

                        case "STARTCLIENT":
                            actionSuccess = await ExecuteStartClientAsync(
                                clientContainerName,
                                TryGetConfig(environment.Configs, EnvConfig.GivenConsoleAppName) ?? $"ag-{student.StudentCode}-client",
                                TryGetConfig(environment.Configs, EnvConfig.DockerClientPath) ?? 
                                    (student.ClientDllPath != null ? $"/apps/{Path.GetFileName(Path.GetDirectoryName(student.ClientDllPath))}/{Path.GetFileName(student.ClientDllPath)}" : null),
                                ct);
                            
                            // Wait for client to start and capture output
                            await Task.Delay(1000, ct);
                            if (!string.IsNullOrEmpty(clientContainerName))
                            {
                                actualClientOutputs[stage] = _dockerExecutor.GetContainerLogs(clientContainerName) ?? "";
                            }
                            break;

                        case "INPUT":
                            actionSuccess = await ExecuteInputAsync(
                                clientContainerName,
                                TryGetConfig(environment.Configs, EnvConfig.GivenConsoleAppName) ?? $"ag-{student.StudentCode}-client",
                                input,
                                ct);
                            
                            // Wait for input to be processed and capture output
                            await Task.Delay(500, ct);
                            if (!string.IsNullOrEmpty(clientContainerName))
                            {
                                actualClientOutputs[stage] = _dockerExecutor.GetContainerLogs(clientContainerName) ?? "";
                            }
                            if (!string.IsNullOrEmpty(serverContainerName))
                            {
                                actualServerOutputs[stage] = _dockerExecutor.GetContainerLogs(serverContainerName) ?? "";
                            }
                            break;

                        case "CLOSECLIENT":
                            actionSuccess = await ExecuteCloseClientAsync(clientContainerName, ct);
                            break;

                        case "CLOSESERVER":
                            actionSuccess = await ExecuteCloseServerAsync(serverContainerName, ct);
                            break;
                    }

                    stepResult.durationMs = (DateTime.Now - stepStart).TotalMilliseconds;
                    stepResult.passed = actionSuccess;
                    stepResult.result = actionSuccess ? "PASS" : "FAIL";
                    stepResult.message = actionSuccess ? $"Action {action} completed" : $"Action {action} failed";

                    result.stepResults.Add(stepResult);

                    // Allow time for Docker operations to complete
                    await Task.Delay(200, ct);
                }

                // Now perform comparisons for each expected output
                _logger.LogInfo("=== Performing Output Comparisons ===");

                foreach (var (stage, expected) in expectedOutputs.OrderBy(e => e.Key))
                {
                    // Compare Client output
                    if (!string.IsNullOrEmpty(expected.ClientConsole))
                    {
                        var clientStep = new StepComparisonResult
                        {
                            stepId = $"CLIENT-CONSOLE-{stage}",
                            stage = stage,
                            action = "COMPARE_TEXT",
                            expectedOutput = expected.ClientConsole,
                            actualOutput = actualClientOutputs.TryGetValue(stage, out var clientOut) ? clientOut : null,
                            pointsPossible = pointsPerComparison
                        };

                        bool clientMatch = TextComparisonUtility.CompareOutput(expected.ClientConsole, clientStep.actualOutput);
                        clientStep.passed = clientMatch;
                        clientStep.result = clientMatch ? "PASS" : "FAIL";
                        clientStep.pointsAwarded = clientMatch ? pointsPerComparison : 0;
                        clientStep.message = clientMatch ? 
                            "Text comparison passed: client output matches" : 
                            "Text comparison failed: client output does not match expected";

                        result.stepResults.Add(clientStep);
                        result.earnedPoints += clientStep.pointsAwarded;

                        _logger.LogInfo($"[Stage {stage}] Client comparison: {clientStep.result} ({clientStep.pointsAwarded:F2} points)");
                        if (!clientMatch)
                        {
                            _logger.LogDebug($"  Expected: '{expected.ClientConsole}'");
                            _logger.LogDebug($"  Actual: '{clientStep.actualOutput}'");
                        }
                    }

                    // Compare Server output
                    if (!string.IsNullOrEmpty(expected.ServerConsole))
                    {
                        var serverStep = new StepComparisonResult
                        {
                            stepId = $"SERVER-CONSOLE-{stage}",
                            stage = stage,
                            action = "COMPARE_TEXT",
                            expectedOutput = expected.ServerConsole,
                            actualOutput = actualServerOutputs.TryGetValue(stage, out var serverOut) ? serverOut : null,
                            pointsPossible = pointsPerComparison
                        };

                        bool serverMatch = TextComparisonUtility.CompareOutput(expected.ServerConsole, serverStep.actualOutput);
                        serverStep.passed = serverMatch;
                        serverStep.result = serverMatch ? "PASS" : "FAIL";
                        serverStep.pointsAwarded = serverMatch ? pointsPerComparison : 0;
                        serverStep.message = serverMatch ? 
                            "Text comparison passed: server output matches" : 
                            "Text comparison failed: server output does not match expected";

                        result.stepResults.Add(serverStep);
                        result.earnedPoints += serverStep.pointsAwarded;

                        _logger.LogInfo($"[Stage {stage}] Server comparison: {serverStep.result} ({serverStep.pointsAwarded:F2} points)");
                        if (!serverMatch)
                        {
                            _logger.LogDebug($"  Expected: '{expected.ServerConsole}'");
                            _logger.LogDebug($"  Actual: '{serverStep.actualOutput}'");
                        }
                    }
                }

                // Determine if test case passed (earned more than 50% of max points, or all comparisons passed)
                result.passed = result.earnedPoints >= (maxPoints * 0.5) || 
                    result.stepResults.Where(s => s.action == "COMPARE_TEXT").All(s => s.passed);

                _logger.LogInfo($"=== Test Case {testCaseName} Result ===");
                _logger.LogInfo($"  Passed: {result.passed}");
                _logger.LogInfo($"  Points: {result.earnedPoints:F2} / {result.maxPoints:F2}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Test case execution failed: {ex.Message}");
                result.passed = false;
                result.errorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Writes test case result files in SampleLogging format.
        /// Creates {testCaseName}_Result.xlsx and updates GradeDetail.xlsx
        /// </summary>
        private async Task WriteTestCaseResultFilesAsync(
            StudentSolution student,
            string testCaseName,
            TestCaseExecutionResult testCaseResult,
            CancellationToken ct)
        {
            try
            {
                var resultFolder = (_logger as LoggingService)?.GetStudentResultFolder(student.StudentCode, student.PaperNo) 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Results", student.StudentCode);

                var testCaseFolder = Path.Combine(resultFolder, testCaseName);
                if (!Directory.Exists(testCaseFolder))
                {
                    Directory.CreateDirectory(testCaseFolder);
                }

                // Write TC_Result.xlsx (matches SampleLogging format)
                var resultPath = Path.Combine(testCaseFolder, $"{testCaseName}_Result.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Result");
                    
                    // Header row
                    worksheet.Cell(1, 1).Value = "StepId";
                    worksheet.Cell(1, 2).Value = "Stage";
                    worksheet.Cell(1, 3).Value = "Action";
                    worksheet.Cell(1, 4).Value = "Passed";
                    worksheet.Cell(1, 5).Value = "Message";
                    worksheet.Cell(1, 6).Value = "DurationMs";
                    
                    var headerRow = worksheet.Row(1);
                    headerRow.Style.Font.Bold = true;

                    int row = 2;
                    foreach (var step in testCaseResult.stepResults)
                    {
                        worksheet.Cell(row, 1).Value = step.stepId;
                        worksheet.Cell(row, 2).Value = step.stage;
                        worksheet.Cell(row, 3).Value = step.action;
                        worksheet.Cell(row, 4).Value = step.passed;
                        worksheet.Cell(row, 5).Value = step.message ?? "";
                        worksheet.Cell(row, 6).Value = step.durationMs;
                        row++;
                    }

                    worksheet.Columns().AdjustToContents();
                    workbook.SaveAs(resultPath);
                }

                // Write GradeDetail.xlsx (matches SampleLogging format)
                var detailPath = Path.Combine(testCaseFolder, "GradeDetail.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("User");
                    
                    // Header row matching SampleLogging format
                    worksheet.Cell(1, 1).Value = "Stage";
                    worksheet.Cell(1, 2).Value = "Input";
                    worksheet.Cell(1, 3).Value = "Action";
                    worksheet.Cell(1, 4).Value = "DataType";
                    worksheet.Cell(1, 5).Value = "Result";
                    worksheet.Cell(1, 6).Value = "ErrorCode";
                    worksheet.Cell(1, 7).Value = "ErrorCategory";
                    worksheet.Cell(1, 8).Value = "PointsAwarded";
                    worksheet.Cell(1, 9).Value = "PointsPossible";
                    worksheet.Cell(1, 10).Value = "DurationMs";
                    worksheet.Cell(1, 11).Value = "DetailPath";
                    worksheet.Cell(1, 12).Value = "Message";
                    
                    var headerRow = worksheet.Row(1);
                    headerRow.Style.Font.Bold = true;

                    int row = 2;
                    foreach (var step in testCaseResult.stepResults)
                    {
                        worksheet.Cell(row, 1).Value = step.stage;
                        worksheet.Cell(row, 2).Value = "";
                        worksheet.Cell(row, 3).Value = step.action;
                        worksheet.Cell(row, 4).Value = "";
                        worksheet.Cell(row, 5).Value = step.result;
                        worksheet.Cell(row, 6).Value = step.errorCode ?? "NONE";
                        worksheet.Cell(row, 7).Value = step.passed ? "None" : "Comparison";
                        worksheet.Cell(row, 8).Value = step.pointsAwarded;
                        worksheet.Cell(row, 9).Value = step.pointsPossible;
                        worksheet.Cell(row, 10).Value = step.durationMs;
                        worksheet.Cell(row, 11).Value = "";
                        worksheet.Cell(row, 12).Value = step.message ?? "";
                        row++;
                    }

                    worksheet.Columns().AdjustToContents();
                    workbook.SaveAs(detailPath);
                }

                _logger.LogInfo($"Test case result files written to {testCaseFolder}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write test case result files: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Writes OverallSummary.xlsx in SampleLogging format.
        /// Format: TestCase, Passed, PointsAwarded, PointsPossible, ErrorNotes
        /// </summary>
        private async Task WriteOverallSummaryAsync(
            StudentSolution student,
            List<(string testCaseName, TestCaseExecutionResult result)> testCaseResults,
            CancellationToken ct)
        {
            try
            {
                var resultFolder = (_logger as LoggingService)?.GetStudentResultFolder(student.StudentCode, student.PaperNo) 
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Results", student.StudentCode);

                if (!Directory.Exists(resultFolder))
                {
                    Directory.CreateDirectory(resultFolder);
                }

                var summaryPath = Path.Combine(resultFolder, "OverallSummary.xlsx");
                using (var workbook = new ClosedXML.Excel.XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Summary");
                    
                    // Header row matching SampleLogging format
                    worksheet.Cell(1, 1).Value = "TestCase";
                    worksheet.Cell(1, 2).Value = "Passed";
                    worksheet.Cell(1, 3).Value = "PointsAwarded";
                    worksheet.Cell(1, 4).Value = "PointsPossible";
                    worksheet.Cell(1, 5).Value = "ErrorNotes";
                    
                    var headerRow = worksheet.Row(1);
                    headerRow.Style.Font.Bold = true;

                    int row = 2;
                    foreach (var (testCaseName, result) in testCaseResults)
                    {
                        worksheet.Cell(row, 1).Value = testCaseName;
                        worksheet.Cell(row, 2).Value = result.passed ? "PASS" : "FAIL";
                        worksheet.Cell(row, 3).Value = result.earnedPoints;
                        worksheet.Cell(row, 4).Value = result.maxPoints;
                        worksheet.Cell(row, 5).Value = result.errorMessage ?? "";
                        row++;
                    }

                    worksheet.Columns().AdjustToContents();
                    workbook.SaveAs(summaryPath);
                }

                _logger.LogInfo($"OverallSummary.xlsx written to {resultFolder}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to write OverallSummary.xlsx: {ex.Message}");
            }

            await Task.CompletedTask;
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
            _logger.LogInfo("Cleaning up containers (force removal)...");

            try
            {
                // Get container names
                var serverContainer = TryGetConfig(environment.Configs, EnvConfig.CodeContainerName);
                var clientContainer = TryGetConfig(environment.Configs, EnvConfig.GivenConsoleContainerName);
                var dbContainer = TryGetConfig(environment.Configs, EnvConfig.DatabaseContainerName);

                _logger.LogInfo($"Containers to remove: server={serverContainer}, client={clientContainer}, db={dbContainer}");

                // Remove Server container (docker rm -f will stop and remove)
                if (!string.IsNullOrEmpty(serverContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing server container: {serverContainer}");
                        _dockerExecutor.RemoveContainer(serverContainer);
                        _logger.LogInfo($"Server container '{serverContainer}' removed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing server container '{serverContainer}': {ex.Message}");
                    }
                }

                // Remove Client container
                if (!string.IsNullOrEmpty(clientContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing client container: {clientContainer}");
                        _dockerExecutor.RemoveContainer(clientContainer);
                        _logger.LogInfo($"Client container '{clientContainer}' removed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing client container '{clientContainer}': {ex.Message}");
                    }
                }

                // Remove Database container
                if (!string.IsNullOrEmpty(dbContainer))
                {
                    try
                    {
                        _logger.LogInfo($"Removing database container: {dbContainer}");
                        _dockerExecutor.RemoveContainer(dbContainer);
                        _logger.LogInfo($"Database container '{dbContainer}' removed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error removing database container '{dbContainer}': {ex.Message}");
                    }
                }

                // NOTE: Removed EnvironmentManagerInvoker.TryDisposeContainer call
                // We already removed all containers above - the invoker call was causing delays

                await Task.Delay(200, ct); // Brief wait for Docker daemon to release resources
                _logger.LogInfo("Container cleanup complete");
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
