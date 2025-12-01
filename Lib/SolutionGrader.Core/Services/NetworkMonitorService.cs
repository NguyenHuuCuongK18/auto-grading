using System.Collections.Concurrent;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Network monitoring service that passively captures network traffic.
/// This class also implements IMiddlewareService for compatibility with existing code
/// that expects middleware functionality.
/// 
/// For Docker grading, this service runs on the host to monitor traffic
/// between containers via the exposed ports.
/// 
/// Requirements for full packet capture:
/// - Linux: libpcap-dev package (sudo apt-get install libpcap-dev)
/// - Windows: Npcap (https://npcap.com/) or WinPcap
/// - Requires admin/sudo privileges for packet capture
/// </summary>
public class NetworkMonitorService : INetworkMonitorService, IMiddlewareService
{
    private readonly IRunContext _run;
    private int _serverPort = 5001;
    private int _proxyPort = 8888;
    private bool _useHttp;
    private bool _isRunning;
    private string _currentStage = "0";
    
    // Thread-safe storage for captured data per stage
    private readonly ConcurrentDictionary<string, CapturedStageData> _capturedData = new();
    
    /// <summary>
    /// Container for captured data for a single stage.
    /// </summary>
    private class CapturedStageData
    {
        public StringBuilder RequestData { get; } = new();
        public StringBuilder ResponseData { get; } = new();
        public string? HttpMethod { get; set; }
        public string? StatusCode { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
    }
    
    public NetworkMonitorService(IRunContext run)
    {
        _run = run;
    }
    
    /// <summary>
    /// Configures the ports to monitor.
    /// </summary>
    public void ConfigurePorts(int proxyPort, int serverPort)
    {
        _proxyPort = proxyPort;
        _serverPort = serverPort;
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Configured to monitor port {serverPort}");
    }
    
    /// <summary>
    /// Starts the network monitor.
    /// </summary>
    public async Task StartAsync(bool useHttp, CancellationToken ct = default)
    {
        _useHttp = useHttp;
        
        if (_isRunning)
        {
            Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Already running, stopping first...");
            await StopAsync(ct);
        }
        
        _isRunning = true;
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Started in no-op mode (libpcap not available)");
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} For full packet capture, install libpcap-dev and run with sudo privileges");
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Stops the network monitor.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return;
        
        _isRunning = false;
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Stopped");
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// IMiddlewareService.ProxyAsync implementation - returns true since we're in passive mode.
    /// </summary>
    public async Task<bool> ProxyAsync(IRunContext context, CancellationToken ct = default)
    {
        // In passive mode, we don't actually proxy traffic
        // Just return true to indicate success
        await Task.CompletedTask;
        return true;
    }
    
    /// <summary>
    /// Marks the start of a new stage.
    /// </summary>
    public void BeginStage(string stage)
    {
        _currentStage = stage;
        _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} Begin stage: {stage}");
    }
    
    /// <summary>
    /// Marks the end of a stage.
    /// </summary>
    public void EndStage(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            data.EndTime = DateTime.UtcNow;
        }
        Console.WriteLine($"{LoggingKeywords.LOG_PREFIX_NETWORK_MONITOR} End stage: {stage}");
    }
    
    /// <summary>
    /// Gets captured request data for a stage.
    /// </summary>
    public string? GetCapturedRequest(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            var result = data.RequestData.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        return null;
    }
    
    /// <summary>
    /// Gets captured response data for a stage.
    /// </summary>
    public string? GetCapturedResponse(string stage)
    {
        if (_capturedData.TryGetValue(stage, out var data))
        {
            var result = data.ResponseData.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        return null;
    }
    
    /// <summary>
    /// Gets HTTP method from captured request.
    /// </summary>
    public string? GetCapturedHttpMethod(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.HttpMethod : null;
    }
    
    /// <summary>
    /// Gets HTTP status code from captured response.
    /// </summary>
    public string? GetCapturedStatusCode(string stage)
    {
        return _capturedData.TryGetValue(stage, out var data) ? data.StatusCode : null;
    }
    
    /// <summary>
    /// Manually adds captured request data (for use by external packet capture implementations).
    /// </summary>
    public void AddCapturedRequest(string stage, string data, string? httpMethod = null)
    {
        var stageData = _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        stageData.RequestData.Append(data);
        if (!string.IsNullOrEmpty(httpMethod))
        {
            stageData.HttpMethod = httpMethod;
        }
        
        // Also store in run context
        _run.AppendServerRequestCapture(_run.CurrentQuestionCode ?? "unknown", stage, data);
    }
    
    /// <summary>
    /// Manually adds captured response data (for use by external packet capture implementations).
    /// </summary>
    public void AddCapturedResponse(string stage, string data, string? statusCode = null)
    {
        var stageData = _capturedData.GetOrAdd(stage, _ => new CapturedStageData());
        stageData.ResponseData.Append(data);
        if (!string.IsNullOrEmpty(statusCode))
        {
            stageData.StatusCode = statusCode;
        }
        
        // Also store in run context
        _run.AppendServerResponseCapture(_run.CurrentQuestionCode ?? "unknown", stage, data);
    }
}
