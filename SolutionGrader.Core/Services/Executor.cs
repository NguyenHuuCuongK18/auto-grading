using System.Diagnostics;
using System.Net.Http;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;
using System;
using System.IO;
namespace SolutionGrader.Core.Services
{
    public sealed class Executor : IExecutor
    {
        private readonly HttpClient _http = new();
        private readonly IExecutableManager _proc;
        private readonly IMiddlewareService _mw;
        private readonly IDataComparisonService _cmp;
        private readonly IDetailLogService _log;
        private readonly IRunContext _run;

        private const int ServerReadyTimeoutSeconds = 5;
        private const int ServerReadyPollIntervalMs = 100;
        private const int RealServerPort = 5001;

        public Executor(IExecutableManager proc, IMiddlewareService mw, IDataComparisonService cmp, IDetailLogService log, IRunContext run)
        {
            _proc = proc;
            _mw = mw;
            _cmp = cmp;
            _log = log;
            _run = run;
        }

        public async Task<(bool, string)> ExecuteAsync(Step step, ExecuteSuiteArgs args, CancellationToken ct)
        {
            string errCode = ErrorCodes.NONE;
            (bool ok, string msg) result;

            try
            {
                switch (step.Action)
                {
                    case var a when a == ActionKeywords.ServerStart:
                        {
                            Console.WriteLine($"[Action] ServerStart: Starting server application...");
                            _proc.StartServer();
                            
                            // Wait for server to be ready
                            var t0 = Environment.TickCount;
                            bool serverReady = false;
                            while (Environment.TickCount - t0 < ServerReadyTimeoutSeconds * 1000)
                            {
                                try
                                {
                                    var res = await _http.GetAsync($"http://127.0.0.1:{RealServerPort}/healthz", ct);
                                    if (res.IsSuccessStatusCode)
                                    {
                                        serverReady = true;
                                        break;
                                    }
                                }
                                catch { /* wait */ }
                                
                                if (_proc.IsServerRunning)
                                {
                                    // Server process is running, even if health check fails
                                    serverReady = true;
                                    break;
                                }
                                
                                await Task.Delay(ServerReadyPollIntervalMs, ct);
                            }
                            
                            if (!serverReady)
                            {
                                Console.WriteLine("[Action] ServerStart: Warning - Server may not be fully initialized");
                            }
                            
                            result = (true, "Server started");
                            break;
                        }

                    case var a when a == ActionKeywords.ClientStart:
                        {
                            Console.WriteLine($"[Action] ClientStart: Starting client application...");
                            _proc.StartClient();
                            result = (true, "Client started");
                            break;
                        }

                    case var a when a == ActionKeywords.RunServer:
                        {
                            var serverPath = _run.ResolveServerExecutable();
                            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
                            { errCode = ErrorCodes.FILE_NOT_FOUND; return (false, "Server executable not found"); }

                            var p = await _proc.StartAsync(serverPath, $"--urls http://127.0.0.1:{RealServerPort}", ct);
                            var t0 = Environment.TickCount;
                            while (Environment.TickCount - t0 < ServerReadyTimeoutSeconds * 1000)
                            {
                                try
                                {
                                    var res = await _http.GetAsync($"http://127.0.0.1:{RealServerPort}/healthz", ct);
                                    if (res.IsSuccessStatusCode) break;
                                }
                                catch { /* wait */ }
                                await Task.Delay(ServerReadyPollIntervalMs, ct);
                            }
                            result = (true, "RunServer OK");
                            break;
                        }

                    case var a when a == ActionKeywords.Wait:
                        var ms = int.TryParse(step.Value ?? "1000", out var v) ? v : 1000;
                        await Task.Delay(ms, ct);
                        result = (true, $"Waited {ms}ms");
                        break;

                    case var a when a == ActionKeywords.HttpRequest:
                        {
                            var parts = (step.Value ?? "").Split('|', 4, StringSplitOptions.TrimEntries);
                            if (parts.Length < 2) { errCode = ErrorCodes.HTTP_REQUEST_INVALID; result = (false, "HTTP_REQUEST requires METHOD|URL"); break; }

                            using var req = new HttpRequestMessage(new HttpMethod(parts[0]), parts[1]);
                            var resp = await _http.SendAsync(req, ct);
                            var body = await resp.Content.ReadAsStringAsync(ct);

                            // Optional expected status code (3rd part)
                            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) && int.TryParse(parts[2], out var expectedStatus))
                            {
                                if ((int)resp.StatusCode != expectedStatus)
                                {
                                    errCode = ErrorCodes.HTTP_NON_SUCCESS;
                                    result = (false, $"HTTP status {(int)resp.StatusCode} != expected {expectedStatus}");
                                    break;
                                }
                            }
                            else if (!resp.IsSuccessStatusCode)
                            {
                                errCode = ErrorCodes.HTTP_NON_SUCCESS;
                                result = (false, $"HTTP {resp.StatusCode}");
                                break;
                            }

                            // Optional expected body substring (4th part)
                            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]) &&
                                !body.Contains(parts[3], StringComparison.OrdinalIgnoreCase))
                            {
                                errCode = ErrorCodes.TEXT_MISMATCH;
                                result = (false, "Expected body text not found");
                                break;
                            }

