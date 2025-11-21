//using LogMaster;
using EnvironmentManager.Services;
using System;
using System.Text;
using Domain.Entities.Enum;
using Domain.Entities.Constants;
using Domain.Entities.Main;
using System.Reflection;
using System.Linq;
using Newtonsoft.Json;

namespace EnvironmentManager
{
    class Program
    {
        private static string _environmentType;
        //private static readonly ILogger _logger = Log4netLogger.GetLogger(typeof(Program), "EnvironmentManager");

        static int Main(string[] args)
        {
            //Log4netLogger.UseConsoleAppender();
            //Log4netLogger.UseFileAppender($"Logs/FileLogger_{Guid.NewGuid()}.log");

            Thread.Sleep(15000);

            try
            {
                if (args == null || args.Length == 0)
                {
                    return PrintUsage();
                }

                var verb = args[0].Trim().ToLowerInvariant();
                var param = args.Length > 1 ? args[1] : null;

                return verb switch
                {
                    "setupcontainer" => param != null ? SetupContainer(param) : PrintUsage(),
                    "disposecontainer" => param != null ? DisposeContainer(param) : PrintUsage(),
                    "setupenvironmentq" => param != null ? SetupEnvironmentQ(param) : PrintUsage(),
                    "setupenvironmenttc" => param != null ? SetupEnvironmentTc(param) : PrintUsage(),
                    "disposeq" => param != null ? DisposeQ(param) : PrintUsage(),
                    "disposetc" => param != null ? DisposeTc(param) : PrintUsage(),
                    "quickreset" => param != null ? QuickReset(param) : PrintUsage(),
                    _ => PrintUsage()
                };
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Unhandled error in main: {ex.Message}");
                return -1;
            }
        }

        private static int PrintUsage()
        {
            Console.WriteLine("Usage: EnvironmentManager <verb> <base64-environment-json>");
            Console.WriteLine("Verbs:");
            Console.WriteLine("  setupcontainer <envBase64>      - Setup container for testkit");
            Console.WriteLine("  disposecontainer <envBase64>    - Dispose container for testkit");
            Console.WriteLine("  setupenvironmentq <envBase64>   - Setup environment for question");
            Console.WriteLine("  setupenvironmenttc <envBase64>  - Setup environment for testcase");
            Console.WriteLine("  disposeq <envBase64>            - Dispose environment for question");
            Console.WriteLine("  disposetc <envBase64>           - Dispose environment for testcase");
            Console.WriteLine("  quickreset <envBase64>          - Dispose containers, remove network, attempt DB close");
            return -1;
        }

        public static int SetupContainer(string environment)
        {
            try
            {
                var environmentJson = GetEnvironmentByBase64(environment);

                _environmentType = environmentJson.Configs[EnvironmentConfiguration.EnvironmentType];

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(_environmentType);

                environmentExecutor.SetupContainerForTestKit(environmentJson);

                // _logger.LogInfo("Setup container for testkit successfully.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                // _logger.LogErr($"Failed to deserialize XML for environmentForQ. Error: {ex.Message}");
                return -1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed to setup container for testkit. Error: {ex.Message}");
                return -1;
            }
        }

        public static int SetupEnvironmentQ(string environmentForQ)
        {
            try
            {
                var environment = GetEnvironmentByBase64(environmentForQ);

                _environmentType = environment.Configs[EnvironmentConfiguration.EnvironmentType];

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(_environmentType);

                environmentExecutor.SetupEnvironmentForQuestion(environment);
                environmentExecutor.ExecuteSetupEnvironmentForQuestionBySteps();

                // _logger.LogInfo("Environment for question has been set up successfully.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                // _logger.LogErr($"Failed to deserialize XML for environmentForQ. Error: {ex.Message}");
                return -1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed to set up environment for Q. Error: {ex.Message}");
                return -1;
            }
        }

