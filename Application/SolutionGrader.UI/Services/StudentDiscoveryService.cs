using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileMaster.FileEngine;
using SolutionGrader.UI.Models;
using SolutionGrader.Core.Services;

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

            _logger.LogInfo($"[UI Discovery] SharedDiscoveryServices found {discoveredStudents.Count} students total");

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
                    Mark = 0,
                    // Copy warning message from discovery to StatusMessage for display in UI Message column
                    StatusMessage = discovered.WarningMessage
                };
                students.Add(student);
                
                if (!string.IsNullOrEmpty(discovered.WarningMessage))
                {
                    _logger.LogWarning($"[UI Discovery] Added student with warning: {student.StudentCode} (Paper {student.PaperNo}) - {discovered.WarningMessage}");
                }
                else
                {
                    _logger.LogDebug($"[UI Discovery] Added student: {student.StudentCode} (Paper {student.PaperNo}) - Status={student.Status}");
                }
            }

            _logger.LogInfo($"[UI Discovery] Converted to {students.Count} StudentSolution objects, all with Status=Not_Run");
            return students;
        }
    }
}
