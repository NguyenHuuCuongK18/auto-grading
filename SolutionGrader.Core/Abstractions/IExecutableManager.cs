namespace SolutionGrader.Core.Abstractions;

public interface IExecutableManager
{
    bool IsServerRunning { get; }
    bool IsClientRunning { get; }

    void Init(string? clientPath, string? serverPath);
    void StartServer();
    void StartClient();

    System.Threading.Tasks.Task StopServerAsync();
    System.Threading.Tasks.Task StopClientAsync();
    System.Threading.Tasks.Task StopAllAsync();
}
