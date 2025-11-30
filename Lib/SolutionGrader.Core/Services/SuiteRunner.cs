using Domain.Entities.Constants;
using Domain.Entities.Main;
using Domain.Entities.Main.TestCase;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.helper;
using Newtonsoft.Json;
using ProcessLauncher.ProcessLauncher;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using SolutionGrader.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EnvConfig = Domain.Entities.Constants.EnvironmentConfiguration;

namespace SolutionGrader.Core.Services
{
    public sealed class SuiteRunner
    {
        private readonly IFileService _files;
        private readonly IEnvironmentResetService _env;
        private readonly ITestSuiteLoader _suite;
        private readonly ITestCaseParser _parser;
        private readonly IExecutor _exec;
        private readonly IReportService _report;
        private readonly IExecutableManager _proc;
        private readonly INetworkMonitorService? _networkMonitor;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly IAppsettingsCreationService _appsettings;
        private readonly TestCaseOrchestrator _orchestrator;

        public SuiteRunner()
        {
        }

        public SuiteRunner(
            IFileService files,
            IEnvironmentResetService env,
            ITestSuiteLoader suite,
            ITestCaseParser parser,
            IExecutor exec,
            IReportService report,
            IExecutableManager proc,
            INetworkMonitorService? networkMonitor,
            IDetailLogService log,
            IRunContext run,
            IAppsettingsCreationService appsettings)
        {
            _files = files; _env = env; _suite = suite; _parser = parser; _exec = exec; _report = report; _proc = proc; _networkMonitor = networkMonitor; _log = log; _run = run; _appsettings = appsettings;

            // Create orchestrator for step-based execution
            _orchestrator = new TestCaseOrchestrator(files, env, parser, exec, report, proc, networkMonitor, log, run, appsettings);
        }

