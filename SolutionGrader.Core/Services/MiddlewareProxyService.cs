namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http;

public sealed class MiddlewareProxyService : IMiddlewareService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _httpMode;
    private HttpListener? _http;
    private TcpListener? _tcp;
    private Task? _listenTask;

    private const int ProxyPort = 5000;
    private const int RealServerPort = 5001;

    public async Task StartAsync(bool useHttp, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_running && _httpMode == useHttp) return;
            if (_running) { _ = StopCoreAsync(); _running = false; }

            _cts = new CancellationTokenSource();
            _httpMode = useHttp;
            _running = true;

            if (_httpMode) StartHttp(_cts.Token);
            else StartTcp(_cts.Token);
        }
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default) => await StopCoreAsync();

    private async Task StopCoreAsync()
    {
        Task? taskToWait = null;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            taskToWait = _listenTask;
        }

        try { _cts?.Cancel(); } catch { }
        try { if (_http != null && _http.IsListening) { _http.Stop(); _http.Close(); } _http = null; } catch { }
        try { _tcp?.Stop(); _tcp = null; } catch { }

        // Wait for the listen task to complete with a timeout
        if (taskToWait != null)
        {
            try
            {
                await Task.WhenAny(taskToWait, Task.Delay(2000));
            }
            catch { }
        }

        try { _cts?.Dispose(); _cts = null; } catch { }
    }

    private void StartHttp(CancellationToken token)
    {
        try
        {
            _http = new HttpListener();
            // Use 127.0.0.1 for fewer URLACL surprises than "localhost" on Windows
            _http.Prefixes.Add($"http://127.0.0.1:{ProxyPort}/");
            _http.Start();
            Console.WriteLine($"[Proxy] HTTP proxy listening on http://127.0.0.1:{ProxyPort}/ -> http://127.0.0.1:{RealServerPort}/");
            _listenTask = Task.Run(() => ListenHttpAsync(token), token);
        }
        catch (Exception ex) { Console.WriteLine($"[HTTP Proxy ERR] {ex.Message}"); }
    }

    private async Task ListenHttpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try { context = await _http!.GetContextAsync(); }
            catch { break; }
            if (context != null) _ = Task.Run(() => HandleHttpAsync(context), token);
        }
    }

    private async Task HandleHttpAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string body; using (var reader = new StreamReader(req.InputStream, req.ContentEncoding)) body = reader.ReadToEnd();

            var forward = new HttpRequestMessage(new HttpMethod(req.HttpMethod), $"http://127.0.0.1:{RealServerPort}{req.Url?.AbsolutePath}")
            {
                Content = new StringContent(body, req.ContentEncoding)
            };
            if (req.ContentType != null) forward.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(req.ContentType);

            using var client = new HttpClient();
            var response = await client.SendAsync(forward);
            var bytes = await response.Content.ReadAsByteArrayAsync();

            var resp = ctx.Response;
            resp.StatusCode = (int)response.StatusCode;
            resp.ContentType = response.Content.Headers.ContentType?.ToString();
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            resp.Close();
        }
        catch (Exception ex)
        {
            try
            {
                var resp = ctx.Response;
                resp.StatusCode = 502;
                var msg = Encoding.UTF8.GetBytes(ex.Message);
                resp.ContentLength64 = msg.Length;
                await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                resp.Close();
            }
            catch { }
        }
    }

    private void StartTcp(CancellationToken token)
    {
        try
        {
            _tcp = new TcpListener(IPAddress.Loopback, ProxyPort);
            _tcp.Start();
            Console.WriteLine($"[Proxy] TCP proxy listening on 127.0.0.1:{ProxyPort} -> 127.0.0.1:{RealServerPort}");
            _listenTask = Task.Run(() => ListenTcpAsync(token), token);
        }
        catch (Exception ex) { Console.WriteLine($"[TCP Proxy ERR] {ex.Message}"); }
    }

    private async Task ListenTcpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try { client = await _tcp!.AcceptTcpClientAsync(token); }
            catch { break; }
            if (client != null) _ = Task.Run(() => HandleTcpAsync(client, token), token);
        }
    }

    private static async Task HandleTcpAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            using (var server = new TcpClient())
            {
                await server.ConnectAsync(IPAddress.Loopback, RealServerPort, token);
                using var cs = client.GetStream();
                using var ss = server.GetStream();
                var c2s = RelayAsync(cs, ss, token);
                var s2c = RelayAsync(ss, cs, token);
                await Task.WhenAny(c2s, s2c);
            }
        }
        catch { }
    }

    private static async Task RelayAsync(NetworkStream from, NetworkStream to, CancellationToken token)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await from.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            await to.WriteAsync(buffer, 0, read, token);
    }
}
