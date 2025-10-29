using System.Diagnostics;
using System.Net.Http;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Errors;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

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
            _proc = proc; _mw = mw; _cmp = cmp; _log = log; _run = run;
        }

        public async Task<(bool ok, string message)> ExecuteAsync(Step step, ExecuteSuiteArgs args, CancellationToken ct)
        {
            Console.WriteLine($"[Step] Executing: {step.Action} (Stage: {step.Stage}, ID: {step.Id})");
            var useHttp = !string.Equals(args.Protocol, "TCP", StringComparison.OrdinalIgnoreCase);
            try { _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, args.StageTimeoutSeconds)); } catch { }

            int pointsPossible = IsCompare(step.Action) ? 1 : 0;
            string errCode = ErrorCodes.OK;
            string? detailPath = null;
            string? actualPath = null;

            var sw = Stopwatch.StartNew();
            (bool ok, string message) result;

            try
            {
                switch (step.Action)
                {
                    case var a when a == ActionKeywords.ClientStart:
                        if (!_proc.IsServerRunning) _proc.StartServer();
                        await WaitForServerReadyAsync(ct);
                        await _mw.StartAsync(useHttp, ct);
                        _proc.StartClient();
                        result = (true, "Client started (with server & middleware)");
                        break;

                    case var a when a == ActionKeywords.ServerStart:
                        _proc.StartServer();
                        await WaitForServerReadyAsync(ct);
                        await _mw.StartAsync(useHttp, ct);
                        result = (true, "Server started (middleware ensured)");
                        break;

                    case var a when a == ActionKeywords.ClientClose:
                        await _proc.StopClientAsync();
                        await _mw.StopAsync();
                        result = (true, "Client stopped (middleware stopped)");
                        break;

                    case var a when a == ActionKeywords.ServerClose:
                        await _proc.StopServerAsync();
                        await _mw.StopAsync();
                        result = (true, "Server stopped (middleware stopped)");
                        break;

                    case var a when a == ActionKeywords.KillAll:
                        try { await _proc.StopAllAsync(); await _mw.StopAsync(); result = (true, "All processes + middleware stopped"); }
                        catch { errCode = ErrorCodes.KILL_ALL_FAILED; result = (false, "KillAll failed"); }
                        break;

                    case var a when a == ActionKeywords.RunClient:
                        if (!_proc.IsServerRunning) _proc.StartServer();
                        await WaitForServerReadyAsync(ct);
                        await _mw.StartAsync(useHttp, ct);
                        _proc.StartClient();
                        result = (true, "RunClient OK");
                        break;

                    case var a when a == ActionKeywords.RunServer:
                        _proc.StartServer();
                        await WaitForServerReadyAsync(ct);
                        await _mw.StartAsync(useHttp, ct);
                        result = (true, "RunServer OK");
                        break;

                    case var a when a == ActionKeywords.Wait:
                        var ms = int.TryParse(step.Value ?? "1000", out var v) ? v : 1000;
                        await Task.Delay(ms, ct);
                        result = (true, $"Waited {ms}ms");
                        break;

                    case var a when a == ActionKeywords.HttpRequest:
                        {
                            var parts = (step.Value ?? "").Split('|', 3, StringSplitOptions.TrimEntries);
                            if (parts.Length < 2) { errCode = ErrorCodes.HTTP_REQUEST_INVALID; result = (false, "HTTP_REQUEST requires METHOD|URL"); break; }

                            using var req = new HttpRequestMessage(new HttpMethod(parts[0]), parts[1]);
                            var resp = await _http.SendAsync(req, ct);
                            var body = await resp.Content.ReadAsStringAsync(ct);

                            if (!resp.IsSuccessStatusCode) { errCode = ErrorCodes.HTTP_NON_SUCCESS; result = (false, $"HTTP {resp.StatusCode}"); break; }
                            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) &&
                                !body.Contains(parts[2], StringComparison.OrdinalIgnoreCase))
                            { errCode = ErrorCodes.TEXT_MISMATCH; result = (false, "Expected text not found"); break; }

                            // Write client actual
                            try
                            {
                                var stage = ParseStage(step.Id);
                                var q = step.QuestionCode;
                                actualPath = Path.Combine(_run.ResultRoot, "actual", "clients", q, $"{stage}.txt");
                                Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
                                await File.WriteAllTextAsync(actualPath, body, ct);
                            }
                            catch { }

                            result = (true, "HTTP ok");
                            break;
                        }

                    case var a when a == ActionKeywords.AssertText:
                        {
                            if (string.Equals(step.Stage, "INPUT", StringComparison.OrdinalIgnoreCase))
                            {
                                errCode = ErrorCodes.INPUT_VALIDATION_SKIPPED;
                                _log.LogSkip(step, "Skipped input validation (stdin)", errCode);
                                pointsPossible = 0;
                                result = (true, "Skipped input validation (stdin)");
                                break;
                            }

                            if (string.IsNullOrWhiteSpace(step.Target) || string.IsNullOrWhiteSpace(step.Value))
                            { result = (true, "Ignored: expected missing"); break; }

                            if (!File.Exists(step.Target))
                            { errCode = ErrorCodes.FILE_NOT_FOUND; result = (false, $"File not found: {step.Target}"); break; }

                            var txt = await File.ReadAllTextAsync(step.Target, ct);
                            txt = txt.Replace("\r", "").Replace("\n", "").Trim();
                            var val = (step.Value ?? "").Replace("\r", "").Replace("\n", "").Trim();
                            var ok = txt.Contains(val, StringComparison.OrdinalIgnoreCase);
                            if (!ok) errCode = ErrorCodes.TEXT_MISMATCH;
                            result = ok ? (true, "Text ok") : (false, "Text mismatch");
                            actualPath = step.Target;
                            break;
                        }

                    case var a when a == ActionKeywords.CaptureFile:
                        try
                        {
                            if (string.IsNullOrWhiteSpace(step.Target) || string.IsNullOrWhiteSpace(step.Value))
                            { result = (true, "Ignored: expected missing"); break; }
                            Directory.CreateDirectory(Path.GetDirectoryName(step.Value)!);
                            File.Copy(step.Target, step.Value, overwrite: true);
                            actualPath = step.Value;
                            result = (true, "Captured file");
                        }
                        catch (DirectoryNotFoundException) { errCode = ErrorCodes.PATH_NOT_FOUND; result = (false, "Capture failed: path not found"); }
                        catch (UnauthorizedAccessException) { errCode = ErrorCodes.PERMISSION_DENIED; result = (false, "Capture failed: permission denied"); }
                        catch { errCode = ErrorCodes.FILE_COPY_FAILED; result = (false, "Capture failed"); }
                        break;

                    case var a when a == ActionKeywords.CompareFile:
                        {
                            var (ok, msg) = _cmp.CompareFile(step.Target, step.Value);
                            errCode = ok ? ErrorCodes.OK :
                                (msg.StartsWith("Size differs") ? ErrorCodes.FILE_SIZE_MISMATCH :
                                 msg.StartsWith("Content differs") ? ErrorCodes.FILE_HASH_MISMATCH :
                                 msg.StartsWith("Actual file missing") ? ErrorCodes.ACTUAL_FILE_MISSING :
                                 ErrorCodes.UNKNOWN_EXCEPTION);
                            actualPath = step.Value;
                            result = (ok, msg);
                            break;
                        }

                    case var a when a == ActionKeywords.CompareText:
                        {
                            var svc = _cmp as DataComparisonService;
                            DetailedCompareResult detail = svc != null
                                ? svc.CompareTextDetailed(step.Target, step.Value, true)
                                : new DetailedCompareResult { AreEqual = _cmp.CompareText(step.Target, step.Value).Item1, Message = _cmp.CompareText(step.Target, step.Value).Item2 };
                            if (!detail.AreEqual)
                            {
                                errCode = detail.Message.StartsWith("Actual text missing") ? ErrorCodes.ACTUAL_FILE_MISSING : ErrorCodes.TEXT_MISMATCH;
                                var stage = ParseStage(step.Id);
                                detailPath = _log.WriteTextMismatchDiff(step.QuestionCode, stage, step.Target ?? "", step.Value ?? "", detail);
                            }
                            actualPath = step.Value;
                            result = (detail.AreEqual, detail.Message);
                            break;
                        }

                    case var a when a == ActionKeywords.CompareJson:
                        {
                            var (ok, msg) = _cmp.CompareJson(step.Target, step.Value);
                            errCode = ok ? ErrorCodes.OK :
                                      (msg.StartsWith("Actual JSON missing") ? ErrorCodes.ACTUAL_FILE_MISSING : ErrorCodes.JSON_MISMATCH);
                            actualPath = step.Value;
                            result = (ok, msg);
                            break;
                        }

                    case var a when a == ActionKeywords.CompareCsv:
                        {
                            var (ok, msg) = _cmp.CompareCsv(step.Target, step.Value);
                            errCode = ok ? ErrorCodes.OK :
                                      (msg.StartsWith("Actual CSV missing") ? ErrorCodes.ACTUAL_FILE_MISSING : ErrorCodes.CSV_MISMATCH);
                            actualPath = step.Value;
                            result = (ok, msg);
                            break;
                        }

                    default:
                        errCode = ErrorCodes.UNSUPPORTED_ACTION;
                        result = (false, $"Unsupported action: {step.Action}");
                        break;
                }
            }
            catch (FileNotFoundException ex)
            {
                errCode = ex.Message.Contains("Server executable") ? ErrorCodes.SERVER_EXE_MISSING :
                          ex.Message.Contains("Client executable") ? ErrorCodes.CLIENT_EXE_MISSING :
                          ErrorCodes.FILE_NOT_FOUND;
                result = (false, ex.Message);
            }
            catch (TaskCanceledException)
            {
                errCode = ErrorCodes.STEP_TIMEOUT;
                result = (false, "Step timed out");
            }
            catch (Exception ex)
            {
                errCode = ErrorCodes.UNKNOWN_EXCEPTION;
                result = (false, ex.Message);
            }

            sw.Stop();
            
            // If expected output is missing, don't count this step toward grading
            if (result.message.Contains("Ignored: expected missing", StringComparison.OrdinalIgnoreCase))
            {
                pointsPossible = 0;
            }
            
            var awarded = (result.ok && pointsPossible > 0) ? pointsPossible : 0;

            _log.LogStepGrade(step, result.ok, result.message, awarded, pointsPossible, sw.Elapsed.TotalMilliseconds, errCode, detailPath, actualPath);

            return result;
        }

        private async Task WaitForServerReadyAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(ServerReadyTimeoutSeconds))
            {
                ct.ThrowIfCancellationRequested();
                if (!_proc.IsServerRunning) { await Task.Delay(ServerReadyPollIntervalMs, ct); continue; }
                if (await IsPortListeningAsync(RealServerPort, ct)) return;
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
                cts.CancelAfter(200);
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch { return false; }
        }

        private static bool IsCompare(string action) =>
            string.Equals(action, ActionKeywords.CompareFile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, ActionKeywords.CompareText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, ActionKeywords.CompareJson, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, ActionKeywords.CompareCsv, StringComparison.OrdinalIgnoreCase);

        private static int ParseStage(string id)
        {
            var lastDash = id?.LastIndexOf('-') ?? -1;
            if (lastDash >= 0 && lastDash + 1 < id!.Length && int.TryParse(id.Substring(lastDash + 1), out var s)) return s;
            return 0;
        }
    }
}
