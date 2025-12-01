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
    /// Marks the start of a new stage and takes a snapshot of current output positions.
    /// Call this BEFORE executing stage actions to properly attribute output to stages.
    /// </summary>
    void BeginStage(string stage);
    
    /// <summary>
    /// Marks the end of a stage and records the final output positions.
    /// </summary>
    void EndStage(string stage);
    
    /// <summary>
    /// Gets the output that occurred ONLY during a specific stage (not accumulated).
    /// </summary>
    string GetClientStageOutput(string stage);
    
    /// <summary>
    /// Gets the output that occurred ONLY during a specific stage (not accumulated).
    /// </summary>
    string GetServerStageOutput(string stage);
}
