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

        public ExecutableManager(IRunContext run) { _run = run; }

        public bool IsServerRunning => _server is { HasExited: false };
        public bool IsClientRunning => _client is { HasExited: false };

        public void Init(string? clientPath, string? serverPath)
        {
            _clientPath = clientPath;
            _serverPath = serverPath;
            _client = null; _server = null;
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

        private static Process Create(string exe) => new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
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

        private async Task PumpAsync(Process p, string logName, bool appendServer)
        {
            try
            {
                using var sw = new StreamWriter(Path.Combine(AppContext.BaseDirectory, logName), append: true, Encoding.UTF8);
                sw.AutoFlush = true;

                async Task readAsync(StreamReader reader)
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        await sw.WriteLineAsync(line);
                        AppendActual(appendServer ? FileKeywords.Folder_Servers : FileKeywords.Folder_Clients, line);
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
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
            catch { }
            finally { try { p?.Dispose(); } catch { } }
        }
    }
}
