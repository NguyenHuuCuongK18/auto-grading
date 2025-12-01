using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Abstractions.Docker;

/// <summary>
/// Interface for Docker-based grading operations.
/// Manages the complete lifecycle of grading a student in Docker containers.
/// </summary>
public interface IDockerGradingService
{
    /// <summary>
    /// Sets up the Docker environment for a student grading session.
    /// Creates containers, copies files, and starts the database.
    /// </summary>
    /// <param name="studentConfig">Configuration for the student's submission.</param>
    /// <param name="testKitPath">Path to the test kit folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the setup result.</returns>
    Task<(bool Success, string Message)> SetupEnvironmentAsync(
        DockerStudentConfig studentConfig,
        string testKitPath,
        CancellationToken ct = default);
    
    /// <summary>
    /// Starts the server application in the Docker container.
    /// This is separated from setup to allow for pre-setup configuration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the start result.</returns>
    Task<(bool Success, string Message)> StartServerAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Starts the client application in the Docker container.
    /// This is separated from setup to allow for pre-setup configuration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the start result.</returns>
    Task<(bool Success, string Message)> StartClientAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Sends input to the client application in the Docker container.
    /// Uses named pipes for communication.
    /// </summary>
    /// <param name="input">The input to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the send result.</returns>
    Task<(bool Success, string Message)> SendClientInputAsync(string input, CancellationToken ct = default);
    
    /// <summary>
    /// Sends input to the server application in the Docker container.
    /// </summary>
    /// <param name="input">The input to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the send result.</returns>
    Task<(bool Success, string Message)> SendServerInputAsync(string input, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current output from the client container.
    /// </summary>
    /// <returns>The client output.</returns>
    string GetClientOutput();
    
    /// <summary>
    /// Gets the current output from the server container.
    /// </summary>
    /// <returns>The server output.</returns>
    string GetServerOutput();
    
    /// <summary>
    /// Waits for new output from the client container.
    /// </summary>
    /// <param name="timeoutSeconds">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if new output was received.</returns>
    Task<bool> WaitForClientOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default);
    
    /// <summary>
    /// Waits for new output from the server container.
    /// </summary>
    /// <param name="timeoutSeconds">Maximum time to wait.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if new output was received.</returns>
    Task<bool> WaitForServerOutputAsync(int timeoutSeconds = 15, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if the server process is still running.
    /// </summary>
    bool IsServerRunning { get; }
    
    /// <summary>
    /// Checks if the client process is still running.
    /// </summary>
    bool IsClientRunning { get; }
    
    /// <summary>
    /// Cleans up between test cases.
    /// Resets application state without destroying containers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the cleanup result.</returns>
    Task<(bool Success, string Message)> CleanupTestCaseAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Disposes the Docker environment for the student.
    /// Stops and removes all containers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the dispose result.</returns>
    Task<(bool Success, string Message)> DisposeEnvironmentAsync(CancellationToken ct = default);
}

/// <summary>
/// Configuration for a student's Docker grading session.
/// </summary>
public class DockerStudentConfig
{
    /// <summary>
    /// Gets or sets the student code.
    /// </summary>
    public string StudentCode { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the paper number.
    /// </summary>
    public string PaperNo { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the path to the student's solution folder.
    /// </summary>
    public string SolutionPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets whether the student has a client project.
    /// </summary>
    public bool HasClient { get; set; } = true;
    
    /// <summary>
    /// Gets or sets whether the student has a server project.
    /// </summary>
    public bool HasServer { get; set; } = true;
    
    /// <summary>
    /// Gets or sets the client project name.
    /// </summary>
    public string ClientProjectName { get; set; } = "Client";
    
    /// <summary>
    /// Gets or sets the server project name.
    /// </summary>
    public string ServerProjectName { get; set; } = "Server";
    
    /// <summary>
    /// Gets or sets the path to the client DLL file.
    /// </summary>
    public string? ClientDllPath { get; set; }
    
    /// <summary>
    /// Gets or sets the path to the server DLL file.
    /// </summary>
    public string? ServerDllPath { get; set; }
    
    /// <summary>
    /// Gets or sets the Docker image name for the code containers.
    /// </summary>
    public string CodeImageName { get; set; } = "mcr.microsoft.com/dotnet/sdk:8.0";
    
    /// <summary>
    /// Gets or sets the container name for the server.
    /// </summary>
    public string ServerContainerName { get; set; } = "ag-server";
    
    /// <summary>
    /// Gets or sets the container name for the client.
    /// </summary>
    public string ClientContainerName { get; set; } = "ag-client";
    
    /// <summary>
    /// Gets or sets the internal port for the server.
    /// </summary>
    public int ServerInternalPort { get; set; } = 5000;
    
    /// <summary>
    /// Gets or sets the host port for the server.
    /// </summary>
    public int ServerHostPort { get; set; } = 5000;
}
