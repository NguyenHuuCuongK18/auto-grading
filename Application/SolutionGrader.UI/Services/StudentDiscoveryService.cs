using System.IO;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services;

/// <summary>
/// Service for discovering student submissions in the Submit folder.
/// </summary>
public class StudentDiscoveryService
{
    private readonly ILoggingService _logger;
    
    public StudentDiscoveryService(ILoggingService logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Discovers all student submissions in the Submit folder.
    /// </summary>
    /// <param name="submitFolderPath">Path to the Submit folder.</param>
    /// <param name="config">Grading configuration.</param>
    /// <returns>List of discovered student solutions.</returns>
    public List<StudentSolution> DiscoverStudents(string submitFolderPath, GradingConfiguration config)
    {
        var students = new List<StudentSolution>();
        
        if (!Directory.Exists(submitFolderPath))
        {
            _logger.LogWarning($"Submit folder not found: {submitFolderPath}");
            return students;
        }
        
        // Structure: Submit/{PaperNo}/{StudentCode}/1/solution
        foreach (var paperDir in Directory.GetDirectories(submitFolderPath))
        {
            var paperNo = Path.GetFileName(paperDir);
            _logger.LogInfo($"Scanning paper: {paperNo}");
            
            foreach (var studentDir in Directory.GetDirectories(paperDir))
            {
                var studentCode = Path.GetFileName(studentDir);
                
                // Look for solution folder (Submit/{PaperNo}/{StudentCode}/1/solution)
                var solutionPath = Path.Combine(studentDir, "1", "solution");
                
                if (!Directory.Exists(solutionPath))
                {
                    _logger.LogWarning($"Solution folder not found for {studentCode}: {solutionPath}");
                    continue;
                }
                
                var student = new StudentSolution
                {
                    StudentCode = studentCode,
                    PaperNo = paperNo,
                    SolutionPath = solutionPath
                };
                
                // Find client and server paths based on configuration
                if (config.HasClient)
                {
                    student.ClientPath = FindProjectPath(solutionPath, config.ClientProjectName, "Client");
                }
                
                if (config.HasServer)
                {
                    student.ServerPath = FindProjectPath(solutionPath, config.ServerProjectName, "Server");
                }
                
                students.Add(student);
                _logger.LogInfo($"Found student: {studentCode} (Paper {paperNo})");
            }
        }
        
        return students;
    }
    
    /// <summary>
    /// Finds the path to a project's DLL or executable.
    /// </summary>
    private string? FindProjectPath(string solutionPath, string projectName, string defaultFolderName)
    {
        // Try several naming conventions:
        // 1. {projectName}/{projectName}_{studentCode}/{projectName}.dll
        // 2. Q11/Q11_{studentCode}/Project11.dll
        // 3. Q12/Q12_{studentCode}/Project12.dll
        
        var searchPatterns = new[]
        {
            $"{projectName}/**/{projectName}.dll",
            $"{projectName}/**/{projectName}.exe",
            "Q11/**/*.dll",
            "Q12/**/*.dll",
            "Project11/**/*.dll",
            "Project12/**/*.dll"
        };
        
        // Search for DLLs in known locations
        foreach (var subDir in Directory.GetDirectories(solutionPath, "*", SearchOption.TopDirectoryOnly))
        {
            var subDirName = Path.GetFileName(subDir);
            
            // Check for project-named directories (Q11, Q12, Project11, etc.)
            if (subDirName.Contains(projectName, StringComparison.OrdinalIgnoreCase) ||
                (defaultFolderName == "Server" && (subDirName.Equals("Q11", StringComparison.OrdinalIgnoreCase) || subDirName.Contains("Project11", StringComparison.OrdinalIgnoreCase))) ||
                (defaultFolderName == "Client" && (subDirName.Equals("Q12", StringComparison.OrdinalIgnoreCase) || subDirName.Contains("Project12", StringComparison.OrdinalIgnoreCase))))
            {
                // Look for published folder with DLL
                foreach (var publishDir in Directory.GetDirectories(subDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var dllFiles = Directory.GetFiles(publishDir, "*.dll", SearchOption.TopDirectoryOnly);
                    var mainDll = dllFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).Contains(projectName, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(f).Equals("Project11", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(f).Equals("Project12", StringComparison.OrdinalIgnoreCase));
                    
                    if (mainDll != null)
                        return mainDll;
                }
            }
        }
        
        return null;
    }
}
