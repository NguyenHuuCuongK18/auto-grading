#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EnvironmentBuilder.DockerCommand
{
    /// <summary>
    /// Manages Docker container console attachments for reliable output capture.
    /// 
    /// This class solves the Docker output buffering issue by using `docker attach --sig-proxy=false`
    /// to attach to the container's console and read output directly, bypassing the buffering
    /// issues with `docker logs`.
    /// 
    /// The approach:
    /// 1. Containers are run with `-t` flag to allocate a pseudo-TTY
    /// 2. A background process attaches to the container using `docker attach --sig-proxy=false`
    /// 3. Output is captured in real-time and stored in a thread-safe buffer
    /// 4. Stage markers are used to separate output by stage for grading comparison
    /// 
    /// Based on the reference implementation from:
    /// https://github.com/dongnuc/Recorder_NetWorking.git
    /// </summary>
    public class DockerConsoleAttachment : IDisposable
    {
        private readonly string _containerName;
        private readonly string _appName;
        private Process? _attachProcess;
        private readonly StringBuilder _outputBuffer;
        private readonly ConcurrentDictionary<int, StringBuilder> _stageOutputs;
        private int _currentStage;
        private readonly object _bufferLock = new object();
        private bool _isRunning;
        private Thread? _readThread;
        private CancellationTokenSource? _cts;

        /// <summary>
        /// Gets the current output buffer content.
        /// </summary>
        public string CurrentOutput
        {
            get
            {
                lock (_bufferLock)
                {
                    return _outputBuffer.ToString();
                }
            }
        }

        /// <summary>
        /// Gets or sets the current stage number for output separation.
        /// </summary>
        public int CurrentStage
        {
            get => _currentStage;
            set
            {
                _currentStage = value;
                if (!_stageOutputs.ContainsKey(value))
                {
                    _stageOutputs[value] = new StringBuilder();
                }
            }
        }

        /// <summary>
        /// Gets whether the attachment is active and running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Creates a new Docker console attachment for the specified container.
        /// </summary>
        /// <param name="containerName">The name of the Docker container to attach to</param>
        /// <param name="appName">A friendly name for logging purposes</param>
        public DockerConsoleAttachment(string containerName, string appName)
        {
            _containerName = containerName;
            _appName = appName;
            _outputBuffer = new StringBuilder();
            _stageOutputs = new ConcurrentDictionary<int, StringBuilder>();
            _currentStage = 0;
            _isRunning = false;
        }

        /// <summary>
        /// Start the attachment process to read console output.
        /// </summary>
        public void StartAttachment()
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{_appName}] Attachment already running");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();

                ProcessStartInfo psi;
                if (OperatingSystem.IsWindows())
                {
                    // On Windows, use cmd.exe to run docker attach
                    psi = new ProcessStartInfo("cmd.exe", $"/c docker attach --sig-proxy=false {_containerName}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    // On Linux/macOS, use bash
                    var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
                    psi = new ProcessStartInfo(shell, $"-c \"docker attach --sig-proxy=false {_containerName}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                }

                _attachProcess = new Process { StartInfo = psi };
                _attachProcess.Start();
                _isRunning = true;

                // Start background thread to read output
                _readThread = new Thread(ReadOutputLoop)
                {
                    IsBackground = true,
                    Name = $"DockerAttach-{_appName}"
                };
                _readThread.Start();

                Console.WriteLine($"[{_appName}] Console attachment started for {_containerName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_appName}] Failed to start attachment: {ex.Message}");
                _isRunning = false;
            }
        }

        /// <summary>
        /// Background loop to read output from the attached process.
        /// </summary>
        private void ReadOutputLoop()
        {
            try
            {
                var buffer = new char[4096];
                var reader = _attachProcess?.StandardOutput;
                
                while (_isRunning && reader != null && !_cts!.IsCancellationRequested)
                {
                    try
                    {
                        // Use async read with timeout to avoid blocking forever
                        var readTask = reader.ReadAsync(buffer, 0, buffer.Length);
                        if (readTask.Wait(100, _cts.Token))
                        {
                            int charsRead = readTask.Result;
                            if (charsRead > 0)
                            {
                                var text = new string(buffer, 0, charsRead);
                                lock (_bufferLock)
                                {
                                    _outputBuffer.Append(text);
                                    if (_stageOutputs.TryGetValue(_currentStage, out var stageBuffer))
                                    {
                                        stageBuffer.Append(text);
                                    }
                                }
                            }
                            else if (charsRead == 0)
                            {
                                // End of stream
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{_appName}] Read error: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_appName}] Read loop error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>
        /// Mark the beginning of a new stage and reset stage output buffer.
        /// </summary>
        /// <param name="stage">The stage number</param>
        public void StartStage(int stage)
        {
            _currentStage = stage;
            _stageOutputs[stage] = new StringBuilder();
            Console.WriteLine($"[{_appName}] Stage {stage} started");
        }

        /// <summary>
        /// Get the output captured for a specific stage.
        /// </summary>
        /// <param name="stage">The stage number</param>
        /// <returns>The output captured during the specified stage</returns>
        public string GetStageOutput(int stage)
        {
            if (_stageOutputs.TryGetValue(stage, out var buffer))
            {
                lock (_bufferLock)
                {
                    return buffer.ToString();
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Get output captured since the last call or since a baseline.
        /// </summary>
        /// <param name="baseline">The baseline length to compare against</param>
        /// <returns>New output since the baseline</returns>
        public string GetNewOutputSince(int baseline)
        {
            lock (_bufferLock)
            {
                if (_outputBuffer.Length > baseline)
                {
                    return _outputBuffer.ToString(baseline, _outputBuffer.Length - baseline);
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// Get the current length of the output buffer.
        /// </summary>
        public int OutputLength
        {
            get
            {
                lock (_bufferLock)
                {
                    return _outputBuffer.Length;
                }
            }
        }

        /// <summary>
        /// Clear all output buffers.
        /// </summary>
        public void ClearBuffers()
        {
            lock (_bufferLock)
            {
                _outputBuffer.Clear();
                foreach (var stage in _stageOutputs.Values)
                {
                    stage.Clear();
                }
                _stageOutputs.Clear();
            }
        }

        /// <summary>
        /// Stop the attachment process.
        /// </summary>
        public void StopAttachment()
        {
            _isRunning = false;
            _cts?.Cancel();

            try
            {
                if (_attachProcess != null && !_attachProcess.HasExited)
                {
                    _attachProcess.Kill();
                    _attachProcess.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_appName}] Error stopping attachment: {ex.Message}");
            }

            _attachProcess?.Dispose();
            _attachProcess = null;
            _cts?.Dispose();
            _cts = null;

            Console.WriteLine($"[{_appName}] Console attachment stopped for {_containerName}");
        }

        /// <summary>
        /// Wait for specific text to appear in the output.
        /// </summary>
        /// <param name="text">The text to wait for</param>
        /// <param name="timeoutMs">Maximum time to wait in milliseconds</param>
        /// <returns>True if text appeared within timeout, false otherwise</returns>
        public bool WaitForOutput(string text, int timeoutMs = 10000)
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                lock (_bufferLock)
                {
                    if (_outputBuffer.ToString().Contains(text))
                    {
                        return true;
                    }
                }
                Thread.Sleep(100);
            }
            return false;
        }

        public void Dispose()
        {
            StopAttachment();
        }
    }

    /// <summary>
    /// Manages multiple Docker console attachments for grading scenarios.
    /// </summary>
    public class DockerConsoleManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, DockerConsoleAttachment> _attachments;

        public DockerConsoleManager()
        {
            _attachments = new ConcurrentDictionary<string, DockerConsoleAttachment>();
        }

        /// <summary>
        /// Create and start an attachment for a container.
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="appName">Friendly name for logging</param>
        /// <returns>The console attachment</returns>
        public DockerConsoleAttachment CreateAttachment(string containerName, string appName)
        {
            var attachment = new DockerConsoleAttachment(containerName, appName);
            _attachments[containerName] = attachment;
            return attachment;
        }

        /// <summary>
        /// Get an existing attachment by container name.
        /// </summary>
        public DockerConsoleAttachment? GetAttachment(string containerName)
        {
            _attachments.TryGetValue(containerName, out var attachment);
            return attachment;
        }

        /// <summary>
        /// Stop and remove an attachment.
        /// </summary>
        public void RemoveAttachment(string containerName)
        {
            if (_attachments.TryRemove(containerName, out var attachment))
            {
                attachment.StopAttachment();
                attachment.Dispose();
            }
        }

        /// <summary>
        /// Stop and remove all attachments.
        /// </summary>
        public void RemoveAllAttachments()
        {
            foreach (var containerName in _attachments.Keys.ToList())
            {
                RemoveAttachment(containerName);
            }
        }

        public void Dispose()
        {
            RemoveAllAttachments();
        }
    }
}
