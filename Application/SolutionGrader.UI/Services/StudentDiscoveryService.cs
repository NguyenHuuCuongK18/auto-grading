using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for discovering student submissions from the Submit folder.
    /// Implements the folder structure: Submit/[PaperNo]/[StudentCode]/[QuestionNo]/solution
    /// </summary>
    public class StudentDiscoveryService
    {
        private readonly ILoggingService _logger;

        public StudentDiscoveryService(ILoggingService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Discovers all student submissions from the submit folder.
        /// </summary>
        /// <param name="submitFolderPath">Path to the Submit folder</param>
        /// <param name="config">Grading configuration with project names for DLL lookup</param>
        /// <returns>List of discovered student solutions</returns>
        public List<StudentSolution> DiscoverStudents(string submitFolderPath, GradingConfiguration config)
        {
            var students = new List<StudentSolution>();

            if (!Directory.Exists(submitFolderPath))
            {
                _logger.LogError($"Submit folder not found: {submitFolderPath}");
                return students;
            }

            _logger.LogInfo($"Scanning submit folder: {submitFolderPath}");

            // Get all paper folders (1, 2, 3, etc.)
            var paperFolders = Directory.GetDirectories(submitFolderPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .OrderBy(d => int.Parse(Path.GetFileName(d)));

            foreach (var paperFolder in paperFolders)
            {
                string paperNo = Path.GetFileName(paperFolder);
                _logger.LogDebug($"Processing paper folder: {paperNo}");

                // Get all student folders within the paper folder
                var studentFolders = Directory.GetDirectories(paperFolder);

                foreach (var studentFolder in studentFolders)
                {
                    string studentCode = Path.GetFileName(studentFolder);
                    
                    // Skip system files like "Sinh_viên.txt"
                    if (studentCode.Contains(".")) continue;

                    var student = ProcessStudentFolder(studentFolder, paperNo, studentCode, config);
                    if (student != null)
                    {
                        students.Add(student);
                        _logger.LogDebug($"Found student: {studentCode} (Paper {paperNo})");
                    }
                }
            }

            _logger.LogInfo($"Discovered {students.Count} student submissions");
            return students;
        }

        /// <summary>
        /// Processes a student folder and creates a StudentSolution object.
        /// </summary>
        private StudentSolution? ProcessStudentFolder(string studentFolder, string paperNo, string studentCode, GradingConfiguration config)
        {
            try
            {
                // Look for question folder (1, 2, etc.) - we focus on question 1
                var questionFolderPath = Path.Combine(studentFolder, "1");
                if (!Directory.Exists(questionFolderPath))
                {
                    _logger.LogWarning($"Question folder '1' not found for student {studentCode}");
                    return null;
                }

                // Look for solution folder or try to extract zip
                var solutionPath = Path.Combine(questionFolderPath, "solution");
                
                if (!Directory.Exists(solutionPath))
                {
                    // Try to extract zip file
                    var zipFiles = Directory.GetFiles(questionFolderPath, "*.zip");
                    if (zipFiles.Length > 0)
                    {
                        try
                        {
                            System.IO.Compression.ZipFile.ExtractToDirectory(zipFiles[0], solutionPath);
                            _logger.LogInfo($"Extracted solution from zip for student {studentCode}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Failed to extract zip for student {studentCode}: {ex.Message}");
                            return null;
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Solution folder not found and no zip file for student {studentCode}");
                        return null;
                    }
                }

                var student = new StudentSolution
                {
                    StudentCode = studentCode,
                    PaperNo = paperNo,
                    SolutionPath = solutionPath,
                    Status = GradingStatus.Not_Run
                };

                // Find DLL paths based on project names
                if (config.HasClient)
                {
                    student.ClientDllPath = FindDllPath(solutionPath, config.ClientProjectName);
                    if (string.IsNullOrEmpty(student.ClientDllPath))
                    {
                        _logger.LogWarning($"Client DLL not found for student {studentCode} (looking for {config.ClientProjectName}.dll)");
                    }
                }

                if (config.HasServer)
                {
                    student.ServerDllPath = FindDllPath(solutionPath, config.ServerProjectName);
                    if (string.IsNullOrEmpty(student.ServerDllPath))
                    {
                        _logger.LogWarning($"Server DLL not found for student {studentCode} (looking for {config.ServerProjectName}.dll)");
                    }
                }

                return student;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing student folder {studentCode}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Searches for a DLL file with the given project name recursively within the solution folder.
        /// Search is case-insensitive to handle project name variations.
        /// </summary>
        /// <param name="solutionPath">Root path to search from</param>
        /// <param name="projectName">Project name (without .dll extension)</param>
        /// <returns>Full path to the DLL if found, null otherwise</returns>
        public string? FindDllPath(string solutionPath, string projectName)
        {
            if (string.IsNullOrEmpty(projectName) || !Directory.Exists(solutionPath))
                return null;

            string dllFileName = $"{projectName}.dll";
            _logger.LogDebug($"Searching for DLL: {dllFileName} in {solutionPath}");

            // Search recursively for the DLL file - case insensitive
            try
            {
                // First try exact match
                var dllFiles = Directory.GetFiles(solutionPath, dllFileName, SearchOption.AllDirectories);
                
                // If not found, try case-insensitive search
                if (dllFiles.Length == 0)
                {
                    _logger.LogDebug($"Exact match not found, trying case-insensitive search...");
                    // Get all DLL files and filter case-insensitively
                    var allDlls = Directory.GetFiles(solutionPath, "*.dll", SearchOption.AllDirectories);
                    dllFiles = allDlls.Where(f => 
                        Path.GetFileName(f).Equals(dllFileName, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(f).Equals($"{projectName.Replace(" ", "")}.dll", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                
                if (dllFiles.Length > 0)
                {
                    // Prefer the first one found (typically in a publish/output folder)
                    // Also prefer paths that contain common output folder names
                    // Exclude system DLLs
                    var filteredDlls = dllFiles
                        .Where(p => !Path.GetFileName(p).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !Path.GetFileName(p).StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !p.Contains("runtimes", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (filteredDlls.Length == 0)
                        filteredDlls = dllFiles; // Use original if all filtered out

                    var preferredPaths = filteredDlls
                        .OrderByDescending(p => p.Contains("publish", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(p => p.Contains("Debug", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(p => p.Length); // Prefer shorter paths (less nested)

                    var result = preferredPaths.First();
                    _logger.LogDebug($"Found DLL: {result}");
                    return result;
                }
                else
                {
                    _logger.LogDebug($"No DLL files matching '{dllFileName}' found in {solutionPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error searching for DLL {dllFileName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Gets the Docker-compatible path for a DLL relative to the solution root.
        /// </summary>
        /// <param name="solutionPath">Solution path on host</param>
        /// <param name="dllPath">Full DLL path on host</param>
        /// <param name="containerAppRoot">Root path in container (e.g., /apps)</param>
        /// <returns>Docker container path</returns>
        public string GetDockerPath(string solutionPath, string? dllPath, string containerAppRoot = "/apps")
        {
            if (string.IsNullOrEmpty(dllPath))
                return string.Empty;

            // Get the relative path from solution root
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(solutionPath)!, dllPath);
            
            // Convert to Unix path style for Docker
            var unixPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            
            return $"{containerAppRoot}/{unixPath}";
        }

        /// <summary>
        /// Filters students by paper number.
        /// </summary>
        public List<StudentSolution> FilterByPaper(List<StudentSolution> students, string paperNo)
        {
            if (string.IsNullOrEmpty(paperNo) || paperNo.ToLower() == "all")
                return students;

            return students.Where(s => s.PaperNo == paperNo).ToList();
        }

        /// <summary>
        /// Gets distinct paper numbers from the student list.
        /// </summary>
        public List<string> GetPaperNumbers(List<StudentSolution> students)
        {
            return students.Select(s => s.PaperNo).Distinct().OrderBy(p => int.Parse(p)).ToList();
        }
    }
}
