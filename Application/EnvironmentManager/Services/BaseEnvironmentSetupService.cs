using Domain.Entities.Constants;
using EnvironmentBuilder.DockerCommand;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace EnvironmentManager.Services
{
    /// <summary>
    /// Base service class for setting up Docker-based environments for grading.
    /// Provides common container management functionality for database and code containers.
    /// </summary>
    public abstract class BaseEnvironmentSetupService
    {
        #region PROPS & CTOR
        public virtual string EnvironmentType { get; } = "base";
        protected DockerCommandExecutor dockerCommandExecutor;
        protected Domain.Entities.Main.Environment questionEnvironment { get; set; }
        protected Domain.Entities.Main.Environment testCaseEnvironment { get; set; }
        protected Dictionary<string, string> questionConfigs { get; set; }
        protected Dictionary<string, string> testCaseConfigs { get; set; }

        public BaseEnvironmentSetupService()
        {
            dockerCommandExecutor = new DockerCommandExecutor();

            if (!IsDockerRunning())
            {
                throw new Exception("Docker is not running. Please start Docker before proceeding.");
            }
        }
        #endregion

        #region SETUP
        /// <summary>
        /// Sets up Docker containers for a test kit, including database and code containers.
        /// </summary>
        /// <param name="environment">Environment configuration for the test kit</param>
        public virtual void SetupContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
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

                if (!dockerCommandExecutor.IsContainerRunning(sqlContainerName))
                {
                    SetupDatabaseContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    SetupCodeContainer();
                }

                Thread.Sleep(3000);
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
        /// <summary>
        /// Executes environment setup for a question by processing the configured steps.
        /// </summary>
        public virtual void ExecuteSetupEnvironmentForQuestionBySteps()
        {
            List<string> steps = questionEnvironment.Steps;

            foreach (string step in steps)
            {
                switch (step)
                {
                    case EnvironmentQAction.CopyEssentialFilesAndFolders:
                        CopyEssentialFilesAndFolders();
                        break;

                    case EnvironmentQAction.GenerateDatabaseScript:
                        GenerateDbScript();
                        break;

                    case EnvironmentQAction.GenerateConnectionFile:
                        GenerateConnectionFile();
                        break;

                    default:
                        throw new Exception($"Action named '{step}' is not defined!");
                }
            }
        }

        /// <summary>
        /// Executes environment setup for a test case by processing the configured steps.
        /// </summary>
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
        /// <summary>
        /// Disposes Docker containers for a test kit.
        /// </summary>
        /// <param name="environment">Environment configuration for the test kit</param>
        public virtual void DisposeContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
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

                if (dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(codeContainerName);
                }

                if (dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(databaseContainerName);
                }
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
        /// <summary>
        /// Gets a value from the configuration dictionary, returning default if not found.
        /// </summary>
        protected static string TryGetValueOrDefault(
            Dictionary<string, string> configs,
            string key,
            string defaultValue = "")
        {
            if (configs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;

            return defaultValue;
        }

        /// <summary>
        /// Checks if Docker daemon is running.
        /// </summary>
        protected bool IsDockerRunning()
        {
            return dockerCommandExecutor.IsDockerRunning();
        }

        /// <summary>
        /// Waits for a container to be running, checking at specified intervals.
        /// </summary>
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
