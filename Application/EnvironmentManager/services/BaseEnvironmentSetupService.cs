using Domain.Entities.Constants;
using EnvironmentBuilder.DockerCommand;
//using LogMaster;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace EnvironmentManager.Services
{
    public abstract class BaseEnvironmentSetupService
    {
        #region PROPS & CTOR
        public virtual string EnvironmentType { get; } = "base";
        //protected static ILogger logger;
        protected DockerCommandExecutor dockerCommandExecutor;
        protected Domain.Entities.Main.Environment questionEnvironment { get; set; }
        protected Domain.Entities.Main.Environment testCaseEnvironment { get; set; }
        protected Dictionary<string, string> questionConfigs { get; set; }
        protected Dictionary<string, string> testCaseConfigs { get; set; }

        public BaseEnvironmentSetupService()
        {
            //logger = Log4netLogger.GetLogger(typeof(Program), "EnvironmentManager");
            dockerCommandExecutor = new DockerCommandExecutor();

            //Log4netLogger.UseConsoleAppender();
            if (!IsDockerRunning())
            {
                throw new Exception("Docker is not running. Please start Docker before proceeding.");
            }
        }
        #endregion

        #region SETUP
        public virtual void SetupContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
            //logger.LogInfo("Setting up container for testkit");

            questionEnvironment = environment;
            questionConfigs = environment.Configs;

            try
            {
                string sqlContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseContainerName
                );

                string codeContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeContainerName
                );

                string networkName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DockerNetwork
                );

                // Ensure network exists (ignore already exists)
                if (!string.IsNullOrWhiteSpace(networkName))
                {
                    try { dockerCommandExecutor.CreateNetwork(networkName, 10000); } catch { /* ignore network create failures */ }
                }

                if (!dockerCommandExecutor.IsContainerRunning(sqlContainerName))
                {
                    //logger.LogInfo("Database container is not running, try starting...");

                    SetupDatabaseContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    //logger.LogInfo("Code container is not running, try starting...");

                    SetupCodeContainer();
                }

                // Give containers a brief time to settle
                Thread.Sleep(2000);

                // Ensure logs folder exists inside code container and create empty logs
                try
                {
                    dockerCommandExecutor.MakeDirectory(codeContainerName, "/logs");
                    dockerCommandExecutor.ExecDockerCommand($"{codeContainerName} sh -c \"touch /logs/client.log /logs/server.log\"");
                }
                catch { }

                // Prepare database: copy/init script then reset/create
                try
                {
                    GenerateDbScript();
                    ResetDatabase();
                }
                catch { }

                // Copy essential runtimes / given executables as defined by language helper
                try
                {
                    CopyEssentialFilesAndFolders();
                }
                catch { }

                //logger.LogInfo("Finished setting up essential containers and pre-suite assets.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed setting up containers for testkit. Details: {ex.Message}");
            }
        }

        public abstract void SetupEnvironmentForQuestion(Domain.Entities.Main.Environment environment);
        public abstract void SetupEnvironmentForTestCase(Domain.Entities.Main.Environment environment);
        #endregion

        #region EXEC
        public virtual void ExecuteSetupEnvironmentForQuestionBySteps()
        {
            List<string> steps = questionEnvironment.Steps;

            foreach (string step in steps)
            {
                switch (step)
                {
                    case EnvironmentQAction.CopyEssentialFilesAndFolders:
                        CopyEssentialFilesAndFolders();
                        //logger.LogInfo("Copying essential files and folders");
                        break;

                    case EnvironmentQAction.GenerateDatabaseScript:
                        GenerateDbScript();
                        //logger.LogInfo("Try generating database initialization script");
                        break;

                    case EnvironmentQAction.GenerateConnectionFile:
                        GenerateConnectionFile();
                        //logger.LogInfo("Try creating connection file");
                        break;

                    default:
                        //logger.LogErr($"Action named '{step}' is not defined!");
                        throw new Exception($"Action named '{step}' is not defined!");
                }
            }
        }

        public virtual void ExecuteSetupEnvironmentForTestCaseBySteps()
        {
            List<string> steps = testCaseEnvironment.Steps;

            if (steps.Count == 0) return;

            foreach (string step in steps)
            {
                switch (step)
                {
                    case EnvironmentTcAction.ResetDatabase:
                        ResetDatabase();
                        break;

                    default:
                        throw new Exception($"Action named '{step}' is not defined!");
                }
            }
        }
        #endregion

        #region DISPOSE
        public virtual void DisposeContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
            //logger.LogInfo("Setting up environment for testkit");

            questionEnvironment = environment;
            questionConfigs = environment.Configs;

            try
            {
                string databaseContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseContainerName
                );

                string codeContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeContainerName
                );

                string networkName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DockerNetwork
                );

                if (dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    //logger.LogInfo("Code container is running, try disposing...");

                    dockerCommandExecutor.RemoveContainer(codeContainerName);
                }

                if (dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    //logger.LogInfo("Database container is running, try disposing...");

                    dockerCommandExecutor.RemoveContainer(databaseContainerName);
                }

                // Remove network last (ignore failures)
                if (!string.IsNullOrWhiteSpace(networkName))
                {
                    try { dockerCommandExecutor.RemoveNetwork(networkName); } catch { }
                }

                //logger.LogInfo("Finished disposing essential containers.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed disposing containers. Details: {ex.Message}");
            }
        }

        public abstract void DisposeEnvironmentForQuestion();
        public abstract void DisposeEnvironmentForTestCase();
        #endregion

        #region CORE
        public abstract void SetupCodeContainer();
        public abstract void SetupDatabaseContainer();
        public abstract void CopyEssentialFilesAndFolders();
        public abstract void ResetDatabase();
        public abstract void GenerateDbScript();
        public abstract void GenerateConnectionFile();
        #endregion

        #region UTILITIES
        protected static string TryGetValueOrDefault(
            Dictionary<string, string> configs,
            string key,
            string defaultValue = "")
        {
            if (configs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;

            return defaultValue;
        }

        protected bool IsDockerRunning()
        {
            return dockerCommandExecutor.IsDockerRunning();
        }

        protected bool IsContainerRunning(string containerName, int checkIntervalMs = 1)
        {
            while (true)
            {
                if (!dockerCommandExecutor.IsContainerRunning(containerName))
                    Thread.Sleep(checkIntervalMs);
                else
                    return true;
            }
        }
        #endregion

    }
}
