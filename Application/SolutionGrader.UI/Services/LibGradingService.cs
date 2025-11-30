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
    /// Service that bridges the UI to the Lib folder's SuiteRunner.
    /// This service does NOT contain its own grading logic - it delegates to the Lib folder's
    /// SuiteRunner which handles Docker container setup, file copying, and test execution.
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
                
                // NetworkMonitorService passively sniffs packets
                INetworkMonitorService networkMonitor = new NetworkMonitorService(runctx);
                
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
    }
}
