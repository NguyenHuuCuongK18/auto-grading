using System.Diagnostics;
using System.Text;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Abstractions.Docker;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services.Docker;

/// <summary>
/// Service for Docker-based grading operations.
/// 
/// This service manages the complete lifecycle of grading a student's submission
/// using Docker containers. It handles:
/// 1. Container creation with TTY flag (-t) for proper console buffering
/// 2. File copying to containers
/// 3. Process starting/stopping
/// 4. Input/output management via named pipes
/// 5. Console attachment for real-time output reading
/// 
/// Key implementation details:
/// - Uses "docker run -t" to allocate a pseudo-TTY, avoiding buffer issues
/// - Uses "docker attach --sig-proxy=false" to read console output without signal propagation
/// - Uses named pipes (/tmp/{appName}_input_pipe) for sending stdin to containers
/// - Supports both client and server containers for network applications
/// </summary>
public class DockerGradingService : IDockerGradingService, IDisposable
{
    private readonly IRunContext _run;
    private DockerStudentConfig? _config;
    private DockerConsoleService? _clientConsole;
    private DockerConsoleService? _serverConsole;
    private readonly StringBuilder _clientOutput = new();
    private readonly StringBuilder _serverOutput = new();
    private bool _serverStarted;
    private bool _clientStarted;
    private bool _disposed;
    
    // Container names with unique suffix to avoid conflicts
    private string? _serverContainerName;
    private string? _clientContainerName;
    private string? _databaseContainerName;
    private string? _networkName;
    
    public DockerGradingService(IRunContext run)
    {
        _run = run;
    }
    
    /// <summary>
    /// Gets whether the server is running.
    /// </summary>
    public bool IsServerRunning => _serverStarted && _serverConsole?.IsAttached == true;
    
    /// <summary>
    /// Gets whether the client is running.
    /// </summary>
    public bool IsClientRunning => _clientStarted && _clientConsole?.IsAttached == true;
    
