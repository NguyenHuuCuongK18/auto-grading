using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Services;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service that bridges the UI to the Lib folder's grading services.
    /// 
    /// This service provides two grading modes:
    /// 1. Local grading via SuiteRunner (executables run directly on host)
    /// 2. Docker grading via DockerGradingService (executables run in containers)
    /// 
    /// The Docker grading mode:
    /// - Sets up server and client Docker containers with TTY support
    /// - Exposes server port to host for NetworkMonitor packet capture
    /// - Uses application log files to capture console output (bypasses docker logs buffering)
    /// - Shares the same DockerGradingService with SolutionGrader.CLI for consistency
    /// 
    /// The UI should only:
    /// 1. Collect user input (paths, configuration)
    /// 2. Call this service to execute grading
    /// 3. Display results from the Lib folder's output
    /// 
    /// This ensures consistency between CLI and UI grading behavior.
    /// </summary>
    public class LibGradingService
    {
        private readonly ILoggingService _uiLogger;

        public LibGradingService(ILoggingService logger)
        {
            _uiLogger = logger;
        }

        /// <summary>
        /// Executes grading for a single test suite using the Lib folder's SuiteRunner.
        /// This is the same grading logic used by SolutionGrader.Cli.
        /// </summary>
        /// <param name="suitePath">Path to the test suite folder or Header.xlsx</param>
        /// <param name="resultRoot">Output directory for grading results</param>
        /// <param name="clientExePath">Optional path to client executable</param>
        /// <param name="serverExePath">Optional path to server executable</param>
        /// <param name="useInnerEnv">Enable test case-specific environment.xlsx files</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Exit code from grading (1 = success, -1 = failure)</returns>
        public async Task<int> ExecuteSuiteAsync(
            string suitePath,
            string resultRoot,
            string? clientExePath = null,
            string? serverExePath = null,
            bool useInnerEnv = false,
            CancellationToken ct = default)
        {
            _uiLogger.LogInfo($"[LibGradingService] Starting grading via Lib folder's SuiteRunner");
            _uiLogger.LogInfo($"[LibGradingService] Suite path: {suitePath}");
            _uiLogger.LogInfo($"[LibGradingService] Result root: {resultRoot}");

            try
            {
                // Create timestamped results folder
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var timestampedResultRoot = Path.Combine(resultRoot, string.Format(FileKeywords.Pattern_GradeResult, timestamp));

                var args = new ExecuteSuiteArgs
                {
                    SuitePath = suitePath,
                    ResultRoot = timestampedResultRoot,
                    ClientExePath = clientExePath,
                    ServerExePath = serverExePath,
                    UseInnerTestCaseEnvironment = useInnerEnv
                };

                // Create all the services exactly as the CLI does (SolutionGrader.Cli/Program.cs)
                // This ensures IDENTICAL behavior between CLI and UI.
                // 
                // NOTE: This service creation is intentionally duplicated from CLI's Program.cs
                // to ensure both maintain identical dependency configuration. Any changes to
                // grading behavior should be made in the Lib folder's services themselves,
                // not in the service creation logic here.
                //
                // TODO: Consider extracting to a factory class if this becomes a maintenance burden.
                IFileService files = new FileService();
                var env = new EnvironmentResetService(files);
                var suite = new ExcelSuiteLoader();
                var parse = new ExcelDetailParser();

                // Use default grading configuration (DateTime/Time excluded from grading)
                var gradingConfig = GradingConfig.Default;

                // AppsettingsCreationService uses GraderPort from GradingConfig
                var appsettings = new AppsettingsCreationService(gradingConfig);

                IRunContext runctx = new RunContext();
                IExecutableManager proc = new ExecutableManager(runctx);
                
                // OPTIMIZATION: Use SharedNetworkMonitorAdapter for optimal resource usage
                // This uses a single shared monitor for all students instead of one per student
                // 97% reduction in monitor instances (e.g., 1 monitor for 32 students instead of 32)
                // Per user request: Singular network monitor with port-based traffic isolation
                // Extract student code from result root path (e.g., Results/GradeResult_20241206/StudentCode)
                string extractedStudentCode = Path.GetFileName(resultRoot) ?? "UnknownStudent";
                INetworkMonitorService networkMonitor = new SharedNetworkMonitorAdapter(extractedStudentCode, runctx);
                
                IDataComparisonService cmp = new DataComparisonService(runctx);
                IDetailLogService log = new ExcelDetailLogService(files, runctx);

                IExecutor exec = new Executor(proc, cmp, log, runctx, gradingConfig);
                IReportService rep = new ReportService(files);

                // Create SuiteRunner with all dependencies - SAME as CLI
                var runner = new SuiteRunner(files, env, suite, parse, exec, rep, proc, networkMonitor, log, runctx, appsettings);
                
                _uiLogger.LogInfo($"[LibGradingService] Results will be saved to: {timestampedResultRoot}");

                // Execute grading using Lib folder's SuiteRunner
                var result = await runner.ExecuteSuiteAsync(args, ct);
                
                _uiLogger.LogInfo($"[LibGradingService] Grading completed with exit code: {result}");
                return result;
            }
            catch (OperationCanceledException)
            {
                _uiLogger.LogWarning("[LibGradingService] Grading was cancelled");
                return -1;
            }
            catch (Exception ex)
            {
                _uiLogger.LogError($"[LibGradingService] Grading failed: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Executes grading for multiple student submissions using the Lib folder's SuiteRunner.ExecutePaper.
        /// This handles the full paper grading flow with Docker containers.
        /// </summary>
        /// <param name="suitePath">Path to the test suite folder</param>
        /// <param name="resultRoot">Output directory for grading results</param>
        /// <param name="submissionRoot">Root folder containing student submissions</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Exit code from grading (1 = success, -1 = failure)</returns>
        public async Task<int> ExecutePaperAsync(
            string suitePath,
            string resultRoot,
            string submissionRoot,
            CancellationToken ct = default)
        {
            _uiLogger.LogInfo($"[LibGradingService] Starting paper grading via Lib folder's SuiteRunner.ExecutePaper");
            _uiLogger.LogInfo($"[LibGradingService] Suite path: {suitePath}");
            _uiLogger.LogInfo($"[LibGradingService] Submission root: {submissionRoot}");
            _uiLogger.LogInfo($"[LibGradingService] Result root: {resultRoot}");

            try
            {
                // Create timestamped results folder
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var timestampedResultRoot = Path.Combine(resultRoot, string.Format(FileKeywords.Pattern_GradeResult, timestamp));

                var args = new ExecuteSuiteArgs
                {
                    SuitePath = suitePath,
                    ResultRoot = timestampedResultRoot,
                    SubmissionRoot = submissionRoot,
                    UseInnerTestCaseEnvironment = true
                };

                // ExecutePaper uses SuiteRunner's parameterless constructor because it has its own
                // internal service creation flow (see SuiteRunner.ExecutePaper method).
                // This is intentionally different from ExecuteSuiteAsync which requires explicit
                // service injection for the orchestrator-based grading flow.
                //
                // The ExecutePaper method handles:
                // - Loading environment via EnvironmentService.GetEnvironment()
                // - Setting up containers via EnvironmentManagerInvoker.TrySetupContainer()
                // - Copying files via EnvironmentManagerInvoker.TrySetupQuestion()
                // - Cleanup via EnvironmentManagerInvoker.TryDisposeContainer()
                var runner = new SuiteRunner();
                
                _uiLogger.LogInfo($"[LibGradingService] Results will be saved to: {timestampedResultRoot}");

                // Execute paper grading using Lib folder's SuiteRunner.ExecutePaper
                var result = await runner.ExecutePaper(args, ct);
                
                _uiLogger.LogInfo($"[LibGradingService] Paper grading completed with exit code: {result}");
                return result;
            }
            catch (OperationCanceledException)
            {
                _uiLogger.LogWarning("[LibGradingService] Paper grading was cancelled");
                return -1;
            }
            catch (Exception ex)
            {
                _uiLogger.LogError($"[LibGradingService] Paper grading failed: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Executes Docker-based grading for a single student using the shared DockerGradingService.
        /// 
        /// This method delegates to Lib/SolutionGrader.Core/Services/DockerGradingService which:
        /// 1. Sets up Docker containers with TTY support for reliable output capture
        /// 2. Exposes server port to HOST for NetworkMonitor packet sniffing
        /// 3. Captures console output via application log files (bypasses docker logs buffering)
        /// 4. Generates appsettings.json with proper container networking configuration
        /// 5. Executes test cases from Detail.xlsx and compares outputs
        /// 
        /// This is the SAME service used by SolutionGrader.CLI's dockergrade command,
        /// ensuring identical behavior between UI and CLI.
        /// </summary>
        /// <param name="testKitPath">Path to the test kit folder (contains Header.xlsx, Environment.xlsx)</param>
        /// <param name="resultRoot">Output directory for grading results</param>
        /// <param name="serverDllPath">Path to student's server DLL</param>
        /// <param name="clientDllPath">Path to student's client DLL</param>
        /// <param name="studentCode">Student code for container naming</param>
        /// <param name="dockerConfig">Docker grading configuration (ports, network, etc.)</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="onContainersReady">Optional callback when containers are ready</param>
        /// <returns>Docker grading result with test case scores</returns>
        public async Task<DockerGradingResult> ExecuteDockerGradingAsync(
            string testKitPath,
            string resultRoot,
            string? serverDllPath,
            string? clientDllPath,
            string studentCode,
            DockerGradingConfig? dockerConfig = null,
            CancellationToken ct = default,
            Action? onContainersReady = null)
        {
            _uiLogger.LogInfo($"[LibGradingService] Starting Docker grading via Lib's DockerGradingService");
            _uiLogger.LogInfo($"[LibGradingService] Test kit: {testKitPath}");
            _uiLogger.LogInfo($"[LibGradingService] Student: {studentCode}");

            try
            {
                // Use result root directly without adding another timestamp folder
                // The calling code (GradingOrchestrationService) already provides a proper path
                // Previous structure: {resultRoot}/GradeResult_{timestamp}/{studentCode}
                // New simplified structure: {resultRoot} (calling code provides the full path)
                var studentResultPath = resultRoot;
                
                // Ensure the directory exists
                Directory.CreateDirectory(studentResultPath);

                // Use default config if not provided
                var config = dockerConfig ?? new DockerGradingConfig();

                // Create services
                IRunContext runctx = new RunContext();
                
                // OPTIMIZATION: Use SharedNetworkMonitorAdapter for optimal resource usage
                // Per user request: Singular network monitor with port-based traffic isolation
                INetworkMonitorService networkMonitor = new SharedNetworkMonitorAdapter(studentCode, runctx);

                // Create DockerGradingService from Lib
                var dockerGrading = new DockerGradingService(networkMonitor, runctx);
                
                // Subscribe to progress events
                dockerGrading.ProgressUpdated += (sender, args) => 
                    _uiLogger.LogInfo($"[DockerGrading] {args.Message}");
                
                // Subscribe to containers ready event for staggered startup optimization
                if (onContainersReady != null)
                {
                    dockerGrading.ContainersReady += (sender, args) => onContainersReady();
                }

                _uiLogger.LogInfo($"[LibGradingService] Results will be saved to: {studentResultPath}");

                // Execute grading using shared DockerGradingService
                var result = await dockerGrading.GradeStudentAsync(
                    config,
                    testKitPath,
                    studentResultPath,
                    serverDllPath,
                    clientDllPath,
                    studentCode,
                    ct);

                _uiLogger.LogInfo($"[LibGradingService] Docker grading completed: {(result.Passed ? "PASSED" : "FAILED")} ({result.TotalMark:F2}/{result.MaxMark:F2})");
                return result;
            }
            catch (OperationCanceledException)
            {
                _uiLogger.LogWarning("[LibGradingService] Docker grading was cancelled");
                return new DockerGradingResult { StudentCode = studentCode, ErrorMessage = "Cancelled" };
            }
            catch (Exception ex)
            {
                _uiLogger.LogError($"[LibGradingService] Docker grading failed: {ex.Message}");
                return new DockerGradingResult { StudentCode = studentCode, ErrorMessage = ex.Message };
            }
        }
        
        /// <summary>
        /// Disposes all Docker containers including the database container.
        /// Call this at the end of a grading session to clean up all resources.
        /// </summary>
        /// <param name="dockerConfig">Docker grading configuration</param>
        public void DisposeAllContainers(DockerGradingConfig? dockerConfig = null)
        {
            try
            {
                var config = dockerConfig ?? new DockerGradingConfig();
                
                // Create a temporary DockerGradingService to call DisposeAllContainers
                IRunContext runctx = new RunContext();
                var dockerGrading = new DockerGradingService(null, runctx);
                dockerGrading.DisposeAllContainers(config);
                
                _uiLogger.LogInfo("[LibGradingService] All Docker containers disposed");
            }
            catch (Exception ex)
            {
                _uiLogger.LogError($"[LibGradingService] Failed to dispose containers: {ex.Message}");
            }
        }
    }
}
