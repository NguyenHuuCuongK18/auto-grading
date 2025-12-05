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
    /// Helpers for modifying assemblies.
    /// 
    /// - Auth: NhatNM -
    /// </summary>
    internal class AsmHelper
    {
        private AsmHelper() { }

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

            foreach (var type in GetAllTypes(asm.MainModule))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var ins = method.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        var op = ins[i];
                        if (op.OpCode == OpCodes.Ldstr && op.Operand is string s)
                        {
                            if (s.Contains(oldIp) || s.ToLower().Contains("localhost"))
                            {
                                op.Operand = s.Replace(oldIp, newIp).Replace("localhost", newIp);
                                ipChanges++;
                            }
                            if (s.Contains(oldPort.ToString()))
                            {
                                op.Operand = s.Replace(oldPort.ToString(), newPort.ToString());
                                portChanges++;
                            }
                        }

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

            string temp = dll + ".patched";
            asm.Write(temp);
            asm.Dispose();
            File.Replace(temp, dll, dll + ".backup");
            return (ipChanges, portChanges);
        }

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
