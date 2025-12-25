// This file contains the Container Setup region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using Domain.Entities.Docker.DockerSupporter.Entity;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Helpers;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Container Setup

        /// <summary>
        /// Sets up all required Docker containers for grading:
        /// 1. MSSQL Database Container - provides database backend for student applications
        /// 2. Server Container - runs the student's server application
        /// 3. Client Container - runs the student's client application
        /// 
        /// All containers are connected to the same Docker network for inter-container communication.
        /// The server container port is EXPOSED to the host for NetworkMonitor packet capture.
        /// </summary>

        /// <summary>
        /// Setup unified container that runs both client and server processes.
        /// Processes are managed by supervisord and started/stopped by test case actions.
        /// CLIENT AND SERVER ARE NOT STARTED AUTOMATICALLY - they start only when test case Detail.xlsx says so.
        /// Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
        /// The C# code reads these files incrementally after each action to separate output by stage.
        /// </summary>
        private async Task SetupUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            OnProgress($"[SETUP] Creating unified container: {unifiedContainer}");

            // Remove existing unified container if any
            _commandExecutor.RunCommand($"docker rm -f {unifiedContainer} 2>/dev/null || true", null, null, 10000);

            // Create the unified container with supervisord
            // Processes are controlled by test case actions (StartClient, StartServer, CloseClient, CloseServer)
            // Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
            // The C# code reads these files incrementally after each action to separate output by stage
            //
            // NOTE: --cap-add=NET_ADMIN is required for the container entrypoint to enable 'quickack'
            // on the loopback interface. This forces proper 4-way TCP close (FIN-ACK -> ACK -> FIN-ACK -> ACK)
            // instead of 3-way close where Linux piggybacks ACK with FIN.
            var dockerCmd = $"docker run -d --name {unifiedContainer} " +
                           $"--network {config.DockerNetwork} " +
                           $"--cap-add=NET_ADMIN " +  // Required for ip route quickack
                           $"-t " +  // TTY for unbuffered logs
                           $"{config.CodeImageName}";

            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);
            
            // CRITICAL: Register container as active IMMEDIATELY after creation
            // This prevents periodic cleanup from killing this container while it's in use
            RegisterActiveContainer(unifiedContainer);
            OnProgress($"[SETUP] Unified container {unifiedContainer} created and registered as active");
            OnProgress($"[SETUP] Unified container ready - supervisord running, processes idle");

            // Wait for supervisord to be ready
            await Task.Delay(1000);

            // Copy DLLs and appsettings to container (in separate /apps/server and /apps/client folders)
            await CopyFilesToUnifiedContainerAsync(
                serverDllPath,
                clientDllPath,
                config,
                testKitConfig,
                unifiedContainer);

            OnProgress($"[Unified] Container ready - processes will start when test cases execute StartClient/StartServer actions");
            OnProgress($"[Unified] Logs will be written to unified files: /apps/server/server.log and /apps/client/client.log");
            OnProgress($"[Unified] C# code reads these files incrementally to separate output by stage");
        }

        /// <summary>
        /// Sets up the MSSQL database container if not already running.
        /// The database container is shared between students for efficiency.
        /// </summary>
        private async Task SetupDatabaseContainerAsync(DockerGradingConfig config)
        {
            var databaseContainer = config.DatabaseContainerName;

            // Check if database container is already running
            if (_dockerExecutor.IsContainerRunning(databaseContainer))
            {
                OnProgress($"[Docker] Database container {databaseContainer} is already running");
                return;
            }

            // Check if container exists but stopped
            if (_dockerExecutor.IsContainerExist(databaseContainer))
            {
                OnProgress($"[Docker] Starting existing database container {databaseContainer}...");
                _dockerExecutor.StartExistedContainer(databaseContainer);
                // Wait for container to be running with quick health checks (no logging spam)
                await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 10);
                return;
            }

            // Create new MSSQL database container
            OnProgress($"[Docker] Creating new MSSQL database container {databaseContainer}...");

            var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
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
                    { "MSSQL_SA_PASSWORD", databasePassword }
                }
            };

            _dockerExecutor.RunContainer(databaseBase, 3000);
            OnProgress($"[Docker] Database container {databaseContainer} created with port {config.DatabaseContainerHostPort}:{config.DatabaseContainerInternalPort} exposed");

            // Wait for MSSQL to fully start with polling instead of fixed delay
            OnProgress("[Docker] Waiting for MSSQL to start...");
            await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 20);
        }

        /// <summary>
        /// Creates a database instance within the shared MSSQL container for a student.
        /// This ensures each student has their own isolated database even when sharing the container.
        /// </summary>
        /// <param name="config">Docker grading configuration</param>
        /// <param name="databaseName">Name of the database to create (e.g., Library_student1)</param>
        /// <param name="sqlScriptPath">Optional path to SQL initialization script on host machine</param>
        private async Task CreateDatabaseInstanceAsync(DockerGradingConfig config, string databaseName, string? sqlScriptPath = null)
        {
            var databaseContainer = config.DatabaseContainerName;
            var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
            var databaseUsername = config.DatabaseUsername ?? "sa";

            // SECURITY: Validate database name to prevent SQL injection
            // Database names should only contain alphanumeric characters, underscores, and hyphens
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_\-]+$"))
            {
                throw new ArgumentException($"Invalid database name '{databaseName}'. Database names must contain only letters, numbers, underscores, and hyphens.", nameof(databaseName));
            }

            OnProgress($"[Database] Creating database instance '{databaseName}' in container {databaseContainer}");

            try
            {
                // Step 1: Check if database already exists
                var checkDbSql = $"SELECT name FROM sys.databases WHERE name = '{databaseName}'";
                var checkCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";

                var (checkSuccess, checkOutput) = _dockerExecutor.ExecDockerCommandWithOutput(checkCommand, 5000);

                if (checkSuccess && checkOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Database '{databaseName}' already exists, dropping it first");

                    // Drop existing database
                    var dropSql = $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
                    var dropCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{dropSql}\"";
                    _dockerExecutor.ExecDockerCommand(dropCommand, 10000);

                    OnProgress($"[Database] Dropped existing database '{databaseName}'");
                    await Task.Delay(1000); // Wait for drop to complete
                }

                // Step 2: Create database
                if (!string.IsNullOrEmpty(sqlScriptPath) && File.Exists(sqlScriptPath))
                {
                    // Create database from SQL script
                    OnProgress($"[Database] Creating database '{databaseName}' from SQL script: {sqlScriptPath}");

                    // Copy SQL script to container
                    var containerSqlPath = $"/tmp/{databaseName}.sql";
                    _dockerExecutor.CopyFileToContainer(databaseContainer, sqlScriptPath, containerSqlPath);

                    // Execute SQL script
                    var execScriptCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -i {containerSqlPath}";
                    var (scriptSuccess, scriptOutput) = _dockerExecutor.ExecDockerCommandWithOutput(execScriptCommand, 30000);

                    if (scriptSuccess)
                    {
                        OnProgress($"[Database] Successfully created database '{databaseName}' from script");
                    }
                    else
                    {
                        OnProgress($"[Database] WARNING: Failed to create database from script: {scriptOutput}");
                        OnProgress($"[Database] SQL server container will remain running, but database instance was not created");
                        throw new Exception($"Failed to create database from SQL script: {scriptOutput}");
                    }
                }
                else
                {
                    // Create empty database (no SQL script provided)
                    OnProgress($"[Database] Creating empty database '{databaseName}' (no SQL script provided)");

                    var createDbSql = $"CREATE DATABASE [{databaseName}]";
                    var createCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{createDbSql}\"";

                    var (createSuccess, createOutput) = _dockerExecutor.ExecDockerCommandWithOutput(createCommand, 10000);

                    if (createSuccess)
                    {
                        OnProgress($"[Database] Successfully created empty database '{databaseName}'");
                    }
                    else
                    {
                        OnProgress($"[Database] WARNING: Failed to create database: {createOutput}");
                        OnProgress($"[Database] SQL server container will remain running, but database instance was not created");
                        throw new Exception($"Failed to create database: {createOutput}");
                    }
                }

                // Step 3: Verify database was created
                var verifyCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";
                var (verifySuccess, verifyOutput) = _dockerExecutor.ExecDockerCommandWithOutput(verifyCommand, 5000);

                if (verifySuccess && verifyOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Verified database '{databaseName}' exists and is ready");
                }
                else
                {
                    OnProgress($"[Database] WARNING: Could not verify database '{databaseName}' exists");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database] WARNING: Failed to create database instance '{databaseName}': {ex.Message}");
                OnProgress($"[Database] Skipping database creation but keeping SQL server container running");
                // Don't throw - allow grading to continue without database
                // The SQL server container will remain running for the session
            }
        }

        /// <summary>
        /// Waits for a container to be in running state with efficient polling.
        /// Uses short intervals without logging to avoid spam while ensuring container is ready.
        /// </summary>
        private async Task WaitForContainerRunningAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 2; // Check every 500ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (_dockerExecutor.IsContainerRunning(containerName))
                {
                    // Container is running, give it a moment to fully initialize
                    await Task.Delay(500);
                    return;
                }
                await Task.Delay(500); // Check every 500ms without logging
            }
            // If we get here, container didn't start in time but proceed anyway
            OnProgress($"[Docker] Warning: Container {containerName} may not be fully ready after {maxWaitSeconds}s");
        }

        /// <summary>
        /// OPTIMIZATION: Dynamically waits for a container to be removed instead of fixed delays.
        /// Checks every 100ms up to maxWaitSeconds. Returns immediately when container is gone.
        /// Much faster than fixed waits - typically returns in 0-200ms vs 500ms+ fixed delay.
        /// </summary>
        private async Task WaitForContainerRemovedAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 10; // Check every 100ms
            for (int i = 0; i < maxAttempts; i++)
            {
                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    // Container is gone - return immediately
                    OnProgress($"[Docker Cleanup] Container {containerName} successfully removed (waited {i * 100}ms)");
                    return;
                }
                await Task.Delay(100); // Check every 100ms without logging
            }

            // CRITICAL: Container still exists after max wait - this is a zombie container
            OnProgress($"[Docker Cleanup] WARNING: Container {containerName} still exists after {maxWaitSeconds}s - attempting force removal");

            // Try force removal with -f flag
            try
            {
                var forceCommand = $"rm -f {containerName}";
                _dockerExecutor.ExecDockerCommand(forceCommand, 5000);
                OnProgress($"[Docker Cleanup] Force removal attempted for {containerName}");

                // Wait a bit more to see if force removal worked
                await Task.Delay(1000);

                if (!_dockerExecutor.IsContainerExist(containerName))
                {
                    OnProgress($"[Docker Cleanup] Force removal successful for {containerName}");
                }
                else
                {
                    OnProgress($"[Docker Cleanup] CRITICAL: Container {containerName} is a zombie - cannot be removed. This may cause resource exhaustion!");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Cleanup] ERROR: Force removal failed for {containerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// OPTIMIZATION: Dynamically waits for processes to be killed in a container.
        /// Checks every 50ms up to maxWaitMs. Returns immediately when no target processes remain.
        /// Much faster than fixed waits - typically returns in 0-100ms vs 100ms+ fixed delay.
        /// </summary>
        /// <summary>
        /// Checks Docker container count and warns if approaching limits.
        /// CRITICAL for batch grading 200+ students to prevent resource exhaustion.
        /// </summary>
        private void CheckDockerContainerLimit()
        {
            try
            {
                // Count total containers (running + stopped)
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput("ps -a -q", 5000);
                if (success)
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var totalContainers = containerIds.Length;

                    OnProgress($"[Docker Resource Monitor] Total containers: {totalContainers}");

                    // Docker default limit is typically 256-512 containers per daemon
                    // Warn at 50% and 75% thresholds
                    if (totalContainers > 380) // 75% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] CRITICAL WARNING: {totalContainers} containers exist! Approaching Docker daemon limit. Container creation may fail soon!");
                    }
                    else if (totalContainers > 256) // 50% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] WARNING: {totalContainers} containers exist. Consider aggressive cleanup to prevent exhaustion.");
                    }
                    else if (totalContainers > 128) // 25% of 512
                    {
                        OnProgress($"[Docker Resource Monitor] Info: {totalContainers} containers exist. Monitoring for potential exhaustion.");
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Resource Monitor] Warning: Could not check container count: {ex.Message}");
            }
        }

        /// <summary>
        /// Aggressively cleans up old auto-grading containers that may have been left behind.
        /// CRITICAL for batch grading 200+ students to prevent Docker exhaustion.
        /// </summary>
        private void AggressiveCleanupOldContainers()
        {
            OnProgress("[Docker Aggressive Cleanup] Starting cleanup of old auto-grading containers...");

            try
            {
                // Find all auto-grading containers (ag-server-*, ag-client-*)
                var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(
                    "ps -a --filter 'name=ag-server-' --filter 'name=ag-client-' -q", 5000);

                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    var containerIds = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    OnProgress($"[Docker Aggressive Cleanup] Found {containerIds.Length} old auto-grading containers to remove");

                    foreach (var containerId in containerIds)
                    {
                        try
                        {
                            _dockerExecutor.ExecDockerCommand($"rm -f {containerId}", 5000);
                        }
                        catch
                        {
                            // Ignore individual failures, continue with cleanup
                        }
                    }

                    OnProgress($"[Docker Aggressive Cleanup] Cleanup complete. Removed {containerIds.Length} containers.");
                }
                else
                {
                    OnProgress("[Docker Aggressive Cleanup] No old containers found.");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Docker Aggressive Cleanup] Warning: Cleanup encountered errors: {ex.Message}");
            }
        }

        private async Task WaitForProcessesKilledAsync(string containerName, string processPattern, int maxWaitMs = 500)
        {
            var maxAttempts = maxWaitMs / 50; // Check every 50ms
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    // Check if any target processes still exist
                    var command = $"{containerName} sh -c \"ps aux | grep '{processPattern}' | grep -v grep | wc -l\"";
                    var (success, output) = _dockerExecutor.ExecDockerCommandWithOutput(command, 1000);

                    if (success && int.TryParse(output.Trim(), out int count) && count == 0)
                    {
                        // All processes killed - return immediately
                        return;
                    }
                }
                catch
                {
                    // Error checking processes - assume they're gone
                    return;
                }

                await Task.Delay(50); // Check every 50ms without logging
            }
            // If we get here, some processes may still exist but proceed anyway
        }


        /// <summary>
        /// Copy DLLs and appsettings to unified container in SEPARATE folders.
        /// Server goes to /apps/server, Client goes to /apps/client.
        /// This ensures appsettings.json and DLL mod fallback work correctly.
        /// </summary>
        private async Task CopyFilesToUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer)
        {
            var dllModService = new DllModificationService();
            var tempDirectories = new List<string>();

            try
            {
                // Create /apps/server and /apps/client directories in container
                _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/server");
                _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/client");

                // Copy SERVER files (without DLL modification - that's a fallback after appsettings)
                if (!string.IsNullOrEmpty(serverDllPath))
                {
                    var serverDir = Path.GetDirectoryName(serverDllPath);
                    if (serverDir != null)
                    {
                        try
                        {
                            // Copy original files to /apps/server
                            // DLL modification will be applied later as a fallback if appsettings.json is not found
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/server/AutoGrading_UnifiedServer_*/
                            // With "/.": creates /apps/server/*.dll, /apps/server/appsettings.json, etc.
                            var serverSource = serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(serverSource, $"{unifiedContainer}:/apps/server/");
                            OnProgress($"[Unified] Copied server files to /apps/server (DLL mod will be applied as fallback if needed)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Server copy failed: {ex.Message}");
                        }
                    }
                }

                // Copy CLIENT files (without DLL modification - that's a fallback after appsettings)
                if (!string.IsNullOrEmpty(clientDllPath))
                {
                    var clientDir = Path.GetDirectoryName(clientDllPath);
                    if (clientDir != null)
                    {
                        try
                        {
                            // Copy original files to /apps/client
                            // DLL modification will be applied later as a fallback if appsettings.json is not found
                            // CRITICAL: Append "/." to copy directory CONTENTS, not the directory itself
                            // Without "/.": creates /apps/client/AutoGrading_UnifiedClient_*/
                            // With "/.": creates /apps/client/*.dll, /apps/client/appsettings.json, etc.
                            var clientSource = clientDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                            _dockerExecutor.CopyFileToContainer(clientSource, $"{unifiedContainer}:/apps/client/");
                            OnProgress($"[Unified] Copied client files to /apps/client (DLL mod will be applied as fallback if needed)");
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"[Unified] WARNING: Client copy failed: {ex.Message}");
                        }
                    }
                }

                // Configure appsettings.json (modify existing or apply DLL mod as fallback)
                ConfigureAppsettingsInUnifiedContainer(config, testKitConfig, unifiedContainer, _currentStudentCode ?? "Unknown");
            }
            finally
            {
                // Cleanup temp directories (if any were created for DLL modification fallback)
                foreach (var tempDir in tempDirectories)
                {
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch (Exception cleanEx)
                    {
                        OnProgress($"[Unified] WARNING: Failed to cleanup temp directory {tempDir}: {cleanEx.Message}");
                    }
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Recursively copies a directory and all its contents to a new location.
        /// Used for creating temporary staging areas for DLL modification.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
            }

            // Create destination directory
            Directory.CreateDirectory(destDir);

            // Copy all files
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destDir, file.Name);
                file.CopyTo(targetFilePath, overwrite: true);
            }

            // Recursively copy subdirectories
            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestDir = Path.Combine(destDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestDir);
            }
        }

        /// <summary>
        /// Generate appsettings.json for UNIFIED container.
        /// Server goes to /apps/server/appsettings.json, Client goes to /apps/client/appsettings.json.
        /// Both use localhost (127.0.0.1) for communication within the same container.
        /// </summary>
        /// <summary>
        /// Configures appsettings.json in the unified container using modification-first approach:
        /// 1. Check if appsettings.json exists in /apps/server and /apps/client
        /// 2. If exists: Modify only Port, IpAddress, ConnectionString (preserves student settings)
        /// 3. If not exists AND UseDllModificationFallback=true: DLL mod already applied during copy
        /// 4. If not exists AND UseDllModificationFallback=false: Log warning (may fail at runtime)
        /// 
        /// This approach respects student configuration while enabling grading.
        /// 
        /// Connection String Logic:
        /// - If UseSharedDatabaseContainer=true: Connects to Student_{StudentCode} database on shared container
        /// - If UseSharedDatabaseContainer=false: Connects to database specified in testKitConfig
        /// </summary>
        private void ConfigureAppsettingsInUnifiedContainer(
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer,
            string studentCode)
        {
            // Build connection string based on database container architecture
            string connectionString;
            if (config.UseSharedDatabaseContainer)
            {
                // Shared container: Each student gets Student_{StudentCode} database
                connectionString = ConnectionStringHelper.BuildForStudentDatabase(
                    config.SharedDatabasePort,
                    studentCode,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
                OnProgress($"[Database] Using shared container with database: Student_{studentCode}");
            }
            else
            {
                // Legacy: Use database name from testKitConfig (e.g., Library_StudentCode)
                connectionString = ConnectionStringHelper.BuildForDocker(
                    config.DatabaseContainerHostPort,
                    testKitConfig.DatabaseName,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
                OnProgress($"[Database] Using per-student container with database: {testKitConfig.DatabaseName}");
            }

            // UNIFIED CONTAINER: Both client and server use localhost (127.0.0.1)
            var serverIpAddress = "127.0.0.1";  // Bind to localhost
            var clientIpAddress = "127.0.0.1";  // Connect to localhost
            var port = config.CodeContainerInternalPort;

            OnProgress($"[Unified] Configuring appsettings for localhost communication (127.0.0.1:{port})");

            // Try to modify SERVER appsettings if it exists
            // If appsettings.json doesn't exist and DLL mod is enabled, apply DLL modification as fallback
            TryModifyAppsettingsOrDllModInContainer(
                unifiedContainer,
                "/apps/server",
                "/apps/server/appsettings.json",
                serverIpAddress,
                port,
                connectionString,
                "Server",
                config.ServerProjectName,
                isServer: true,
                dllModFallbackEnabled: config.UseDllModificationFallback);

            // Try to modify CLIENT appsettings if it exists
            // If appsettings.json doesn't exist and DLL mod is enabled, apply DLL modification as fallback
            TryModifyAppsettingsOrDllModInContainer(
                unifiedContainer,
                "/apps/client",
                "/apps/client/appsettings.json",
                clientIpAddress,
                port,
                null, // Client doesn't need connection string
                "Client",
                config.ClientProjectName,
                isServer: false,
                dllModFallbackEnabled: config.UseDllModificationFallback);
        }

        /// <summary>
        /// Attempts to modify an existing appsettings.json file inside a container.
        /// If the file doesn't exist AND DLL mod fallback is enabled, applies DLL modification instead.
        /// 
        /// NEW BEHAVIOR (as requested by @dongnuc):
        /// 1. First, try to modify appsettings.json if it exists in the container
        /// 2. If appsettings.json doesn't exist AND dllModFallbackEnabled is true:
        ///    - Download DLLs from container
        ///    - Apply DLL modification
        ///    - Upload modified DLLs back to container
        /// 3. If appsettings.json doesn't exist AND dllModFallbackEnabled is false:
        ///    - Log warning that grading may fail
        /// </summary>
        private void TryModifyAppsettingsOrDllModInContainer(
            string container,
            string containerDir,
            string appsettingsPath,
            string ipAddress,
            int port,
            string? connectionString,
            string componentName,
            string projectName,
            bool isServer,
            bool dllModFallbackEnabled)
        {
            // Check if appsettings.json exists
            var checkCmd = $"{container} test -f {appsettingsPath}";
            var (exists, _) = _dockerExecutor.ExecDockerCommandWithOutput(checkCmd, 3000);

            if (!exists)
            {
                // Appsettings.json not found - use DLL modification as fallback if enabled
                if (dllModFallbackEnabled)
                {
                    OnProgress($"[Unified] {componentName} appsettings not found at {appsettingsPath} - applying DLL modification fallback");
                    ApplyDllModificationInContainer(container, containerDir, componentName, projectName, isServer, port, ipAddress);
                }
                else
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings not found at {appsettingsPath} and DLL mod is disabled - may fail at runtime");
                }
                return;
            }

            // Appsettings exists - download, modify, upload
            OnProgress($"[Unified] Found {componentName} appsettings at {appsettingsPath}, modifying...");

            string? tempFile = null;
            try
            {
                // Download appsettings from container
                tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_{componentName}_{Guid.NewGuid()}.json");
                var copyFromCmd = $"docker cp {container}:{appsettingsPath} \"{tempFile}\"";
                var copyResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 5000);

                if (copyResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] WARNING: Failed to download {componentName} appsettings for modification");
                    return;
                }

                // Modify the file
                var modified = ModifyAppsettingsFile(tempFile, ipAddress, port, connectionString, componentName);

                if (!modified)
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings modification failed or no changes needed");
                    return;
                }

                // Upload modified appsettings back to container
                _dockerExecutor.CopyFileToContainer(tempFile, $"{container}:{appsettingsPath}");
                OnProgress($"[Unified] {componentName} appsettings modified: IpAddress={ipAddress}, Port={port}");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR modifying {componentName} appsettings: {ex.Message}");
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>
        /// Applies DLL modification to files inside a container.
        /// Downloads DLLs, modifies them, and uploads them back.
        /// This is used as a fallback when appsettings.json is not found.
        /// </summary>
        private void ApplyDllModificationInContainer(
            string container,
            string containerDir,
            string componentName,
            string projectName,
            bool isServer,
            int targetPort,
            string targetIp)
        {
            string? tempDir = null;
            try
            {
                // Create temp directory for DLL modification
                tempDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_DllMod_{componentName}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                // Download files from container
                var copyFromCmd = $"docker cp {container}:{containerDir}/. \"{tempDir}\"";
                OnProgress($"[Unified] Downloading {componentName} files from container for DLL modification...");
                var downloadResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 10000);

                if (downloadResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] ERROR: Failed to download {componentName} files from container: {downloadResult.ErrorToString()}");
                    return;
                }

                // Apply DLL modification
                var dllModService = new DllModificationService();
                var result = dllModService.CheckAndPatchIfNeeded(
                    tempDir,
                    projectName,
                    isServer,
                    targetPort,
                    targetIp);

                OnProgress($"[Unified] {componentName} DLL mod fallback: {result.GetSummary()}");

                if (!result.Success && result.RequiresDllModification)
                {
                    OnProgress($"[Unified] WARNING: {componentName} DLL modification failed - student code may not work correctly");
                    return;
                }

                // Upload modified files back to container
                var tempSource = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                OnProgress($"[Unified] Uploading modified {componentName} DLLs back to container...");
                _dockerExecutor.CopyFileToContainer(tempSource, $"{container}:{containerDir}/");
                OnProgress($"[Unified] {componentName} DLL modification applied successfully in container");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR applying {componentName} DLL modification in container: {ex.Message}");
            }
            finally
            {
                // Cleanup temp directory
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception cleanEx)
                    {
                        OnProgress($"[Unified] WARNING: Failed to cleanup temp directory {tempDir}: {cleanEx.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Modifies an appsettings.json file, preserving all existing settings while updating specific values.
        /// Returns true if modification was successful.
        /// </summary>
        private bool ModifyAppsettingsFile(string filePath, string ipAddress, int port, string? connectionString, string componentName)
        {
            try
            {
                var jsonText = File.ReadAllText(filePath);
                var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(jsonText);

                if (jsonNode == null || jsonNode is not System.Text.Json.Nodes.JsonObject jsonObj)
                {
                    OnProgress($"[Unified] ERROR: Invalid JSON in {componentName} appsettings");
                    return false;
                }

                var modified = false;

                // Update ConnectionStrings.MyCnn if it exists (server only)
                if (connectionString != null && jsonObj["ConnectionStrings"] is System.Text.Json.Nodes.JsonObject connStrings)
                {
                    if (connStrings["MyCnn"] != null)
                    {
                        connStrings["MyCnn"] = connectionString;
                        modified = true;
                        OnProgress($"[Unified] Updated {componentName} ConnectionStrings.MyCnn");
                    }
                }

                // Update IpAddress if it exists
                if (jsonObj["IpAddress"] != null)
                {
                    jsonObj["IpAddress"] = ipAddress;
                    modified = true;
                }

                // Update Port if it exists (handle both string and number formats)
                if (jsonObj["Port"] != null)
                {
                    var originalPort = jsonObj["Port"];
                    if (originalPort?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    {
                        jsonObj["Port"] = port.ToString();
                    }
                    else
                    {
                        jsonObj["Port"] = port;
                    }
                    modified = true;
                }

                if (modified)
                {
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(filePath, jsonNode.ToJsonString(options));
                    return true;
                }
                else
                {
                    OnProgress($"[Unified] WARNING: No matching properties found to modify in {componentName} appsettings");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR modifying {componentName} appsettings file: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
