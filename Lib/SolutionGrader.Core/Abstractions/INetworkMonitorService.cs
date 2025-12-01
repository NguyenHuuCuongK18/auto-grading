namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Interface for network monitoring service using packet capture (libpcap/Npcap).
/// This service monitors network traffic on a specified port without proxying,
/// allowing passive capture of client-server communication for grading.
/// 
/// Unlike IMiddlewareService which actively proxies traffic, INetworkMonitorService
/// passively sniffs packets on the network interface, making it suitable for
/// Docker environments where the grader runs outside the container.
/// </summary>
public interface INetworkMonitorService
{
    /// <summary>
    /// Starts the network monitor to capture traffic on the configured port.
    /// </summary>
    /// <param name="useHttp">Whether to use HTTP parsing mode for captured packets.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(bool useHttp, CancellationToken ct = default);
    
    /// <summary>
    /// Stops the network monitor and finalizes captured data.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Configures the ports to monitor.
    /// </summary>
    /// <param name="proxyPort">The port where grader listens (for legacy compatibility, unused in passive mode).</param>
    /// <param name="serverPort">The server port to monitor traffic on.</param>
    void ConfigurePorts(int proxyPort, int serverPort);
    
    /// <summary>
    /// Gets captured request data for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>The captured request data, or null if not available.</returns>
    string? GetCapturedRequest(string stage);
    
    /// <summary>
    /// Gets captured response data for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>The captured response data, or null if not available.</returns>
    string? GetCapturedResponse(string stage);
    
    /// <summary>
    /// Gets the HTTP method from captured request for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>The HTTP method, or null if not available.</returns>
    string? GetCapturedHttpMethod(string stage);
    
    /// <summary>
    /// Gets the HTTP status code from captured response for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>The HTTP status code, or null if not available.</returns>
    string? GetCapturedStatusCode(string stage);
    
    /// <summary>
    /// Marks the start of a new stage for traffic capture correlation.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    void BeginStage(string stage);
    
    /// <summary>
    /// Marks the end of a stage for traffic capture correlation.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    void EndStage(string stage);
}
