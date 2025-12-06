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

                    // Find solution folder or zip file (LAZY EXTRACTION - don't extract during discovery)
                    var questionFolder = Path.Combine(studentDir, "1");
                    var solutionPath = Path.Combine(questionFolder, "solution");
                    
                    if (!Directory.Exists(questionFolder))
                    {
                        logger?.Invoke($"No question folder for {studentCode}");
                        continue;
                    }
                    
                    // Check if solution exists OR if there's a zip file we can extract later
                    bool hasSolution = Directory.Exists(solutionPath);
                    bool hasZip = false;
                    
                    if (!hasSolution)
                    {
                        var zipFiles = Directory.GetFiles(questionFolder, "*.zip");
                        hasZip = zipFiles.Length > 0;
                        
                        if (!hasZip)
                        {
                            logger?.Invoke($"No solution folder and no zip file for {studentCode}");
                            continue;
                        }
                        
                        // Store zip path for lazy extraction later (during grading)
                        logger?.Invoke($"Found zip file for {studentCode} - will extract when grading starts");
                    }

                    // Find server and client DLLs (only if solution is already extracted)
                    string? serverDllPath = null;
                    string? clientDllPath = null;
                    
                    if (Directory.Exists(solutionPath))
                    {
                        serverDllPath = FindDll(solutionPath, serverProjectName);
                        clientDllPath = FindDll(solutionPath, clientProjectName);
                        
                        // At least one component should exist (for now, just log if none found)
                        if (string.IsNullOrEmpty(serverDllPath) && string.IsNullOrEmpty(clientDllPath))
                        {
                            logger?.Invoke($"No DLLs found for {studentCode}");
                        }
                    }
                    // If solution not extracted yet, DLL paths will be null - that's OK
                    // They'll be found after extraction during grading

                    students.Add(new DiscoveredStudent
                    {
                        StudentCode = studentCode!,
                        PaperNo = paperNo!,
                        SolutionPath = solutionPath,
                        ServerDllPath = serverDllPath,
                        ClientDllPath = clientDllPath
                    });

                    logger?.Invoke($"Found student: {studentCode} (Paper {paperNo}, Server: {(serverDllPath != null ? "✓" : "✗")}, Client: {(clientDllPath != null ? "✓" : "✗")})");
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
        private static string? FindDll(string solutionPath, string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
                return null;

            var searchPattern = $"{projectName}.dll";
            
            // OPTIMIZED: Instead of recursive AllDirectories search, check common paths directly
            // This is 10-100x faster than recursive searches for large solution folders
            
            // Common .NET project structure patterns to check (most to least common):
            var commonPaths = new[]
            {
                // .NET Core/5+/6+ Debug
                Path.Combine(solutionPath, projectName, "bin", "Debug"),
                // .NET Core/5+/6+ Release
                Path.Combine(solutionPath, projectName, "bin", "Release"),
                // Root bin Debug
                Path.Combine(solutionPath, "bin", "Debug"),
                // Root bin Release  
                Path.Combine(solutionPath, "bin", "Release"),
                // Direct in solution root
                solutionPath
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
            try
            {
                var binFolders = Directory.GetDirectories(solutionPath, "bin", SearchOption.AllDirectories);
                foreach (var binFolder in binFolders)
                {
                    var dllFiles = Directory.GetFiles(binFolder, searchPattern, SearchOption.AllDirectories);
                    if (dllFiles.Length > 0)
                        return dllFiles[0];
                }
            }
            catch { /* Ignore access errors */ }

            return null;
        }

        #endregion

        #region Solution Extraction

        /// <summary>
        /// Extracts solution zip file if not already extracted.
        /// This supports lazy extraction - zip files are only extracted when needed (during grading).
        /// </summary>
        /// <param name="solutionPath">Expected solution folder path</param>
        /// <param name="logger">Optional action for logging messages</param>
        /// <returns>True if solution is ready (already exists or successfully extracted), false otherwise</returns>
        public static bool EnsureSolutionExtracted(string solutionPath, Action<string>? logger = null)
        {
            // If solution already exists, nothing to do
            if (Directory.Exists(solutionPath))
            {
                return true;
            }

            // Look for zip file in parent directory
            var questionFolder = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrEmpty(questionFolder) || !Directory.Exists(questionFolder))
            {
                logger?.Invoke($"Question folder not found: {questionFolder}");
                return false;
            }

            var zipFiles = Directory.GetFiles(questionFolder, "*.zip");
            if (zipFiles.Length == 0)
            {
                logger?.Invoke($"No zip file found in {questionFolder}");
                return false;
            }

            // Extract zip file to solution folder
            try
            {
                logger?.Invoke($"Extracting solution from {Path.GetFileName(zipFiles[0])} to {solutionPath}");
                FileExtractor.ExtractDestination(zipFiles[0], solutionPath);
                logger?.Invoke($"Successfully extracted solution");
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
