using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DllMod;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Service for handling DLL modification as a fallback when appsettings.json is not available.
    /// 
    /// This service provides functionality to:
    /// 1. Check if appsettings.json exists in student submissions
    /// 2. Attempt to modify compiled DLL files to patch hardcoded IP addresses and ports
    /// 3. Log the results of modification attempts
    /// 
    /// Use case: When students hardcode connection settings in their code instead of using
    /// configuration files, this service can patch the compiled DLL to use the correct
    /// IP and port for the grading environment.
    /// 
    /// Example workflow:
    /// - Check if appsettings.json exists
    /// - If not found, locate the main DLL file
    /// - Try to patch common hardcoded values (localhost, 127.0.0.1) with Docker-appropriate values
    /// - Try common port numbers (3000, 4000, 5000, 8000, 8080, etc.)
    /// - Return success/failure status with details
    /// </summary>
    public class DllModificationService
    {
        /// <summary>
        /// Checks if appsettings.json exists in the specified directory.
        /// </summary>
        /// <param name="directoryPath">Path to the directory to check</param>
        /// <returns>True if appsettings.json exists, false otherwise</returns>
        public bool AppsettingsExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return false;
            }

            var appsettingsPath = Path.Combine(directoryPath, "appsettings.json");
            return File.Exists(appsettingsPath);
        }

        /// <summary>
        /// Finds the main DLL file in a directory.
        /// Looks for DLL files and tries to identify the main application DLL.
        /// </summary>
        /// <param name="directoryPath">Path to the directory to search</param>
        /// <param name="projectName">Optional project name hint to prioritize matching DLLs</param>
        /// <returns>Path to the main DLL file, or null if not found</returns>
        public string? FindMainDll(string directoryPath, string? projectName = null)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return null;
            }

            var dllFiles = Directory.GetFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly);
            
            if (dllFiles.Length == 0)
            {
                return null;
            }

            // If project name is provided, try to find a matching DLL
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var matchingDll = dllFiles.FirstOrDefault(dll => 
                    Path.GetFileNameWithoutExtension(dll).Equals(projectName, StringComparison.OrdinalIgnoreCase));
                
                if (matchingDll != null)
                {
                    return matchingDll;
                }
            }

            // Filter out common non-application DLLs
            var excludedPrefixes = new[] { "System.", "Microsoft.", "Newtonsoft.", "runtime." };
            var appDlls = dllFiles.Where(dll =>
            {
                var fileName = Path.GetFileName(dll);
                return !excludedPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }).ToArray();

            // Return the first application DLL found
            return appDlls.Length > 0 ? appDlls[0] : dllFiles[0];
        }

        /// <summary>
        /// Attempts to patch a DLL file for server use.
        /// Replaces hardcoded localhost references with the specified bind address (usually "0.0.0.0").
        /// Replaces hardcoded ports with the specified server port.
        /// </summary>
        /// <param name="dllPath">Path to the DLL file to patch</param>
        /// <param name="newPort">The port number to use</param>
        /// <param name="newIp">The IP address to bind to (default: "0.0.0.0" for all interfaces)</param>
        /// <returns>Result of the modification attempt</returns>
        public DllModificationResult PatchServerDll(string dllPath, int newPort, string? newIp = null)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                return new DllModificationResult
                {
                    Success = false,
                    Message = $"DLL file not found: {dllPath}"
                };
            }

            // Default to 0.0.0.0 for server (bind to all interfaces)
            var targetIp = newIp ?? "0.0.0.0";

            Console.WriteLine($"[DllMod] Attempting to patch server DLL: {Path.GetFileName(dllPath)}");
            Console.WriteLine($"[DllMod] Target configuration: IP={targetIp}, Port={newPort}");

            try
            {
                // For server, we want to bind to the specified IP to accept connections
                var result = DllModifier.TryPatchWithCommonValues(
                    dllPath,
                    newIp: targetIp,
                    newPort: newPort
                );

                Console.WriteLine($"[DllMod] Server patch result: {result.Message}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DllMod] Error patching server DLL: {ex.Message}");
                return new DllModificationResult
                {
                    Success = false,
                    Message = $"Error patching server DLL: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Attempts to patch a DLL file for client use.
        /// Replaces hardcoded localhost references with the specified target IP.
        /// Replaces hardcoded ports with the specified client port.
        /// </summary>
        /// <param name="dllPath">Path to the DLL file to patch</param>
        /// <param name="newPort">The port number to use</param>
        /// <param name="newIp">The target IP/hostname to connect to (default: "host.docker.internal" for legacy mode, or server container name for internal networking)</param>
        /// <param name="additionalOldIps">Additional old IP addresses/hostnames to search for and replace (e.g., old container names)</param>
        /// <returns>Result of the modification attempt</returns>
        public DllModificationResult PatchClientDll(string dllPath, int newPort, string? newIp = null, string[]? additionalOldIps = null)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                return new DllModificationResult
                {
                    Success = false,
                    Message = $"DLL file not found: {dllPath}"
                };
            }

            // Default to host.docker.internal for legacy port mapping mode
            var targetIp = newIp ?? "host.docker.internal";

            Console.WriteLine($"[DllMod] Attempting to patch client DLL: {Path.GetFileName(dllPath)}");
            Console.WriteLine($"[DllMod] Target configuration: IP={targetIp}, Port={newPort}");
            
            if (additionalOldIps != null && additionalOldIps.Length > 0)
            {
                Console.WriteLine($"[DllMod] Additional old IPs to search: {string.Join(", ", additionalOldIps)}");
            }

            try
            {
                // For client, patch to connect to the specified target IP
                // This can be host.docker.internal (legacy) or server container name (internal networking)
                var result = DllModifier.TryPatchWithCommonValues(
                    dllPath,
                    newIp: targetIp,
                    newPort: newPort,
                    oldIpsToTry: additionalOldIps  // Pass additional old IPs to search for
                );

                Console.WriteLine($"[DllMod] Client patch result: {result.Message}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DllMod] Error patching client DLL: {ex.Message}");
                return new DllModificationResult
                {
                    Success = false,
                    Message = $"Error patching client DLL: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Comprehensive check and patch operation for a project directory.
        /// 
        /// This method:
        /// 1. Checks if appsettings.json exists
        /// 2. If it doesn't exist, finds and patches the main DLL
        /// 3. Returns detailed results
        /// </summary>
        /// <param name="directoryPath">Path to the project directory</param>
        /// <param name="projectName">Project name hint for DLL identification</param>
        /// <param name="isServer">True if this is a server component, false if client</param>
        /// <param name="targetPort">The port to configure</param>
        /// <param name="targetIp">The IP address/hostname to configure (optional - uses defaults based on isServer flag)</param>
        /// <param name="additionalOldIps">Additional old IP addresses/hostnames to search for (for client patching)</param>
        /// <returns>Result of the check and patch operation</returns>
        public DllFallbackResult CheckAndPatchIfNeeded(
            string directoryPath,
            string? projectName,
            bool isServer,
            int targetPort,
            string? targetIp = null,
            string[]? additionalOldIps = null)
        {
            var result = new DllFallbackResult
            {
                DirectoryPath = directoryPath,
                ProjectName = projectName ?? "Unknown",
                IsServer = isServer,
                TargetPort = targetPort,
                TargetIp = targetIp
            };

            // Check if appsettings.json exists
            result.AppsettingsExists = AppsettingsExists(directoryPath);
            
            if (result.AppsettingsExists)
            {
                result.RequiresDllModification = false;
                result.Message = "appsettings.json found - DLL modification not needed";
                Console.WriteLine($"[DllMod] {result.Message}");
                return result;
            }

            Console.WriteLine($"[DllMod] appsettings.json not found in {directoryPath}");
            result.RequiresDllModification = true;

            // Find main DLL
            var dllPath = FindMainDll(directoryPath, projectName);
            if (dllPath == null)
            {
                result.Success = false;
                result.Message = "No DLL file found for modification";
                Console.WriteLine($"[DllMod] {result.Message}");
                return result;
            }

            result.DllPath = dllPath;
            Console.WriteLine($"[DllMod] Found DLL for modification: {Path.GetFileName(dllPath)}");

            // Patch the DLL
            var modResult = isServer 
                ? PatchServerDll(dllPath, targetPort, targetIp)
                : PatchClientDll(dllPath, targetPort, targetIp, additionalOldIps);

            result.Success = modResult.Success;
            result.IpReplacements = modResult.IpReplacements;
            result.PortReplacements = modResult.PortReplacements;
            result.Message = modResult.Message;

            return result;
        }
    }

    // DllFallbackResult class has been extracted to Domain/Models/DllFallbackResult.cs
}
