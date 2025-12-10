using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Domain.Models;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Services;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service that bridges the UI to the Lib folder's Docker grading service.
    /// 
    /// This service provides Docker-based grading exclusively:
    /// - Sets up Docker containers with TTY support
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
        /// <returns>Docker grading result with test case scores</returns>
        public async Task<DockerGradingResult> ExecuteDockerGradingAsync(
            string testKitPath,
            string resultRoot,
            string? serverDllPath,
            string? clientDllPath,
            string studentCode,
            DockerGradingConfig? dockerConfig = null,
            CancellationToken ct = default)
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
                
                // SIDECAR PATTERN: Network monitoring is done via Docker containers attached to student containers
                // The sidecar monitor captures traffic on the container's loopback interface
                // Pass null to DockerGradingService - it will create per-student network monitor containers
                // This replaces the old HOST-based SharedNetworkMonitorAdapter approach
                INetworkMonitorService? networkMonitor = null;  // Sidecar pattern - no HOST monitoring

                // Create DockerGradingService from Lib
                var dockerGrading = new DockerGradingService(networkMonitor, runctx);
                
                // Subscribe to progress events
                dockerGrading.ProgressUpdated += (sender, args) => 
                    _uiLogger.LogInfo($"[DockerGrading] {args.Message}");

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

                _uiLogger.LogInfo($"[LibGradingService] Docker grading completed: {result.TotalMark:F2}/{result.MaxMark:F2}");
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
