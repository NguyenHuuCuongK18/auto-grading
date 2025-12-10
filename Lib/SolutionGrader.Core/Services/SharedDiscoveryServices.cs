using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using FileMaster.FileEngine;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Shared discovery services to eliminate code duplication between CLI and UI.
    /// Contains common logic for discovering students and test kits.
    /// 
    /// REFACTORING: This consolidates duplicate discovery code that was in:
    /// - CliDockerGradingService.DiscoverStudents()
    /// - StudentDiscoveryService.DiscoverStudents()
    /// - CliDockerGradingService.GetTestKitForPaper()
    /// - TestKitDiscoveryService.GetTestKitForPaper()
    /// 
    /// Benefits:
    /// - Single source of truth for discovery logic
    /// - Easier to maintain and update
    /// - Ensures consistency between CLI and UI
    /// - Reduces code duplication
    /// </summary>
    public static class SharedDiscoveryServices
    {
        #region Student Discovery

        /// <summary>
        /// Core student discovery logic shared by CLI and UI.
        /// Discovers students from the Submit folder structure: Submit/[PaperNo]/[StudentCode]/[QuestionNo]/solution
        /// </summary>
        /// <param name="submitPath">Path to Submit folder</param>
        /// <param name="serverProjectName">Expected server project name for DLL search</param>
        /// <param name="clientProjectName">Expected client project name for DLL search</param>
        /// <param name="paperFilter">Optional paper number filter (null = all papers)</param>
        /// <param name="studentFilter">Optional student code filter (null = all students)</param>
        /// <param name="logger">Optional action for logging messages</param>
        /// <returns>List of discovered students with their metadata</returns>
        public static List<DiscoveredStudent> DiscoverStudents(
            string submitPath,
            string serverProjectName,
            string clientProjectName,
            string? paperFilter = null,
            string? studentFilter = null,
            Action<string>? logger = null)
        {
            var students = new List<DiscoveredStudent>();

            if (!Directory.Exists(submitPath))
            {
                logger?.Invoke($"Submit folder not found: {submitPath}");
                return students;
            }

            // Get paper folders (numbered folders like "1", "2", etc.)
            var paperDirs = Directory.GetDirectories(submitPath)
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .OrderBy(d => int.Parse(Path.GetFileName(d)!));

            foreach (var paperDir in paperDirs)
            {
                var paperNo = Path.GetFileName(paperDir);
                if (!string.IsNullOrEmpty(paperFilter) && paperNo != paperFilter)
                    continue;

                // Get student folders (exclude files like "Sinh_viên.txt")
                var studentDirs = Directory.GetDirectories(paperDir)
                    .Where(d => !Path.GetFileName(d)!.Contains("."))
                    .OrderBy(d => d);

                foreach (var studentDir in studentDirs)
                {
                    var studentCode = Path.GetFileName(studentDir);
                    if (!string.IsNullOrEmpty(studentFilter) && studentCode != studentFilter)
                        continue;

                    // Check for solution structure: {studentCode}/1/ or {studentCode}/solution/1/
                    var questionFolder1 = Path.Combine(studentDir, "1");
                    var questionFolder2 = Path.Combine(studentDir, "solution", "1");
                    
                    string? solutionPath = null;
                    
                    // Check first structure: {studentCode}/1/
                    if (Directory.Exists(questionFolder1))
                    {
                        solutionPath = questionFolder1;
                        logger?.Invoke($"Found student: {studentCode} (Paper {paperNo}) - using structure /1");
                    }
                    // Check second structure: {studentCode}/solution/1/
                    else if (Directory.Exists(questionFolder2))
                    {
                        solutionPath = questionFolder2;
                        logger?.Invoke($"Found student: {studentCode} (Paper {paperNo}) - using structure /solution/1");
                    }
                    else
                    {
                        // Student doesn't have expected folder structure, but still add them
                        // The grading phase will handle this and log appropriate error message
                        solutionPath = studentDir; // Use student dir as fallback
                        logger?.Invoke($"Found student: {studentCode} (Paper {paperNo}) - WARNING: No question folder /1 or /solution/1 found, will handle during grading");
                    }

                    // Load ALL students - grading phase will validate files and log errors as needed
                    students.Add(new DiscoveredStudent
                    {
                        StudentCode = studentCode!,
                        PaperNo = paperNo!,
                        SolutionPath = solutionPath,
                        ServerDllPath = null, // Will be found during grading
                        ClientDllPath = null  // Will be found during grading
                    });
                }
            }

            return students;
        }

        /// <summary>
        /// Find a DLL file for a given project name.
        /// Optimized to avoid repeated recursive searches - uses direct path construction.
        /// </summary>
        /// <param name="solutionPath">Root path to search</param>
        /// <param name="projectName">Project name to match</param>
        /// <returns>Path to DLL if found, null otherwise</returns>
        private static string? FindDll(string questionFolderPath, string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
                return null;

            var searchPattern = $"{projectName}.dll";
            
            // OPTIMIZED: Instead of recursive AllDirectories search, check common paths directly
            // This is 10-100x faster than recursive searches for large solution folders
            
            // The actual solution folder is at: {studentCode}/1/solution/
            var solutionFolderPath = Path.Combine(questionFolderPath, "solution");
            
            // Common .NET project structure patterns to check (most to least common):
            var commonPaths = new[]
            {
                // .NET Core/5+/6+ Debug in solution folder
                Path.Combine(solutionFolderPath, projectName, "bin", "Debug"),
                // .NET Core/5+/6+ Release in solution folder
                Path.Combine(solutionFolderPath, projectName, "bin", "Release"),
                // Root bin Debug in solution folder
                Path.Combine(solutionFolderPath, "bin", "Debug"),
                // Root bin Release in solution folder
                Path.Combine(solutionFolderPath, "bin", "Release"),
                // Direct in solution folder root
                solutionFolderPath,
                // Legacy: also check question folder directly (for backward compatibility)
                Path.Combine(questionFolderPath, projectName, "bin", "Debug"),
                Path.Combine(questionFolderPath, projectName, "bin", "Release"),
                Path.Combine(questionFolderPath, "bin", "Debug"),
                Path.Combine(questionFolderPath, "bin", "Release"),
                questionFolderPath
            };
            
            foreach (var basePath in commonPaths)
            {
                if (!Directory.Exists(basePath))
                    continue;
                    
                // Check for target framework subfolders (net8.0, net7.0, net6.0, netcoreapp3.1, etc.)
                try
                {
                    var subdirs = Directory.GetDirectories(basePath);
                    foreach (var subdir in subdirs)
                    {
                        var subdirName = Path.GetFileName(subdir);
                        if (subdirName.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                        {
                            var dllPath = Path.Combine(subdir, searchPattern);
                            if (File.Exists(dllPath))
                                return dllPath;
                        }
                    }
                }
                catch { /* Ignore access errors */ }
                
                // Also check directly in the base path
                var directDllPath = Path.Combine(basePath, searchPattern);
                if (File.Exists(directDllPath))
                    return directDllPath;
            }
            
            // Fallback: Only if common paths fail, do a limited recursive search in bin folders
            // This handles unusual project structures but is slower
            // Search in solution folder first, then question folder
            foreach (var searchRoot in new[] { solutionFolderPath, questionFolderPath })
            {
                if (!Directory.Exists(searchRoot))
                    continue;
                    
                try
                {
                    var binFolders = Directory.GetDirectories(searchRoot, "bin", SearchOption.AllDirectories);
                    foreach (var binFolder in binFolders)
                    {
                        var dllFiles = Directory.GetFiles(binFolder, searchPattern, SearchOption.AllDirectories);
                        if (dllFiles.Length > 0)
                            return dllFiles[0];
                    }
                }
                catch { /* Ignore access errors */ }
            }

            return null;
        }

        #endregion

        #region Solution Extraction

        /// <summary>
        /// Extracts solution zip file if not already extracted.
        /// This supports lazy extraction - zip files are only extracted when needed (during grading).
        /// 
        /// Supports structure: {studentCode}/1/solution/
        /// - /1 is the question number folder
        /// - /solution is where the actual project files are (or where zip extracts to)
        /// </summary>
        /// <param name="questionFolderPath">Question folder path ({studentCode}/1)</param>
        /// <param name="logger">Optional action for logging messages</param>
        /// <returns>True if solution is ready (already exists or successfully extracted), false otherwise</returns>
        public static bool EnsureSolutionExtracted(string questionFolderPath, Action<string>? logger = null)
        {
            // The actual solution folder is inside the question folder: {studentCode}/1/solution
            var solutionFolderPath = Path.Combine(questionFolderPath, "solution");
            
            // Check if solution folder already exists with extracted files
            if (Directory.Exists(solutionFolderPath))
            {
                bool hasExtractedFiles = Directory.Exists(Path.Combine(solutionFolderPath, "bin")) ||
                                        Directory.GetFiles(solutionFolderPath, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0 ||
                                        Directory.GetFiles(solutionFolderPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0;
                
                if (hasExtractedFiles)
                {
                    logger?.Invoke($"Solution folder already exists with extracted files: {solutionFolderPath}");
                    return true;
                }
            }

            // Look for zip file in the question folder ({studentCode}/1/)
            string? zipPath = null;
            var zipFiles = Directory.GetFiles(questionFolderPath, "*.zip", SearchOption.TopDirectoryOnly);
            if (zipFiles.Length > 0)
            {
                zipPath = zipFiles[0];
                logger?.Invoke($"Found zip file in question folder: {Path.GetFileName(zipPath)}");
            }
            
            if (string.IsNullOrEmpty(zipPath))
            {
                logger?.Invoke($"No zip file found for extraction in {questionFolderPath}");
                return false;
            }

            // Extract zip file to the solution folder ({studentCode}/1/solution)
            // Create the solution folder if it doesn't exist
            try
            {
                if (!Directory.Exists(solutionFolderPath))
                {
                    Directory.CreateDirectory(solutionFolderPath);
                    logger?.Invoke($"Created solution folder: {solutionFolderPath}");
                }
                
                logger?.Invoke($"Extracting {Path.GetFileName(zipPath)} to {solutionFolderPath}");
                FileExtractor.ExtractDestination(zipPath, solutionFolderPath);
                logger?.Invoke($"Successfully extracted solution to solution folder");
                return true;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"Failed to extract solution: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Test Kit Discovery

        /// <summary>
        /// Core test kit discovery logic shared by CLI and UI.
        /// Finds the test kit folder for a given paper number.
        /// 
        /// Search order:
        /// 1. Check Mapping.xlsx for explicit mapping
        /// 2. Try direct paper number folder
        /// 3. Try Q{paperNo} folder
        /// </summary>
        /// <param name="testKitRoot">Root test kit folder path</param>
        /// <param name="paperNo">Paper number to find</param>
        /// <param name="logger">Optional action for logging messages</param>
        /// <returns>Path to test kit folder if found, null otherwise</returns>
        public static string? GetTestKitForPaper(string testKitRoot, string paperNo, Action<string>? logger = null)
        {
            if (!Directory.Exists(testKitRoot))
            {
                logger?.Invoke($"Test kit root folder not found: {testKitRoot}");
                return null;
            }

            // Try to find mapping file
            var mappingPath = Path.Combine(testKitRoot, "Mapping.xlsx");
            if (File.Exists(mappingPath))
            {
                try
                {
                    using var wb = new XLWorkbook(mappingPath);
                    var ws = wb.Worksheet(1);

                    foreach (var row in ws.RowsUsed().Skip(1)) // Skip header
                    {
                        var paperNoCell = row.Cell(1).GetValue<string>();
                        
                        // Parse paper number (could be string "1" or int 1)
                        if (string.IsNullOrEmpty(paperNoCell))
                            continue;
                            
                        // Support both string and numeric paper numbers
                        var paperMatch = paperNoCell.Trim() == paperNo.Trim();
                        
                        if (paperMatch)
                        {
                            // Column 2 is Question (Q1, Q2, etc.) - not used for folder lookup
                            // Column 3 is QuestionKit (Q11, Q12, Q21, Q22, etc.) - this is the folder name
                            var questionKitFolder = row.Cell(3).GetValue<string>();
                            
                            if (!string.IsNullOrEmpty(questionKitFolder))
                            {
                                var questionPath = Path.Combine(testKitRoot, questionKitFolder);
                                if (Directory.Exists(questionPath))
                                {
                                    logger?.Invoke($"Found test kit via Mapping.xlsx: {questionKitFolder} for paper {paperNo}");
                                    return questionPath;
                                }
                                else
                                {
                                    logger?.Invoke($"Mapping found {questionKitFolder} for paper {paperNo}, but folder doesn't exist");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"Error reading Mapping.xlsx: {ex.Message}");
                }
            }

            // Fallback: try direct folder matching
            var directPath = Path.Combine(testKitRoot, paperNo);
            if (Directory.Exists(directPath))
            {
                logger?.Invoke($"Found test kit via direct match: {paperNo}");
                return directPath;
            }

            // Try Q{paperNo} format
            var qPath = Path.Combine(testKitRoot, $"Q{paperNo}");
            if (Directory.Exists(qPath))
            {
                logger?.Invoke($"Found test kit via Q format: Q{paperNo}");
                return qPath;
            }

            logger?.Invoke($"No test kit found for paper {paperNo}");
            return null;
        }

        #endregion
    }

    /// <summary>
    /// Represents a discovered student with all metadata.
    /// Shared data structure for CLI and UI.
    /// </summary>
    public class DiscoveredStudent
    {
        public string StudentCode { get; set; } = "";
        public string PaperNo { get; set; } = "";
        public string SolutionPath { get; set; } = "";
        public string? ServerDllPath { get; set; }
        public string? ClientDllPath { get; set; }
    }
}
