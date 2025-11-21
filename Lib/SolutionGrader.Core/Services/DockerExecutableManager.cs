using System;
using System.Diagnostics;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionGrader.Core.Services
{
    public sealed class DockerExecutableManager : IExecutableManager
    {
        private readonly IRunContext _run;
        private readonly StringBuilder _clientOutputBuffer = new();
        private readonly StringBuilder _serverOutputBuffer = new();
        private string? _codeContainer; // single container
        private string? _clientLogPath;
        private string? _serverLogPath;
        private CancellationTokenSource? _clientLogCts;
        private CancellationTokenSource? _serverLogCts;
        private Task? _clientLogTask;
        private Task? _serverLogTask;

        // Unified docker logs (parse prefixes from docker logs -f)
        private bool _useUnifiedDockerLogs = false;
        private CancellationTokenSource? _dockerLogsCts;
        private Task? _dockerLogsTask;
        private static readonly string ClientPrefix = "[CLIENT]";
        private static readonly string ServerPrefix = "[SERVER]";

        // Track if container was validated
        private bool _containerExists = false;

        public bool IsServerRunning => _containerExists && ((_useUnifiedDockerLogs && _dockerLogsTask != null && !_dockerLogsTask.IsCompleted) || (_serverLogTask != null && !_serverLogTask.IsCompleted));
        public bool IsClientRunning => _containerExists && ((_useUnifiedDockerLogs && _dockerLogsTask != null && !_dockerLogsTask.IsCompleted) || (_clientLogTask != null && !_clientLogTask.IsCompleted));

        public DockerExecutableManager(IRunContext run) => _run = run;

        public void Init(string? clientPath, string? serverPath)
        {
            _codeContainer = clientPath; // container name is passed in clientPath
            StopAllAsync().GetAwaiter().GetResult();
            _clientOutputBuffer.Clear();
            _serverOutputBuffer.Clear();
            ValidateContainer();
        }

        private void ValidateContainer()
        {
            _containerExists = false;
            if (string.IsNullOrWhiteSpace(_codeContainer)) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"ps -q -f name={_codeContainer}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                _containerExists = !string.IsNullOrWhiteSpace(output.Trim());
                if (!_containerExists)
                {
                    Console.WriteLine($"[Docker] Container '{_codeContainer}' not found. Ensure EnvironmentManager created and started it.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Container validation error: {ex.Message}");
            }
        }

        public void ConfigureDockerLogs(string? clientLogPath, string? serverLogPath)
        {
            _clientLogPath = clientLogPath;
            _serverLogPath = serverLogPath;
            _useUnifiedDockerLogs = (string.IsNullOrWhiteSpace(_clientLogPath) && string.IsNullOrWhiteSpace(_serverLogPath))
                                    || string.Equals(_clientLogPath, "docker-logs", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(_serverLogPath, "docker-logs", StringComparison.OrdinalIgnoreCase);
        }

        public void RefreshContainer()
        {
            ValidateContainer();
        }

        public void StartServer()
        {
            if (!_containerExists) { ValidateContainer(); }
            if (!_containerExists) { Console.WriteLine("[Docker] StartServer skipped: container missing"); return; }
            if (_useUnifiedDockerLogs) { StartUnifiedDockerLogs(); return; }
            if (string.IsNullOrWhiteSpace(_serverLogPath)) { Console.WriteLine("[Docker] StartServer skipped: server log path empty"); return; }
            if (_serverLogTask != null && !_serverLogTask.IsCompleted) return;
            _serverLogCts = new CancellationTokenSource();
            _serverLogTask = Task.Run(() => TailLogFile(_codeContainer!, _serverLogPath!, _serverOutputBuffer, true, _serverLogCts.Token));
        }

        public void StartClient()
        {
            if (!_containerExists) { ValidateContainer(); }
            if (!_containerExists) { Console.WriteLine("[Docker] StartClient skipped: container missing"); return; }
            if (_useUnifiedDockerLogs) { StartUnifiedDockerLogs(); return; }
            if (string.IsNullOrWhiteSpace(_clientLogPath)) { Console.WriteLine("[Docker] StartClient skipped: client log path empty"); return; }
            if (_clientLogTask != null && !_clientLogTask.IsCompleted) return;
            _clientLogCts = new CancellationTokenSource();
            _clientLogTask = Task.Run(() => TailLogFile(_codeContainer!, _clientLogPath!, _clientOutputBuffer, false, _clientLogCts.Token));
        }

        public Task<Process?> StartAsync(string executablePath, string arguments, CancellationToken ct) => Task.FromResult<Process?>(null);
        public Task StopServerAsync() { try { _serverLogCts?.Cancel(); } catch { } try { _dockerLogsCts?.Cancel(); } catch { } return Task.CompletedTask; }
        public Task StopClientAsync() { try { _clientLogCts?.Cancel(); } catch { } try { _dockerLogsCts?.Cancel(); } catch { } return Task.CompletedTask; }
        public Task StopAllAsync() { try { _clientLogCts?.Cancel(); } catch { } try { _serverLogCts?.Cancel(); } catch { } try { _dockerLogsCts?.Cancel(); } catch { } return Task.CompletedTask; }

        public void SendClientInput(string input) { Console.WriteLine("[Docker] SendClientInput not implemented for single container mode."); }

        public async Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default)
        {
            var initial = GetClientOutput().Length;
            var start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - start).TotalSeconds < timeoutSeconds)
            {
                await Task.Delay(200, ct);
                if (GetClientOutput().Length > initial) return true;
            }
            return GetClientOutput().Length > initial;
        }

        public async Task<bool> WaitForServerOutputAsync(int timeoutSeconds = 5, CancellationToken ct = default)
        {
            var initial = GetServerOutput().Length;
            var start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - start).TotalSeconds < timeoutSeconds)
            {
                await Task.Delay(200, ct);
                if (GetServerOutput().Length > initial) return true;
            }
            return GetServerOutput().Length > initial;
        }

        public string GetClientOutput() { lock (_clientOutputBuffer) return _clientOutputBuffer.ToString(); }
        public string GetServerOutput() { lock (_serverOutputBuffer) return _serverOutputBuffer.ToString(); }

        private void StartUnifiedDockerLogs()
        {
            if (_dockerLogsTask != null && !_dockerLogsTask.IsCompleted) return;
            _dockerLogsCts = new CancellationTokenSource();
            _dockerLogsTask = Task.Run(() => StreamDockerLogs(_codeContainer!, _dockerLogsCts.Token));
        }

        private void StreamDockerLogs(string container, CancellationToken token)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"logs -f {container}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Console.WriteLine("[Docker] Failed to start docker logs process.");
                    return;
                }

                void route(string? line)
                {
                    if (line == null) return;
                    if (line.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var content = line.Substring(ClientPrefix.Length).TrimStart();
                        lock (_clientOutputBuffer) _clientOutputBuffer.AppendLine(content);
                        AppendActual(FileKeywords.Folder_Clients, content);
                    }
                    else if (line.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var content = line.Substring(ServerPrefix.Length).TrimStart();
                        lock (_serverOutputBuffer) _serverOutputBuffer.AppendLine(content);
                        AppendActual(FileKeywords.Folder_Servers, content);
                    }
                    else
                    {
                        // No prefix: route to BOTH client and server buffers so client comparisons still see output
                        lock (_serverOutputBuffer) _serverOutputBuffer.AppendLine(line);
                        lock (_clientOutputBuffer) _clientOutputBuffer.AppendLine(line);
                        AppendActual(FileKeywords.Folder_Servers, line);
                        AppendActual(FileKeywords.Folder_Clients, line);
                    }
                }

                while (!proc.HasExited && !token.IsCancellationRequested)
                {
                    var line = proc.StandardOutput.ReadLine();
                    if (line != null) route(line);
                    var err = proc.StandardError.ReadLine();
                    if (err != null) route(err);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] logs error {container} => {ex.Message}");
            }
        }

        private void TailLogFile(string container, string path, StringBuilder buffer, bool isServer, CancellationToken token)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {container} sh -c \"test -f '{Escape(path)}' && tail -F '{Escape(path)}'\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Console.WriteLine("[Docker] Failed to exec tail in container.");
                    return;
                }

                void handle(string? line)
                {
                    if (line == null) return;
                    lock (buffer) buffer.AppendLine(line);
                    AppendActual(isServer ? FileKeywords.Folder_Servers : FileKeywords.Folder_Clients, line);
                }

                while (!proc.HasExited && !token.IsCancellationRequested)
                {
                    var line = proc.StandardOutput.ReadLine();
                    if (line != null) handle(line);
                    var err = proc.StandardError.ReadLine();
                    if (err != null) handle(err);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Docker] Tail error {container}:{path} => {ex.Message}");
            }
        }

        private static string Escape(string s) => s.Replace("'", "'\\''");

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
            }
            catch { }
        }
    }
}
