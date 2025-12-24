using Domain.Entities.Constants;
using Domain.Entities.Enum;
using EnvironmentManager.Services;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace EnvironmentManager
{
    /// <summary>
    /// Entry point for the Environment Manager CLI tool.
    /// Handles Docker container setup and disposal for grading environments.
    /// </summary>
    internal class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) return PrintUsage();
                var verb = args[0].Trim().ToLowerInvariant();
                var param = args.Length > 1 ? args[1] : null;
                return verb switch
                {
                    "setupcontainer" => param != null ? SetUpContainer(param) : PrintUsage(),
                    "disposecontainer" => param != null ? DisposeContainer(param) : PrintUsage(),
                    "setupenvironmentq" => param != null ? SetupEnvironmentQ(param) : PrintUsage(),
                    _ => PrintUsage()
                };
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Sets up Docker containers for a test kit based on the provided environment configuration.
        /// </summary>
        private static int SetUpContainer(string env)
        {
            try
            {
                var environmentJson = GetEnvironmentByBase64(env);

                string envType = environmentJson.Configs[EnvironmentConfiguration.EnvironmentType];

                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(envType);

                environmentExecutor.SetupContainerForTestKit(environmentJson);

                return 1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Disposes Docker containers for a test kit based on the provided environment configuration.
        /// </summary>
        public static int DisposeContainer(string environment)
        {
            try
            {
                var environmentJson = GetEnvironmentByBase64(environment);

                string envType = environmentJson.Configs[EnvironmentConfiguration.EnvironmentType];

                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(envType);

                environmentExecutor.DisposeContainerForTestKit(environmentJson);

                return 1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Sets up the environment for a specific question, including file copying and database setup.
        /// </summary>
        public static int SetupEnvironmentQ(string environmentForQ)
        {
            try
            {
                var environment = GetEnvironmentByBase64(environmentForQ);

                string envType = environment.Configs[EnvironmentConfiguration.EnvironmentType];

                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(envType);

                environmentExecutor.SetupEnvironmentForQuestion(environment);
                environmentExecutor.ExecuteSetupEnvironmentForQuestionBySteps();

                return 1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Parses command-line arguments into a dictionary.
        /// </summary>
        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--")) continue;
                var key = a.TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { map[key] = args[i + 1]; i++; }
                else { map[key] = "true"; }
            }
            return map;
        }

        private static int PrintUsage()
        {
            Console.WriteLine(@"
Usage:
  SolutionGrader.Cli ExecuteSuite --suite <suiteFolder|Header.xlsx> --out <resultRoot>
                                [--client <client.exe>] [--server <server.exe>]
                                [--use-inner-env]

Required Arguments:
  --suite   Path to test suite folder or Header.xlsx file
  --out     Output directory for grading results

Optional Arguments:
  --client  Path to client executable (overrides Meta/Given/Client if provided)
  --server  Path to server executable (overrides Meta/Given/Server if provided)
  --use-inner-env  Enable test case-specific environment.xlsx files
                   When specified, each test case can have its own environment.xlsx
                   to override database paths and configurations (default: false)

Configuration:
  All other configuration (database script, ports, timeouts, etc.) is read from:
  - environment.xlsx: Database script path, given executables, ports
  - Header.xlsx: Protocol, database configuration, test case marks
  
  The grading system will:
  - Use executables from Meta/Given folder when --client/--server not specified
  - Auto-generate appsettings.json from Header.xlsx with database configuration
  - Use database script from environment.xlsx (Default_Database_File_Path)
  - Use default timeout of 10 seconds per stage
  - Use suite-level environment.xlsx by default (unless --use-inner-env is specified)
");
            return -1;
        }

        private static int Stud() { return -1; }

        /// <summary>
        /// Gets the appropriate environment setup service based on the environment type.
        /// </summary>
        private static BaseEnvironmentSetupService GetEnvironmentSetupService(string envType)
        {
            try
            {
                string[] services = new string[]
                {
                    ProcessName.DotNetEnvironmentManagerHelperPath
                };

                foreach (string service in services)
                {
                    Assembly asm = Assembly.LoadFrom(service);

                    var types = asm.GetTypes().Where(t => typeof(BaseEnvironmentSetupService).IsAssignableFrom(t) && !t.IsAbstract);

                    foreach (var item in types)
                    {
                        BaseEnvironmentSetupService setupService = (BaseEnvironmentSetupService)Activator.CreateInstance(item);

                        if (setupService.EnvironmentType == envType)
                            return setupService;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                throw new Exception($"Can not find suitable environment setup service. Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Decodes a Base64-encoded environment JSON string and deserializes it.
        /// </summary>
        /// <exception cref="Exception">Thrown when deserialization fails or returns null</exception>
        private static Domain.Entities.Main.Environment GetEnvironmentByBase64(string base64)
        {
            try
            {
                string jsonData = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

                var environmentData = JsonConvert.DeserializeObject<Domain.Entities.Main.Environment>(jsonData);

                if (environmentData == null)
                {
                    throw new Exception("Deserialization of environment JSON returned null.");
                }

                return environmentData;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while decode base64. Details: {ex.Message}");
            }
        }
    }
}
