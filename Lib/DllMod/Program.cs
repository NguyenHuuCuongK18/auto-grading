using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Runtime.CompilerServices;

namespace DllMod
{
    /// <summary>
    /// Dll Modifier Tool
    /// 
    /// Simply replaces localhost IP and port 4000 in the given DLL file.
    /// 
    /// - Auth: NhatNM -
    /// </summary>
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) return PrintUsage();
            string? action = args.Length > 0 ? args[0].Trim().ToLower() : null;
            (string? dll, string? oip, string? ip, int? oport, int? port) parameters = ParseArgs(args);
            return action switch
            {
                "dllmod" => !IsAnyNull(parameters) ? ModifyDll(parameters) : PrintUsage(),
                _ => PrintUsage()
            };
        }

        private static int ModifyDll((string? dll, string? oip, string? ip, int? oport, int? port) parameters)
        {
            (int ip, int port) = AsmHelper.Patch(parameters);
            Console.WriteLine($"Replaced: {ip} Ips and {port} Ports");
            return 1;
        }

        private static int PrintUsage()
        {
            Console.WriteLine("Usage: <action> -d <dll_path> -oi <old_ip> -i <new_ip> -op <old_port> -p <new_port>");
            return -1;
        }

        private static (string? dll, string? oip, string? ip, int? oport, int? port) ParseArgs(string[] args)
        {
            string? dll = null;
            string? oip = null;
            string? ip = null;
            int? oport = null;
            int? port = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-d":
                    case "--dll":
                        if (i + 1 < args.Length) dll = args[++i];
                        break;

                    case "-oi":
                    case "--old-ip":
                        if (i + 1 < args.Length) oip = args[++i];
                        break;

                    case "-i":
                    case "--ip":
                        if (i + 1 < args.Length) ip = args[++i];
                        break;

                    case "-op":
                    case "--old-port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int oprt))
                        {
                            oport = oprt;
                            i++;
                        }
                        break;

                    case "-p":
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int prt))
                        {
                            port = prt;
                            i++;
                        }
                        break;
                }
            }
            return (dll, oip, ip, oport, port);
        }

        private static bool IsAnyNull(ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                if (tuple[i] is null) return true;
            }
            return false;
        }
    }
}
