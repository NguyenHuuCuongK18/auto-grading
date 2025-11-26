using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.IO;
namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Executes test steps including process management, data comparisons, and validations.
    /// 
    /// NOTE: The Executor no longer depends on IMiddlewareService.
    /// Network monitoring is now handled by TestCaseOrchestrator using INetworkMonitorService.
    /// The Executor focuses on step execution and data comparison.
    /// </summary>
    public sealed class Executor : IExecutor
    {
        private readonly HttpClient _http = new();
        private readonly IExecutableManager _proc;
        private readonly IDataComparisonService _cmp;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;
        private readonly GradingConfig _gradingConfig;

        private int _configuredServerPort = PortKeywords.DEFAULT_SERVER_FALLBACK_PORT;

        public Executor(IExecutableManager proc, IDataComparisonService cmp, IDetailLogService log, IRunContext run, GradingConfig? gradingConfig = null)
        {
            _proc = proc;
            _cmp = cmp;
            _log = log;
            _run = run;
            _gradingConfig = gradingConfig ?? GradingConfig.Default;
        }

        public void ConfigureServerPort(int serverPort)
        {
            _configuredServerPort = serverPort;
        }

        /// <summary>
        /// Checks if a TCP port is in listening state WITHOUT establishing a connection.
        /// This prevents triggering "Client connected/disconnected" messages on the server
        /// that should only appear when actual client connections occur in later stages.
        /// 
        /// Implementation: Attempts to bind to the port using a test listener.
        /// - If binding SUCCEEDS: Port was available (not in use) -> returns false
        /// - If binding FAILS with AddressAlreadyInUse: Port is occupied by our server -> returns true
        /// 
        /// This approach avoids connecting to the server while still verifying it's listening.
        /// </summary>
        private static bool IsTcpPortInListeningState(int port)
        {
            try
            {
                // Try to bind to the port with a test listener
                using var testListener = new TcpListener(IPAddress.Loopback, port);
                testListener.Start();  // This will succeed if port is available
                testListener.Stop();
                
                // If we got here, we successfully bound to the port
                // This means the port was NOT in use (server not listening yet)
                return false;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Failed to bind because port is already in use
                // This means our server is listening on this port
                return true;
            }
            catch (SocketException ex)
            {
                // Other socket errors (e.g., permission denied, invalid port)
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} Port check failed: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                // Unexpected errors
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} Unexpected error in port check: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Checks if a TCP port is listening and accepting connections.
        /// WARNING: This method establishes a connection which may trigger server logging.
        /// Use IsTcpPortInListeningState for health checks during ServerStart to avoid
        /// premature "Client connected" messages.
        /// </summary>
        private static bool IsTcpPortListening(int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(AppsettingKeywords.TCP_LOCALHOST, port);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public async Task<(bool, string)> ExecuteAsync(Step step, ExecuteSuiteArgs args, CancellationToken ct)
        {
            string errCode = ErrorCodes.NONE;
            (bool ok, string msg) result;

            try
            {
                switch (step.Action)
                {
                    case var a when a == ActionKeywords.ServerStart:
                        {
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {LoggingKeywords.MSG_ACTION_SERVER_START}");
                            
                            try
                            {
                                _proc.StartServer();
                            }
                            catch (Exception ex)
                            {
                                errCode = ErrorCodes.SERVER_EXE_MISSING;
                                result = (false, $"Failed to start server: {ex.Message}");
                                break;
                            }
                            
                            // Wait for server to be ready
                            var t0 = Environment.TickCount;
                            bool serverReady = false;
                            bool isHttpProtocol = args.Protocol?.Equals(AppsettingKeywords.PROTOCOL_HTTP, StringComparison.OrdinalIgnoreCase) == true;
                            
                            // For TCP servers, we use a non-connecting health check to avoid triggering
                            // "Client connected/disconnected" messages that should appear in later stages
                            // For HTTP servers, we use HTTP health checks that don't trigger TCP connection events
                            while (Environment.TickCount - t0 < PortKeywords.SERVER_READY_TIMEOUT_SECONDS * 1000)
                            {
                                // First check if the process has crashed
                                if (!_proc.IsServerRunning)
                                {
                                    // Process exited early - wait a bit for output then fail
                                    await Task.Delay(200, ct);
                                    break;
                                }
                                
                                // For HTTP servers, try HTTP health check
                                if (isHttpProtocol)
                                {
                                    try
                                    {
                                        var res = await _http.GetAsync($"http://127.0.0.1:{_configuredServerPort}/healthz", ct);
                                        if (res.IsSuccessStatusCode)
                                        {
                                            serverReady = true;
                                            break;
                                        }
                                    }
                                    catch { /* HTTP health check failed, will retry */ }
                                }
                                else
                                {
                                    // For TCP servers, use non-connecting port check to avoid triggering connection messages
                                    // We check if the port is in listening state without establishing a connection
                                    if (IsTcpPortInListeningState(_configuredServerPort))
                                    {
                                        serverReady = true;
                                        break;
                                    }
                                }
                                
                                await Task.Delay(PortKeywords.HEALTH_CHECK_POLL_INTERVAL_MS, ct);
                            }
                            
                            if (!_proc.IsServerRunning)
                            {
                                errCode = ErrorCodes.PROCESS_CRASHED;
                                var output = _proc.GetServerOutput() ?? "";
                                var errorPreview = output.Length > 500 ? output.Substring(0, 500) + "..." : output;
                                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {LoggingKeywords.MSG_ACTION_SERVER_FULL_LOG_AVAILABLE}");
                                result = (false, $"Server process failed to start or crashed immediately. Output: {errorPreview}");
                                break;
                            }
                            
                            if (!serverReady)
                            {
                                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {LoggingKeywords.MSG_ACTION_SERVER_NOT_INITIALIZED}");
                            }
                            
                            // Wait for server output to stabilize after startup
                            // Use shorter wait time to capture only initial startup messages
                            // Connection-related messages will be captured when actual connections occur
                            await _proc.WaitForServerOutputAsync(1, ct);
                            
                            var serverOutput = _proc.GetServerOutput();
                            var outputPreview = serverOutput.Length > 100 
                                ? serverOutput.Substring(0, 100) + "..." 
                                : serverOutput;
                            
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {string.Format(LoggingKeywords.MSG_ACTION_SERVER_OUTPUT, outputPreview)}");
                            result = (true, $"Server started successfully. Process running: {_proc.IsServerRunning}");
                            break;
                        }

                    case var a when a == ActionKeywords.ClientStart:
                        {
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {LoggingKeywords.MSG_ACTION_CLIENT_START}");
                            
                            try
                            {
                                _proc.StartClient();
                            }
                            catch (Exception ex)
                            {
                                errCode = ErrorCodes.CLIENT_EXE_MISSING;
                                result = (false, $"Failed to start client: {ex.Message}");
                                break;
                            }
                            
                            // Wait for client output to stabilize after startup
                            await _proc.WaitForClientOutputAsync(3, ct);
                            
                            if (!_proc.IsClientRunning)
                            {
                                errCode = ErrorCodes.PROCESS_CRASHED;
                                var output = _proc.GetClientOutput() ?? "";
                                var errorPreview = output.Length > 500 ? output.Substring(0, 500) + "..." : output;
                                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {LoggingKeywords.MSG_ACTION_CLIENT_FULL_LOG_AVAILABLE}");
                                result = (false, $"Client process failed to start or crashed immediately. Output: {errorPreview}");
                                break;
                            }
                            
                            var clientOutput = _proc.GetClientOutput();
                            var outputPreview = clientOutput.Length > 100 
                                ? clientOutput.Substring(0, 100) + "..." 
                                : clientOutput;
                            
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {string.Format(LoggingKeywords.MSG_ACTION_CLIENT_OUTPUT, outputPreview)}");
                            
                            // Wait for server output after client starts (in case connection triggers server messages)
                            // This allows "client connected" messages to be captured in the correct stage
                            await _proc.WaitForServerOutputAsync(PortKeywords.CONNECTION_OUTPUT_WAIT_SECONDS, ct);
                            
                            result = (true, $"Client started successfully. Process running: {_proc.IsClientRunning}");
                            break;
                        }

                    case var a when a == ActionKeywords.ClientInput:
                        {
                            // Support empty/blank input - if step.Value is null or empty, send empty line
                            var inputValue = step.Value ?? string.Empty;
                            var displayInput = string.IsNullOrEmpty(inputValue) ? "(empty)" : inputValue;
                            
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} {string.Format(LoggingKeywords.MSG_ACTION_CLIENT_INPUT_SENDING, displayInput)}");
                            _proc.SendClientInput(inputValue);
                            
                            // Wait for client to process input and respond, or timeout (default 15 seconds)
                            const int timeoutSeconds = 15;
                            var gotOutput = await _proc.WaitForClientOutputAsync(timeoutSeconds, ct);
                            
                            if (gotOutput)
                            {
                                result = (true, $"Sent input: {displayInput}, received response");
                            }
                            else if (_proc.IsClientRunning)
                            {
                                // Client still running but no output - might be waiting for more input
                                result = (true, $"Sent input: {displayInput}, no response yet (client still running)");
                            }
                            else
                            {
                                // Client exited - might be an error
                                errCode = ErrorCodes.PROCESS_CRASHED;
                                result = (false, $"Sent input: {displayInput}, but client process exited");
                            }
                            break;
                        }

                    case var a when a == ActionKeywords.RunServer:
                        {
                            var serverPath = _run.ResolveServerExecutable();
                            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
                            { errCode = ErrorCodes.FILE_NOT_FOUND; return (false, "Server executable not found"); }

                            var p = await _proc.StartAsync(serverPath, $"--urls http://127.0.0.1:{_configuredServerPort}", ct);
                            var t0 = Environment.TickCount;
                            while (Environment.TickCount - t0 < PortKeywords.SERVER_READY_TIMEOUT_SECONDS * 1000)
                            {
                                try
                                {
                                    var res = await _http.GetAsync($"http://127.0.0.1:{_configuredServerPort}/healthz", ct);
                                    if (res.IsSuccessStatusCode) break;
                                }
                                catch { /* wait */ }
                                await Task.Delay(PortKeywords.HEALTH_CHECK_POLL_INTERVAL_MS, ct);
                            }
                            result = (true, "RunServer OK");
                            break;
                        }

                    case var a when a == ActionKeywords.Wait:
                        var ms = int.TryParse(step.Value ?? "1000", out var v) ? v : 1000;
                        await Task.Delay(ms, ct);
                        result = (true, $"Waited {ms}ms");
                        break;

                    case var a when a == ActionKeywords.HttpRequest:
                        {
                            var parts = (step.Value ?? "").Split('|', 4, StringSplitOptions.TrimEntries);
                            if (parts.Length < 2) { errCode = ErrorCodes.HTTP_REQUEST_INVALID; result = (false, "HTTP_REQUEST requires METHOD|URL"); break; }

                            using var req = new HttpRequestMessage(new HttpMethod(parts[0]), parts[1]);
                            var resp = await _http.SendAsync(req, ct);
                            var body = await resp.Content.ReadAsStringAsync(ct);

                            // Optional expected status code (3rd part)
                            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2], out var expectedStatus))
                            {
                                if ((int)resp.StatusCode != expectedStatus)
                                {
                                    errCode = ErrorCodes.HTTP_NON_SUCCESS;
                                    result = (false, $"HTTP status {(int)resp.StatusCode} != expected {expectedStatus}");
                                    break;
                                }
                            }
                            else if (!resp.IsSuccessStatusCode)
                            {
                                errCode = ErrorCodes.HTTP_NON_SUCCESS;
                                result = (false, $"HTTP {resp.StatusCode}");
                                break;
                            }

                            // Optional expected body substring (4th part)
                            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) &&
                                !body.Contains(parts[3], StringComparison.OrdinalIgnoreCase))
                            {
                                errCode = ErrorCodes.TEXT_MISMATCH;
                                result = (false, "Expected body text not found");
                                break;
                            }

                            // Write client actual as before
                            try
                            {
                                var stageLabel = GetStageLabel(step);
                                _run.SetClientOutput(step.QuestionCode, stageLabel, body);
                            }
                            catch { /* ignore capture errors */ }

                            result = (true, "HTTP ok");
                            break;
                        }

                    case var a when a == ActionKeywords.CompareText:
                        // Use comprehensive validation if step has metadata
                        if (step.Metadata?.Count > 0)
                        {
                            result = _cmp.ValidateStep(step, ResolveActualPath(step), _gradingConfig);
                        }
                        else
                        {
                            result = _cmp.CompareText(step.Target, ResolveActualPath(step));
                        }
                        break;

                    case var a when a == ActionKeywords.CompareJson:
                        // Use comprehensive validation if step has metadata
                        if (step.Metadata?.Count > 0)
                        {
                            result = _cmp.ValidateStep(step, ResolveActualPath(step), _gradingConfig);
                        }
                        else
                        {
                            result = _cmp.CompareJson(step.Target, ResolveActualPath(step));
                        }
                        break;

                    case var a when a == ActionKeywords.CompareCsv:
                        result = _cmp.CompareCsv(step.Target, step.Value);
                        break;

                    case var a when a == ActionKeywords.TcpRelay:
                        {
                            // NOTE: TcpRelay action is now deprecated.
                            // Network monitoring is handled by TestCaseOrchestrator using INetworkMonitorService.
                            // This action is kept for backward compatibility but simply logs and continues.
                            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_ACTION} TcpRelay action is deprecated - network monitoring handled by orchestrator");
                            result = (true, "TcpRelay action deprecated - network monitor active");
                            break;
                        }

                    default:
                        result = (false, $"Unknown action: {step.Action}");
                        errCode = ErrorCodes.UNKNOWN;
                        break;
                }
            }
            catch (OperationCanceledException) { errCode = ErrorCodes.TIMEOUT; return (false, "Step timed out"); }
            catch (HttpRequestException ex) { errCode = ErrorCodes.HTTP_NON_SUCCESS; return (false, ex.Message); }
            catch (Exception ex) { errCode = ErrorCodes.UNKNOWN; return (false, ex.Message); }

            return result;

            string? ResolveActualPath(Step step)
            {
                var actual = step.Value;
                if (!string.IsNullOrWhiteSpace(actual) &&
                    (actual.StartsWith("memory://", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(actual)))
                    return actual;

                var stage = ParseStageFromId(step.Id);
                var stageLabel = GetStageLabel(step);
                if (string.IsNullOrWhiteSpace(stage))
                    return actual;

                // OC-DATA- steps should read from server response (data sent from server to client)
                if (step.Id.StartsWith($"{GradingKeywords.StepPrefix_OutputClient}DATA-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerResponseCaptureKey(step.QuestionCode, stageLabel);

                // Other OC- steps read from client output
                if (step.Id.StartsWith(GradingKeywords.StepPrefix_OutputClient, StringComparison.OrdinalIgnoreCase))
                    return _run.GetClientCaptureKey(step.QuestionCode, stageLabel);
                    
                // Legacy: CLIENT- prefix (same as OC-)
                if (step.Id.StartsWith("CLIENT-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetClientCaptureKey(step.QuestionCode, stageLabel);
                
                // NETWORK-RESPAYLOAD steps read from network response body capture
                if (step.Id.StartsWith("NETWORK-RESPAYLOAD", StringComparison.OrdinalIgnoreCase))
                {
                    // First try the dedicated body capture, then fall back to full response
                    var bodyKey = $"network.{stageLabel}.res.body";
                    if (_run.TryGetCapturedOutput(bodyKey, out _))
                        return $"memory://{bodyKey}";
                    return _run.GetServerResponseCaptureKey(step.QuestionCode, stageLabel);
                }
                
                // NETWORK-REQPAYLOAD steps read from network request body capture
                if (step.Id.StartsWith("NETWORK-REQPAYLOAD", StringComparison.OrdinalIgnoreCase))
                {
                    // First try the dedicated body capture, then fall back to full request
                    var bodyKey = $"network.{stageLabel}.req.body";
                    if (_run.TryGetCapturedOutput(bodyKey, out _))
                        return $"memory://{bodyKey}";
                    return _run.GetServerRequestCaptureKey(step.QuestionCode, stageLabel);
                }
                
                // NETWORK-METHOD steps are handled separately (HTTP method comparison)
                if (step.Id.StartsWith("NETWORK-METHOD", StringComparison.OrdinalIgnoreCase))
                    return actual; // This is handled by ValidateStep with metadata

                if (step.Id.StartsWith($"{GradingKeywords.StepPrefix_OutputServer}REQ-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerRequestCaptureKey(step.QuestionCode, stageLabel);

                if (step.Id.StartsWith($"{GradingKeywords.StepPrefix_OutputServer}OUT-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);

                // Fallback for other OS- prefixed steps
                if (step.Id.StartsWith(GradingKeywords.StepPrefix_OutputServer, StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);
                    
                // Legacy: SERVER- prefix (same as OS-)
                if (step.Id.StartsWith("SERVER-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);

                return actual;
            }

            string GetStageLabel(Step step)
            {
                if (!string.IsNullOrWhiteSpace(step.Stage))
                    return step.Stage;

                var parsed = ParseStageFromId(step.Id);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return parsed;

                return _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
            }

            static string ParseStageFromId(string id)
            {
                // IDs are like "OC-HTTP-Stage", "OC-CMP-Stage", etc.
                var i = id.LastIndexOf('-');
                return i >= 0 && i < id.Length - 1 ? id[(i + 1)..] : id;
            }
        }
    }
}
