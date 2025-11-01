using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
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

        private readonly IRunContext _run;

        public MiddlewareProxyService(IRunContext run) { _run = run; }

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

        public Task StopAsync(CancellationToken ct = default) => StopCoreAsync();

        public async Task<bool> ProxyAsync(IRunContext context, CancellationToken ct = default)
        {
            try
            {
                // Start the proxy if not already running
                await StartAsync(_httpMode, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

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

            if (taskToWait != null) { try { await Task.WhenAny(taskToWait, Task.Delay(2000)); } catch { } }
            try { _cts?.Dispose(); _cts = null; } catch { }
        }

        private void StartHttp(CancellationToken token)
        {
            try
            {
                _http = new HttpListener();
                _http.Prefixes.Add($"http://localhost:{ProxyPort}/");
                _http.Start();
                Console.WriteLine($"[Proxy] HTTP proxy listening on http://localhost:{ProxyPort}/ -> http://localhost:{RealServerPort}/");
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

                // Build complete URL with query string using localhost (not 127.0.0.1) to match Host header
                var urlBuilder = new UriBuilder("http", "localhost", RealServerPort, req.Url?.AbsolutePath ?? "/", req.Url?.Query ?? "");
                var targetUrl = urlBuilder.ToString();

                var forward = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUrl);
                
                // Set content with proper media type if body exists
                if (!string.IsNullOrEmpty(body) || req.ContentLength64 > 0)
                {
                    forward.Content = new StringContent(body, req.ContentEncoding);
                    if (req.ContentType != null)
                    {
                        forward.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(req.ContentType);
                    }
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await client.SendAsync(forward);
                var bytes = await response.Content.ReadAsByteArrayAsync();

                // Capture server traffic to memory (no txt files) with metadata
                TryAppendServerActual(body, bytes, req.HttpMethod, (int)response.StatusCode);

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

        private void TryAppendServerActual(string requestBody, byte[] responseBytes, string httpMethod = "GET", int statusCode = 200)
        {
            try
            {
                var question = _run.CurrentQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                var stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");

                // Store HTTP request and response separately in memory to avoid overwriting console output
                _run.SetServerRequest(question, stage, requestBody ?? "");
                
                string respText;
                int normalizedByteSize;
                try
                {
                    respText = Encoding.UTF8.GetString(responseBytes);
                    
                    // Calculate byte size from normalized content to avoid formatting differences
                    // For JSON, normalize and recalculate the byte size
                    if (respText.TrimStart().StartsWith("{") || respText.TrimStart().StartsWith("["))
                    {
                        try
                        {
                            // Parse and serialize JSON to canonical form (no whitespace)
                            var jsonDoc = JsonDocument.Parse(respText);
                            var normalizedJson = JsonSerializer.Serialize(jsonDoc, new JsonSerializerOptions
                            {
                                WriteIndented = false,
                                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                            });
                            normalizedByteSize = Encoding.UTF8.GetByteCount(normalizedJson);
                        }
                        catch
                        {
                            // If JSON parsing fails, use raw byte size
                            normalizedByteSize = responseBytes?.Length ?? 0;
                        }
                    }
                    else
                    {
                        // For non-JSON content, use raw byte size
                        normalizedByteSize = responseBytes?.Length ?? 0;
                    }
                }
                catch
                {
                    respText = $"<binary {responseBytes?.Length ?? 0} bytes>";
                    normalizedByteSize = responseBytes?.Length ?? 0;
                }
                _run.SetServerResponse(question, stage, respText);
                
                // Store HTTP metadata (method, status code, normalized byte size)
                // Using normalized byte size ensures consistent comparison regardless of formatting
                _run.SetHttpMetadata(question, stage, httpMethod, statusCode, normalizedByteSize);

                // Note: HTTP traffic is now stored in memory only (servers-req and servers-resp namespaces)
                // This data is available for comparison steps and included in Excel output when tests fail
            }
            catch { }
        }
    }
}
