using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using EnvironmentManager.Models;

namespace EnvironmentManager.Services
{
    /// <summary>
    /// Abstract base class for environment setup services.
    /// Provides common container lifecycle management and utility methods.
    /// 
    /// This is the foundation for all language-specific environment setup services.
    /// Subclasses implement the abstract methods for their specific container configurations.
    /// </summary>
    public abstract class BaseEnvironmentSetupService
    {
        #region Properties and Constructor
        
        /// <summary>
        /// The type of environment this service handles (e.g., "dotnet", "java", "python").
        /// </summary>
        public abstract string EnvironmentType { get; }
        
        /// <summary>
        /// Docker command executor for running Docker commands.
        /// </summary>
        protected readonly DockerCommandExecutor DockerExecutor;
        
        /// <summary>
        /// Optional callback for progress messages.
        /// </summary>
        protected readonly Action<string>? ProgressCallback;

        /// <summary>
        /// Creates a new environment setup service.
        /// </summary>
        /// <param name="progressCallback">Optional callback for progress messages.</param>
        protected BaseEnvironmentSetupService(Action<string>? progressCallback = null)
        {
            DockerExecutor = new DockerCommandExecutor();
            ProgressCallback = progressCallback;

            if (!IsDockerRunning())
            {
                throw new Exception("Docker is not running. Please start Docker before proceeding.");
            }
        }

        #endregion

        #region Abstract Methods - Must be implemented by subclasses

        /// <summary>
        /// Sets up the code container for the environment.
        /// </summary>
        public abstract Task SetupCodeContainerAsync(EnvironmentConfig config, string containerName);

        /// <summary>
        /// Sets up the database container for the environment.
        /// </summary>
        public abstract Task SetupDatabaseContainerAsync(EnvironmentConfig config);

        /// <summary>
        /// Copies essential files and folders to containers.
        /// </summary>
        public abstract Task CopyFilesToContainerAsync(EnvironmentConfig config, string containerName);

        /// <summary>
        /// Configures the application settings in the container.
        /// </summary>
        public abstract void ConfigureAppsettings(EnvironmentConfig config, string containerName);

        /// <summary>
        /// Cleans up the code container after grading.
        /// </summary>
        public abstract Task CleanupCodeContainerAsync(string containerName);

        /// <summary>
        /// Resets the database for a new test case.
        /// </summary>
        public abstract Task ResetDatabaseAsync(EnvironmentConfig config, string databaseName);

        #endregion

        #region Common Container Methods

        /// <summary>
        /// Waits for a container to be in running state with efficient polling.
        /// </summary>
        /// <param name="containerName">Name of the container to wait for.</param>
        /// <param name="maxWaitSeconds">Maximum seconds to wait.</param>
        public async Task WaitForContainerRunningAsync(string containerName, int maxWaitSeconds = 20)
        {
            var maxAttempts = maxWaitSeconds * 2; // Check every 500ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (DockerExecutor.IsContainerRunning(containerName))
                {
                    await Task.Delay(500);
                    return;
                }
                await Task.Delay(500);
            }
            OnProgress($"[Container] Warning: Container {containerName} may not be fully ready after {maxWaitSeconds}s");
        }

        /// <summary>
        /// Waits for a container to be removed with efficient polling.
        /// </summary>
        /// <param name="containerName">Name of the container to wait for removal.</param>
        /// <param name="maxWaitSeconds">Maximum seconds to wait.</param>
        public async Task WaitForContainerRemovedAsync(string containerName, int maxWaitSeconds = 5)
        {
            var maxAttempts = maxWaitSeconds * 10; // Check every 100ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (!DockerExecutor.IsContainerExist(containerName))
                {
                    OnProgress($"[Cleanup] Container {containerName} successfully removed (waited {i * 100}ms)");
                    return;
                }
                await Task.Delay(100);
            }

            // Container still exists - attempt force removal
            OnProgress($"[Cleanup] WARNING: Container {containerName} still exists after {maxWaitSeconds}s - attempting force removal");
            ForceRemoveContainer(containerName);
        }

        /// <summary>
        /// Force removes a container.
        /// </summary>
        public void ForceRemoveContainer(string containerName)
        {
            try
            {
                DockerExecutor.ExecDockerCommand($"rm -f {containerName}", 5000);
                OnProgress($"[Cleanup] Force removal completed for {containerName}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Cleanup] ERROR: Force removal failed for {containerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a container if it exists.
        /// </summary>
        public void RemoveContainerIfExists(string containerName)
        {
            try
            {
                if (DockerExecutor.IsContainerExist(containerName))
                {
                    DockerExecutor.RemoveContainer(containerName, 10000);
                }
            }
            catch
            {
                // Ignore errors - container may already be removed
            }
        }

        /// <summary>
        /// Checks Docker container count and warns if approaching limits.
        /// </summary>
        public void CheckDockerContainerLimit()
        {
            try
            {
                var (success, output) = DockerExecutor.ExecDockerCommandWithOutput("ps -a -q", 5000);
                if (success)
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var totalContainers = containerIds.Length;

                    OnProgress($"[Docker Monitor] Total containers: {totalContainers}");

                    if (totalContainers > 380)
                    {
                        OnProgress($"[Docker Monitor] CRITICAL WARNING: {totalContainers} containers exist! Approaching Docker daemon limit.");
                    }
                    else if (totalContainers > 256)
                    {
                        OnProgress($"[Docker Monitor] WARNING: {totalContainers} containers exist. Consider cleanup.");
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Monitor] Warning: Could not check container count: {ex.Message}");
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Reports progress to the callback if available.
        /// </summary>
        protected void OnProgress(string message)
        {
            ProgressCallback?.Invoke(message);
        }

        /// <summary>
        /// Checks if Docker is running.
        /// </summary>
        protected bool IsDockerRunning()
        {
            return DockerExecutor.IsDockerRunning();
        }

        /// <summary>
        /// Gets a value from a dictionary, or returns a default value.
        /// </summary>
        protected static string TryGetValueOrDefault(Dictionary<string, string>? configs, string key, string defaultValue = "")
        {
            if (configs != null && configs.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                return value;
            return defaultValue;
        }

        #endregion
    }
}
