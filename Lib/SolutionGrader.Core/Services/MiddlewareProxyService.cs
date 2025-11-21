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

        private int _proxyPort = 5000;
        private int _realServerPort = 5001;

        // Host binding for middleware (use * to expose to containers)
        private string _listenHost = "*"; // HttpListener: use * for all (Linux) instead of localhost
        // Host the real server is reachable at FROM the middleware (host mapped port)
        private string _serverHost = "localhost";

        private readonly IRunContext _run;

        public MiddlewareProxyService(IRunContext run) { _run = run; }

        public void ConfigurePorts(int proxyPort, int serverPort)
        {
            lock (_gate)
            {
                if (_running)
                {
                    throw new InvalidOperationException("Cannot configure ports while the middleware is running. Stop the service first.");
                }
                _proxyPort = proxyPort;
                _realServerPort = serverPort;
            }
        }

        // Allow external configuration of listen and server hosts (e.g. host.docker.internal)
        public void ConfigureNetwork(string? listenHost, string? serverHost)
        {
            lock (_gate)
            {
                if (_running)
                {
                    throw new InvalidOperationException("Cannot configure network while the middleware is running. Stop the service first.");
                }
                if (!string.IsNullOrWhiteSpace(listenHost)) _listenHost = NormalizeListenHost(listenHost!);
                if (!string.IsNullOrWhiteSpace(serverHost)) _serverHost = serverHost!;
            }
        }

        private static string NormalizeListenHost(string h)
        {
            // Accept *, +, 0.0.0.0 as wildcard
            if (h == "+" || h == "0.0.0.0") return "*";
            return h;
        }

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
                await StartAsync(_httpMode, ct);
                return true;
            }
            catch { return false; }
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
                var prefix = $"http://{_listenHost}:{_proxyPort}/"; // * works on Linux for all interfaces
                _http.Prefixes.Add(prefix);
                _http.Start();
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROXY} {string.Format(LoggingKeywords.MSG_PROXY_HTTP_LISTENING, _proxyPort, _realServerPort)} (bind={_listenHost} forward={_serverHost})");
                _listenTask = Task.Run(() => ListenHttpAsync(token), token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_HTTP_PROXY_ERR} {string.Format(LoggingKeywords.MSG_PROXY_HTTP_ERROR, ex.Message)}");
            }
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

                // Forward to mapped host server port (host side)
                var urlBuilder = new UriBuilder(AppsettingKeywords.PROTOCOL_HTTP, _serverHost, _realServerPort, req.Url?.AbsolutePath ?? "/", req.Url?.Query ?? "");
                var targetUrl = urlBuilder.ToString();

                var forward = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUrl);

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
                var ip = _listenHost == "*" ? IPAddress.Any : (_listenHost == "localhost" ? IPAddress.Loopback : IPAddress.Parse(_listenHost));
                _tcp = new TcpListener(ip, _proxyPort);
                _tcp.Start();
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROXY} {string.Format(LoggingKeywords.MSG_PROXY_TCP_LISTENING, _proxyPort, _realServerPort)} (bind={_listenHost} forward={_serverHost})");
                _listenTask = Task.Run(() => ListenTcpAsync(token), token);
            }
            catch (Exception ex) { Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_TCP_PROXY_ERR} {string.Format(LoggingKeywords.MSG_PROXY_TCP_ERROR, ex.Message)}"); }
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

        private async Task HandleTcpAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (var server = new TcpClient())
                {
                    // Forward to host mapped server port
                    await server.ConnectAsync(_serverHost, _realServerPort, token);
                    using var cs = client.GetStream();
                    using var ss = server.GetStream();

                    var requestCapture = new List<byte>();
                    var responseCapture = new List<byte>();

                    var c2sTask = Task.Run(async () =>
                    {
                        var buffer = new byte[8192];
                        try
                        {
                            while (true)
                            {
                                if (cs.DataAvailable)
                                {
                                    int read = await cs.ReadAsync(buffer, 0, buffer.Length, token);
                                    if (read > 0)
                                    {
                                        lock (requestCapture) requestCapture.AddRange(buffer.Take(read));
                                        await ss.WriteAsync(buffer, 0, read, token);
                                        await ss.FlushAsync(token);
                                    }
                                    else break;
                                }
                                else
                                {
                                    await Task.Delay(10, token);
                                    if (!cs.DataAvailable) break;
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Console.WriteLine($"[TCP Relay c2s] Error: {ex.Message}"); }
                    }, token);

                    var s2cTask = Task.Run(async () =>
                    {
                        var buffer = new byte[8192];
                        try
                        {
                            var waited = 0;
                            while (!ss.DataAvailable && waited < 2000)
                            {
                                await Task.Delay(50, token); waited += 50;
                            }
                            while (true)
                            {
                                if (ss.DataAvailable)
                                {
                                    int read = await ss.ReadAsync(buffer, 0, buffer.Length, token);
                                    if (read > 0)
                                    {
                                        lock (responseCapture) responseCapture.AddRange(buffer.Take(read));
                                        await cs.WriteAsync(buffer, 0, read, token);
                                        await cs.FlushAsync(token);
                                    }
                                    else break;
                                }
                                else
                                {
                                    await Task.Delay(10, token);
                                    if (!ss.DataAvailable) break;
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Console.WriteLine($"[TCP Relay s2c] Error: {ex.Message}"); }
                    }, token);

                    await Task.WhenAll(c2sTask, s2cTask);

                    byte[] reqBytes; byte[] respBytes;
                    lock (requestCapture) reqBytes = requestCapture.ToArray();
                    lock (responseCapture) respBytes = responseCapture.ToArray();
                    if (reqBytes.Length > 0 || respBytes.Length > 0) StoreTcpCapture(reqBytes, respBytes);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void StoreTcpCapture(byte[] requestBytes, byte[] responseBytes)
        {
            try
            {
                var question = _run.CurrentQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                var stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
                var requestText = requestBytes.Length > 0 ? Encoding.UTF8.GetString(requestBytes) : string.Empty;
                var responseText = responseBytes.Length > 0 ? Encoding.UTF8.GetString(responseBytes) : string.Empty;
                _run.SetServerRequest(question, stage, requestText);
                _run.SetServerResponse(question, stage, responseText);
            }
            catch { }
        }

        private void TryAppendServerActual(string requestBody, byte[] responseBytes, string httpMethod = "GET", int statusCode = 200)
        {
            try
            {
                var question = _run.CurrentQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                var stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
                _run.SetServerRequest(question, stage, requestBody ?? string.Empty);

                string respText; int normalizedByteSize;
                try
                {
                    respText = Encoding.UTF8.GetString(responseBytes);
                    if (respText.TrimStart().StartsWith("{") || respText.TrimStart().StartsWith("["))
                    {
                        try
                        {
                            var jsonDoc = JsonDocument.Parse(respText);
                            var normalizedJson = JsonSerializer.Serialize(jsonDoc, new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                            normalizedByteSize = Encoding.UTF8.GetByteCount(normalizedJson);
                        }
                        catch { normalizedByteSize = responseBytes?.Length ?? 0; }
                    }
                    else
                    {
                        normalizedByteSize = responseBytes?.Length ?? 0;
                    }
                }
                catch { respText = $"<binary {responseBytes?.Length ?? 0} bytes>"; normalizedByteSize = responseBytes?.Length ?? 0; }

                _run.SetServerResponse(question, stage, respText);
                _run.SetHttpMetadata(question, stage, httpMethod, statusCode, normalizedByteSize);
            }
            catch { }
        }
    }
}
