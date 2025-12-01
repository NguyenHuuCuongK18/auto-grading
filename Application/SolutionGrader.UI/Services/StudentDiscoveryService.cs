using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service for discovering student submissions in the Submit folder.
    /// 
    /// Expected folder structure:
    /// Submit/
    ///   {PaperNo}/
    ///     {StudentCode}/
    ///       {QuestionNo}/
    ///         solution/
    ///           {ProjectName}/
    ///             *.dll (published files)
    /// 
    /// Example:
    /// Submit/1/cuongnhhe186494/1/solution/Q11/Q11.dll
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
        /// <param name="config">Grading configuration with project name settings.</param>
        /// <returns>List of discovered student solutions.</returns>
        public List<StudentSolution> DiscoverStudents(string submitFolderPath, GradingConfiguration config)
        {
            var students = new List<StudentSolution>();

            if (!Directory.Exists(submitFolderPath))
            {
                _logger.LogWarning($"Submit folder does not exist: {submitFolderPath}");
                return students;
            }

            // Get all paper folders (numbered folders like "1", "2")
            var paperFolders = Directory.GetDirectories(submitFolderPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .OrderBy(d => int.Parse(Path.GetFileName(d)));

            foreach (var paperFolder in paperFolders)
            {
                var paperNo = Path.GetFileName(paperFolder);
                _logger.LogDebug($"Scanning paper folder: {paperNo}");

                // Get all student folders within this paper
                var studentFolders = Directory.GetDirectories(paperFolder);

                foreach (var studentFolder in studentFolders)
                {
                    var studentCode = Path.GetFileName(studentFolder);
                    
                    // Skip special files like "Sinh_viên.txt"
                    if (studentCode.StartsWith(".") || studentCode.Contains("_viên"))
                        continue;

                    // Get question folders (numbered folders like "1", "2")
                    var questionFolders = Directory.GetDirectories(studentFolder)
                        .Where(d => int.TryParse(Path.GetFileName(d), out _))
                        .OrderBy(d => int.Parse(Path.GetFileName(d)));

                    foreach (var questionFolder in questionFolders)
                    {
                        var questionNo = Path.GetFileName(questionFolder);
                        var solutionPath = Path.Combine(questionFolder, "solution");

                        if (!Directory.Exists(solutionPath))
                        {
                            _logger.LogDebug($"No solution folder for {studentCode} Q{questionNo}");
                            continue;
                        }

                        var student = new StudentSolution
                        {
                            StudentCode = studentCode,
                            PaperNo = paperNo,
                            QuestionNo = questionNo,
                            SolutionPath = solutionPath
                        };

                        // Find client and server paths
                        student.ClientPath = FindProjectPath(solutionPath, config.ClientProjectName, config.HasClient);
                        student.ServerPath = FindProjectPath(solutionPath, config.ServerProjectName, config.HasServer);

                        // Validate that required components exist
                        bool hasRequiredComponents = true;
                        var missingComponents = new List<string>();

                        if (config.HasClient && string.IsNullOrEmpty(student.ClientPath))
                        {
                            missingComponents.Add($"Client ({config.ClientProjectName})");
                            hasRequiredComponents = false;
                        }

                        if (config.HasServer && string.IsNullOrEmpty(student.ServerPath))
                        {
                            missingComponents.Add($"Server ({config.ServerProjectName})");
                            hasRequiredComponents = false;
                        }

                        if (!hasRequiredComponents)
                        {
                            student.StatusMessage = $"Missing: {string.Join(", ", missingComponents)}";
                            _logger.LogWarning($"Student {studentCode}: {student.StatusMessage}");
                        }

                        students.Add(student);
                        _logger.LogDebug($"Discovered: {studentCode} Paper {paperNo} Q{questionNo}");
                    }
                }
            }

            _logger.LogInfo($"Discovered {students.Count} student submissions");
            return students;
        }

        /// <summary>
        /// Finds the path to a project folder by name.
        /// Searches for folders containing the project name and verifies DLL exists.
        /// </summary>
        private string? FindProjectPath(string solutionPath, string projectName, bool required)
        {
            if (string.IsNullOrEmpty(projectName))
                return null;

            // Look for folder matching project name (case-insensitive)
            var projectFolders = Directory.GetDirectories(solutionPath)
                .Where(d => Path.GetFileName(d).Equals(projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (projectFolders.Count == 0)
            {
                // Try partial match (e.g., Q11 for project named Project11)
                projectFolders = Directory.GetDirectories(solutionPath)
                    .Where(d => Path.GetFileName(d).Contains(projectName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var folder in projectFolders)
            {
                // Check if DLL file exists
                var dllName = $"{Path.GetFileName(folder)}.dll";
                var dllPath = Path.Combine(folder, dllName);

                if (File.Exists(dllPath))
                {
                    return folder;
                }

                // Try with project name instead of folder name
                dllPath = Path.Combine(folder, $"{projectName}.dll");
                if (File.Exists(dllPath))
                {
                    return folder;
                }
            }

            // If not required and not found, that's okay
            if (!required)
                return null;

            // Return first folder even without DLL (might be source code)
            return projectFolders.FirstOrDefault();
        }

        /// <summary>
        /// Gets all unique paper numbers from the Submit folder.
        /// </summary>
        public List<string> GetPaperNumbers(string submitFolderPath)
        {
            if (!Directory.Exists(submitFolderPath))
                return new List<string>();

            return Directory.GetDirectories(submitFolderPath)
                .Select(d => Path.GetFileName(d))
                .Where(n => int.TryParse(n, out _))
                .OrderBy(n => int.Parse(n))
                .ToList();
        }

        /// <summary>
        /// Gets all students for a specific paper.
        /// </summary>
        public List<StudentSolution> GetStudentsForPaper(string submitFolderPath, string paperNo, GradingConfiguration config)
        {
            var allStudents = DiscoverStudents(submitFolderPath, config);
            return allStudents.Where(s => s.PaperNo == paperNo).ToList();
        }
    }
}
