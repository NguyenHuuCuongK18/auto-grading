using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileMaster.FileEngine;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for discovering student submissions from the Submit folder.
    /// 
    /// REFACTORED: Now uses SharedDiscoveryServices to eliminate code duplication
    /// with CliDockerGradingService. The core discovery logic is centralized.
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
        /// Uses shared discovery logic to ensure consistency with CLI.
        /// </summary>
        /// <param name="submitFolderPath">Path to the Submit folder</param>
        /// <param name="config">Grading configuration with project names for DLL lookup</param>
        /// <returns>List of discovered student solutions</returns>
        public List<StudentSolution> DiscoverStudents(string submitFolderPath, GradingConfiguration config)
        {
            _logger.LogInfo($"Scanning submit folder: {submitFolderPath}");

            // Use shared discovery service to eliminate code duplication
            var discoveredStudents = SharedDiscoveryServices.DiscoverStudents(
                submitFolderPath,
                config.ServerProjectName,
                config.ClientProjectName,
                paperFilter: null,
                studentFilter: null,
                logger: msg => _logger.LogDebug(msg));

            // Convert to UI-specific StudentSolution objects
            var students = new List<StudentSolution>();
            foreach (var discovered in discoveredStudents)
            {
                var student = new StudentSolution
                {
                    StudentCode = discovered.StudentCode,
                    PaperNo = discovered.PaperNo,
                    SolutionPath = discovered.SolutionPath,
                    Status = GradingStatus.Not_Run,
                    ProgressPercent = 0,
                    Mark = 0
                };
                students.Add(student);
            }

            _logger.LogInfo($"Discovered {students.Count} student submissions");
            return students;
        }
    }
}
