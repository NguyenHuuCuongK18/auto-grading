namespace SolutionGrader.Core.Services;

using SolutionGrader.Core.Abstractions;
using System.IO;
using System.Text.RegularExpressions;

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

    public Dictionary<string, Dictionary<string, (string? Q11, string? Q12)>> GetStudentSubmission(string solutionRoot)
    {
        if (!Directory.Exists(solutionRoot))
        {
            throw new DirectoryNotFoundException("No solution directory found!");
        }

        var solutions = new Dictionary<string, Dictionary<string, (string? Q11, string? Q12)>>();

        // Assuming the current dir is StudentSolution
        // Looping through all papaer
        foreach (var paperFolder in Directory.EnumerateDirectories(solutionRoot))
        {
            string paper = Path.GetFileName(paperFolder);

            if (!solutions.ContainsKey(paper))
                solutions[paper] = new Dictionary<string, (string?, string?)>();
            // Looping through all submission inside a paper
            foreach (var submission in Directory.EnumerateDirectories(paperFolder))
            {

                string studentCode = Path.GetFileName(submission);
                var dlls = Directory.GetFiles(submission, "*.dll", SearchOption.AllDirectories);

                string? q11PublishFolder = null;
                string? q12PublishFolder = null;
                foreach (var dll in dlls)
                {
                    // Get the publish folder
                    if (dll.EndsWith("Q11.dll"))
                    {
                        q11PublishFolder = dll.Replace("\\Q11.dll", "");
                    }
                    if (dll.EndsWith("Q12.dll"))
                    {
                        q12PublishFolder = dll.Replace("\\Q12.dll", "");
                    }

                    // Temp logic for solution using ProjectName.dll
                    if (dll.EndsWith("Project11.dll"))
                    {
                        q11PublishFolder = dll.Replace("\\Project11.dll", "");
                    }
                    if (dll.EndsWith("Project12.dll"))
                    {
                        q12PublishFolder = dll.Replace("\\Project12.dll", "");
                    }
                }

                if ( string.IsNullOrEmpty(q11PublishFolder) && string.IsNullOrEmpty(q12PublishFolder))
                {
                    continue;
                }

                solutions[paper][studentCode] = (q11PublishFolder, q12PublishFolder);
            }
        }

        return solutions;
    }
}