    /// <summary>
    /// Sets up the Docker environment for grading.
    /// </summary>
    public async Task<(bool Success, string Message)> SetupEnvironmentAsync(
        DockerStudentConfig studentConfig,
        string testKitPath,
        CancellationToken ct = default)
    {
        _config = studentConfig;
        
        // Generate unique names to avoid conflicts with parallel grading
        var suffix = $"_{studentConfig.StudentCode}_{DateTime.Now.Ticks}";
        _serverContainerName = $"ag-server{suffix}";
        _clientContainerName = $"ag-client{suffix}";
        _databaseContainerName = $"ag-db{suffix}";
        _networkName = $"ag-network{suffix}";
        
        try
        {
            Console.WriteLine($"[DockerGrading] Setting up environment for {studentConfig.StudentCode}");
            
            // 1. Create Docker network
            await CreateNetworkAsync(_networkName, ct);
            
            // 2. Create database container (MSSQL)
            await CreateDatabaseContainerAsync(ct);
            
            // 3. Create server container
            if (studentConfig.HasServer)
            {
                await CreateContainerAsync(_serverContainerName, studentConfig.CodeImageName, 
                    studentConfig.ServerInternalPort, studentConfig.ServerHostPort, ct);
                
                // Copy server files
                if (!string.IsNullOrEmpty(studentConfig.ServerDllPath))
                {
                    await CopyFilesToContainerAsync(_serverContainerName, studentConfig.ServerDllPath, ct);
                }
            }
            
            // 4. Create client container
            if (studentConfig.HasClient)
            {
                await CreateContainerAsync(_clientContainerName, studentConfig.CodeImageName, 0, 0, ct);
                
                // Copy client files
                if (!string.IsNullOrEmpty(studentConfig.ClientDllPath))
                {
                    await CopyFilesToContainerAsync(_clientContainerName, studentConfig.ClientDllPath, ct);
                }
                else
                {
                    // Look for golden client in test kit
                    var goldenClientPath = Path.Combine(testKitPath, "Meta", "Given", "Client");
                    if (Directory.Exists(goldenClientPath))
                    {
                        Console.WriteLine($"[DockerGrading] Using golden client from: {goldenClientPath}");
                        await CopyFolderToContainerAsync(_clientContainerName, goldenClientPath, "/app", ct);
                    }
                }
            }
            
            Console.WriteLine($"[DockerGrading] Environment setup complete for {studentConfig.StudentCode}");
            return (true, "Environment setup successful");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DockerGrading] Setup failed: {ex.Message}");
            return (false, $"Setup failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Starts the server application.
    /// </summary>
    public async Task<(bool Success, string Message)> StartServerAsync(CancellationToken ct = default)
    {
        if (_config == null || !_config.HasServer)
            return (false, "Server not configured");
        
        try
        {
            Console.WriteLine($"[DockerGrading] Starting server in {_serverContainerName}");
            
            // Create input pipe
            await CreateInputPipeAsync(_serverContainerName!, "server", ct);
            
            // Start the application with dotnet
            var dllPath = $"/app/{Path.GetFileName(_config.ServerDllPath ?? "Server.dll")}";
            await StartApplicationAsync(_serverContainerName!, "server", dllPath, ct);
            
            // Attach to console for output reading
            _serverConsole = new DockerConsoleService();
            await _serverConsole.AttachAsync(_serverContainerName!, output =>
            {
                lock (_serverOutput)
                {
                    _serverOutput.Append(output);
                }
                _run.AppendServerOutput(_run.CurrentQuestionCode ?? "unknown", 
                    _run.CurrentStageLabel ?? "0", output);
            }, ct);
            
            _serverStarted = true;
            
            // Wait for server to be ready
            await Task.Delay(2000, ct); // Give time for startup
            
            return (true, "Server started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DockerGrading] Failed to start server: {ex.Message}");
            return (false, $"Failed to start server: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Starts the client application.
    /// </summary>
    public async Task<(bool Success, string Message)> StartClientAsync(CancellationToken ct = default)
    {
        if (_config == null || !_config.HasClient)
            return (false, "Client not configured");
        
        try
        {
            Console.WriteLine($"[DockerGrading] Starting client in {_clientContainerName}");
            
            // Create input pipe
            await CreateInputPipeAsync(_clientContainerName!, "client", ct);
            
            // Start the application with dotnet
            var dllPath = $"/app/{Path.GetFileName(_config.ClientDllPath ?? "Client.dll")}";
            await StartApplicationAsync(_clientContainerName!, "client", dllPath, ct);
            
            // Attach to console for output reading
            _clientConsole = new DockerConsoleService();
            await _clientConsole.AttachAsync(_clientContainerName!, output =>
            {
                lock (_clientOutput)
                {
                    _clientOutput.Append(output);
                }
                _run.AppendClientOutput(_run.CurrentQuestionCode ?? "unknown", 
                    _run.CurrentStageLabel ?? "0", output);
            }, ct);
            
            _clientStarted = true;
            
            return (true, "Client started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DockerGrading] Failed to start client: {ex.Message}");
            return (false, $"Failed to start client: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Sends input to the client application.
    /// </summary>
    public async Task<(bool Success, string Message)> SendClientInputAsync(string input, CancellationToken ct = default)
    {
        if (_clientContainerName == null)
            return (false, "Client not configured");
        
        try
        {
            await SendInputToContainerAsync(_clientContainerName, "client", input, ct);
            return (true, $"Sent input: {input}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to send input: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Sends input to the server application.
    /// </summary>
    public async Task<(bool Success, string Message)> SendServerInputAsync(string input, CancellationToken ct = default)
    {
        if (_serverContainerName == null)
            return (false, "Server not configured");
        
        try
        {
            await SendInputToContainerAsync(_serverContainerName, "server", input, ct);
            return (true, $"Sent input: {input}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to send input: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets the client output.
    /// </summary>
    public string GetClientOutput()
    {
        lock (_clientOutput)
        {
            return _clientOutput.ToString();
        }
    }
    
    /// <summary>
    /// Gets the server output.
    /// </summary>
    public string GetServerOutput()
    {
        lock (_serverOutput)
        {
            return _serverOutput.ToString();
        }
    }
    
    /// <summary>
    /// Waits for new client output.
    /// </summary>
    public async Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var startLength = GetClientOutput().Length;
        var startTime = DateTime.UtcNow;
        
        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            
            if (GetClientOutput().Length > startLength)
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Waits for new server output.
    /// </summary>
    public async Task<bool> WaitForServerOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var startLength = GetServerOutput().Length;
        var startTime = DateTime.UtcNow;
        
        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            
            if (GetServerOutput().Length > startLength)
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Cleans up between test cases.
    /// </summary>
    public async Task<(bool Success, string Message)> CleanupTestCaseAsync(CancellationToken ct = default)
    {
        try
        {
            // Clear output buffers
            lock (_clientOutput) { _clientOutput.Clear(); }
            lock (_serverOutput) { _serverOutput.Clear(); }
            
            _clientConsole?.ClearOutput();
            _serverConsole?.ClearOutput();
            
            Console.WriteLine("[DockerGrading] Test case cleanup complete");
            return (true, "Cleanup successful");
        }
        catch (Exception ex)
        {
            return (false, $"Cleanup failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Disposes the Docker environment.
    /// </summary>
    public async Task<(bool Success, string Message)> DisposeEnvironmentAsync(CancellationToken ct = default)
    {
        try
        {
            Console.WriteLine($"[DockerGrading] Disposing environment");
            
            // Detach consoles
            if (_clientConsole != null)
            {
                await _clientConsole.DetachAsync();
                _clientConsole.Dispose();
                _clientConsole = null;
            }
            
            if (_serverConsole != null)
            {
                await _serverConsole.DetachAsync();
                _serverConsole.Dispose();
                _serverConsole = null;
            }
            
            // Stop and remove containers
            if (_clientContainerName != null)
                await StopAndRemoveContainerAsync(_clientContainerName, ct);
            
            if (_serverContainerName != null)
                await StopAndRemoveContainerAsync(_serverContainerName, ct);
            
            if (_databaseContainerName != null)
                await StopAndRemoveContainerAsync(_databaseContainerName, ct);
            
            // Remove network
            if (_networkName != null)
                await RemoveNetworkAsync(_networkName, ct);
            
            _serverStarted = false;
            _clientStarted = false;
            
            return (true, "Environment disposed");
        }
        catch (Exception ex)
        {
            return (false, $"Dispose failed: {ex.Message}");
        }
    }
    
    // Private helper methods
    
    private async Task CreateNetworkAsync(string networkName, CancellationToken ct)
    {
        await RunDockerCommandAsync($"network create {networkName}", ct);
    }
    
    private async Task RemoveNetworkAsync(string networkName, CancellationToken ct)
    {
        await RunDockerCommandAsync($"network rm {networkName}", ct, ignoreErrors: true);
    }
    
    private async Task CreateDatabaseContainerAsync(CancellationToken ct)
    {
        var cmd = $"run -d --name {_databaseContainerName} " +
                  $"--network {_networkName} " +
                  $"-e \"ACCEPT_EULA=Y\" " +
                  $"-e \"MSSQL_SA_PASSWORD=YourStrong@Passw0rd\" " +
                  $"-p 1433:1433 " +
                  $"mcr.microsoft.com/mssql/server:2019-latest";
        
        await RunDockerCommandAsync(cmd, ct);
        
        // Wait for database to be ready
        await Task.Delay(5000, ct);
    }
    
    private async Task CreateContainerAsync(string containerName, string imageName, 
        int internalPort, int hostPort, CancellationToken ct)
    {
        var portMapping = hostPort > 0 ? $"-p {hostPort}:{internalPort}" : "";
        var cmd = $"run -d -t -i --name {containerName} " + // -t for TTY, -i for interactive
                  $"--network {_networkName} " +
                  $"{portMapping} " +
                  $"-e \"DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1\" " + // Disable buffering
                  $"{imageName} " +
                  $"sleep infinity"; // Keep container running
        
        await RunDockerCommandAsync(cmd, ct);
    }
    
    private async Task CopyFilesToContainerAsync(string containerName, string sourcePath, CancellationToken ct)
    {
        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        await RunDockerCommandAsync($"cp \"{sourceDir}\" {containerName}:/app", ct);
    }
    
    private async Task CopyFolderToContainerAsync(string containerName, string sourceFolder, 
        string destFolder, CancellationToken ct)
    {
        await RunDockerCommandAsync($"cp \"{sourceFolder}\" {containerName}:{destFolder}", ct);
    }
    
    private async Task CreateInputPipeAsync(string containerName, string appName, CancellationToken ct)
    {
        var pipePath = $"/tmp/{appName}_input_pipe";
        await RunDockerCommandAsync($"exec {containerName} mkfifo \"{pipePath}\"", ct, ignoreErrors: true);
        
        // Start doorstop process to keep pipe open
        await RunDockerCommandAsync($"exec -d {containerName} sh -c \"sleep infinity > {pipePath}\"", ct);
    }
    
    private async Task StartApplicationAsync(string containerName, string appName, string dllPath, CancellationToken ct)
    {
        var pipePath = $"/tmp/{appName}_input_pipe";
        var cmd = $"exec -d -e DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1 {containerName} " +
                  $"sh -c \"stdbuf -o0 -e0 dotnet {dllPath} > /proc/1/fd/1 2>&1 < {pipePath}\"";
        
        await RunDockerCommandAsync(cmd, ct);
    }
    
    private async Task SendInputToContainerAsync(string containerName, string appName, string input, CancellationToken ct)
    {
        var pipePath = $"/tmp/{appName}_input_pipe";
        var safeInput = input.Replace("'", "'\\''").Replace("\n", "");
        var cmd = $"exec {containerName} sh -c \"echo '{safeInput}' | tee /proc/1/fd/1 > {pipePath}\"";
        
        await RunDockerCommandAsync(cmd, ct);
        Console.WriteLine($"[DockerGrading] Sent input to {appName}: {input}");
    }
    
    private async Task StopAndRemoveContainerAsync(string containerName, CancellationToken ct)
    {
        await RunDockerCommandAsync($"stop {containerName}", ct, ignoreErrors: true);
        await RunDockerCommandAsync($"rm {containerName}", ct, ignoreErrors: true);
    }
    
    private async Task<string> RunDockerCommandAsync(string command, CancellationToken ct, 
        bool ignoreErrors = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(psi) ?? throw new Exception("Failed to start docker");
        
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        
        await process.WaitForExitAsync(ct);
        
        if (process.ExitCode != 0 && !ignoreErrors)
        {
            Console.WriteLine($"[DockerGrading] Command failed: docker {command}");
            Console.WriteLine($"[DockerGrading] Error: {error}");
        }
        
        return output;
    }
    
    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        DisposeEnvironmentAsync().Wait();
        GC.SuppressFinalize(this);
    }
}
