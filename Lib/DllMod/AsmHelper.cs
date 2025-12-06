using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DllMod
{
    /// <summary>
    /// Helpers for modifying assemblies using Mono.Cecil.
    /// 
    /// This class performs low-level IL instruction manipulation to replace
    /// string literals and integer constants in compiled .NET assemblies.
    /// 
    /// - Auth: NhatNM -
    /// </summary>
    internal class AsmHelper
    {
        private AsmHelper() { }

        /// <summary>
        /// Patches a .NET assembly by replacing IP addresses and port numbers in IL code.
        /// 
        /// This method:
        /// 1. Loads the assembly using Mono.Cecil
        /// 2. Iterates through all types and their methods
        /// 3. Scans IL instructions for string literals containing the old IP or "localhost"
        /// 4. Scans IL instructions for integer constants matching the old port
        /// 5. Replaces them with new values
        /// 6. Writes the modified assembly to a temporary file
        /// 7. Replaces the original file and creates a backup
        /// </summary>
        /// <param name="parameters">Tuple containing dll path, old IP, new IP, old port, and new port</param>
        /// <returns>Tuple containing the number of IP and port replacements made</returns>
        public static (int ip, int port) Patch((string? dll, string? oip, string? ip, int? oport, int? port) parameters)
        {
            string dll = parameters.dll!;
            string newIp = parameters.ip!;
            string oldIp = parameters.oip!;
            int newPort = parameters.port!.Value;
            int oldPort = parameters.oport!.Value;

            using var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadWrite = true });

            int ipChanges = 0;
            int portChanges = 0;

            // Iterate through all types (including nested types) in the assembly
            foreach (var type in GetAllTypes(asm.MainModule))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var ins = method.Body.Instructions;
                    
                    // Scan all IL instructions in this method
                    for (int i = 0; i < ins.Count; i++)
                    {
                        var op = ins[i];
                        
                        // Check for string literal instructions (Ldstr = Load String)
                        if (op.OpCode == OpCodes.Ldstr && op.Operand is string s)
                        {
                            // Replace IP addresses in string literals
                            // This handles cases like "http://localhost:8000" or connection strings
                            if (s.Contains(oldIp) || s.ToLower().Contains("localhost"))
                            {
                                op.Operand = s.Replace(oldIp, newIp).Replace("localhost", newIp);
                                ipChanges++;
                            }
                            
                            // Replace port numbers in string literals
                            if (s.Contains(oldPort.ToString()))
                            {
                                op.Operand = s.Replace(oldPort.ToString(), newPort.ToString());
                                portChanges++;
                            }
                        }

                        // Check for integer constant instructions (Ldc_I4 = Load Constant Int32)
                        // This handles cases where port is hardcoded as an integer variable
                        int? val = TryGetInt(op);
                        if (val.HasValue && val.Value == oldPort)
                        {
                            op.OpCode = OpCodes.Ldc_I4;
                            op.Operand = newPort;
                            portChanges++;
                        }
                    }
                }
            }

            // Write modified assembly to temporary file
            string temp = dll + ".patched";
            asm.Write(temp);
            asm.Dispose();
            
            // Replace original file with patched version and create backup
            // File.Replace(source, dest, backup) is atomic and safe
            System.IO.File.Replace(temp, dll, dll + ".backup");
            
            return (ipChanges, portChanges);
        }

        /// <summary>
        /// Attempts to extract an integer value from an IL instruction.
        /// Handles all forms of integer constant loading instructions in IL.
        /// </summary>
        /// <param name="op">The IL instruction to analyze</param>
        /// <returns>The integer value if the instruction loads a constant, null otherwise</returns>
        static int? TryGetInt(Instruction op)
        {
            switch (op.OpCode.Code)
            {
                case Code.Ldc_I4: return (int)op.Operand;
                case Code.Ldc_I4_S: return (sbyte)op.Operand;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                default: return null;
            }
        }

        /// <summary>
        /// Recursively retrieves all types from a module, including nested types.
        /// Uses a stack-based approach to traverse the type hierarchy.
        /// </summary>
        /// <param name="module">The module to extract types from</param>
        /// <returns>Enumerable of all type definitions in the module</returns>
        static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                var type = stack.Pop();
                yield return type;
                foreach (var nested in type.NestedTypes)
                    stack.Push(nested);
            }
        }
    }
}
