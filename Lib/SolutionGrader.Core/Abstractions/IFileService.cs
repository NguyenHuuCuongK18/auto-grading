namespace SolutionGrader.Core.Abstractions;

public interface IFileService
{
    System.IO.Stream OpenRead(string path);
    System.IO.Stream OpenWrite(string path, bool overwrite = true);
    bool Exists(string path);
    System.Collections.Generic.IEnumerable<string> EnumerateFiles(string folder, string searchPattern, bool recursive = false);
    void EnsureDirectory(string path);
    void ClearDirectory(string path);
}
