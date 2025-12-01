using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SolutionGrader.Core.Abstractions.Docker;

namespace SolutionGrader.Core.Services.Docker;

/// <summary>
/// Service for attaching to a Docker container's console output.
/// Uses "docker attach --sig-proxy=false" for safe attachment.
/// 
/// This approach avoids buffering issues by directly connecting to the container's TTY,
/// similar to how the Recorder_NetWorking project reads console output.
/// </summary>
public class DockerConsoleService : IDockerConsoleService, IDisposable
{
    private Process? _attachProcess;
    private readonly StringBuilder _allOutput = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _stageOutputs = new();
    private string? _currentStage;
    private bool _disposed;
    
    /// <summary>
    /// Gets whether the console is currently attached.
    /// </summary>
    public bool IsAttached => _attachProcess != null && !_attachProcess.HasExited;
    
    /// <summary>
    /// Attaches to a container's console and starts reading output.
    /// </summary>
    public async Task AttachAsync(string containerName, Action<string> onOutputReceived, CancellationToken ct = default)
    {
        // Detach any existing connection first
        await DetachAsync();
        
        try
        {
            // Start docker attach process
            // --sig-proxy=false ensures Ctrl+C only stops the attach, not the container
            _attachProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"attach --sig-proxy=false {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true, // For potential future input support
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };
            
            _attachProcess.Start();
            
            // Start background task to read output
            _ = ReadOutputAsync(_attachProcess, onOutputReceived, ct);
            
            Console.WriteLine($"[DockerConsole] Attached to {containerName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DockerConsole] Failed to attach to {containerName}: {ex.Message}");
            throw;
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Background task to read console output.
    /// </summary>
    private async Task ReadOutputAsync(Process process, Action<string> onOutputReceived, CancellationToken ct)
    {
        try
        {
            var buffer = new char[4096];
            var lineBuffer = new StringBuilder();
            
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                // Read available output
                var readCount = await process.StandardOutput.ReadAsync(buffer, ct);
                
                if (readCount > 0)
                {
                    var text = new string(buffer, 0, readCount);
                    
                    // Store in all output buffer
                    lock (_allOutput)
                    {
                        _allOutput.Append(text);
                    }
                    
                    // Store in current stage buffer if any
                    if (_currentStage != null && _stageOutputs.TryGetValue(_currentStage, out var stageBuffer))
                    {
                        lock (stageBuffer)
                        {
                            stageBuffer.Append(text);
                        }
                    }
                    
                    // Notify callback
                    onOutputReceived?.Invoke(text);
                }
                else
                {
                    // No data available, small delay before checking again
                    await Task.Delay(50, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DockerConsole] Error reading output: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Detaches from the container's console.
    /// </summary>
    public async Task DetachAsync()
    {
        if (_attachProcess != null)
        {
            try
            {
                if (!_attachProcess.HasExited)
                {
                    // Send Ctrl+C equivalent to detach gracefully
                    _attachProcess.Kill();
                }
                
                _attachProcess.WaitForExit(1000);
                _attachProcess.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DockerConsole] Error during detach: {ex.Message}");
            }
            finally
            {
                _attachProcess = null;
            }
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Gets all accumulated output.
    /// </summary>
    public string GetAllOutput()
    {
        lock (_allOutput)
        {
            return _allOutput.ToString();
        }
    }
    
    /// <summary>
    /// Gets output for a specific stage.
    /// </summary>
    public string? GetStageOutput(string stage)
    {
        if (_stageOutputs.TryGetValue(stage, out var buffer))
        {
            lock (buffer)
            {
                return buffer.ToString();
            }
        }
        return null;
    }
    
    /// <summary>
    /// Clears all output.
    /// </summary>
    public void ClearOutput()
    {
        lock (_allOutput)
        {
            _allOutput.Clear();
        }
        _stageOutputs.Clear();
        _currentStage = null;
    }
    
    /// <summary>
    /// Marks the beginning of a stage.
    /// </summary>
    public void BeginStage(string stage)
    {
        _currentStage = stage;
        _stageOutputs.GetOrAdd(stage, _ => new StringBuilder());
        Console.WriteLine($"[DockerConsole] Begin stage: {stage}");
    }
    
    /// <summary>
    /// Marks the end of a stage.
    /// </summary>
    public void EndStage(string stage)
    {
        if (_currentStage == stage)
        {
            _currentStage = null;
        }
        Console.WriteLine($"[DockerConsole] End stage: {stage}");
    }
    
    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        DetachAsync().Wait();
        GC.SuppressFinalize(this);
    }
}
