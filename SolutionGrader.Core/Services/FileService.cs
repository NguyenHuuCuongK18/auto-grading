namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.IO;

public sealed class FileService : IFileService
{
    public Stream OpenRead(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream OpenWrite(string path, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) EnsureDirectory(dir!);
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        return File.Open(path, mode, FileAccess.ReadWrite, FileShare.None);
    }

    public bool Exists(string path) => File.Exists(path);

    public System.Collections.Generic.IEnumerable<string> EnumerateFiles(string folder, string pattern, bool recursive = false) =>
        Directory.EnumerateFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public void EnsureDirectory(string path) { if (!Directory.Exists(path)) Directory.CreateDirectory(path); }

    public void ClearDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.GetFiles(path)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(path)) Directory.Delete(d, true);
    }
}
