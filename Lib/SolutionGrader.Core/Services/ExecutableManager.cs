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
        }

        public void StartServer()
        {
            if (_server is { HasExited: false }) return;
            if (string.IsNullOrWhiteSpace(_serverPath) || !File.Exists(_serverPath))
                throw new FileNotFoundException($"Server executable not found: {_serverPath}");

            _server = Create(_serverPath);
            _server.Start();
            _ = PumpAsync(_server, FileKeywords.FileName_ServerLog, appendServer: true);
        }

        public void StartClient()
        {
            if (_client is { HasExited: false }) return;
            if (string.IsNullOrWhiteSpace(_clientPath) || !File.Exists(_clientPath))
                throw new FileNotFoundException($"Client executable not found: {_clientPath}");

            _client = Create(_clientPath);
            _client.Start();
            _ = PumpAsync(_client, FileKeywords.FileName_ClientLog, appendServer: false);
        }

        public async Task<Process?> StartAsync(string executablePath, string arguments, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return null;

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
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
                Console.WriteLine($"[ClientInput] Cannot send input - client not running");
                return;
            }

            try
            {
                _client.StandardInput.WriteLine(input);
                _client.StandardInput.Flush();
                Console.WriteLine($"[ClientInput] Sent: {input}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClientInput] Error sending input: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Waits for the client process to produce output or exit.
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait in seconds (default: 15)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if new output was produced, false if process exited or timed out</returns>
        public async Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default)
        {
            if (_client == null) return false;
            
            // Wait for client to either:
            // 1. Exit (finished processing)
            // 2. Produce output (response received)
            // 3. Timeout
            // 4. Cancellation requested
            
            var startTime = DateTime.UtcNow;
            var initialOutputLength = GetClientOutput().Length;
            
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
            {
                // Check if process exited
                if (_client.HasExited)
                {
                    Console.WriteLine($"[ClientInput] Client process exited");
                    return false;
                }
                
                // Check if new output was produced
                var currentOutputLength = GetClientOutput().Length;
                if (currentOutputLength > initialOutputLength)
                {
                    Console.WriteLine($"[ClientInput] Client produced output ({currentOutputLength - initialOutputLength} bytes)");
                    // Give a little more time for buffered output
                    await Task.Delay(100, ct);
                    return true;
                }
                
                // Short delay before checking again
                await Task.Delay(100, ct);
            }
            
            if (ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ClientInput] Wait cancelled");
            }
            else
            {
                Console.WriteLine($"[ClientInput] Wait timed out after {timeoutSeconds}s");
            }
            
            return false;
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

        private static Process Create(string exe)
        {
            // Handle both .exe and .dll files - on non-Windows, .exe won't run directly
            // Also handle if user passes .exe but we need to use .dll on Linux/Mac
            string fileName = exe;
            string arguments = "";
            
            // If it's a .dll or if it's an .exe on a non-Windows platform, use dotnet
            if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows()))
            {
                // If .exe is provided on non-Windows, try to find the .dll instead
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dllPath = Path.ChangeExtension(exe, ".dll");
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
            
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
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
                    var buffer = appendServer ? _serverOutputBuffer : _clientOutputBuffer;
                    var lineBuffer = new StringBuilder();
                    var charBuffer = new char[4096]; // Read in chunks for efficiency
                    var lastFlushTime = DateTime.UtcNow;
                    const int FlushIntervalMs = 100; // Flush partial lines every 100ms
                    
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
                            
                            // Wait for either the read to complete or the flush interval
                            var delayTask = Task.Delay(FlushIntervalMs);
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
                        Console.WriteLine($"[PumpAsync] Error reading stream: {ex.Message}");
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
                var question = _run.CurrentQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                var stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
                var payload = line + Environment.NewLine;

                if (string.Equals(scope, FileKeywords.Folder_Servers, StringComparison.OrdinalIgnoreCase))
                    _run.AppendServerOutput(question, stage, payload);
                else
                    _run.AppendClientOutput(question, stage, payload);
                var path = Path.Combine(_run.ResultRoot, FileKeywords.Folder_Actual, scope, question, string.Format(FileKeywords.Pattern_StageOutput, stage));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
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
                    Console.WriteLine($"[Process] Process {processId} did not exit gracefully, using TaskKill...");
                    TryTaskKill(processId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Process] Error killing process: {ex.Message}");
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
                Console.WriteLine($"[Process] TaskKill/kill failed: {ex.Message}");
            }
        }
    }
}
