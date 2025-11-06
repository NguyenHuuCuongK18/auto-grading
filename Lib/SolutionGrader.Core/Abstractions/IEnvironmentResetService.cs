namespace SolutionGrader.Core.Abstractions;

public interface IEnvironmentResetService
{
    void ReplaceAppsettings(string? clientTemplate, string? serverTemplate, string? clientExe, string? serverExe);
    System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, Domain.Models.DatabaseConfiguration? dbConfig, bool useDocker, System.Threading.CancellationToken ct);
    void ClearFolder(string path);
}
