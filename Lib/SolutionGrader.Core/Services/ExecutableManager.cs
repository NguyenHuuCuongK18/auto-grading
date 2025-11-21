using System.Diagnostics;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;
using System.IO;


namespace SolutionGrader.Core.Services
{
    public sealed class ExecutableManager : IExecutableManager
    {
        private Process? _client;
        private Process? _server;
        private string? _clientPath;
        private string? _serverPath;

        private readonly IRunContext _run;
        private readonly StringBuilder _clientOutputBuffer = new();
        private readonly StringBuilder _serverOutputBuffer = new();
        
        // Capture context at process start time to ensure output is stored with correct stage
        private string? _clientStartQuestionCode;
        private string? _clientStartStageLabel;
        private string? _serverStartQuestionCode;
        private string? _serverStartStageLabel;

        public ExecutableManager(IRunContext run) { _run = run; }

        public bool IsServerRunning => _server is { HasExited: false };
        public bool IsClientRunning => _client is { HasExited: false };

        public void Init(string? clientPath, string? serverPath)
        {
            _clientPath = clientPath;
            _serverPath = serverPath;
            _client = null; _server = null;
            _clientOutputBuffer.Clear();
            _serverOutputBuffer.Clear();
            _clientStartQuestionCode = null;
            _clientStartStageLabel = null;
            _serverStartQuestionCode = null;
            _serverStartStageLabel = null;
        }

        public void StartServer()
        {
            if (_server is { HasExited: false }) return;
            if (string.IsNullOrWhiteSpace(_serverPath) || !File.Exists(_serverPath))
                throw new FileNotFoundException($"Server executable not found: {_serverPath}");

            // Capture context at start time
            _serverStartQuestionCode = _run.CurrentQuestionCode;
            _serverStartStageLabel = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
            
            _server = Create(_serverPath);
            _server.Start();
            _ = PumpAsync(_server, FileKeywords.FileName_ServerLog, appendServer: true);
        }

        public void StartClient()
        {
            if (_client is { HasExited: false }) return;
            if (string.IsNullOrWhiteSpace(_clientPath) || !File.Exists(_clientPath))
                throw new FileNotFoundException($"Client executable not found: {_clientPath}");

            // Capture context at start time
            _clientStartQuestionCode = _run.CurrentQuestionCode;
            _clientStartStageLabel = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
            
            _client = Create(_clientPath);
            _client.Start();
            _ = PumpAsync(_client, FileKeywords.FileName_ClientLog, appendServer: false);
        }

        public async Task<Process?> StartAsync(string executablePath, string arguments, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return null;

            // Set working directory to the directory containing the executable
            var workingDirectory = Path.GetDirectoryName(executablePath);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            process.Start();
            _ = PumpAsync(process, FileKeywords.FileName_ServerLog, appendServer: true);
            
            return await Task.FromResult(process);
        }

        public Task StopServerAsync() { TryKill(_server); _server = null; return Task.CompletedTask; }
        public Task StopClientAsync() { TryKill(_client); _client = null; return Task.CompletedTask; }
        public Task StopAllAsync() { TryKill(_client); TryKill(_server); _client = null; _server = null; return Task.CompletedTask; }

        public void SendClientInput(string input)
        {
            if (_client == null || _client.HasExited)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {LoggingKeywords.MSG_CLIENT_INPUT_NOT_RUNNING}");
                return;
            }

            try
            {
                // Update the client stage label to current stage before sending input
                // This ensures that the response output is attributed to the current stage (where input was sent)
                // rather than the stage where the client was started
                _clientStartStageLabel = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? _clientStartStageLabel);
                
                // Handle empty or null input by sending just a newline (blank Enter)
                // This allows test cases to send empty input when the value cell is blank
                var inputToSend = input ?? string.Empty;
                _client.StandardInput.WriteLine(inputToSend);
                _client.StandardInput.Flush();
                
                // Log what was actually sent
                var displayInput = string.IsNullOrEmpty(inputToSend) ? "(empty line)" : inputToSend;
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {string.Format(LoggingKeywords.MSG_CLIENT_INPUT_SENT, displayInput)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {string.Format(LoggingKeywords.MSG_CLIENT_INPUT_ERROR, ex.Message)}");
            }
        }
        