        public static int SetupEnvironmentTc(string environmentForTc)
        {
            try
            {
                var environment = GetEnvironmentByBase64(environmentForTc);

                _environmentType = environment.Configs[EnvironmentConfiguration.EnvironmentType];

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(_environmentType);

                environmentExecutor.SetupEnvironmentForTestCase(environment);
                environmentExecutor.ExecuteSetupEnvironmentForTestCaseBySteps();

                // _logger.LogInfo("Environment for test case has been set up successfully.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                // _logger.LogErr($"Failed to deserialize XML for environmentForTC. Error: {ex.Message}");
                return -1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed to set up environment for TC. Error: {ex.Message}");
                return -1;
            }
        }

        public static int DisposeContainer(string environment)
        {
            try
            {
                var environmentJson = GetEnvironmentByBase64(environment);

                _environmentType = environmentJson.Configs[EnvironmentConfiguration.EnvironmentType];

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(_environmentType);

                environmentExecutor.DisposeContainerForTestKit(environmentJson);

                // _logger.LogInfo("Setup container for testkit successfully.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                // _logger.LogErr($"Failed to deserialize XML for environmentForQ. Error: {ex.Message}");
                return -1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed to setup container for testkit. Error: {ex.Message}");
                return -1;
            }
        }

        public static int DisposeQ(string environmentForQ)
        {
            try
            {
                var environmentQ = GetEnvironmentByBase64(environmentForQ);

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(environmentQ.Configs[EnvironmentConfiguration.EnvironmentType]);

                environmentExecutor.SetupEnvironmentForQuestion(environmentQ);
                environmentExecutor.DisposeEnvironmentForQuestion();
                return 1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed during dispose question process. Error: {ex.Message}");
                return -1;
            }
        }

        public static int DisposeTc(string environmentForTc)
        {
            try
            {
                var environmentQ = GetEnvironmentByBase64(environmentForTc);

                // get suitable environment executor
                BaseEnvironmentSetupService environmentExecutor = GetEnvironmentSetupService(environmentQ.Configs[EnvironmentConfiguration.EnvironmentType]);

                environmentExecutor.SetupEnvironmentForQuestion(environmentQ);
                environmentExecutor.DisposeEnvironmentForTestCase();

                // _logger.LogInfo("Dispose testcase completed successfully.");
                return 1;
            }
            catch (Exception ex)
            {
                // _logger.LogErr($"Failed during Dispose process. Error: {ex.Message}");
                return -1;
            }
        }

        public static int QuickReset(string environmentBase64)
        {
            try
            {
                var environment = GetEnvironmentByBase64(environmentBase64);
                if (!environment.Configs.TryGetValue(EnvironmentConfiguration.EnvironmentType, out var type) || string.IsNullOrWhiteSpace(type))
                {
                    return 1; // nothing to reset without type, treat as success
                }
                _environmentType = type;
                var executor = GetEnvironmentSetupService(_environmentType);

                // Best-effort: dispose containers; no environment steps needed
                try { executor.DisposeContainerForTestKit(environment); } catch { }
                try { executor.SetupEnvironmentForQuestion(environment); executor.DisposeEnvironmentForQuestion(); } catch { }
                // test case env not tracked globally here
                return 1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static BaseEnvironmentSetupService GetEnvironmentSetupService(string envType)
        {
            try
            {
                string[] services = new string[]
                {
                    ProcessName.DotNetEnvironmentManagerHelperPath,
                    //ProcessName.JavaJspEnvironmentManagerHelperPath,
                    //ProcessName.JavaSpringEnvironmentManagerHelperPath,
                    //ProcessName.PythonDjangoEnvironmentManagerHelperPath,
                    //ProcessName.NodeJsEnvironmentManagerHelperPath
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

        private static Domain.Entities.Main.Environment GetEnvironmentByBase64(string base64)
        {
            try
            {
                string jsonData = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

                //TODO: Replace with actual FileMaster call
                var environmentData = JsonConvert.DeserializeObject<Domain.Entities.Main.Environment>(jsonData);

                if (environmentData == null)
                {
                    // _logger.LogErr("Deserialization of environment JSON returned null.");
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