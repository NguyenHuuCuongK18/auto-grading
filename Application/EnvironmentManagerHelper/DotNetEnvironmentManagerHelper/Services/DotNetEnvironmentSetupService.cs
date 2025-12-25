using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Domain.Entities.Docker.DockerSupporter.Entity;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;
using EnvironmentManager.Models;
using EnvironmentManager.Services;

namespace DotNetEnvironmentManagerHelper.Services
{
    /// <summary>
    /// .NET-specific environment setup service.
    /// 
    /// Handles the complete Docker container lifecycle for .NET Console Networking Applications:
    /// - Setup unified container (server + client with supervisord)
    /// - Setup MSSQL database container
    /// - Copy DLLs and configure appsettings.json
    /// - Manage database instances per student
    /// - Cleanup containers after grading
    /// 
    /// This service extracts the container setup logic from DockerGradingService
    /// into a dedicated, reusable component following the Single Responsibility Principle.
    /// </summary>
    public class DotNetEnvironmentSetupService : BaseEnvironmentSetupService
    {
        public override string EnvironmentType => "dotnet";

        private readonly CommandExecutor _commandExecutor;

        /// <summary>
        /// Creates a new .NET environment setup service.
        /// </summary>
        /// <param name="progressCallback">Optional callback for progress messages.</param>
        public DotNetEnvironmentSetupService(Action<string>? progressCallback = null) 
            : base(progressCallback)
        {
            _commandExecutor = new CommandExecutor();
        }

        #region Container Setup

        /// <summary>
        /// Sets up a unified container that runs both client and server processes.
        /// The container uses supervisord to manage processes which are started/stopped by test case actions.
        /// 
        /// Key features:
        /// - Uses --cap-add=NET_ADMIN for proper TCP close sequences
        /// - TTY mode (-t) for unbuffered logs
        /// - Processes idle until StartClient/StartServer actions are executed
        /// </summary>
        public override async Task SetupCodeContainerAsync(EnvironmentConfig config, string containerName)
        {
            OnProgress($"[SETUP] Creating unified container: {containerName}");

            // Remove existing container if any
            RemoveContainerIfExists(containerName);

            // Create the unified container with supervisord
            // NOTE: --cap-add=NET_ADMIN is required for the container entrypoint to enable 'quickack'
            // on the loopback interface. This forces proper 4-way TCP close.
            var dockerCmd = $"docker run -d --name {containerName} " +
                           $"--network {config.DockerNetwork} " +
                           $"--cap-add=NET_ADMIN " +  // Required for ip route quickack
                           $"-t " +  // TTY for unbuffered logs
                           $"{config.CodeImageName}";

            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);

            OnProgress($"[SETUP] Unified container {containerName} created");
            OnProgress($"[SETUP] Unified container ready - supervisord running, processes idle");

            // Wait for supervisord to be ready
            await Task.Delay(1000);
        }

        /// <summary>
        /// Sets up the MSSQL database container if not already running.
        /// The database container is shared between students for efficiency.
        /// </summary>
        public override async Task SetupDatabaseContainerAsync(EnvironmentConfig config)
        {
            var databaseContainer = config.DatabaseContainerName;

            // Check if database container is already running
            if (DockerExecutor.IsContainerRunning(databaseContainer))
            {
                OnProgress($"[Database] Container {databaseContainer} is already running");
                return;
            }

            // Check if container exists but stopped
            if (DockerExecutor.IsContainerExist(databaseContainer))
            {
                OnProgress($"[Database] Starting existing container {databaseContainer}...");
                DockerExecutor.StartExistedContainer(databaseContainer);
                await WaitForContainerRunningAsync(databaseContainer, 10);
                return;
            }

            // Create new MSSQL database container
            OnProgress($"[Database] Creating new MSSQL container {databaseContainer}...");

            var databaseBase = new DockerBase
            {
                ImageName = config.DatabaseImageName,
                ContainerName = databaseContainer,
                DockerNetwork = config.DockerNetwork,
                ContainerPort = config.DatabaseContainerInternalPort,
                HostPort = config.DatabaseContainerHostPort,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    { "ACCEPT_EULA", "Y" },
                    { "MSSQL_SA_PASSWORD", config.DatabasePassword }
                }
            };

