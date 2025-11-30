namespace SolutionGrader.Core.Abstractions;

public interface IExecutableManager
{
    bool IsServerRunning { get; }
    bool IsClientRunning { get; }

    void Init(string? clientPath, string? serverPath);
    void StartServer();
    void StartClient();
    System.Threading.Tasks.Task<System.Diagnostics.Process?> StartAsync(string executablePath, string arguments, System.Threading.CancellationToken ct);

    System.Threading.Tasks.Task StopServerAsync();
    System.Threading.Tasks.Task StopClientAsync();
    System.Threading.Tasks.Task StopAllAsync();
    
    void SendClientInput(string input);
    System.Threading.Tasks.Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, System.Threading.CancellationToken ct = default);
    System.Threading.Tasks.Task<bool> WaitForServerOutputAsync(int timeoutSeconds = 5, System.Threading.CancellationToken ct = default);
    string GetClientOutput();
    string GetServerOutput();
    
    /// <summary>
    /// Gets only the NEW output since the last call to this method (or since process started).
    /// This is useful for stage-by-stage output tracking where we only want output generated
    /// after a specific action, not cumulative output from the entire session.
    /// </summary>
    /// <returns>New client output since last check, or empty string if no new output</returns>
    string GetClientOutputSinceLastCheck();
    
    /// <summary>
    /// Gets only the NEW server output since the last call to this method (or since process started).
    /// </summary>
    /// <returns>New server output since last check, or empty string if no new output</returns>
    string GetServerOutputSinceLastCheck();
    
    /// <summary>
    /// Resets the "last check" tracking point to the current output length.
    /// Call this when transitioning to a new stage to ensure only output from the new stage is captured.
    /// </summary>
    void ResetOutputCheckpoints();
}
