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
                _http.Prefixes.Add($"http://localhost:{_proxyPort}/");
                _http.Start();
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROXY} {string.Format(LoggingKeywords.MSG_PROXY_HTTP_LISTENING, _proxyPort, _realServerPort)}");
                _listenTask = Task.Run(() => ListenHttpAsync(token), token);
            }
            catch (Exception ex) { Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_HTTP_PROXY_ERR} {string.Format(LoggingKeywords.MSG_PROXY_HTTP_ERROR, ex.Message)}"); }
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
                var urlBuilder = new UriBuilder(AppsettingKeywords.PROTOCOL_HTTP, "localhost", _realServerPort, req.Url?.AbsolutePath ?? "/", req.Url?.Query ?? "");
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
                _tcp = new TcpListener(IPAddress.Loopback, _proxyPort);
                _tcp.Start();
                Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_PROXY} {string.Format(LoggingKeywords.MSG_PROXY_TCP_LISTENING, _proxyPort, _realServerPort)}");
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
                    await server.ConnectAsync(IPAddress.Loopback, _realServerPort, token);
                    using var cs = client.GetStream();
                    using var ss = server.GetStream();
                    
                    // Capture request and response data for TCP connections
                    var requestCapture = new List<byte>();
                    var responseCapture = new List<byte>();
                    
                    var c2s = RelayAndCaptureAsync(cs, ss, requestCapture, token);
                    var s2c = RelayAndCaptureAsync(ss, cs, responseCapture, token);
                    
                    // Wait for either direction to complete
                    var completed = await Task.WhenAny(c2s, s2c);
                    
                    // Give additional time for the other direction to receive and capture data
                    // This is important because the server may send a response after client finishes sending
                    try
                    {
                        if (completed == c2s)
                        {
                            // Client finished sending, wait for server response with timeout
                            await Task.WhenAny(s2c, Task.Delay(1000, token));
                        }
                        else
                        {
                            // Server finished, wait for client with timeout
                            await Task.WhenAny(c2s, Task.Delay(1000, token));
                        }
                    }
                    catch { }
                    
                    // Store captured data after relay completes
                    StoreTcpCapture(requestCapture.ToArray(), responseCapture.ToArray());
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

        private static async Task RelayAndCaptureAsync(NetworkStream from, NetworkStream to, List<byte> capture, CancellationToken token)
        {
            var buffer = new byte[8192];
            int read;
            int totalCaptured = 0;
            while ((read = await from.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                // Capture the data
                capture.AddRange(buffer.Take(read));
                totalCaptured += read;
                Console.WriteLine($"[Relay] Captured {read} bytes (total: {totalCaptured})");
                await to.WriteAsync(buffer, 0, read, token);
            }
            Console.WriteLine($"[Relay] Stream ended. Total captured: {totalCaptured} bytes");
        }

        private void StoreTcpCapture(byte[] requestBytes, byte[] responseBytes)
        {
            try
            {
                var question = _run.CurrentQuestionCode ?? FileKeywords.Value_UnknownQuestion;
                var stage = _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");

                Console.WriteLine($"[TCP Capture] Request size: {requestBytes.Length}, Response size: {responseBytes.Length}");
                
                // Convert bytes to string (assuming UTF-8 encoding for TCP data)
                var requestText = requestBytes.Length > 0 ? Encoding.UTF8.GetString(requestBytes) : string.Empty;
                var responseText = responseBytes.Length > 0 ? Encoding.UTF8.GetString(responseBytes) : string.Empty;

                // Store TCP request and response
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

                // Store HTTP request and response separately in memory to avoid overwriting console output
                _run.SetServerRequest(question, stage, requestBody ?? string.Empty);
                
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
