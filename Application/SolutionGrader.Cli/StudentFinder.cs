using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class StudentFinder
{
    // Patterns:
    // Reference single-question layout: [solution?]/Q1_<namecode>/Q1.(dll|exe)
    // Dual submission layout (no reference executables):
    //   [solution?]/Q11_<namecode>/Q11.(dll|exe)  => Server
    //   [solution?]/Q12_<namecode>/Q12.(dll|exe)  => Client
    // Student code format: 2 letters + 6 digits

    private static readonly Regex CodeRegex = new("^[A-Za-z]{2}[0-9]{6}$", RegexOptions.Compiled);

    public static IEnumerable<StudentSubmission> FindSubmissions(string submissionRoot)
    {
        if (!Directory.Exists(submissionRoot)) yield break;

        // Support both cases:
        // 1) submissionRoot is the parent containing a StudentSolution folder
        // 2) submissionRoot is the StudentSolution folder itself
        IEnumerable<string> paperRoots;
        var selfName = Path.GetFileName(Path.GetFullPath(submissionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (selfName.Equals("StudentSolution", StringComparison.OrdinalIgnoreCase))
        {
            // Directly enumerate paper folders under the provided StudentSolution path
            paperRoots = Directory.EnumerateDirectories(submissionRoot);
        }
        else
        {
            // Find StudentSolution folder(s) at this level and enumerate their children
            paperRoots = Directory.EnumerateDirectories(submissionRoot, "StudentSolution", SearchOption.TopDirectoryOnly)
                                  .SelectMany(p => Directory.EnumerateDirectories(p));
        }

        foreach (var paperDir in paperRoots)
        {
            foreach (var nameCodeDir in Directory.EnumerateDirectories(paperDir))
            {
                var leaf = Path.GetFileName(nameCodeDir);
                var code = ExtractStudentCode(leaf);
                //if (string.IsNullOrEmpty(code)) continue;

                foreach (var inner in Directory.EnumerateDirectories(nameCodeDir))
                {
                    // Allow optional 'solution' folder inside attempt folder
                    var baseInner = Directory.Exists(Path.Combine(inner, "solution"))
                        ? Path.Combine(inner, "solution")
                        : inner;

                    // Single-question layout (reference mode)
                    var q1DirName = $"Q1_{leaf.ToLowerInvariant()}";
                    var q1Dir = Path.Combine(baseInner, q1DirName);
                    string? singleExe = GetExe(q1Dir, "Q1");

                    // Dual layout (no reference, student provides both)
                    var q11DirName = $"Q11_{leaf.ToLowerInvariant()}"; // server
                    var q12DirName = $"Q12_{leaf.ToLowerInvariant()}"; // client
                    var q11Dir = Path.Combine(baseInner, q11DirName);
                    var q12Dir = Path.Combine(baseInner, q12DirName);
                    string? serverExe = GetExe(q11Dir, "Q11");
                    string? clientExe = GetExe(q12Dir, "Q12");

                    if (singleExe != null)
                    {
                        yield return new StudentSubmission
                        {
                            StudentId = code!,
                            StudentFolder = nameCodeDir,
                            SingleCodePath = singleExe,
                            SingleQuestionFolder = q1Dir,
                            Mode = SubmissionMode.Single
                        };
                    }
                    else if (serverExe != null && clientExe != null)
                    {
                        yield return new StudentSubmission
                        {
                            StudentId = code!,
                            StudentFolder = nameCodeDir,
                            ServerCodePath = serverExe,
                            ClientCodePath = clientExe,
                            ServerQuestionFolder = q11Dir,
                            ClientQuestionFolder = q12Dir,
                            Mode = SubmissionMode.Dual
                        };
                    }
                }
            }
        }
    }

    private static string? GetExe(string folder, string baseName)
    {
        if (!Directory.Exists(folder)) return null;
        var dll = Path.Combine(folder, baseName + ".dll");
        var exe = Path.Combine(folder, baseName + ".exe");
        if (File.Exists(dll)) return dll;
        if (File.Exists(exe)) return exe;
        return null;
    }

    private static string? ExtractStudentCode(string nameCode)
    {
        var match = Regex.Match(nameCode, "([A-Za-z]{2}[0-9]{6})$");
        if (!match.Success) return null;
        return match.Groups[1].Value.ToUpperInvariant();
    }
}

internal enum SubmissionMode { Single, Dual }

internal sealed class StudentSubmission
{
    public required string StudentId { get; init; }
    public required string StudentFolder { get; init; }
    public SubmissionMode Mode { get; init; }

    // Single layout (reference or single executable)
    public string? SingleCodePath { get; init; }
    public string? SingleQuestionFolder { get; init; }

    // Dual layout (separate server/client)
    public string? ServerCodePath { get; init; }
    public string? ClientCodePath { get; init; }
    public string? ServerQuestionFolder { get; init; }
    public string? ClientQuestionFolder { get; init; }
}
