namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.IO;

public sealed class EnvironmentResetService : IEnvironmentResetService
{
    private readonly IFileService _files;
    public EnvironmentResetService(IFileService files) => _files = files;

    public void ReplaceAppsettings(string? clientTemplate, string? serverTemplate, string? clientExe, string? serverExe)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(serverTemplate) && !string.IsNullOrWhiteSpace(serverExe))
            {
                var dest = Path.Combine(Path.GetDirectoryName(serverExe)!, "appsettings.json");
                using var src = _files.OpenRead(serverTemplate);
                using var dst = _files.OpenWrite(dest, overwrite:true);
                src.CopyTo(dst);
            }
            if (!string.IsNullOrWhiteSpace(clientTemplate) && !string.IsNullOrWhiteSpace(clientExe))
            {
                var dest = Path.Combine(Path.GetDirectoryName(clientExe)!, "appsettings.json");
                using var src = _files.OpenRead(clientTemplate);
                using var dst = _files.OpenWrite(dest, overwrite:true);
                src.CopyTo(dst);
            }
        }
        catch (System.Exception ex) { throw new System.InvalidOperationException("Failed to replace appsettings.", ex); }
    }

    public async System.Threading.Tasks.Task RunDatabaseResetAsync(string? dbScriptPath, System.Threading.CancellationToken ct)
    {
        // Hook DB reset here if needed
        await System.Threading.Tasks.Task.CompletedTask;
    }

    public void ClearFolder(string path) => _files.ClearDirectory(path);
}
