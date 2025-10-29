namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.Diagnostics;
using System.Text;

public sealed class ExecutableManager : IExecutableManager
{
    private Process? _client;
    private Process? _server;
    private string? _clientPath;
    private string? _serverPath;

    public bool IsServerRunning => _server is { HasExited: false };
    public bool IsClientRunning => _client is { HasExited: false };

    public void Init(string? clientPath, string? serverPath)
    {
        _clientPath = clientPath;
        _serverPath = serverPath;
        _client = null;
        _server = null;
    }

    public void StartServer()
    {
        if (_server is { HasExited: false }) return;
        if (string.IsNullOrWhiteSpace(_serverPath) || !File.Exists(_serverPath))
            throw new FileNotFoundException($"Server executable not found: {_serverPath}");

        _server = Create(_serverPath);
        _server.Start();
        _ = PumpAsync(_server, "server.log");
    }

    public void StartClient()
    {
        if (_client is { HasExited: false }) return;
        if (string.IsNullOrWhiteSpace(_clientPath) || !File.Exists(_clientPath))
            throw new FileNotFoundException($"Client executable not found: {_clientPath}");

        _client = Create(_clientPath);
        _client.Start();
        _ = PumpAsync(_client, "client.log");
    }

    public async Task StopServerAsync() { TryKill(_server); _server = null; await Task.CompletedTask; }
    public async Task StopClientAsync() { TryKill(_client); _client = null; await Task.CompletedTask; }
    public async Task StopAllAsync() { TryKill(_client); TryKill(_server); _client=null; _server=null; await Task.CompletedTask; }

    private static Process Create(string exe) => new Process
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

    private static async Task PumpAsync(Process p, string logName)
    {
        try
        {
            using var sw = new StreamWriter(Path.Combine(AppContext.BaseDirectory, logName), append: true, Encoding.UTF8);
            sw.AutoFlush = true;
            async Task readAsync(StreamReader reader)
            {
                var buffer = new char[1024];
                int read;
                while (!reader.EndOfStream && (read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    await sw.WriteAsync(buffer, 0, read);
            }
            await Task.WhenAll(readAsync(p.StandardOutput), readAsync(p.StandardError));
        }
        catch { }
    }

    private static void TryKill(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
        finally { try { p?.Dispose(); } catch { } }
    }
}