                            // Write client actual as before
                            try
                            {
                                var stageLabel = GetStageLabel(step);
                                _run.SetClientOutput(step.QuestionCode, stageLabel, body);
                            }
                            catch { /* ignore capture errors */ }

                            result = (true, "HTTP ok");
                            break;
                        }

                    case var a when a == ActionKeywords.CompareText:
                        result = _cmp.CompareText(step.Target, ResolveActualPath(step));
                        break;

                    case var a when a == ActionKeywords.CompareJson:
                        result = _cmp.CompareText(step.Target, ResolveActualPath(step));
                        break;

                    case var a when a == ActionKeywords.CompareCsv:
                        result = _cmp.CompareCsv(step.Target, step.Value);
                        break;

                    case var a when a == ActionKeywords.TcpRelay:
                        {
                            var ok = await _mw.ProxyAsync(_run, ct);
                            if (!ok) { errCode = ErrorCodes.TCP_RELAY_ERROR; result = (false, "TCP relay failed"); }
                            else result = (true, "TCP relay ok");
                            break;
                        }

                    default:
                        result = (false, $"Unknown action: {step.Action}");
                        errCode = ErrorCodes.UNKNOWN;
                        break;
                }
            }
            catch (OperationCanceledException) { errCode = ErrorCodes.TIMEOUT; return (false, "Step timed out"); }
            catch (HttpRequestException ex) { errCode = ErrorCodes.HTTP_NON_SUCCESS; return (false, ex.Message); }
            catch (Exception ex) { errCode = ErrorCodes.UNKNOWN; return (false, ex.Message); }

            return result;

            string? ResolveActualPath(Step step)
            {
                var actual = step.Value;
                if (!string.IsNullOrWhiteSpace(actual) &&
                    (actual.StartsWith("memory://", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(actual)))
                    return actual;

                var stage = ParseStageFromId(step.Id);
                var stageLabel = GetStageLabel(step);
                if (string.IsNullOrWhiteSpace(stage))
                    return actual;

                if (step.Id.StartsWith("OC-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetClientCaptureKey(step.QuestionCode, stageLabel);

                if (step.Id.StartsWith("OS-", StringComparison.OrdinalIgnoreCase))
                    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);

                return actual;
            }

            string GetStageLabel(Step step)
            {
                if (!string.IsNullOrWhiteSpace(step.Stage))
                    return step.Stage;

                var parsed = ParseStageFromId(step.Id);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return parsed;

                return _run.CurrentStageLabel ?? (_run.CurrentStage?.ToString() ?? "0");
            }

            static string ParseStageFromId(string id)
            {
                // IDs are like "OC-HTTP-Stage", "OC-CMP-Stage", etc.
                var i = id.LastIndexOf('-');
                return i >= 0 && i < id.Length - 1 ? id[(i + 1)..] : id;
            }
        }
    }
}
