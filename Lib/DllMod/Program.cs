using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DllMod
{
    /// <summary>
    /// Dll Modifier Library
    /// 
    /// Provides functionality to patch IP addresses and ports directly in compiled .NET DLL files.
    /// This is used as a fallback when appsettings.json is not available in student submissions.
    /// 
    /// The library scans through all IL instructions in the assembly and replaces:
    /// - String literals containing old IP addresses or "localhost" 
    /// - Integer constants matching the old port number
    /// 
    /// Common use case: Student submissions that hardcode connection settings instead of using appsettings.json
    /// 
    /// - Auth: NhatNM -
    /// </summary>
    public static class DllModifier
    {
        /// <summary>
        /// Default list of common ports that students might use for server connections.
        /// These are tried in order when attempting to detect and replace hardcoded ports.
        /// </summary>
        public static readonly int[] CommonPorts = { 3000, 4000, 5000, 8000, 8080, 5001, 5002, 7000, 7001, 9000 };
        
        /// <summary>
        /// Default list of common IP addresses/hostnames that students might hardcode.
        /// </summary>
        public static readonly string[] CommonIpAddresses = { "localhost", "127.0.0.1", "0.0.0.0" };

        /// <summary>
        /// Patches a DLL file by replacing old IP address and port with new values.
        /// Creates a backup of the original file with .backup extension.
        /// </summary>
        /// <param name="dllPath">Full path to the DLL file to modify</param>
        /// <param name="oldIp">Old IP address or hostname to replace (e.g., "localhost", "127.0.0.1")</param>
        /// <param name="newIp">New IP address to use (e.g., "host.docker.internal", "0.0.0.0")</param>
        /// <param name="oldPort">Old port number to replace</param>
        /// <param name="newPort">New port number to use</param>
        /// <returns>Tuple containing the number of IP replacements and port replacements made</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are null or empty</exception>
        /// <exception cref="System.IO.FileNotFoundException">Thrown when the DLL file doesn't exist</exception>
        public static (int IpReplacements, int PortReplacements) PatchDll(
            string dllPath, 
            string oldIp, 
            string newIp, 
            int oldPort, 
            int newPort)
        {
            if (string.IsNullOrWhiteSpace(dllPath))
                throw new ArgumentException("DLL path cannot be null or empty", nameof(dllPath));
            if (string.IsNullOrWhiteSpace(oldIp))
                throw new ArgumentException("Old IP cannot be null or empty", nameof(oldIp));
            if (string.IsNullOrWhiteSpace(newIp))
                throw new ArgumentException("New IP cannot be null or empty", nameof(newIp));
            if (!System.IO.File.Exists(dllPath))
                throw new System.IO.FileNotFoundException($"DLL file not found: {dllPath}");

            return AsmHelper.Patch((dllPath, oldIp, newIp, oldPort, newPort));
        }

        /// <summary>
        /// Attempts to patch a DLL with multiple common port values.
        /// Useful when you don't know which port the student hardcoded.
        /// Tries all common ports in sequence until replacements are found.
        /// </summary>
        /// <param name="dllPath">Full path to the DLL file to modify</param>
        /// <param name="oldIp">Old IP address or hostname to replace</param>
        /// <param name="newIp">New IP address to use</param>
        /// <param name="newPort">New port number to use</param>
        /// <param name="portsToTry">Optional array of ports to try. If null, uses CommonPorts</param>
        /// <returns>Result object containing success status and replacement counts</returns>
        public static DllModificationResult TryPatchWithCommonPorts(
            string dllPath,
            string oldIp,
            string newIp,
            int newPort,
            int[]? portsToTry = null)
        {
            var ports = portsToTry ?? CommonPorts;
            var totalIpReplacements = 0;
            var totalPortReplacements = 0;
            var attemptedPorts = new List<int>();

            foreach (var oldPort in ports)
            {
                attemptedPorts.Add(oldPort);
                try
                {
                    var (ipCount, portCount) = PatchDll(dllPath, oldIp, newIp, oldPort, newPort);
                    totalIpReplacements += ipCount;
                    totalPortReplacements += portCount;
                    
                    // If we found and replaced ports, we can stop trying
                    if (portCount > 0)
                    {
                        return new DllModificationResult
                        {
                            Success = true,
                            IpReplacements = totalIpReplacements,
                            PortReplacements = totalPortReplacements,
                            AttemptedPorts = attemptedPorts,
                            SuccessfulPort = oldPort,
                            Message = $"Successfully patched DLL: {ipCount} IP replacements, {portCount} port replacements (found port {oldPort})"
                        };
                    }
                }
                catch (Exception ex)
                {
                    // Continue trying other ports even if one fails
                    Console.WriteLine($"[DllMod] Warning: Failed to try port {oldPort}: {ex.Message}");
                }
            }

            // Even if no ports were found, IP replacements might have been made
            return new DllModificationResult
            {
                Success = totalIpReplacements > 0,
                IpReplacements = totalIpReplacements,
                PortReplacements = totalPortReplacements,
                AttemptedPorts = attemptedPorts,
                Message = totalIpReplacements > 0 
                    ? $"Partially successful: {totalIpReplacements} IP replacements made, but no matching ports found in {string.Join(", ", attemptedPorts)}"
                    : $"No replacements made. Tried ports: {string.Join(", ", attemptedPorts)}"
            };
        }

        /// <summary>
        /// Attempts to patch a DLL trying multiple IP addresses and ports.
        /// Most comprehensive approach when you're uncertain what the student hardcoded.
        /// </summary>
        /// <param name="dllPath">Full path to the DLL file to modify</param>
        /// <param name="newIp">New IP address to use</param>
        /// <param name="newPort">New port number to use</param>
        /// <param name="oldIpsToTry">Optional array of old IPs to try. If null, uses CommonIpAddresses</param>
        /// <param name="portsToTry">Optional array of ports to try. If null, uses CommonPorts</param>
        /// <returns>Result object containing success status and replacement counts</returns>
        public static DllModificationResult TryPatchWithCommonValues(
            string dllPath,
            string newIp,
            int newPort,
            string[]? oldIpsToTry = null,
            int[]? portsToTry = null)
        {
            var ipsToTry = oldIpsToTry ?? CommonIpAddresses;
            var ports = portsToTry ?? CommonPorts;
            
            var totalIpReplacements = 0;
            var totalPortReplacements = 0;
            var attemptedCombinations = new List<string>();
            int? successfulPort = null;

            // CRITICAL FIX: Try ALL IP addresses, not just stop after first success
            // Student code may have MULTIPLE hardcoded IPs:
            // - Server might use "0.0.0.0" for binding
            // - Client might use "127.0.0.1" or "localhost" for connecting
            // We need to replace ALL of them for client-server communication to work
            foreach (var oldIp in ipsToTry)
            {
                var result = TryPatchWithCommonPorts(dllPath, oldIp, newIp, newPort, ports);
                totalIpReplacements += result.IpReplacements;
                totalPortReplacements += result.PortReplacements;
                attemptedCombinations.Add($"{oldIp} with ports: {string.Join(",", result.AttemptedPorts)}");
                
                // Track the first successful port found
                if (result.Success && result.SuccessfulPort.HasValue && !successfulPort.HasValue)
                {
                    successfulPort = result.SuccessfulPort;
                }
                
                // IMPORTANT: Continue trying other IPs even if we found some replacements
                // Don't return early - we need to catch all hardcoded IPs
            }

            return new DllModificationResult
            {
                Success = totalIpReplacements > 0 || totalPortReplacements > 0,
                IpReplacements = totalIpReplacements,
                PortReplacements = totalPortReplacements,
                AttemptedPorts = ports.ToList(),
                SuccessfulPort = successfulPort,
                Message = (totalIpReplacements > 0 || totalPortReplacements > 0)
                    ? $"Successfully patched DLL: {totalIpReplacements} IP replacements, {totalPortReplacements} port replacements"
                    : $"No replacements made. Tried combinations: {string.Join("; ", attemptedCombinations)}"
            };
        }
    }

    /// <summary>
    /// Result of a DLL modification operation.
    /// Contains detailed information about what was changed and whether the operation succeeded.
    /// </summary>
    public class DllModificationResult
    {
        /// <summary>
        /// Indicates whether the modification was successful.
        /// True if at least some replacements were made.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Number of IP address string literals that were replaced in the DLL.
        /// </summary>
        public int IpReplacements { get; set; }

        /// <summary>
        /// Number of port integer constants that were replaced in the DLL.
        /// </summary>
        public int PortReplacements { get; set; }

        /// <summary>
        /// List of port numbers that were attempted during the modification.
        /// </summary>
        public List<int> AttemptedPorts { get; set; } = new List<int>();

        /// <summary>
        /// The port number that was successfully found and replaced (if any).
        /// Null if no port was found.
        /// </summary>
        public int? SuccessfulPort { get; set; }

        /// <summary>
        /// Human-readable message describing the result of the operation.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
