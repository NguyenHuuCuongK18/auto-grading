namespace SolutionGrader.Core.Abstractions;

public interface IEnvironmentResetService
{
    void ReplaceAppsettings(string? clientTemplate, string? serverTemplate, string? clientExe, string? serverExe);
    System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, System.Threading.CancellationToken ct);
    void ClearFolder(string path);
}
