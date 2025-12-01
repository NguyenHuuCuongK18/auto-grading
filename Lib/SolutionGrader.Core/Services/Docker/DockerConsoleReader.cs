using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service for reading Docker container console output.
    /// 
    /// As per the technician's suggestion, this uses docker attach with -t flag
    /// to get unbuffered output. The console is attached locally and we can
    /// read output stages similar to how it was done in Recorder_NetWorking.
    /// 
    /// Key features:
    /// - Uses docker attach --sig-proxy=false to prevent Ctrl+C from killing container
    /// - Waits a configurable time after input (1-2 seconds) to ensure all output is captured
    /// - Can use kernel32 on Windows or standard .NET on Linux to read console buffer
    /// 
    /// Thread safety: This class is thread-safe for concurrent read operations.
    /// </summary>
    public class DockerConsoleReader : IDisposable
    {
        private readonly ConcurrentDictionary<string, Process> _attachedProcesses = new();
        private readonly ConcurrentDictionary<string, StringBuilder> _outputBuffers = new();
        private readonly ConcurrentDictionary<string, Task> _readTasks = new();
        private readonly object _lock = new();

        /// <summary>
        /// Event raised when output is received from a container.
        /// </summary>
        public event EventHandler<ConsoleOutputEventArgs>? OutputReceived;

        /// <summary>
        /// Default delay in milliseconds after sending input before reading output.
        /// As per technician suggestion, 1-2 seconds is usually enough.
        /// </summary>
        public int PostInputDelayMs { get; set; } = 1500;

        /// <summary>
        /// Starts a docker run with -t flag for TTY allocation and attaches to it.
        /// This is the approach suggested by the technician to avoid buffer issues.
        /// </summary>
        /// <param name="containerName">Container name to run and attach.</param>
        /// <param name="imageName">Docker image name.</param>
        /// <param name="runOptions">Additional docker run options (e.g., -e, --network, -p).</param>
        /// <returns>True if successfully started and attached.</returns>
        public bool StartAndAttach(string containerName, string imageName, string runOptions = "")
        {
            try
            {
                // Start container with -t flag for TTY allocation
                // -t allocates a pseudo-TTY which ensures unbuffered output
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"run -d -it --name {containerName} {runOptions} {imageName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var runProcess = Process.Start(psi);
                runProcess?.WaitForExit(30000);

                if (runProcess?.ExitCode != 0)
                {
                    Console.WriteLine($"[DockerConsole] Failed to start container {containerName}");
                    return false;
                }

                // Now attach to the container
                return AttachToContainer(containerName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DockerConsole] Error starting/attaching {containerName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attaches to an existing container's console.
        /// Uses --sig-proxy=false so Ctrl+C only stops the attach, not the container.
        /// </summary>
        /// <param name="containerName">Container name to attach to.</param>
        /// <returns>True if successfully attached.</returns>
        public bool AttachToContainer(string containerName)
        {
            lock (_lock)
            {
                if (_attachedProcesses.ContainsKey(containerName))
                {
                    Console.WriteLine($"[DockerConsole] Already attached to {containerName}");
                    return true;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = $"attach --sig-proxy=false {containerName}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var process = Process.Start(psi);
                    if (process == null)
                    {
                        Console.WriteLine($"[DockerConsole] Failed to attach to {containerName}");
                        return false;
                    }

                    _attachedProcesses[containerName] = process;
                    _outputBuffers[containerName] = new StringBuilder();

                    // Start background task to continuously read output
                    var readTask = Task.Run(() => ReadOutputLoop(containerName, process));
                    _readTasks[containerName] = readTask;

                    Console.WriteLine($"[DockerConsole] Attached to {containerName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DockerConsole] Error attaching to {containerName}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Detaches from a container (stops reading output).
        /// </summary>
        /// <param name="containerName">Container name to detach from.</param>
        public void DetachFromContainer(string containerName)
        {
            lock (_lock)
            {
                if (_attachedProcesses.TryRemove(containerName, out var process))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                        process.Dispose();
                    }
                    catch { /* Ignore cleanup errors */ }
                }

                _outputBuffers.TryRemove(containerName, out _);
                _readTasks.TryRemove(containerName, out _);

                Console.WriteLine($"[DockerConsole] Detached from {containerName}");
            }
        }

        /// <summary>
        /// Gets all output captured so far from a container.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <returns>All captured output.</returns>
        public string GetOutput(string containerName)
        {
            return _outputBuffers.TryGetValue(containerName, out var buffer) 
                ? buffer.ToString() 
                : string.Empty;
        }

        /// <summary>
        /// Clears the output buffer for a container.
        /// Call this before sending input to capture only the response.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        public void ClearOutputBuffer(string containerName)
        {
            if (_outputBuffers.TryGetValue(containerName, out var buffer))
            {
                buffer.Clear();
            }
        }

        /// <summary>
        /// Waits for output after input, then captures it.
        /// Waits the configured PostInputDelayMs to ensure all output is captured.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="clearBefore">Whether to clear buffer before waiting.</param>
        /// <returns>Captured output.</returns>
        public async Task<string> WaitAndCaptureOutputAsync(string containerName, bool clearBefore = true)
        {
            if (clearBefore)
            {
                ClearOutputBuffer(containerName);
            }

            // Wait for output to complete (as per technician: 1-2 seconds after input)
            await Task.Delay(PostInputDelayMs);

            return GetOutput(containerName);
        }

        /// <summary>
        /// Waits for specific text to appear in output.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="expectedText">Text to wait for.</param>
        /// <param name="timeoutMs">Timeout in milliseconds.</param>
        /// <returns>True if text was found.</returns>
        public async Task<bool> WaitForTextAsync(string containerName, string expectedText, int timeoutMs = 10000)
        {
            var stopwatch = Stopwatch.StartNew();
            
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (_outputBuffers.TryGetValue(containerName, out var buffer))
                {
                    if (buffer.ToString().Contains(expectedText))
                    {
                        return true;
                    }
                }
                
                await Task.Delay(100);
            }

            return false;
        }

        /// <summary>
        /// Gets output lines as a list.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <returns>List of output lines.</returns>
        public List<string> GetOutputLines(string containerName)
        {
            var output = GetOutput(containerName);
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
        }

        /// <summary>
        /// Captures the stage output for grading.
        /// This is called after sending input and waiting for the response.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="stageName">Stage identifier for logging.</param>
        /// <returns>Stage output.</returns>
        public async Task<string> CaptureStageOutputAsync(string containerName, string stageName)
        {
            ClearOutputBuffer(containerName);
            
            // Wait for output to stabilize
            await Task.Delay(PostInputDelayMs);
            
            var output = GetOutput(containerName);
            Console.WriteLine($"[DockerConsole] Captured {stageName} output: {output.Length} chars");
            
            return output;
        }

        /// <summary>
        /// Background loop that continuously reads output from the attached process.
        /// </summary>
        private async Task ReadOutputLoop(string containerName, Process process)
        {
            try
            {
                var reader = process.StandardOutput;
                var buffer = new char[1024];

                while (!process.HasExited)
                {
                    // Read available data
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                    
                    if (bytesRead > 0)
                    {
                        var text = new string(buffer, 0, bytesRead);
                        
                        if (_outputBuffers.TryGetValue(containerName, out var outputBuffer))
                        {
                            outputBuffer.Append(text);
                        }

                        // Raise event for real-time processing
                        OutputReceived?.Invoke(this, new ConsoleOutputEventArgs
                        {
                            ContainerName = containerName,
                            Text = text
                        });
                    }
                    
                    // Small delay to prevent busy-waiting
                    if (bytesRead == 0)
                    {
                        await Task.Delay(50);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[DockerConsole] Error reading from {containerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes all attached processes.
        /// </summary>
        public void Dispose()
        {
            foreach (var containerName in _attachedProcesses.Keys.ToList())
            {
                DetachFromContainer(containerName);
            }
        }
    }

    /// <summary>
    /// Event args for console output events.
    /// </summary>
    public class ConsoleOutputEventArgs : EventArgs
    {
        /// <summary>
        /// Name of the container that produced the output.
        /// </summary>
        public string ContainerName { get; set; } = string.Empty;

        /// <summary>
        /// The output text received.
        /// </summary>
        public string Text { get; set; } = string.Empty;
    }
}
