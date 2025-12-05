using Mono.Cecil;
using Mono.Cecil.Cil;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service for modifying compiled DLL files to replace hardcoded IP addresses and ports.
/// This service uses Mono.Cecil to patch IL code in .NET assemblies, replacing string literals
/// and integer constants that match common localhost patterns and port numbers.
/// 
/// Use case: When student submissions don't have appsettings.json files but have hardcoded
/// connection details in their code, this service can patch the compiled DLLs to work with
/// the grading environment.
/// </summary>
public sealed class DllModificationService : IDllModificationService
{
    private const string LOG_PREFIX = "[DllMod]";
    
    // Common localhost patterns that students might use in their code
    private static readonly string[] LocalhostPatterns = new[]
    {
        "localhost",
        "127.0.0.1",
        "http://localhost",
        "https://localhost"
    };
    
    // Common ports that students might hardcode in their applications
    private static readonly int[] CommonPorts = new[] { 3000, 4000, 5000, 8000, 8080 };
    
    /// <summary>
    /// Attempts to modify all DLL files in the specified directory to replace hardcoded
    /// localhost references and common ports with grading environment values.
    /// </summary>
    /// <param name="dllDirectory">Directory containing DLL files to modify</param>
    /// <param name="targetIp">Target IP address to use (e.g., "http://localhost" or "127.0.0.1")</param>
    /// <param name="targetPort">Target port number for the grading environment</param>
    /// <returns>True if any modifications were made successfully, false otherwise</returns>
    public bool TryModifyDlls(string dllDirectory, string targetIp, int targetPort)
    {
        if (!Directory.Exists(dllDirectory))
        {
            Console.WriteLine($"{LOG_PREFIX} Directory not found: {dllDirectory}");
            return false;
        }
        
        Console.WriteLine($"{LOG_PREFIX} Searching for DLL files in: {dllDirectory}");
        var dllFiles = Directory.GetFiles(dllDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        
        if (dllFiles.Length == 0)
        {
            Console.WriteLine($"{LOG_PREFIX} No DLL files found in directory");
            return false;
        }
        
        Console.WriteLine($"{LOG_PREFIX} Found {dllFiles.Length} DLL file(s) to scan");
        
        bool anySuccess = false;
        foreach (var dllPath in dllFiles)
        {
            try
            {
                // Skip system/framework DLLs that are unlikely to contain student code
                var fileName = Path.GetFileName(dllPath);
                if (IsSystemDll(fileName))
                {
                    Console.WriteLine($"{LOG_PREFIX} Skipping system DLL: {fileName}");
                    continue;
                }
                
                Console.WriteLine($"{LOG_PREFIX} Attempting to patch: {fileName}");
                var (ipChanges, portChanges) = PatchDll(dllPath, targetIp, targetPort);
                
                if (ipChanges > 0 || portChanges > 0)
                {
                    Console.WriteLine($"{LOG_PREFIX} Successfully patched {fileName}: {ipChanges} IP(s), {portChanges} port(s) replaced");
                    anySuccess = true;
                }
                else
                {
                    Console.WriteLine($"{LOG_PREFIX} No changes needed for {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LOG_PREFIX} Failed to patch {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }
        
        return anySuccess;
    }
    
    /// <summary>
    /// Gets a list of common localhost patterns that should be replaced during DLL modification
    /// </summary>
    public string[] GetCommonLocalhostPatterns() => LocalhostPatterns;
    
    /// <summary>
    /// Gets a list of common ports that should be replaced during DLL modification
    /// </summary>
    public int[] GetCommonPorts() => CommonPorts;
    
    /// <summary>
    /// Patches a single DLL file to replace localhost references and port numbers.
    /// Based on the dll-mod tool by NhatNM from https://github.com/LostInUrMind/dll-mod.git
    /// </summary>
    /// <param name="dllPath">Path to the DLL file to patch</param>
    /// <param name="targetIp">Target IP address to replace localhost with</param>
    /// <param name="targetPort">Target port to replace common ports with</param>
    /// <returns>Tuple of (IP changes count, port changes count)</returns>
    private (int ipChanges, int portChanges) PatchDll(string dllPath, string targetIp, int targetPort)
    {
        // Read the assembly with write access
        using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { ReadWrite = true });
        
        int ipChanges = 0;
        int portChanges = 0;
        
        // Iterate through all types and methods in the assembly
        foreach (var type in GetAllTypes(asm.MainModule))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                
                var instructions = method.Body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    var instruction = instructions[i];
                    
                    // Check for string literals (ldstr opcode)
                    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string stringValue)
                    {
                        var modified = false;
                        var newValue = stringValue;
                        
                        // Replace all localhost patterns with target IP
                        foreach (var pattern in LocalhostPatterns)
                        {
                            if (stringValue.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                // For URL patterns, preserve the protocol
                                if (pattern.StartsWith("http://") || pattern.StartsWith("https://"))
                                {
                                    // If target IP doesn't include protocol, add it
                                    var replacementIp = targetIp.StartsWith("http://") || targetIp.StartsWith("https://")
                                        ? targetIp
                                        : $"http://{targetIp}";
                                    newValue = newValue.Replace(pattern, replacementIp, StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    // Strip protocol from target IP if present
                                    var bareIp = targetIp.Replace("http://", "").Replace("https://", "");
                                    newValue = newValue.Replace(pattern, bareIp, StringComparison.OrdinalIgnoreCase);
                                }
                                modified = true;
                            }
                        }
                        
                        // Replace common ports with target port
                        foreach (var port in CommonPorts)
                        {
                            var portStr = port.ToString();
                            if (stringValue.Contains(portStr))
                            {
                                newValue = newValue.Replace(portStr, targetPort.ToString());
                                modified = true;
                                portChanges++;
                            }
                        }
                        
                        if (modified)
                        {
                            instruction.Operand = newValue;
                            ipChanges++;
                        }
                    }
                    
                    // Check for integer constants (ldc.i4 opcodes) that match common ports
                    var intValue = TryGetInt(instruction);
                    if (intValue.HasValue && CommonPorts.Contains(intValue.Value))
                    {
                        instruction.OpCode = OpCodes.Ldc_I4;
                        instruction.Operand = targetPort;
                        portChanges++;
                    }
                }
            }
        }
        
        // Save the modified assembly
        if (ipChanges > 0 || portChanges > 0)
        {
            var tempPath = dllPath + ".patched";
            asm.Write(tempPath);
            asm.Dispose();
            
            // Create backup and replace original
            var backupPath = dllPath + ".backup";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Move(dllPath, backupPath);
            File.Move(tempPath, dllPath);
        }
        
        return (ipChanges, portChanges);
    }
    
    /// <summary>
    /// Attempts to extract an integer value from an IL instruction.
    /// Handles various ldc.i4 opcodes (load constant int32).
    /// </summary>
    private static int? TryGetInt(Instruction instruction)
    {
        return instruction.OpCode.Code switch
        {
            Code.Ldc_I4 => (int)instruction.Operand,
            Code.Ldc_I4_S => (sbyte)instruction.Operand,
            Code.Ldc_I4_0 => 0,
            Code.Ldc_I4_1 => 1,
            Code.Ldc_I4_2 => 2,
            Code.Ldc_I4_3 => 3,
            Code.Ldc_I4_4 => 4,
            Code.Ldc_I4_5 => 5,
            Code.Ldc_I4_6 => 6,
            Code.Ldc_I4_7 => 7,
            Code.Ldc_I4_8 => 8,
            _ => null
        };
    }
    
    /// <summary>
    /// Gets all types in a module, including nested types recursively.
    /// </summary>
    private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
            {
                stack.Push(nested);
            }
        }
    }
    
    /// <summary>
    /// Determines if a DLL file is a system/framework library that should be skipped.
    /// These DLLs are unlikely to contain student code with hardcoded connection strings.
    /// </summary>
    private static bool IsSystemDll(string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();
        
        // Skip Microsoft and System assemblies
        if (lowerName.StartsWith("microsoft.") || 
            lowerName.StartsWith("system.") ||
            lowerName.StartsWith("netstandard") ||
            lowerName.StartsWith("mscorlib") ||
            lowerName.StartsWith("windows."))
        {
            return true;
        }
        
        // Skip common third-party libraries
        var commonLibraries = new[]
        {
            "newtonsoft.json", "entityframework", "automapper", "serilog",
            "nlog", "log4net", "dapper", "npgsql", "mysql", "sqlite",
            "moq", "xunit", "nunit", "fluentvalidation", "autofac",
            "castle.", "ninject", "polly", "mediatr", "swashbuckle"
        };
        
        foreach (var lib in commonLibraries)
        {
            if (lowerName.StartsWith(lib))
            {
                return true;
            }
        }
        
        return false;
    }
}
