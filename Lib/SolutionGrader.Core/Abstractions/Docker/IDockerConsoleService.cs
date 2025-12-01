namespace SolutionGrader.Core.Abstractions.Docker;

/// <summary>
/// Interface for managing Docker container console attachment.
/// Uses "docker attach" to connect to a container's TTY for reading console output.
/// </summary>
public interface IDockerConsoleService
{
    /// <summary>
    /// Attaches to a container's console and starts reading output.
    /// Uses "docker attach --sig-proxy=false" to safely attach without propagating signals.
    /// </summary>
    /// <param name="containerName">The name of the container to attach to.</param>
    /// <param name="onOutputReceived">Callback for receiving console output.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the attachment is established.</returns>
    Task AttachAsync(string containerName, Action<string> onOutputReceived, CancellationToken ct = default);
    
    /// <summary>
    /// Detaches from the container's console.
    /// </summary>
    /// <returns>A task that completes when detached.</returns>
    Task DetachAsync();
    
    /// <summary>
    /// Gets all output received since the last clear.
    /// </summary>
    /// <returns>The accumulated output.</returns>
    string GetAllOutput();
    
    /// <summary>
    /// Gets output received for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>The output for that stage.</returns>
    string? GetStageOutput(string stage);
    
    /// <summary>
    /// Clears all accumulated output.
    /// </summary>
    void ClearOutput();
    
    /// <summary>
    /// Marks the beginning of a new stage for output correlation.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    void BeginStage(string stage);
    
    /// <summary>
    /// Marks the end of a stage and captures all output since the stage began.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    void EndStage(string stage);
    
    /// <summary>
    /// Gets whether the console is currently attached.
    /// </summary>
    bool IsAttached { get; }
}
