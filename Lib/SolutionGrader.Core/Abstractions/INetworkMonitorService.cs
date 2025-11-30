namespace SolutionGrader.Core.Abstractions;

/// <summary>
/// Interface for network monitoring service that captures network traffic.
/// This replaces the old middleware proxy approach - instead of proxying traffic, 
/// we sniff packets on the loopback interface to capture actual network communication.
/// </summary>
public interface INetworkMonitorService
{
    /// <summary>
    /// Gets or sets the port to monitor for network traffic.
    /// Both client and server should connect to this port.
    /// </summary>
    int MonitorPort { get; set; }
    
    /// <summary>
    /// Gets or sets the protocol type being monitored (TCP or HTTP).
    /// This affects how packets are parsed and stored.
    /// </summary>
    string ProtocolType { get; set; }
    
    /// <summary>
    /// Starts capturing network traffic on the specified port.
    /// Must be called before starting client and server processes.
    /// </summary>
    /// <param name="ct">Cancellation token to stop capture</param>
    Task StartAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Stops capturing network traffic and finalizes captured data.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task StopAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Sets the current question code and stage for packet association.
    /// </summary>
    /// <param name="questionCode">Current question code</param>
    /// <param name="stage">Current stage</param>
    void SetCurrentContext(string questionCode, string stage);
    
    /// <summary>
    /// Clears all captured data for a fresh start.
    /// </summary>
    void ClearCaptures();
    
    /// <summary>
    /// Sets the known client port to filter out Windows/Docker health check traffic.
    /// Once set, only traffic involving this port will be captured.
    /// </summary>
    /// <param name="clientPort">The ephemeral port used by the client process</param>
    void SetKnownClientPort(int clientPort);
    
    /// <summary>
    /// Gets whether the monitor is currently capturing.
    /// </summary>
    bool IsCapturing { get; }
}
