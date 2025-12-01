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
        /// Finds the path to the published DLL folder for a given project.
        /// 
        /// The actual structure is:
        /// Submit/{PaperNo}/{StudentCode}/{QuestionNo}/solution/
        ///   Q11/                              (project folder)
        ///     Q11_{studentcode}/              (published output folder with DLLs)
        ///       Project11.dll                 (the actual DLL)
        ///     Program.cs                      (source file)
        ///   Q12/
        ///     Q12_{studentcode}/
        ///       Project12.dll
        ///     Program.cs
        /// 
        /// The user provides the DLL name (e.g., "Project12"), and we need to find
        /// the folder containing that DLL by searching recursively.
        /// </summary>
        private string? FindProjectPath(string solutionPath, string projectName, bool required)
        {
            if (string.IsNullOrEmpty(projectName))
                return null;

            // Expected DLL name is projectName.dll (e.g., Project12.dll)
            var dllName = $"{projectName}.dll";

            // Search recursively for the DLL file
            var dllFiles = Directory.GetFiles(solutionPath, dllName, SearchOption.AllDirectories);

            if (dllFiles.Length > 0)
            {
                // Return the directory containing the first match
                var foundPath = Path.GetDirectoryName(dllFiles[0]);
                _logger.LogDebug($"Found {dllName} at: {foundPath}");
                return foundPath;
            }

            // Try alternative names commonly used for client/server projects
            // These are fallback patterns based on the project naming conventions
            // The primary search uses the user-specified projectName
            string[] alternativeNames = projectName.Contains("11") 
                ? new[] { "Q11.dll", "Project11.dll", "Server.dll" }
                : projectName.Contains("12")
                    ? new[] { "Q12.dll", "Project12.dll", "Client.dll" }
                    : new[] { "Q11.dll", "Q12.dll", "Project11.dll", "Project12.dll", "Client.dll", "Server.dll" };
                    
            foreach (var altName in alternativeNames)
            {
                if (altName == dllName) continue; // Already tried

                var altFiles = Directory.GetFiles(solutionPath, altName, SearchOption.AllDirectories);
                if (altFiles.Length > 0)
                {
                    var foundPath = Path.GetDirectoryName(altFiles[0]);
                    _logger.LogDebug($"Found alternative {altName} at: {foundPath}");
                    return foundPath;
                }
            }

            // If not required and not found, that's okay
            if (!required)
                return null;

            _logger.LogWarning($"Could not find {dllName} in {solutionPath}");
            return null;
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
