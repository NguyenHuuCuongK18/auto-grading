namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System.Diagnostics;
using System.Net.Http;

public sealed class Executor : IExecutor
{
    private readonly HttpClient _http = new();
    private readonly IExecutableManager _proc;
    private readonly IMiddlewareService _mw;
    private readonly IDataComparisonService _cmp;

    private const int ServerReadyTimeoutSeconds = 5;
    private const int ServerReadyPollIntervalMs = 100;
    private const int RealServerPort = 5001;

    public Executor(IExecutableManager proc, IMiddlewareService mw, IDataComparisonService cmp)
    {
        _proc = proc;
        _mw = mw;
        _cmp = cmp;
    }

    public async Task<(bool ok, string message)> ExecuteAsync(Step step, ExecuteSuiteArgs args, CancellationToken ct)
    {
        var useHttp = !string.Equals(args.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);

        // hard ceiling so sockets/connect/read don't hang forever
        try { _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, args.StageTimeoutSeconds)); } catch { }

        switch (step.Action)
        {
            case var a when a == ActionKeywords.ClientStart:
                if (!_proc.IsServerRunning) _proc.StartServer();
                await WaitForServerReadyAsync(ct);
                await _mw.StartAsync(useHttp, ct);
                _proc.StartClient();
                return (true, "Client started (with server & middleware)");

            case var a when a == ActionKeywords.ServerStart:
                _proc.StartServer();
                await WaitForServerReadyAsync(ct);
                await _mw.StartAsync(useHttp, ct);
                return (true, "Server started (middleware ensured)");

            case var a when a == ActionKeywords.ClientClose:
                await _proc.StopClientAsync();
                await _mw.StopAsync();
                return (true, "Client stopped (middleware stopped)");

            case var a when a == ActionKeywords.ServerClose:
                await _proc.StopServerAsync();
                await _mw.StopAsync();
                return (true, "Server stopped (middleware stopped)");

            case var a when a == ActionKeywords.KillAll:
                await _proc.StopAllAsync();
                await _mw.StopAsync();
                return (true, "All processes + middleware stopped");

            case var a when a == ActionKeywords.RunClient:
                if (!_proc.IsServerRunning) _proc.StartServer();
                await WaitForServerReadyAsync(ct);
                await _mw.StartAsync(useHttp, ct);
                _proc.StartClient();
                return (true, "RunClient OK");

            case var a when a == ActionKeywords.RunServer:
                _proc.StartServer();
                await WaitForServerReadyAsync(ct);
                await _mw.StartAsync(useHttp, ct);
                return (true, "RunServer OK");

            case var a when a == ActionKeywords.Wait:
                var ms = int.TryParse(step.Value ?? "1000", out var v) ? v : 1000;
                await Task.Delay(ms, ct);
                return (true, $"Waited {ms}ms");

            case var a when a == ActionKeywords.HttpRequest:
                var parts = (step.Value ?? "").Split('|', 3, StringSplitOptions.TrimEntries);
                if (parts.Length < 2) return (false, "HTTP_REQUEST requires METHOD|URL");
                using (var req = new HttpRequestMessage(new HttpMethod(parts[0]), parts[1]))
                {
                    var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode) return (false, $"HTTP {resp.StatusCode}");
                    if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) && !body.Contains(parts[2], StringComparison.OrdinalIgnoreCase))
                        return (false, "Expected text not found");
                    return (true, "HTTP ok");
                }

            case var a when a == ActionKeywords.AssertText:
                if (string.IsNullOrWhiteSpace(step.Target) || string.IsNullOrWhiteSpace(step.Value))
                    return (true, "Ignored: expected missing");
                if (!File.Exists(step.Target)) return (false, $"File not found: {step.Target}");
                var txt = await File.ReadAllTextAsync(step.Target, ct);
                txt = txt.Replace("\r", "").Replace("\n", "").Trim();
                var val = (step.Value ?? "").Replace("\r", "").Replace("\n", "").Trim();
                return txt.Contains(val, StringComparison.OrdinalIgnoreCase)
                    ? (true, "Text ok") : (false, "Text mismatch");

            case var a when a == ActionKeywords.AssertFileExists:
                if (string.IsNullOrWhiteSpace(step.Target))
                    return (true, "Ignored: expected missing");
                return File.Exists(step.Target) ? (true, "File exists") : (false, $"File not found: {step.Target}");

            case var a when a == ActionKeywords.CaptureFile:
                if (string.IsNullOrWhiteSpace(step.Target) || string.IsNullOrWhiteSpace(step.Value))
                    return (true, "Ignored: expected missing");
                Directory.CreateDirectory(Path.GetDirectoryName(step.Value)!);
                File.Copy(step.Target, step.Value, overwrite: true);
                return (true, "Captured file");

            case var a when a == ActionKeywords.CompareFile:
                return _cmp.CompareFile(step.Target, step.Value);

            case var a when a == ActionKeywords.CompareText:
                return _cmp.CompareText(step.Target, step.Value);

            case var a when a == ActionKeywords.CompareJson:
                return _cmp.CompareJson(step.Target, step.Value);

            case var a when a == ActionKeywords.CompareCsv:
                return _cmp.CompareCsv(step.Target, step.Value);

            default:
                return (false, $"Unsupported action: {step.Action}");
        }
    }

    private async Task WaitForServerReadyAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(ServerReadyTimeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            
            // Check if process is running
            if (!_proc.IsServerRunning)
            {
                await Task.Delay(ServerReadyPollIntervalMs, ct);
                continue;
            }
            
            // Check if server is actually listening on the port by attempting a connection
            if (await IsPortListeningAsync(RealServerPort, ct))
            {
                return;
            }
            
            await Task.Delay(ServerReadyPollIntervalMs, ct);
        }
        Console.WriteLine($"[WARNING] Server not fully initialized after {ServerReadyTimeoutSeconds}s wait. Continuing anyway.");
    }

    private static async Task<bool> IsPortListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(200); // Quick check with 200ms timeout
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
