using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using EnvironmentBuilder.CommandSupporter;
using Domain.Models;
using Domain.Entities.Docker.DockerSupporter.Entity;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Helpers;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for setting up Docker containers for grading.
    /// Handles:
    /// - Unified container creation (client + server in same container)
    /// - Database container setup and initialization
    /// - File copying to containers
    /// - Appsettings.json configuration
    /// - DLL modification fallback
    /// </summary>
    public sealed class DockerContainerSetupService
    {
        private const string DefaultDatabasePassword = "YourStrong@Passw0rd";
        
        private readonly DockerCommandExecutor _dockerExecutor;
        private readonly CommandExecutor _commandExecutor;
        
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        public DockerContainerSetupService()
        {
            _dockerExecutor = new DockerCommandExecutor();
            _commandExecutor = _dockerExecutor.GetCommandExecutor();
        }
        
        /// <summary>
        /// Setup unified container that runs both client and server processes.
        /// Processes are managed by supervisord and started/stopped by test case actions.
        /// CLIENT AND SERVER ARE NOT STARTED AUTOMATICALLY - they start only when test case Detail.xlsx says so.
        /// Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
        /// </summary>
        public async Task SetupUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer,
            string? studentCode)
        {
            OnProgress($"[SETUP] Creating unified container: {unifiedContainer}");
            
            // Remove existing unified container if any
            _commandExecutor.RunCommand($"docker rm -f {unifiedContainer} 2>/dev/null || true", null, null, 10000);
            
            // Create the unified container with supervisord
            // NOTE: --cap-add=NET_ADMIN is required for the container entrypoint to enable 'quickack'
            // on the loopback interface. This forces proper 4-way TCP close sequence.
            var dockerCmd = $"docker run -d --name {unifiedContainer} " +
                           $"--network {config.DockerNetwork} " +
                           $"--cap-add=NET_ADMIN " +
                           $"-t " +
                           $"{config.CodeImageName}";
            
            _commandExecutor.RunCommand(dockerCmd, null, null, 30000);
            OnProgress($"[SETUP] Unified container ready - supervisord running, processes idle");
            
            // Wait for supervisord to be ready
            await Task.Delay(1000);
            
            // Copy DLLs and appsettings to container
            await CopyFilesToUnifiedContainerAsync(
                serverDllPath, 
                clientDllPath, 
                config, 
                testKitConfig,
                unifiedContainer,
                studentCode);
            
            OnProgress($"[Unified] Container ready - processes will start when test cases execute StartClient/StartServer actions");
        }
        
        /// <summary>
        /// Sets up the MSSQL database container if not already running.
        /// The database container is shared between students for efficiency.
        /// </summary>
        public async Task SetupDatabaseContainerAsync(DockerGradingConfig config)
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
            OnProgress($"[Docker] Database container {databaseContainer} created with port {config.DatabaseContainerHostPort}:{config.DatabaseContainerInternalPort}");
            
            // Wait for MSSQL to fully start
            OnProgress("[Docker] Waiting for MSSQL to start...");
            await WaitForContainerRunningAsync(databaseContainer, maxWaitSeconds: 20);
        }
        
        /// <summary>
        /// Creates a database instance within the shared MSSQL container for a student.
        /// </summary>
        public async Task CreateDatabaseInstanceAsync(
            DockerGradingConfig config, 
            string databaseName, 
            string? sqlScriptPath = null)
        {
            var databaseContainer = config.DatabaseContainerName;
            var databasePassword = config.DatabasePassword ?? DefaultDatabasePassword;
            var databaseUsername = config.DatabaseUsername ?? "sa";
            
            // SECURITY: Validate database name to prevent SQL injection
            if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_\-]+$"))
            {
                throw new ArgumentException($"Invalid database name '{databaseName}'.", nameof(databaseName));
            }
            
            OnProgress($"[Database] Creating database instance '{databaseName}' in container {databaseContainer}");
            
            try
            {
                // Check if database already exists
                var checkDbSql = $"SELECT name FROM sys.databases WHERE name = '{databaseName}'";
                var checkCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";
                
                var (checkSuccess, checkOutput) = _dockerExecutor.ExecDockerCommandWithOutput(checkCommand, 5000);
                
                if (checkSuccess && checkOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Database '{databaseName}' already exists, dropping it first");
                    
                    var dropSql = $"USE master; ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
                    var dropCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{dropSql}\"";
                    _dockerExecutor.ExecDockerCommand(dropCommand, 10000);
                    
                    OnProgress($"[Database] Dropped existing database '{databaseName}'");
                    await Task.Delay(1000);
                }
                
                // Create database
                if (!string.IsNullOrEmpty(sqlScriptPath) && File.Exists(sqlScriptPath))
                {
                    OnProgress($"[Database] Creating database '{databaseName}' from SQL script: {sqlScriptPath}");
                    
                    var containerSqlPath = $"/tmp/{databaseName}.sql";
                    _dockerExecutor.CopyFileToContainer(databaseContainer, sqlScriptPath, containerSqlPath);
                    
                    var execScriptCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -i {containerSqlPath}";
                    var (scriptSuccess, scriptOutput) = _dockerExecutor.ExecDockerCommandWithOutput(execScriptCommand, 30000);
                    
                    if (scriptSuccess)
                    {
                        OnProgress($"[Database] Successfully created database '{databaseName}' from script");
                    }
                    else
                    {
                        throw new Exception($"Failed to create database from SQL script: {scriptOutput}");
                    }
                }
                else
                {
                    OnProgress($"[Database] Creating empty database '{databaseName}'");
                    
                    var createDbSql = $"CREATE DATABASE [{databaseName}]";
                    var createCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{createDbSql}\"";
                    
                    var (createSuccess, createOutput) = _dockerExecutor.ExecDockerCommandWithOutput(createCommand, 10000);
                    
                    if (!createSuccess)
                    {
                        throw new Exception($"Failed to create database: {createOutput}");
                    }
                }
                
                // Verify database was created
                var verifyCommand = $"exec {databaseContainer} /opt/mssql-tools/bin/sqlcmd -S localhost -U {databaseUsername} -P \"{databasePassword}\" -Q \"{checkDbSql}\" -h -1";
                var (verifySuccess, verifyOutput) = _dockerExecutor.ExecDockerCommandWithOutput(verifyCommand, 5000);
                
                if (verifySuccess && verifyOutput.Contains(databaseName))
                {
                    OnProgress($"[Database] Verified database '{databaseName}' exists and is ready");
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[Database] WARNING: Failed to create database instance '{databaseName}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Copy DLLs and appsettings to unified container in SEPARATE folders.
        /// Server goes to /apps/server, Client goes to /apps/client.
        /// </summary>
        private async Task CopyFilesToUnifiedContainerAsync(
            string? serverDllPath,
            string? clientDllPath,
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer,
            string? studentCode)
        {
            // Create directories in container
            _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/server");
            _dockerExecutor.MakeDirectory(unifiedContainer, "/apps/client");
            
            // Copy SERVER files
            if (!string.IsNullOrEmpty(serverDllPath))
            {
                var serverDir = Path.GetDirectoryName(serverDllPath);
                if (serverDir != null)
                {
                    try
                    {
                        var serverSource = serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                        _dockerExecutor.CopyFileToContainer(serverSource, $"{unifiedContainer}:/apps/server/");
                        OnProgress($"[Unified] Copied server files to /apps/server");
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[Unified] WARNING: Server copy failed: {ex.Message}");
                    }
                }
            }
            
            // Copy CLIENT files
            if (!string.IsNullOrEmpty(clientDllPath))
            {
                var clientDir = Path.GetDirectoryName(clientDllPath);
                if (clientDir != null)
                {
                    try
                    {
                        var clientSource = clientDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                        _dockerExecutor.CopyFileToContainer(clientSource, $"{unifiedContainer}:/apps/client/");
                        OnProgress($"[Unified] Copied client files to /apps/client");
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[Unified] WARNING: Client copy failed: {ex.Message}");
                    }
                }
            }
            
            // Configure appsettings.json
            ConfigureAppsettingsInUnifiedContainer(config, testKitConfig, unifiedContainer, studentCode ?? "Unknown");
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Configures appsettings.json in the unified container.
        /// </summary>
        private void ConfigureAppsettingsInUnifiedContainer(
            DockerGradingConfig config,
            TestKitConfig testKitConfig,
            string unifiedContainer,
            string studentCode)
        {
            // Build connection string
            string connectionString;
            if (config.UseSharedDatabaseContainer)
            {
                connectionString = ConnectionStringHelper.BuildForStudentDatabase(
                    config.SharedDatabasePort,
                    studentCode,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
            }
            else
            {
                connectionString = ConnectionStringHelper.BuildForDocker(
                    config.DatabaseContainerHostPort,
                    testKitConfig.DatabaseName,
                    config.DatabaseUsername,
                    config.DatabasePassword ?? DefaultDatabasePassword);
            }
            
            var serverIpAddress = "127.0.0.1";
            var clientIpAddress = "127.0.0.1";
            var port = config.CodeContainerInternalPort;
            
            OnProgress($"[Unified] Configuring appsettings for localhost communication (127.0.0.1:{port})");
            
            // Modify SERVER appsettings
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
            
            // Modify CLIENT appsettings
            TryModifyAppsettingsOrDllModInContainer(
                unifiedContainer,
                "/apps/client",
                "/apps/client/appsettings.json",
                clientIpAddress,
                port,
                null,
                "Client",
                config.ClientProjectName,
                isServer: false,
                dllModFallbackEnabled: config.UseDllModificationFallback);
        }
        
        /// <summary>
        /// Attempts to modify an existing appsettings.json file inside a container.
        /// If the file doesn't exist AND DLL mod fallback is enabled, applies DLL modification instead.
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
            var checkCmd = $"{container} test -f {appsettingsPath}";
            var (exists, _) = _dockerExecutor.ExecDockerCommandWithOutput(checkCmd, 3000);
            
            if (!exists)
            {
                if (dllModFallbackEnabled)
                {
                    OnProgress($"[Unified] {componentName} appsettings not found - applying DLL modification fallback");
                    ApplyDllModificationInContainer(container, containerDir, componentName, projectName, isServer, port, ipAddress);
                }
                else
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings not found and DLL mod is disabled");
                }
                return;
            }
            
            OnProgress($"[Unified] Found {componentName} appsettings at {appsettingsPath}, modifying...");
            
            string? tempFile = null;
            try
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"appsettings_{componentName}_{Guid.NewGuid()}.json");
                var copyFromCmd = $"docker cp {container}:{appsettingsPath} \"{tempFile}\"";
                var copyResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 5000);
                
                if (copyResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] WARNING: Failed to download {componentName} appsettings for modification");
                    return;
                }
                
                var modified = ModifyAppsettingsFile(tempFile, ipAddress, port, connectionString, componentName);
                
                if (!modified)
                {
                    OnProgress($"[Unified] WARNING: {componentName} appsettings modification failed or no changes needed");
                    return;
                }
                
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
                tempDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_DllMod_{componentName}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                
                var copyFromCmd = $"docker cp {container}:{containerDir}/. \"{tempDir}\"";
                OnProgress($"[Unified] Downloading {componentName} files from container for DLL modification...");
                var downloadResult = _commandExecutor.RunCommandAndCaptureOutput(copyFromCmd, null, null, 10000);
                
                if (downloadResult.ExitCode != 0)
                {
                    OnProgress($"[Unified] ERROR: Failed to download {componentName} files from container");
                    return;
                }
                
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
                    OnProgress($"[Unified] WARNING: {componentName} DLL modification failed");
                    return;
                }
                
                var tempSource = tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/.";
                OnProgress($"[Unified] Uploading modified {componentName} DLLs back to container...");
                _dockerExecutor.CopyFileToContainer(tempSource, $"{container}:{containerDir}/");
                OnProgress($"[Unified] {componentName} DLL modification applied successfully");
            }
            catch (Exception ex)
            {
                OnProgress($"[Unified] ERROR applying {componentName} DLL modification: {ex.Message}");
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
        
        /// <summary>
        /// Modifies an appsettings.json file, preserving all existing settings while updating specific values.
        /// </summary>
        private bool ModifyAppsettingsFile(string filePath, string ipAddress, int port, string? connectionString, string componentName)
        {
            try
            {
                var jsonText = File.ReadAllText(filePath);
                var jsonNode = JsonNode.Parse(jsonText);
                
                if (jsonNode == null || jsonNode is not JsonObject jsonObj)
                {
                    OnProgress($"[Unified] ERROR: Invalid JSON in {componentName} appsettings");
                    return false;
                }
                
                var modified = false;
                
                // Update ConnectionStrings.MyCnn if it exists (server only)
                if (connectionString != null && jsonObj["ConnectionStrings"] is JsonObject connStrings)
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
                
                // Update Port if it exists
                if (jsonObj["Port"] != null)
                {
                    var originalPort = jsonObj["Port"];
                    if (originalPort?.GetValueKind() == JsonValueKind.String)
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
                    var options = new JsonSerializerOptions { WriteIndented = true };
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
        
        /// <summary>
        /// Waits for a container to be in running state with efficient polling.
        /// </summary>
        private async Task WaitForContainerRunningAsync(string containerName, int maxWaitSeconds)
        {
            var maxAttempts = maxWaitSeconds * 2;
            for (int i = 0; i < maxAttempts; i++)
            {
                if (_dockerExecutor.IsContainerRunning(containerName))
                {
                    await Task.Delay(500);
                    return;
                }
                await Task.Delay(500);
            }
            OnProgress($"[Docker] Warning: Container {containerName} may not be fully ready after {maxWaitSeconds}s");
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