        /// <summary>
        /// Waits for the client process to produce output or exit, with stabilization detection.
        /// This method waits until output stabilizes (no new output for a period) to ensure
        /// all console output from multiple Write/WriteLine calls is captured.
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait in seconds (default: 15)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if new output was produced, false if process exited or timed out</returns>
        public async Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default)
        {
            if (_client == null) return false;
            
            // Wait for client to either:
            // 1. Exit (finished processing)
            // 2. Produce output and stabilize (no new output for stabilization period)
            // 3. Timeout
            // 4. Cancellation requested
            
            // Increased stabilization time to 1000ms (1 second) to ensure all console output is captured.
            // This is especially important when applications use multiple Console.Write/WriteLine calls
            // that may be buffered or delayed, preventing premature stage cutoff.
            const int stabilizationMs = 1000; // Wait 1000ms with no new output to consider stable
            const int pollIntervalMs = 50; // Check for new output every 50ms
            
            var startTime = DateTime.UtcNow;
            var initialOutputLength = GetClientOutput().Length;
            var lastOutputLength = initialOutputLength;
            var lastOutputTime = DateTime.UtcNow;
            bool hasReceivedOutput = false;
            
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
            {
                // Check if process exited
                if (_client.HasExited)
                {
                    // Process exited - wait a bit more for any buffered output to flush
                    await Task.Delay(200, ct);
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {LoggingKeywords.MSG_CLIENT_PROCESS_EXITED}");
                    return hasReceivedOutput;
                }
                
                // Check current output length
                var currentOutputLength = GetClientOutput().Length;
                
                // If we received new output, update tracking
                if (currentOutputLength > lastOutputLength)
                {
                    hasReceivedOutput = true;
                    lastOutputLength = currentOutputLength;
                    lastOutputTime = DateTime.UtcNow;
                }
                
                // If we have received output and it has stabilized (no new output for stabilization period)
                if (hasReceivedOutput && (DateTime.UtcNow - lastOutputTime).TotalMilliseconds >= stabilizationMs)
                {
                    var totalOutputReceived = currentOutputLength - initialOutputLength;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {string.Format(LoggingKeywords.MSG_CLIENT_PRODUCED_OUTPUT, totalOutputReceived)} (stabilized)");
                    return true;
                }
                
                // Short delay before checking again
                await Task.Delay(pollIntervalMs, ct);
            }
            
            // Timeout or cancellation
            if (ct.IsCancellationRequested)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {LoggingKeywords.MSG_CLIENT_INPUT_WAIT_CANCELLED}");
            }
            else
            {
                var finalOutputLength = GetClientOutput().Length;
                if (finalOutputLength > initialOutputLength)
                {
                    var totalOutputReceived = finalOutputLength - initialOutputLength;
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {string.Format(LoggingKeywords.MSG_CLIENT_INPUT_WAIT_TIMEOUT, timeoutSeconds)} but received {totalOutputReceived} chars");
                    return true; // We did receive output, even if not fully stabilized
                }
                else
                {
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_CLIENT_INPUT} {string.Format(LoggingKeywords.MSG_CLIENT_INPUT_WAIT_TIMEOUT, timeoutSeconds)}");
                }
            }
            
            return hasReceivedOutput;
        }

        public string GetClientOutput()
        {
            lock (_clientOutputBuffer)
            {
                return _clientOutputBuffer.ToString();
            }
        }

        public string GetServerOutput()
        {
            lock (_serverOutputBuffer)
            {
                return _serverOutputBuffer.ToString();
            }
        }
        
        /// <summary>
        /// Waits for the server process to produce output or exit, with stabilization detection.
        /// Similar to WaitForClientOutputAsync but for server process.
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait in seconds (default: 5)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if new output was produced, false if process exited or timed out</returns>
        public async Task<bool> WaitForServerOutputAsync(int timeoutSeconds = 5, CancellationToken ct = default)
        {
            if (_server == null) return false;
            
            const int stabilizationMs = 1000; // Wait 1000ms with no new output to consider stable
            const int pollIntervalMs = 50; // Check for new output every 50ms
            
            var startTime = DateTime.UtcNow;
            var initialOutputLength = GetServerOutput().Length;
            var lastOutputLength = initialOutputLength;
            var lastOutputTime = DateTime.UtcNow;
            bool hasReceivedOutput = false;
            
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
            {
                // Check if process exited
                if (_server.HasExited)
                {
                    // Process exited - wait a bit more for any buffered output to flush
                    await Task.Delay(200, ct);
                    return hasReceivedOutput;
                }
                
                // Check current output length
                var currentOutputLength = GetServerOutput().Length;
                
                // If we received new output, update tracking
                if (currentOutputLength > lastOutputLength)
                {
                    hasReceivedOutput = true;
                    lastOutputLength = currentOutputLength;
                    lastOutputTime = DateTime.UtcNow;
                }
                
                // If we have received output and it has stabilized (no new output for stabilization period)
                if (hasReceivedOutput && (DateTime.UtcNow - lastOutputTime).TotalMilliseconds >= stabilizationMs)
                {
                    return true;
                }
                
                // Short delay before checking again
                await Task.Delay(pollIntervalMs, ct);
            }
            
            return hasReceivedOutput;
        }

        private static Process Create(string exe)
        {
            // Handle both .exe and .dll files - on non-Windows, .exe won't run directly
            // Also handle if user passes .exe but we need to use .dll on Linux/Mac
            string fileName = exe;
            string arguments = "";
            
            // If it's a .dll or if it's an .exe on a non-Windows platform, use dotnet
            if (exe.EndsWith(FileKeywords.Extension_Dll, StringComparison.OrdinalIgnoreCase) ||
                (exe.EndsWith(FileKeywords.Extension_Exe, StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows()))
            {
                // If .exe is provided on non-Windows, try to find the .dll instead
                if (exe.EndsWith(FileKeywords.Extension_Exe, StringComparison.OrdinalIgnoreCase))
                {
                    var dllPath = Path.ChangeExtension(exe, FileKeywords.Extension_Dll);
                    if (File.Exists(dllPath))
                    {
                        fileName = "dotnet";
                        arguments = dllPath;
                    }
                    else
                    {
                        // No .dll found, try running .exe with dotnet anyway (might fail)
                        fileName = "dotnet";
                        arguments = exe;
                    }
                }
                else
                {
                    fileName = "dotnet";
                    arguments = exe;
                }
            }
            
            // Set working directory to the directory containing the executable
            // This ensures appsettings.json and other config files are found
            var workingDirectory = Path.GetDirectoryName(exe);
            
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };
        }

        private async Task PumpAsync(Process p, string logName, bool appendServer)
        {
            try
            {
                using var sw = new StreamWriter(Path.Combine(AppContext.BaseDirectory, logName), append: true, Encoding.UTF8);
                sw.AutoFlush = true;

                async Task readAsync(StreamReader reader)
                {
                    const int ReadBufferSize = 4096; // Read buffer size in characters
                    const int FlushIntervalMs = 100; // Flush partial lines every 100ms
                    
                    var buffer = appendServer ? _serverOutputBuffer : _clientOutputBuffer;
                    var lineBuffer = new StringBuilder();
                    var charBuffer = new char[ReadBufferSize];
                    var lastFlushTime = DateTime.UtcNow;
                    
                    try
                    {
                        Task<int>? pendingReadTask = null;
                        
                        while (true)
                        {
                            // Start a read task if we don't have one pending
                            if (pendingReadTask == null)
                            {
                                pendingReadTask = reader.ReadAsync(charBuffer, 0, charBuffer.Length);
                            }
                            
                            // Only create delay task if we need to wait for flush interval
                            var delayTask = lineBuffer.Length > 0 
                                ? Task.Delay(FlushIntervalMs) 
                                : Task.Delay(Timeout.Infinite); // Never complete if no partial data
                            var completedTask = await Task.WhenAny(pendingReadTask, delayTask);
                            
                            int charsRead = 0;
                            if (completedTask == pendingReadTask)
                            {
                                charsRead = await pendingReadTask;
                                pendingReadTask = null; // Clear so we start a new read next iteration
                                
                                if (charsRead == 0)
                                {
                                    // End of stream
                                    break;
                                }
                            }
                            
                            // If we read data, process it
                            if (charsRead > 0)
                            {
                                for (int i = 0; i < charsRead; i++)
                                {
                                    var ch = charBuffer[i];
                                    lineBuffer.Append(ch);
                                    
                                    // Flush on newline
                                    if (ch == '\n')
                                    {
                                        var output = lineBuffer.ToString();
                                        lineBuffer.Clear();
                                        lastFlushTime = DateTime.UtcNow;
                                        
                                        await sw.WriteAsync(output);
                                        lock (buffer) { buffer.Append(output); }
                                        
                                        var outputForFile = output.TrimEnd('\r', '\n');
                                        if (outputForFile.Length > 0 || output.EndsWith('\n'))
                                        {
                                            AppendActual(appendServer ? FileKeywords.Folder_Servers : FileKeywords.Folder_Clients, outputForFile);
                                        }
                                    }
                                }
                            }
                            
                            // Check if we should flush partial line based on time
                            if (lineBuffer.Length > 0 && (DateTime.UtcNow - lastFlushTime).TotalMilliseconds >= FlushIntervalMs)
                            {
                                var output = lineBuffer.ToString();
                                lineBuffer.Clear();
                                lastFlushTime = DateTime.UtcNow;
                                
                                await sw.WriteAsync(output);
                                lock (buffer) { buffer.Append(output); }
                                
                                var outputForFile = output.TrimEnd('\r', '\n');
                                if (outputForFile.Length > 0)
                                {
                                    AppendActual(appendServer ? FileKeywords.Folder_Servers : FileKeywords.Folder_Clients, outputForFile);
                                }
                            }
                        }
                        
                        // Flush any remaining content when stream ends
                        if (lineBuffer.Length > 0)
                        {
                            var output = lineBuffer.ToString();
                            await sw.WriteAsync(output);
                            lock (buffer) { buffer.Append(output); }
                            
                            var outputForFile = output.TrimEnd('\r', '\n');
                            if (outputForFile.Length > 0)
                            {
                                AppendActual(appendServer ? FileKeywords.Folder_Servers : FileKeywords.Folder_Clients, outputForFile);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PUMP_ASYNC} {string.Format(LoggingKeywords.MSG_PUMP_ERROR_READING_STREAM, ex.Message)}");
                    }
                }

                await Task.WhenAll(readAsync(p.StandardOutput), readAsync(p.StandardError));
            }
            catch { }
        }

        private void AppendActual(string scope, string line)
        {
            try
            {
                // For stage attribution:
                // - Client output: Use current stage (updated when input is sent)
                // - Server output: Use current stage (so connection messages appear in the stage where connection happens)
                // 
                // The current stage is maintained by the orchestrator and reflects the currently executing step.
                // Output is attributed to the current stage at the time it's read by PumpAsync, which closely
                // matches when it was generated by the process (with minimal buffering delay).
                //
                // Note: Connection initialization can occur at any point depending on the client/server implementation
                // (e.g., at startup, on first input, or anywhere in between). The current stage approach ensures
                // output is attributed to whichever stage is executing when the connection actually occurs.
                string? question;
                string? stage;
                
                if (string.Equals(scope, FileKeywords.Folder_Servers, StringComparison.OrdinalIgnoreCase))
                {
                    // Use current stage for server output
                    // This ensures connection messages are attributed to the stage where they occur,
                    // regardless of when the connection is initialized by the client
                    question = _run.CurrentQuestionCode ?? _serverStartQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                    stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? _serverStartStageLabel ?? "0");
                }
                else
                {
                    // Use current stage for client output (updated when input is sent)
                    question = _run.CurrentQuestionCode ?? _clientStartQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                    stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? _clientStartStageLabel ?? "0");
                }
                
                var payload = line + Environment.NewLine;

                // Only store in memory - no txt file writes
                if (string.Equals(scope, FileKeywords.Folder_Servers, StringComparison.OrdinalIgnoreCase))
                    _run.AppendServerOutput(question, stage, payload);
                else
                    _run.AppendClientOutput(question, stage, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] AppendActual Exception: {ex.Message}");
            }
        }

        private static void TryKill(Process? p)
        {
            if (p == null) return;
            
            try 
            { 
                if (p.HasExited) return;
                
                var processId = p.Id;
                
                // Try graceful kill first with entire process tree
                p.Kill(entireProcessTree: true);
                
                // Wait up to 1 second for process to exit
                if (!p.WaitForExit(1000))
                {
                    // If still running after 1 second, use TaskKill as fallback
                    Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROCESS} {string.Format(LoggingKeywords.MSG_PROCESS_TASKKILL_USED, processId)}");
                    TryTaskKill(processId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROCESS} {string.Format(LoggingKeywords.MSG_PROCESS_KILL_ERROR, ex.Message)}");
            }
            finally 
            { 
                try { p?.Dispose(); } 
                catch { } 
            }
        }
        
        /// <summary>
        /// Forcefully terminates a process using platform-specific commands.
        /// Uses TaskKill on Windows, kill -9 on Unix-like systems.
        /// </summary>
        /// <param name="processId">The process ID to terminate</param>
        private static void TryTaskKill(int processId)
        {
            try
            {
                // Use TaskKill on Windows or kill on Unix
                if (OperatingSystem.IsWindows())
                {
                    var taskKill = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /T /PID {processId}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    taskKill.Start();
                    taskKill.WaitForExit(2000); // Wait up to 2 seconds
                }
                else
                {
                    // On Unix, use kill -9 (SIGKILL)
                    var kill = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "kill",
                            Arguments = $"-9 {processId}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    kill.Start();
                    kill.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROCESS} {string.Format(LoggingKeywords.MSG_PROCESS_TASKKILL_FAILED, ex.Message)}");
            }
        }
    }
}