            DockerExecutor.RunContainer(databaseBase, 3000);
            OnProgress($"[Database] Container {databaseContainer} created with port {config.DatabaseContainerHostPort}:{config.DatabaseContainerInternalPort}");

            // Wait for MSSQL to fully start
            OnProgress("[Database] Waiting for MSSQL to start...");
            await WaitForContainerRunningAsync(databaseContainer, 20);
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Copies DLL files to the unified container in separate server/client folders.
        /// Server files go to /apps/server, Client files go to /apps/client.
        /// </summary>
        public override async Task CopyFilesToContainerAsync(EnvironmentConfig config, string containerName)
        {
            // Create /apps/server and /apps/client directories in container
            DockerExecutor.MakeDirectory(containerName, "/apps/server");
            DockerExecutor.MakeDirectory(containerName, "/apps/client");

            // Copy SERVER files
            if (!string.IsNullOrEmpty(config.ServerDllPath))
            {
                var serverDir = Path.GetDirectoryName(config.ServerDllPath);
                if (serverDir != null)
                {
                    try
                    {
                        // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                        var serverSource = serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                        DockerExecutor.CopyFileToContainer(serverSource, $"{containerName}:/apps/server/");
                        OnProgress($"[File Copy] Copied server files to /apps/server");
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[File Copy] WARNING: Server copy failed: {ex.Message}");
                    }
                }
            }

            // Copy CLIENT files
            if (!string.IsNullOrEmpty(config.ClientDllPath))
            {
                var clientDir = Path.GetDirectoryName(config.ClientDllPath);
                if (clientDir != null)
                {
                    try
                    {
                        var clientSource = clientDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                        DockerExecutor.CopyFileToContainer(clientSource, $"{containerName}:/apps/client/");
                        OnProgress($"[File Copy] Copied client files to /apps/client");
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[File Copy] WARNING: Client copy failed: {ex.Message}");
                    }
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Configures appsettings.json in the container for both server and client.
        /// Updates IpAddress, Port, and ConnectionStrings settings.
        /// </summary>
        public override void ConfigureAppsettings(EnvironmentConfig config, string containerName)
        {
            ConfigureComponentAppsettings(containerName, "/apps/server", "Server", config);
            ConfigureComponentAppsettings(containerName, "/apps/client", "Client", config);
        }

        private void ConfigureComponentAppsettings(string containerName, string appPath, string componentName, EnvironmentConfig config)
        {
            var appsettingsPath = $"{appPath}/appsettings.json";

            // Check if appsettings.json exists
            var checkCommand = $"exec {containerName} test -f {appsettingsPath} && echo 'exists' || echo 'notfound'";
            var (success, output) = DockerExecutor.ExecDockerCommandWithOutput(checkCommand, 3000);

            if (success && output.Contains("exists"))
            {
                OnProgress($"[{componentName}] Found appsettings.json - modifying connection settings");
                ModifyAppsettingsInContainer(containerName, appsettingsPath, config, componentName);
            }
            else
            {
                OnProgress($"[{componentName}] No appsettings.json found");
            }
        }

        private void ModifyAppsettingsInContainer(string containerName, string appsettingsPath, EnvironmentConfig config, string componentName)
        {
            try
            {
                // Read current appsettings
                var catCommand = $"exec {containerName} cat {appsettingsPath}";
                var (success, content) = DockerExecutor.ExecDockerCommandWithOutput(catCommand, 5000);

                if (!success || string.IsNullOrEmpty(content))
                {
                    OnProgress($"[{componentName}] WARNING: Could not read appsettings.json");
                    return;
                }

                // Build connection string
                var connectionString = $"Server={config.DatabaseContainerName},{config.DatabaseContainerInternalPort};database={config.DatabaseName};uid={config.DatabaseUsername};Password={config.DatabasePassword};Encrypt=false;TrustServerCertificate=true";

                // Modify JSON content using regex replacement
                var modified = content;

                // Replace IpAddress
                modified = System.Text.RegularExpressions.Regex.Replace(
                    modified,
                    @"(""IpAddress""\s*:\s*)""[^""]*""",
                    componentName == "Server" ? "$1\"0.0.0.0\"" : "$1\"host.docker.internal\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Replace Port
                modified = System.Text.RegularExpressions.Regex.Replace(
                    modified,
                    @"(""Port""\s*:\s*)\d+",
                    $"$1{config.CodeContainerInternalPort}",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Replace connection string
                modified = System.Text.RegularExpressions.Regex.Replace(
                    modified,
                    @"(""MyCnn""\s*:\s*)""[^""]*""",
                    $"$1\"{connectionString.Replace("\"", "\\\"")}\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (modified != content)
                {
                    // Escape for shell and write back
                    var escapedContent = modified
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("$", "\\$")
                        .Replace("`", "\\`");

                    var writeCommand = $"exec {containerName} sh -c 'echo \"{escapedContent}\" > {appsettingsPath}'";
                    DockerExecutor.ExecDockerCommand(writeCommand, 5000);
                    OnProgress($"[{componentName}] Successfully modified appsettings.json");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[{componentName}] WARNING: Failed to modify appsettings.json: {ex.Message}");
            }
        }

        #endregion

        #region Database Operations

        /// <summary>
        /// Creates a database instance within the shared MSSQL container for a student.
        /// Each student gets their own isolated database within the shared container.
        /// </summary>
        public async Task CreateDatabaseInstanceAsync(EnvironmentConfig config, string databaseName)
        {
            var databaseContainer = config.DatabaseContainerName;

            // SECURITY: Validate database name to prevent SQL injection
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_\-]+$"))
            {
                throw new ArgumentException($"Invalid database name '{databaseName}'.", nameof(databaseName));
            }

            OnProgress($"[Database] Creating database instance '{databaseName}'");

            try
            {
                // Check if database already exists
                var checkDbSql = $"SELECT name FROM sys.databases WHERE name = '{databaseName}'";
                var checkCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {config.DatabaseUsername} -P \"{config.DatabasePassword}\" -Q \"{checkDbSql}\" -h -1";

                var (checkSuccess, checkOutput) = DockerExecutor.ExecDockerCommandWithOutput(checkCommand, 5000);

                if (checkSuccess && checkOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Database '{databaseName}' already exists, dropping it first");
                    await DropDatabaseAsync(config, databaseName);
                }

                // Create database
                if (!string.IsNullOrEmpty(config.SqlScriptPath) && File.Exists(config.SqlScriptPath))
                {
                    await CreateDatabaseFromScriptAsync(config, databaseName);
                }
                else
                {
                    await CreateEmptyDatabaseAsync(config, databaseName);
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database] WARNING: Failed to create database '{databaseName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the database for a new test case.
        /// </summary>
        public override async Task ResetDatabaseAsync(EnvironmentConfig config, string databaseName)
        {
            OnProgress($"[Database] Resetting database '{databaseName}'");

            await DropDatabaseAsync(config, databaseName);
            await CreateDatabaseInstanceAsync(config, databaseName);
        }

        private async Task DropDatabaseAsync(EnvironmentConfig config, string databaseName)
        {
            var dropSql = $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
            var dropCommand = $"exec {config.DatabaseContainerName} /opt/mssql-tools/bin/sqlcmd -S localhost -U {config.DatabaseUsername} -P \"{config.DatabasePassword}\" -Q \"{dropSql}\"";
            DockerExecutor.ExecDockerCommand(dropCommand, 10000);
            OnProgress($"[Database] Dropped database '{databaseName}'");
            await Task.Delay(1000);
        }

        private async Task CreateDatabaseFromScriptAsync(EnvironmentConfig config, string databaseName)
        {
            OnProgress($"[Database] Creating database '{databaseName}' from SQL script");

            var containerSqlPath = $"/tmp/{databaseName}.sql";
            DockerExecutor.CopyFileToContainer(config.SqlScriptPath!, $"{config.DatabaseContainerName}:{containerSqlPath}");

            var execScriptCommand = $"exec {config.DatabaseContainerName} /opt/mssql-tools/bin/sqlcmd -S localhost -U {config.DatabaseUsername} -P \"{config.DatabasePassword}\" -i {containerSqlPath}";
            var (scriptSuccess, scriptOutput) = DockerExecutor.ExecDockerCommandWithOutput(execScriptCommand, 30000);

            if (scriptSuccess)
            {
                OnProgress($"[Database] Successfully created database '{databaseName}' from script");
            }
            else
            {
                OnProgress($"[Database] WARNING: Failed to create database from script: {scriptOutput}");
            }

            await Task.CompletedTask;
        }

        private async Task CreateEmptyDatabaseAsync(EnvironmentConfig config, string databaseName)
        {
            OnProgress($"[Database] Creating empty database '{databaseName}'");

            var createDbSql = $"CREATE DATABASE [{databaseName}]";
            var createCommand = $"exec {config.DatabaseContainerName} /opt/mssql-tools/bin/sqlcmd -S localhost -U {config.DatabaseUsername} -P \"{config.DatabasePassword}\" -Q \"{createDbSql}\"";

            var (createSuccess, createOutput) = DockerExecutor.ExecDockerCommandWithOutput(createCommand, 10000);

            if (createSuccess)
            {
                OnProgress($"[Database] Successfully created empty database '{databaseName}'");
            }
            else
            {
                OnProgress($"[Database] WARNING: Failed to create database: {createOutput}");
            }

            await Task.CompletedTask;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleans up the unified container and its resources.
        /// </summary>
        public override async Task CleanupCodeContainerAsync(string containerName)
        {
            OnProgress($"[Cleanup] Cleaning up container {containerName}");

            try
            {
                // Kill any running dotnet processes first
                try
                {
                    var killCommand = $"exec {containerName} pkill -9 dotnet 2>/dev/null || true";
                    DockerExecutor.ExecDockerCommand(killCommand, 5000);
                    await Task.Delay(100);
                }
                catch
                {
                    // Ignore - container may already be stopped
                }

                // Remove the container
                DockerExecutor.RemoveContainer(containerName, 10000);
                OnProgress($"[Cleanup] Successfully cleaned up container {containerName}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Cleanup] Warning: Cleanup failed for {containerName}: {ex.Message}");
                ForceRemoveContainer(containerName);
            }
        }

        /// <summary>
        /// Cleans up the database instance from the shared MSSQL container.
        /// </summary>
        public async Task CleanupDatabaseInstanceAsync(EnvironmentConfig config, string databaseName)
        {
            OnProgress($"[Cleanup] Dropping database '{databaseName}'");

            try
            {
                var dropSql = $"USE master; IF EXISTS (SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
                var dropCommand = $"exec {config.DatabaseContainerName} /opt/mssql-tools/bin/sqlcmd -S localhost -U {config.DatabaseUsername} -P \"{config.DatabasePassword}\" -Q \"{dropSql}\"";

                DockerExecutor.ExecDockerCommand(dropCommand, 10000);
                OnProgress($"[Cleanup] Successfully dropped database '{databaseName}'");
            }
            catch (Exception ex)
            {
                OnProgress($"[Cleanup] Warning: Failed to drop database '{databaseName}': {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Aggressively cleans up old auto-grading containers.
        /// </summary>
        public void AggressiveCleanupOldContainers()
        {
            OnProgress("[Cleanup] Starting aggressive cleanup of old containers...");

            try
            {
                var (success, output) = DockerExecutor.ExecDockerCommandWithOutput(
                    "ps -a --filter 'name=ag-' -q", 5000);

                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    OnProgress($"[Cleanup] Found {containerIds.Length} old containers to remove");

                    foreach (var containerId in containerIds)
                    {
                        try
                        {
                            DockerExecutor.ExecDockerCommand($"rm -f {containerId.Trim()}", 5000);
                        }
                        catch
                        {
                            // Ignore individual failures
                        }
                    }

                    OnProgress($"[Cleanup] Aggressive cleanup complete");
                }
                else
                {
                    OnProgress("[Cleanup] No old containers found");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Cleanup] Warning: Cleanup encountered errors: {ex.Message}");
            }
        }

        #endregion
    }
}