        public async Task<int> ExecutePaper(ExecuteSuiteArgs args, CancellationToken ct = default)
        {

            if (args.SubmissionRoot == null)
            {
                throw new ArgumentNullException("No submission folder provided!");
            }

            var fileService = new FileService();
            Dictionary<string, Dictionary<string, (string? Q11, string? Q12)>> studentSubmissions = fileService.GetStudentSubmission(args.SubmissionRoot);



            // Loop through each paper and grade submissions ONE BY ONE - no parallelism here
            foreach (KeyValuePair<string, Dictionary<string, (string? Q11, string? Q12)>> paper in studentSubmissions)
            {
                if (paper.Value.Count == 0)
                {
                    Console.WriteLine($"[ENV] No submissions found for paper no {paper.Key}, skipping...");
                    continue;
                }

                // Start env init for each student submission
                foreach (KeyValuePair<string, (string? Q11, string? Q12)> submission in paper.Value)
                {
                    Console.WriteLine($"[ENV] Start creating enviroment config for {submission.Key}");
                    // This only assume there's only 1 testsuite, current one has no multi suit handler
                    string envPath = Path.Combine(args.SuitePath, FileKeywords.FileName_Environment);
                    global::Domain.Entities.Main.Environment env = EnvironmentService.GetEnvironment(envPath);

                    var (q11, q12) = submission.Value;

                    string? givenPath = null;
                    if (q11 == null || q12 == null)
                    {
                        givenPath = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsolePath, "");
                        if (string.IsNullOrEmpty(givenPath))
                            throw new ArgumentNullException("[ENV] Given console path missing");
                    }

                    // If Q11 null -> it's the given -> use given. Same for Q12.
                    q11 ??= givenPath;
                    q12 ??= givenPath;

                    ModifyEnvironmentForContainerInit(env, q11, q12);

                    // Setup other common configs, will extract onto other place later
                    SetOrAddConfig(env.Configs, EnvConfig.RuntimesFolder, args.SuitePath + "\\" + TryGetValueOrDefault(env.Configs, EnvConfig.RuntimesFolder));
                    SetOrAddConfig(env.Configs, EnvConfig.DefaultDatabaseFilePath, args.SuitePath + "\\" + TryGetValueOrDefault(env.Configs, EnvConfig.DefaultDatabaseFilePath));

                    // 
                    SetOrAddConfig(env.Configs, EnvConfig.DockerServerPath, TryGetDllPath(q11));
                    SetOrAddConfig(env.Configs, EnvConfig.DockerClientPath, TryGetDllPath(q12));
                    try
                    {
                        // Mute error, i know this is bad
                        EnvironmentManagerInvoker.TrySetupContainer(env, out _);
                        EnvironmentManagerInvoker.TrySetupQuestion(env, out _);

                        // Grading logic goes here
                        // Client and server will output in docker log, use MonitorLog to get the log line-by-line
                        // Log located at tmp/<app-name>.log
                        //
                        // use SendInputToContainer() to send input to client
                        // throw it in here so that i can test env first, remove when intergrate grading logic
                        //
                        if (false)
                        {
                            var executor = new DockerCommandExecutor();

                            string containerName = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleContainerName); // get client, yes, given is client
                            string appName = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleAppName);
                            string input = "SAMPLE INPUT FOR TESTING PURPOSES";
                            executor.SendInputToContainer(containerName, appName, input);
                        }


                        // Dispose all containers
                        EnvironmentManagerInvoker.TryDisposeContainer(env, out _);
                        Console.WriteLine($"[ENV] Disposed environment for {submission.Key}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ENV] Failed to create env for {submission.Key}, error: {ex.Message}");
                    }
                }
            }
            return 1;
        }

        // Method to get real-time logs from a Docker container
        public async Task MonitorLogsAsync(string containerName, Action<string> onLogLine)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"logs -f --tail 0 {containerName}", // Only show newline at the time this method called -> no old log
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);

            // Read the stream line by line
            while (!process.StandardOutput.EndOfStream)
            {
                string line = await process.StandardOutput.ReadLineAsync();
                if (line != null)
                {
                    onLogLine(line); // Trigger the callback with the new log line
                }
            }
        }

        private string TryGetDllPath(string path)
        {
            string dllPath = "";
            string root = new DirectoryInfo(path).Name;

            string[] fileName = { "Q11.dll", "Q12.dll", "Project11.dll", "Project12.dll" };

            foreach (var name in fileName)
            {
                string potentialPath = Path.Combine(path, name);
                if (File.Exists(potentialPath))
                {
                    dllPath = "/apps/" + root + "/" + name;
                    break;
                }
            }
            return dllPath;
        }

        // Will throw ts some where later, currently here because im too lazy to implement abstraction
        // The Code Container will be Q11 and Given Container will be Q12 in the ENV config
        // Why this way? Because i want to cut corners and reuse existing fields
        private void ModifyEnvironmentForContainerInit(global::Domain.Entities.Main.Environment env, string q11, string q12)
        {
            string? givenConsoleImageName = TryGetValueOrDefault(env.Configs, EnvConfig.CodeImageName, null);

            if (!string.IsNullOrEmpty(givenConsoleImageName))
            {
                SetOrAddConfig(env.Configs, EnvConfig.GivenConsoleImageName, givenConsoleImageName);
            }
            else
            {
                throw new ArgumentNullException("[ENV] Fym the image is null?");
            }


            SetOrAddConfig(env.Configs, EnvConfig.CodeFilePath, q11);
            SetOrAddConfig(env.Configs, EnvConfig.CodeContainerName, "ag-server");
            SetOrAddConfig(env.Configs, EnvConfig.StudentQuestionName, "ag-server");
            SetOrAddConfig(env.Configs, EnvConfig.GivenConsolePath, q12);
            SetOrAddConfig(env.Configs, EnvConfig.GivenConsoleContainerName, "ag-client");
            SetOrAddConfig(env.Configs, EnvConfig.GivenConsoleAppName, "ag-client");
            SetOrAddConfig(env.Configs, EnvConfig.DatabaseName, TryGetValueOrDefault(env.Configs, EnvConfig.DefaultDatabaseName));
            //SetOrAddConfig(env.Configs, EnvConfig.DatabaseUsername, TryGetValueOrDefault(env.Configs, EnvConfig.DatabaseUsername));
            //SetOrAddConfig(env.Configs, EnvConfig.DatabaseContainerName, TryGetValueOrDefault(env.Configs, EnvConfig.DatabaseContainerName));
            //SetOrAddConfig(env.Configs, EnvConfig.DatabasePassword, TryGetValueOrDefault(env.Configs, EnvConfig.DatabasePassword));

            string? givenConsoleInternalPort = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleContainerInternalPort, null);
            if (string.IsNullOrEmpty(givenConsoleInternalPort))
            {
                givenConsoleInternalPort = TryGetValueOrDefault(env.Configs, EnvConfig.CodeContainerInternalPort, "");
                SetOrAddConfig(env.Configs, EnvConfig.GivenConsoleContainerInternalPort, givenConsoleInternalPort);
            }

            string? givenConsoleHostPort = TryGetValueOrDefault(env.Configs, EnvConfig.GivenConsoleContainerHostPort, null);
            if (string.IsNullOrEmpty(givenConsoleHostPort))
            {
                givenConsoleHostPort = TryGetValueOrDefault(env.Configs, EnvConfig.CodeContainerHostPort, "");
                SetOrAddConfig(env.Configs, EnvConfig.GivenConsoleContainerHostPort, givenConsoleHostPort);
            }
        }

        public async Task<int> ExecuteSuiteAsync(ExecuteSuiteArgs args, CancellationToken ct = default)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_LOADING, args.SuitePath)}");
            var def = _suite.Load(args.SuitePath, args.UseInnerTestCaseEnvironment);
            args.Protocol = def.Protocol;

            // Set datetime format in RunContext if available from header
            if (!string.IsNullOrWhiteSpace(def.DateTimeFormat))
            {
                _run.DateTimeFormat = def.DateTimeFormat;
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_PROTOCOL, args.Protocol)}");
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} {string.Format(LoggingKeywords.MSG_SUITE_CASES_FOUND, def.Cases.Count)}");
            _files.EnsureDirectory(args.ResultRoot);

            foreach (var q in def.Cases)
            {
                ct.ThrowIfCancellationRequested();

                Console.WriteLine($"\n{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_STARTING, q.Name, q.Mark)}");

                // Determine executables to use
                string? clientExePath = args.ClientExePath;
                string? serverExePath = args.ServerExePath;

                // If not provided via command-line, try to use from environment
                if (string.IsNullOrWhiteSpace(clientExePath) && !string.IsNullOrWhiteSpace(def.Environment?.GivenClientPath))
                {
                    clientExePath = def.Environment.GivenClientPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using client from environment: {clientExePath}");
                }

                if (string.IsNullOrWhiteSpace(serverExePath) && !string.IsNullOrWhiteSpace(def.Environment?.GivenServerPath))
                {
                    serverExePath = def.Environment.GivenServerPath;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using server from environment: {serverExePath}");
                }

                // Handle Grade_Content field to determine which executable to use
                // This allows grading only the client or server component independently.
                // When Grade_Content is set, the system will:
                // 1. "Client" -> Use the student's client but plug in a reference server
                // 2. "Server" -> Use the student's server but plug in a reference client
                // 
                // Resolution priority for reference executables:
                // 1. Test case environment (q.Environment.GivenServerPath/GivenClientPath)
                // 2. Suite environment (def.Environment.GivenServerPath/GivenClientPath) - typically from Meta/Given folder
                if (!string.IsNullOrWhiteSpace(q.GradeContent))
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Grade_Content: {q.GradeContent}");

                    if (q.GradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                    {
                        // Grading client only - use given/reference server if available
                        // First try test case environment, then fall back to suite environment
                        var referenceServerPath = q.Environment?.GivenServerPath
                                                  ?? def.Environment?.GivenServerPath;

                        if (!string.IsNullOrWhiteSpace(referenceServerPath))
                        {
                            serverExePath = referenceServerPath;
                            var source = !string.IsNullOrWhiteSpace(q.Environment?.GivenServerPath)
                                ? "test case environment"
                                : "suite environment (Meta/Given)";
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference server from {source}: {serverExePath}");
                        }
                        else
                        {
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Warning: Grade_Content is 'Client' but no reference server found in test case or suite environment");
                        }
                    }
                    else if (q.GradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                    {
                        // Grading server only - use given/reference client if available
                        // First try test case environment, then fall back to suite environment
                        var referenceClientPath = q.Environment?.GivenClientPath
                                                  ?? def.Environment?.GivenClientPath;

                        if (!string.IsNullOrWhiteSpace(referenceClientPath))
                        {
                            clientExePath = referenceClientPath;
                            var source = !string.IsNullOrWhiteSpace(q.Environment?.GivenClientPath)
                                ? "test case environment"
                                : "suite environment (Meta/Given)";
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Using reference client from {source}: {clientExePath}");
                        }
                        else
                        {
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Warning: Grade_Content is 'Server' but no reference client found in test case or suite environment");
                        }
                    }
                }

                // Log final executable paths for debugging
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Final client executable: {clientExePath ?? "(none)"}");
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Final server executable: {serverExePath ?? "(none)"}");

                var outDir = Path.Combine(args.ResultRoot, q.Name);

                // ***** STEP-BASED ORCHESTRATION *****
                // Each step is separated for clarity and can be monitored/logged independently

                // Step 1: Setup Environment
                var (setupOk, setupMsg) = await _orchestrator.SetupEnvironmentAsync(q, def, args, clientExePath, serverExePath, ct);
                if (!setupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at environment setup: {setupMsg}");
                    continue;
                }

                // Step 2: Read Test Kit Information
                var (readOk, readMsg, steps) = _orchestrator.ReadTestKitInfo(q, outDir);
                if (!readOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at test kit reading: {readMsg}");
                    continue;
                }

                // Step 3: Initialize Processes and Start Network Monitor
                var (initOk, initMsg) = await _orchestrator.InitializeProcessesAsync(clientExePath, serverExePath, ct);
                if (!initOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case failed at process initialization: {initMsg}");
                    continue;
                }

                // Step 4: Execute and Grade Steps
                var (execOk, execMsg, results) = await _orchestrator.ExecuteAndGradeStepsAsync(steps, args, ct);
                if (!execOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Test case execution encountered issues: {execMsg}");
                }

                // Step 5: Write Results
                var (writeOk, writeMsg) = await _orchestrator.WriteResultsAsync(outDir, steps[0].QuestionCode, results, ct);
                if (!writeOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Failed to write results: {writeMsg}");
                }

                // Step 6: Cleanup
                var (cleanupOk, cleanupMsg) = await _orchestrator.CleanupAsync();
                if (!cleanupOk)
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Cleanup encountered issues: {cleanupMsg}");
                }

                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} {string.Format(LoggingKeywords.MSG_TESTCASE_COMPLETED, q.Name)}\n");
                
                // Step 7: Wait for port to be released before next test case
                // This prevents "Address already in use" errors when the socket is in TIME_WAIT state
                // after the previous test case's server process terminates.
                await WaitForPortReleaseAsync(PortKeywords.DEFAULT_GRADER_PORT, ct);
            }

            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_SUITE} All test cases completed successfully");
            return 1;
        }

        private static string TryGetValueOrDefault(
            Dictionary<string, string> configs,
            string key,
            string defaultValue = "")
        {
            if (configs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;

            return defaultValue;
        }

        public static void SetOrAddConfig(Dictionary<string, string> configs, string key, string value)
        {
            if (configs.ContainsKey(key))
            {
                configs.Remove(key);
            }
            configs.Add(key, value);
        }

        /// <summary>
        /// Waits for a TCP port to be released (no longer in use).
        /// This is crucial between test cases to prevent "Address already in use" errors
        /// when the socket is still in TIME_WAIT state after the previous server terminates.
        /// 
        /// The method first checks if the port is already available (fast path).
        /// If not, it polls with exponential backoff until:
        /// - The port becomes available (returns true)
        /// - Maximum wait time is exceeded (returns false)
        /// - Cancellation is requested
        /// 
        /// NOTE: The "Address already in use" error typically occurs because:
        /// 1. The previous server process was killed but the socket is in TIME_WAIT state
        /// 2. TIME_WAIT can last up to 2*MSL (Maximum Segment Lifetime), typically 60 seconds on Windows
        /// 3. Student code may not properly close sockets before terminating
        /// 
        /// For faster recovery, the student server should use SO_REUSEADDR socket option.
        /// </summary>
        /// <param name="port">The TCP port to wait for</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="maxWaitSeconds">Maximum time to wait in seconds (default: 5 - only waits if port is in use)</param>
        /// <returns>True if port is available, false if timeout exceeded</returns>
        private static async Task<bool> WaitForPortReleaseAsync(int port, CancellationToken ct, int maxWaitSeconds = 5)
        {
            // Fast path: Check if port is already available (most common case)
            if (IsPortAvailable(port))
            {
                return true;
            }
            
            // Port is in use - now we need to wait
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Port {port} is in use, waiting for release (max {maxWaitSeconds}s)...");
            
            var startTime = DateTime.UtcNow;
            int delayMs = 100; // Start with 100ms delay
            const int maxDelayMs = 500; // Cap at 500ms for faster response
            
            // Try to forcefully close any lingering connections to the port
            TryKillProcessOnPort(port);
            
            while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds && !ct.IsCancellationRequested)
            {
                await Task.Delay(delayMs, ct);
                
                if (IsPortAvailable(port))
                {
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Port {port} is now available (after {elapsed:F1}s)");
                    return true;
                }
                
                // Exponential backoff
                delayMs = Math.Min(delayMs * 2, maxDelayMs);
            }
            
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} WARNING: Port {port} may still be in use after {maxWaitSeconds}s wait. " +
                              "Consider using SO_REUSEADDR in server code.");
            return false;
        }

        /// <summary>
        /// Attempts to find and kill any process listening on the specified port.
        /// This is a best-effort cleanup to help release ports faster.
        /// Uses System.Net.NetworkInformation for cross-platform support.
        /// Falls back to netstat -ano on Windows if needed.
        /// </summary>
        /// <param name="port">The TCP port to clean up</param>
        private static void TryKillProcessOnPort(int port)
        {
            try
            {
                // First try cross-platform approach using System.Net.NetworkInformation
                // This is more reliable but doesn't give us PID information directly
                var listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners();
                
                bool portInUse = listeners.Any(ep => ep.Port == port);
                if (!portInUse)
                {
                    return; // Port not in use, nothing to do
                }
                
                // Port is in use - try Windows-specific netstat to find and kill the process
                // This is best-effort and may fail on non-Windows systems
                TryKillProcessOnPortWindows(port);
            }
            catch
            {
                // Best effort - ignore errors
            }
        }
        
        /// <summary>
        /// Windows-specific implementation using netstat to find and kill processes on a port.
        /// </summary>
        private static void TryKillProcessOnPortWindows(int port)
        {
            try
            {
                // Windows netstat -ano format: "  TCP    0.0.0.0:5000    0.0.0.0:0    LISTENING    1234"
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return;
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                
                // Parse netstat output to find PIDs using the port
                // Use regex for more precise port matching to avoid matching 15000 when looking for 5000
                var portPattern = new System.Text.RegularExpressions.Regex(
                    $@"^\s*TCP\s+\S+:({port})\s+\S+\s+LISTENING\s+(\d+)", 
                    System.Text.RegularExpressions.RegexOptions.Multiline | 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                var matches = portPattern.Matches(output);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count >= 3)
                    {
                        var pidStr = match.Groups[2].Value;
                        if (int.TryParse(pidStr, out var pid) && pid > 0)
                        {
                            try
                            {
                                var proc = System.Diagnostics.Process.GetProcessById(pid);
                                if (proc != null && !proc.HasExited)
                                {
                                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TESTCASE} Killing process {pid} using port {port}");
                                    proc.Kill();
                                    proc.WaitForExit(2000);
                                }
                            }
                            catch { /* Process may have already exited */ }
                        }
                    }
                }
            }
            catch
            {
                // Best effort - ignore errors
            }
        }

        /// <summary>
        /// Checks if a TCP port is available (not in use by any process).
        /// Attempts to bind to the port; if successful, the port is available.
        /// Uses SO_REUSEADDR to handle TIME_WAIT state more gracefully.
        /// </summary>
        /// <param name="port">The TCP port to check</param>
        /// <returns>True if port is available, false if in use</returns>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork, 
                    System.Net.Sockets.SocketType.Stream, 
                    System.Net.Sockets.ProtocolType.Tcp);
                
                // Enable SO_REUSEADDR to allow binding even if port is in TIME_WAIT
                socket.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket, 
                    System.Net.Sockets.SocketOptionName.ReuseAddress, 
                    true);
                
                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                socket.Close();
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }
    }
}
