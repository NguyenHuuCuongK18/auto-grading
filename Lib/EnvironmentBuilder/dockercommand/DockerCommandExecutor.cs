using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Web;
using Domain.Entities.Docker.DockerSupporter.Entity;
using EnvironmentBuilder.CommandSupporter;

namespace EnvironmentBuilder.DockerCommand
{
    public class DockerCommandExecutor
    {
        private readonly CommandExecutor _commandExecutor;
        
        // CRITICAL FIX: Thread-safe cache for verified Docker images
        // Prevents race conditions in parallel batch grading where multiple threads
        // check the same image simultaneously and get inconsistent results
        private static readonly HashSet<string> _verifiedImages = new HashSet<string>();
        private static readonly object _verifiedImagesLock = new object();

        public DockerCommandExecutor()
        {
            _commandExecutor = new CommandExecutor();
        }

        #region DOCKER CMD
        public void ExecDockerCommand(string command, int timeoutInMilliseconds = 30000)
        {
            try
            {
                command = "docker exec " + command;
                var success = _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
                if (!success)
                {
                    // Command failed but didn't throw - this might be expected (e.g., pkill when no process exists)
                    // Don't throw, just return silently
                }
            }
            catch (Exception ex)
            {
                // log
                // throw
                throw new Exception($"Error while executing command. Details: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Executes a docker exec command and returns whether it succeeded.
        /// Unlike ExecDockerCommand, this returns a boolean indicating success.
        /// </summary>
        public bool TryExecDockerCommand(string command, int timeoutInMilliseconds = 30000)
        {
            try
            {
                command = "docker exec " + command;
                return _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Executes a docker exec command and returns the output.
        /// Used for commands like 'ps aux' where we need to parse the output.
        /// </summary>
        public (bool success, string output) ExecDockerCommandWithOutput(string command, int timeoutInMilliseconds = 30000)
        {
            try
            {
                command = "docker exec " + command;
                var result = _commandExecutor.RunCommandAndCaptureOutput(command, null, null, timeoutInMilliseconds);
                return (result.ExitCode == 0, string.Join("\n", result.Output));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
        #endregion

        #region I/O
        public void CopyFileToContainer(
            string sourcePath,
            string destinationPath,
            string newName = "")
        {
            try
            {
                string command = $"docker cp \"{sourcePath}\" \"{destinationPath}\"";
                if (!string.IsNullOrEmpty(newName))
                {
                    command += string.IsNullOrEmpty(newName) ? "" : $"/{newName}";
                }
                _commandExecutor.RunCommand(command, null, null, 10000);

            }
            catch (Exception ex)
            {
                // throw 
                throw new Exception($"Error while copy file from {sourcePath} to {destinationPath}. Details: {ex.Message}");
            }
        }

        public void CopyFolder(
            string sourceDirectory,
            string destinationDirectory)
        {
            try
            {
                Directory.CreateDirectory(destinationDirectory);

                foreach (string file in Directory.GetFiles(sourceDirectory))
                {
                    string destFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                    File.Copy(file, destFile, true);
                }

                foreach (string subDir in Directory.GetDirectories(sourceDirectory))
                {
                    string destSubDir = Path.Combine(destinationDirectory, Path.GetFileName(subDir));
                    CopyFolder(subDir, destSubDir);
                }
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while copy directory from {sourceDirectory} to {destinationDirectory}. Details: {ex.Message}");
            }
        }

        public void EditFile(
            string containerId,
            string filePath,
            string content)
        {
            try
            {
                // in case of shell syntax error
                string escapedContent = content.Replace("\"", "\\\"").Replace("'", "'\\''");

                // replace file content
                string command = $"docker exec {containerId} sh -c \"echo \\\"{escapedContent}\\\" > {filePath}\"";

                _commandExecutor.RunCommand(command, null, null, 500);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error replacing content of {filePath} in container {containerId}. Details: {ex.Message}");
            }
        }


        public void RemoveFile(string containerName, string sourceFilePath, bool isRootPermission = false)
        {
            try
            {
                string command = $"docker exec {(isRootPermission ? "-u root " : "")} {containerName} rm -f {sourceFilePath}";
                _commandExecutor.RunCommand(command);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while removing file {sourceFilePath}. Details: {ex.Message}");
            }
        }

        public void RemoveFolder(string containerName, string sourcePath)
        {
            try
            {
                string command = $"docker exec {containerName} rm -rf {sourcePath}";
                _commandExecutor.RunCommand(command);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while removing folder {sourcePath}. Details: {ex.Message}");
            }
        }

        public string CopyPcapFile(string containerName, string containerFilePath = "/tmp/capture.pcap", string fileName = "capture.pcap", int timeoutInMilliseconds = 120000)
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localFilePath = System.IO.Path.Combine(appDataPath, fileName);
            string command = $"docker cp {containerName}:{containerFilePath} \"{localFilePath}\"";

            try
            {
                bool success = _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);

                if (!success)
                {
                    throw new Exception("Failed to copy file from container.");
                }

                Console.WriteLine($"File copied to: {localFilePath}");
                return localFilePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error copying file: {ex.Message}");
                throw;
            }
        }

        //check folder exist
        public bool IsFolderExist(string containerName, string containerFolderPath, int timeoutInMilliseconds = 5000)
        {
            try
            {
                // Command to check if folder exists in /apps directory in the container
                string command = $"test -d {containerFolderPath}";

                // Run the command inside the container and capture the output and exit code
                CommandResult result = _commandExecutor.RunCommandAndCaptureOutput(
                    $"docker exec {containerName} sh -c \"{command}\"",
                   timeoutInMilliseconds: timeoutInMilliseconds
                );

                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {

                // log

                // throw
                throw new Exception($"Error while check folder {containerFolderPath} in {containerName}. Details: {ex.Message}");
            }
        }


        //make directory
        public void MakeDirectory(string containerName, string containerFolderPath, int timeoutInMilliseconds = 5000)
        {
            try
            {
                // Command to create a directory in the container
                string command = $"mkdir -p {containerFolderPath}";
                // Run the command inside the container
                _commandExecutor.RunCommand(
                    $"docker exec {containerName} sh -c \"{command}\"",
                    timeoutInMilliseconds: timeoutInMilliseconds
                );
            }
            catch (Exception ex)
            {
                // log
                // throw
                throw new Exception($"Error while creating folder {containerFolderPath} in {containerName}. Details: {ex.Message}");
            }
        }
        #endregion

        #region IMAGE
        public DockerBase BuildImage(string imageName, string workingDirectory = "", string dockerFileName = "Dockerfile", int timeoutInMilliseconds = 120000)
        {
            string command = $"set \"DOCKER_BUILDKIT=1\" && docker build --progress=plain -t {imageName} -f {dockerFileName} .";
            try
            {
                var output = _commandExecutor.RunCommandAndCaptureOutput(command, workingDirectory, null, timeoutInMilliseconds);

                if (output.ExitCode != 0)
                    throw new Exception(output.ErrorToString());

                return new DockerBase { ImageName = imageName };
            }
            catch (Exception ex)
            {
                throw new Exception($"Build Solution Failed! Detail: " + ex.Message);
            }
        }

        public void PullImage(string imageName)
        {
            try
            {
                string command = $"docker pull {imageName}";
                _commandExecutor.RunCommandWithRetry(command, null, null, 30000, 3, 5000);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error . Details: {ex.Message}");
            }
        }

        public DockerBase RemoveImage(string imageName, int timeoutInMilliseconds = 30000)
        {
            try
            {
                string command = $"docker rmi -f {imageName}";
                _commandExecutor.RunCommandWithoutExitCheck(command, null, null, timeoutInMilliseconds);
                return new DockerBase { ImageName = imageName };
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error . Details: {ex.Message}");
            }
        }

        public bool IsImageExists(string imageName)
        {
            try
            {
                string command = $"docker images -q {imageName}";
                var result = _commandExecutor.RunCommandAndCaptureOutput(command);
                return result.Output.Count > 0;
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while checking image named {imageName} existent!. Details: {ex.Message}");
            }
        }
        #endregion

        #region CONTAINER
        public void RunContainer(DockerBase dockerBase, int timeoutInMilliseconds = 120000)
        {
            try
            {
                if (!string.IsNullOrEmpty(dockerBase.DockerNetwork))
                    CreateNetwork(dockerBase.DockerNetwork, timeoutInMilliseconds);

                if (IsContainerExist(dockerBase.ContainerName))
                {
                    StartExistedContainer(dockerBase.ContainerName);
                    return;
                }

                string envVars = "";
                foreach (KeyValuePair<string, string> kv in dockerBase.EnvironmentVariables)
                    envVars += $"-e {kv.Key}={kv.Value} ";

                // Only add port mapping if HostPort > 0
                // Client containers typically don't need port mapping since they initiate connections
                // to the server within the Docker network (using container name as hostname)
                string portMapping = dockerBase.HostPort > 0 && dockerBase.ContainerPort > 0
                    ? $"-p {dockerBase.HostPort}:{dockerBase.ContainerPort} "
                    : "";

                string command = $@"docker run -d " +
                                 "--privileged " +
                                 $"--name {dockerBase.ContainerName} " +
                                 $"--network {dockerBase.DockerNetwork} " +
                                 $"{envVars} " +
                                 $"{portMapping}" +
                                 $"{dockerBase.ImageName}";
                _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while try to run container {dockerBase.ContainerName}. Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Run a container with TTY support (-t flag) for reliable console output capture.
        /// This is required when using docker attach to read output without buffering issues.
        /// Also supports additional Docker flags via DockerBase.AdditionalFlags property.
        /// </summary>
        /// <param name="dockerBase">Container configuration</param>
        /// <param name="timeoutInMilliseconds">Timeout for the operation</param>
        public void RunContainerWithTty(DockerBase dockerBase, int timeoutInMilliseconds = 120000)
        {
            try
            {
                if (!string.IsNullOrEmpty(dockerBase.DockerNetwork))
                    CreateNetwork(dockerBase.DockerNetwork, timeoutInMilliseconds);

                if (IsContainerExist(dockerBase.ContainerName))
                {
                    RemoveContainer(dockerBase.ContainerName, timeoutInMilliseconds);
                }

                string envVars = "";
                foreach (KeyValuePair<string, string> kv in dockerBase.EnvironmentVariables)
                    envVars += $"-e {kv.Key}={kv.Value} ";

                // Add -t flag for TTY allocation - this resolves output buffering issues
                // when using docker attach to read console output
                // Only add port mapping if HostPort > 0 (client containers don't need port mapping)
                string portMapping = dockerBase.HostPort > 0 && dockerBase.ContainerPort > 0
                    ? $"-p {dockerBase.HostPort}:{dockerBase.ContainerPort} "
                    : "";
                
                // Include additional flags if provided (e.g., --add-host for host.docker.internal)
                string additionalFlags = !string.IsNullOrEmpty(dockerBase.AdditionalFlags)
                    ? $"{dockerBase.AdditionalFlags} "
                    : "";
                    
                string command = $@"docker run -d -t " +
                                 "--privileged " +
                                 $"--name {dockerBase.ContainerName} " +
                                 $"--network {dockerBase.DockerNetwork} " +
                                 $"{envVars} " +
                                 $"{portMapping}" +
                                 $"{additionalFlags}" +
                                 $"{dockerBase.ImageName}";
                _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
                Console.WriteLine($"[Docker] Container {dockerBase.ContainerName} started with TTY support");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while try to run container {dockerBase.ContainerName} with TTY. Details: {ex.Message}");
            }
        }


        public void RemoveContainer(string containerName, int timeoutInMilliseconds = 30000)
        {
            try
            {
                string command = $"docker rm -f -v {containerName}";
                _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while removing container. Details: {ex.Message}");
            }
        }

        public void StopContainer(string containerName, int timeoutInMilliseconds = 30000)
        {
            try
            {
                string command = $"docker stop {containerName}";
                _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while stoping container. Details: {ex.Message}");
            }
        }

        public void StartExistedContainer(string containerName)
        {
            try
            {
                string command = $"docker start {containerName}";
                _commandExecutor.RunCommand(command);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while try to start container named {containerName}. Details: {ex.Message}");
            }
        }

        public void ConnectContainersToNetwork(string[] containerNames, string networkName, int timeoutInMilliseconds = 30000)
        {
            try
            {
                if (!string.IsNullOrEmpty(networkName))
                    CreateNetwork(networkName, timeoutInMilliseconds);

                foreach (var containerName in containerNames)
                {
                    string command = $"docker network connect {networkName} {containerName}";
                    _commandExecutor.RunCommand(command, null, null, timeoutInMilliseconds);
                }
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while connecting containers to network. Details: {ex.Message}");
            }
        }

        public string GetContainerLogs(string containerName)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = $"logs {containerName}",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return output;
            }
            catch (Exception)
            {
                return null;
            }
        }
        #endregion

        #region NETWORK
        public void CreateNetwork(string networkName, int timeoutInMilliseconds = 30000)
        {
            try
            {
                // CRITICAL FIX: Check if network already exists before creating
                // This prevents race conditions in parallel grading where multiple
                // students are graded simultaneously and all try to create the same network
                string checkCommand = $"docker network ls --format \"{{{{.Name}}}}\" --filter name=^{networkName}$";
                var result = _commandExecutor.RunCommandAndCaptureOutput(checkCommand, null, null, 10000);
                
                // If network exists, output will contain the network name
                bool networkExists = result.Output.Any(line => line.Trim().Equals(networkName, StringComparison.OrdinalIgnoreCase));
                
                if (networkExists)
                {
                    Console.WriteLine($"[Docker] Network {networkName} already exists, skipping creation");
                    return;
                }

                // create new network
                string createNetworkCommand = $"docker network create {networkName}";
                _commandExecutor.RunCommandWithoutExitCheck(createNetworkCommand, null, null, timeoutInMilliseconds);
                Console.WriteLine($"[Docker] Network {networkName} created successfully");
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while connecting containers to network. Details: {ex.Message}");
            }
        }

        public void RemoveNetwork(string networkName)
        {
            try
            {
                string removeNetworkCommand = $"docker network rm {networkName}";
                _commandExecutor.RunCommandWithoutExitCheck(removeNetworkCommand, null, null);
            }
            catch (Exception ex)
            {
                // log

                // throw
                throw new Exception($"Error while connecting containers to network. Details: {ex.Message}");
            }
        }
        #endregion

        #region UTILITIES
        public bool IsContainerExist(string containerName)
        {
            try
            {
                string command = $"docker ps -a -q -f name={containerName}";
                var result = _commandExecutor.RunCommandAndCaptureOutput(command);
                return result.Output.Count > 0;
            }
            catch (Exception ex)
            {
                // log 

                // return
                return false;
            }
        }

        public bool IsContainerRunning(string containerName)
        {
            try
            {
                string command = $"docker ps -q -f name={containerName}";
                var result = _commandExecutor.RunCommandAndCaptureOutput(command);
                return result.Output.Count > 0;
            }
            catch (Exception ex)
            {
                // log 

                // return
                return false;
            }
        }

        private bool IsContainerRunning(int port)
        {
            string url = $"http://localhost:{port}";
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = 5000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return true;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse errorResponse)
                {
                    return errorResponse.StatusCode != null ? true : false;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public bool WaitForContainerToRun(
            int port,
            int timeoutInMilliseconds = 10000,
            int checkIntervalMilliseconds = 100)
        {
            int elapsedMilliseconds = 0;

            while (elapsedMilliseconds < timeoutInMilliseconds)
            {
                if (IsContainerRunning(port))
                {
                    return true;
                }

                System.Threading.Thread.Sleep(checkIntervalMilliseconds);
                elapsedMilliseconds += checkIntervalMilliseconds;
            }

            throw new Exception("Container Server not run!");
        }

        public bool IsDockerRunning()
        {
            try
            {
                string command = "docker info";
                var result = _commandExecutor.RunCommandAndCaptureOutput(command);

                if (result.Errors != null && result.Errors.Any(e => e.Contains("error during connect") || e.Contains("ERROR")))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                // log 

                // return
                return false;
            }
        }
        
        /// <summary>
        /// Checks if a Docker image exists locally with retry logic for parallel grading reliability.
        /// 
        /// CRITICAL FIX: Uses caching and retry logic to prevent race conditions in parallel batch grading.
        /// When multiple threads check the same image simultaneously, Docker commands can return inconsistent
        /// results due to timing issues. This implementation:
        /// 1. Caches verified images to avoid redundant checks
        /// 2. Retries on failure to handle transient Docker issues
        /// 3. Uses thread-safe operations to prevent race conditions
        /// </summary>
        public bool IsImageExist(string imageName)
        {
            // Check cache first (thread-safe)
            lock (_verifiedImagesLock)
            {
                if (_verifiedImages.Contains(imageName))
                {
                    return true;
                }
            }
            
            // Try up to 3 times with small delays to handle transient Docker issues
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // Use 'docker image inspect' instead of 'docker images -q' for more reliable results
                    // inspect returns detailed JSON if image exists, or error if it doesn't
                    string command = $"docker image inspect {imageName}";
                    var result = _commandExecutor.RunCommandAndCaptureOutput(command, null, null, 10000);
                    
                    // If inspect succeeds and returns output, image exists
                    bool imageExists = result.ExitCode == 0 && result.Output.Any(line => !string.IsNullOrWhiteSpace(line));
                    
                    if (imageExists)
                    {
                        // Cache the result (thread-safe)
                        lock (_verifiedImagesLock)
                        {
                            _verifiedImages.Add(imageName);
                        }
                        Console.WriteLine($"[Docker] Image {imageName} verified (attempt {attempt})");
                        return true;
                    }
                    
                    // CRITICAL FIX: Don't immediately return false on first failure
                    // Docker commands can return inconsistent results under load
                    // Retry to ensure we're not getting a false negative
                    if (attempt < 3)
                    {
                        Console.WriteLine($"[Docker] Attempt {attempt}/3: Image {imageName} not found, retrying...");
                        Thread.Sleep(100 * attempt); // Exponential backoff: 100ms, 200ms
                    }
                    else
                    {
                        // After all retries, if still not found, it really doesn't exist
                        Console.WriteLine($"[Docker] Image {imageName} does not exist after {attempt} attempts");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Docker] Attempt {attempt}/3: Error checking if image {imageName} exists: {ex.Message}");
                    
                    if (attempt < 3)
                    {
                        // Wait a bit before retrying (exponential backoff)
                        Thread.Sleep(100 * attempt);
                    }
                    else
                    {
                        // Final attempt failed - assume image doesn't exist
                        Console.WriteLine($"[Docker] All attempts failed to check image {imageName}. Assuming it doesn't exist.");
                        return false;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Ensures a Docker image exists locally. If it doesn't exist, provides clear error message.
        /// This prevents silent failures and Docker pull hangs during grading.
        /// </summary>
        /// <param name="imageName">The name of the Docker image</param>
        /// <throws>Exception if the image doesn't exist with instructions on how to build it</throws>
        public void EnsureImageExists(string imageName)
        {
            if (!IsImageExist(imageName))
            {
                throw new Exception($"Docker image '{imageName}' does not exist locally.\n\n" +
                    $"Please build the image using one of these methods:\n" +
                    $"1. From DockerImage folder:\n" +
                    $"   docker build -t {imageName} ./DockerImage\n\n" +
                    $"2. If the image should be pulled from a registry:\n" +
                    $"   docker pull {imageName}\n\n" +
                    $"3. Check TestKit/[Question]/Environment.xlsx for the correct image name\n\n" +
                    $"Available images:\n{GetAvailableImages()}");
            }
        }
        
        /// <summary>
        /// Gets a list of all available Docker images for error messages.
        /// </summary>
        private string GetAvailableImages()
        {
            try
            {
                string command = "docker images --format \"{{.Repository}}:{{.Tag}}\"";
                var result = _commandExecutor.RunCommandAndCaptureOutput(command, null, null, 10000);
                
                if (result.Output.Any())
                {
                    return string.Join("\n", result.Output.Take(10)); // Show first 10 images
                }
                
                return "(No images found)";
            }
            catch
            {
                return "(Unable to list images)";
            }
        }
        #endregion

        #region DOTNET
        public bool WaitForPublishFileDeployment(string containerName, string appName, int maxWaitTimeMs = 70000, int checkIntervalMs = 1000)
        {
            try
            {
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {
                    // Check both the PID file and the actual process
                    string checkCommand = $"docker exec {containerName} /bin/bash -c \"APP_NAME={appName}; if [ -f \\\"/tmp/$APP_NAME.pid\\\" ] && [ -f \\\"/tmp/$APP_NAME.port\\\" ]; then PID=$(cat \\\"/tmp/$APP_NAME.pid\\\"); PORT=$(cat \\\"/tmp/$APP_NAME.port\\\"); PROC_OK=$(kill -0 $PID 2>/dev/null && echo \\\"OK\\\" || echo \\\"FAIL\\\"); NGINX_OK=$(grep -q \\\"location /$APP_NAME/\\\" /etc/nginx/nginx.conf && nginx -t >/dev/null 2>&1 && echo \\\"OK\\\" || echo \\\"FAIL\\\"); APP_TYPE=$(grep -q \\\"API configuration for $APP_NAME\\\" /etc/nginx/nginx.conf && echo \\\"webapi\\\" || echo \\\"webapp\\\"); if [ \\\"$APP_TYPE\\\" = \\\"webapi\\\" ]; then APP_RESPONSE=$(curl -s -m 1 \\\"http://localhost:$PORT/\\\" -H \\\"Accept: application/json\\\" -w \\\"%{{http_code}}\\\" -o /dev/null); else APP_RESPONSE=$(curl -s -m 1 \\\"http://localhost:$PORT/\\\" -w \\\"%{{http_code}}\\\" -o /dev/null); fi; if [ -n \\\"$APP_RESPONSE\\\" ]; then APP_OK=\\\"OK\\\"; else APP_OK=\\\"FAIL\\\"; fi; if [ \\\"$PROC_OK\\\" = \\\"OK\\\" ] && [ \\\"$NGINX_OK\\\" = \\\"OK\\\" ] && [ \\\"$APP_OK\\\" = \\\"OK\\\" ]; then echo \\\"FULLY DEPLOYED ($APP_TYPE) - Process:$PROC_OK, Port:$PORT, App:$APP_OK (HTTP $APP_RESPONSE), Nginx:$NGINX_OK\\\"; else echo \\\"PARTIAL DEPLOYMENT ($APP_TYPE) - Process:$PROC_OK, Port:$PORT, App:$APP_OK (HTTP $APP_RESPONSE), Nginx:$NGINX_OK\\\"; fi; else echo \\\"NOT DEPLOYED\\\"; fi\"\r\n";

                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains("fully"))
                        {
                            string port = output.Trim().Split(':')[1];
                            Console.WriteLine($"Application {appName} is deployed and running on port {port}. Output: {output}");
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting for {appName} deployment... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Deployment timeout for {appName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while checking deployment of {appName}. Details: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Starts an application inside a Docker container for grading purposes.
        /// 
        /// CRITICAL FOR GRADING: This method does NOT wait for the application to be "ready".
        /// It simply starts the application and returns after a brief delay to allow process startup.
        /// 
        /// Why we don't wait for port readiness:
        /// - Grading must handle students whose code fails (e.g., prints "Hello World" and exits)
        /// - Waiting for port would cause 30-second timeouts for broken student code
        /// - Test cases need to execute even if server exits immediately (to properly fail)
        /// - Client will get RST when trying to connect if server is not listening (this is expected)
        /// 
        /// The flow for grading is:
        /// 1. Start server (even if it exits immediately)
        /// 2. Start client
        /// 3. Client attempts connection (succeeds if server running, RST if not)
        /// 4. Capture network flow and outputs
        /// 5. Grade based on actual behavior
        /// </summary>
        public bool WaitForPublishConsoleFileDeployment(string containerName, string appName, string appPath, string expectedPort, int maxWaitTimeMs = 70000, int checkIntervalMs = 1000)
        {
            try
            {
                int expPort = int.Parse(expectedPort);

                StartApplicationInContainer(containerName, appName, appPath);

                // For grading, we don't wait for port readiness.
                // Just give the process a moment to start, then return.
                // This allows grading to proceed even if student's code exits immediately.
                Thread.Sleep(500); // Brief delay to allow process startup
                
                bool running = IsProcessRunning(containerName, appName);
                Console.WriteLine($"[{appName}] Application started. Process running: {running}");
                
                return true; // Always return true to allow grading to proceed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{appName}] Error starting application: {ex.Message}");
                return false;
            }
        }

        private void StartApplicationInContainer(string containerName, string appName, string appPath)
        {
            string inputPipe = $"/tmp/{appName}_input_pipe";
            string pidFile = $"/tmp/{appName}.pid";

            // IMPORTANT: Clean up any existing processes and pipes BEFORE starting
            // This prevents "Address already in use" errors when restarting applications
            Console.WriteLine($"[{appName}] Cleaning up any existing processes and pipes...");
            
            // Kill any existing application process using stored PID file (safer than pkill)
            // This avoids killing PID 1 which would terminate the container
            try
            {
                // Read the PID from the pid file and kill that specific process
                string killByPidCommand = $"{containerName} sh -c \"if [ -f {pidFile} ]; then kill -9 $(cat {pidFile}) 2>/dev/null || true; fi\"";
                ExecDockerCommand(killByPidCommand, 5000);
            }
            catch { /* Ignore - no PID file or process */ }
            
            // Fallback: Kill dotnet processes by finding PIDs (excluding PID 1)
            // This is safer than 'pkill -9 dotnet' which could kill the container's main process
            try
            {
                // Find dotnet PIDs (excluding PID 1) and kill them
                string safeDotnetKillCommand = $"{containerName} sh -c \"ps aux | grep dotnet | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"";
                ExecDockerCommand(safeDotnetKillCommand, 5000);
            }
            catch { /* Ignore - no processes to kill */ }
            
            // Kill any existing sleep processes keeping pipes open (excluding PID 1)
            try
            {
                string safeSleepKillCommand = $"{containerName} sh -c \"ps aux | grep 'sleep 10000' | grep -v grep | awk '{{if ($2 != 1) print $2}}' | xargs -r kill -9 2>/dev/null || true\"";
                ExecDockerCommand(safeSleepKillCommand, 5000);
            }
            catch { /* Ignore - no processes to kill */ }
            
            // Remove existing pipe if it exists (ignore errors)
            try
            {
                string removePipeCommand = $"{containerName} rm -f {inputPipe}";
                ExecDockerCommand(removePipeCommand, 5000);
            }
            catch { /* Ignore - pipe may not exist */ }
            
            // Remove existing log file (ignore errors)
            string logFile = $"/tmp/{appName}_output.log";
            try
            {
                string removeLogCommand = $"{containerName} rm -f {logFile}";
                ExecDockerCommand(removeLogCommand, 5000);
            }
            catch { /* Ignore */ }
            
            // Remove existing PID file (ignore errors)
            try
            {
                string removePidCommand = $"{containerName} rm -f {pidFile}";
                ExecDockerCommand(removePidCommand, 5000);
            }
            catch { /* Ignore */ }
            
            // Wait for port to be released after killing processes
            Thread.Sleep(500);

            string createInputFileCommand = $"{containerName} mkfifo \"{inputPipe}\"";
            ExecDockerCommand(createInputFileCommand, 60000);

            string startDoorstopCommand = $"-d {containerName} sh -c \"sleep 10000 > {inputPipe}\"";
            ExecDockerCommand(startDoorstopCommand, 60000);

            // Modified: Write output to BOTH /proc/1/fd/1 (docker logs) AND a log file
            // The log file provides reliable output capture even when docker logs has buffering issues
            // Also save the PID to a file for safe cleanup later
            string command = $"-d -i -e DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1 {containerName} sh -c \"stdbuf -oL -eL dotnet {appPath} 2>&1 < {inputPipe} | tee {logFile} > /proc/1/fd/1 & echo $! > {pidFile}\"";
            Console.WriteLine($"[{appName}] Starting application...");
            ExecDockerCommand(command, 60000);

            Console.WriteLine($"[{appName}] Application started. Monitor with: docker logs -f {containerName}");
        }

        /// <summary>
        /// Get the application output log from the container.
        /// This reads from the log file created by StartApplicationInContainer.
        /// </summary>
        /// <param name="containerName">Container name</param>
        /// <param name="appName">Application name</param>
        /// <returns>The application output</returns>
        public string GetApplicationLog(string containerName, string appName)
        {
            try
            {
                string logFile = $"/tmp/{appName}_output.log";
                string command = $"docker exec {containerName} cat {logFile}";
                return RunCommand(command);
            }
            catch
            {
                return "";
            }
        }

        private bool IsProcessRunning(string containerName, string processName)
        {
            string output = RunCommand($"docker top {containerName}");
            return output.Contains(processName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPortOpen(string containerName, int internalPort)
        {
            if (internalPort == -1) return true;
            string output = RunCommand($"docker container port {containerName} {internalPort}/tcp");
            return !string.IsNullOrWhiteSpace(output);
        }

        /// <summary>
        /// Runs a shell command and returns the output.
        /// Cross-platform: uses cmd.exe on Windows, /bin/bash on Linux/macOS.
        /// </summary>
        private string RunCommand(string cmd)
        {
            ProcessStartInfo psi;
            
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                // On Linux/macOS, use bash or sh
                var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
                psi = new ProcessStartInfo(shell, $"-c \"{cmd.Replace("\"", "\\\"")}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            using Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }

        public void SendInputToContainer(string containerName, string appName, string inputMessage)
        {
            string inputPipe = $"/tmp/{appName}_input_pipe";
            string safeInput = inputMessage.Replace("'", "'\\''");
            string command = $"{containerName} sh -c \"echo '{safeInput}' | tee /proc/1/fd/1 > {inputPipe}\"";
            Console.WriteLine($"[{appName}] Sending input: {inputMessage}");
            ExecDockerCommand(command, 5000);
        }
        #endregion

        #region JAVA
        //public void RemoveSqlFileInVolume(string sqlContainerName, string databaseName)
        //{
        //    // delete in /opt/mssql
        //    string command = $"docker exec {sqlContainerName} rm /var/opt/mssql/{databaseName}.sql";
        //    _commandExecutor.RunCommand(command);

        //    // delete in /data (volume)
        //    command = $"docker exec {sqlContainerName} sh -c \"find /var/opt/mssql/data -type f -name '*{databaseName}*' -delete\"";
        //    _commandExecutor.RunCommand(command);
        //}

        //public void RemoveDeployedFolder(string containerName, string folderName)
        //{
        //    try
        //    {
        //        string command = $"docker exec -it {containerName} rm -rf /usr/local/tomcat/webapps/{folderName}";
        //        _commandExecutor.RunCommand(command);

        //        string command2 = $"docker exec -it {containerName} rm -rf /apps/{folderName}";
        //        _commandExecutor.RunCommand(command2);
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Error while removing deployed folder. Detail: {ex.Message}");
        //    }
        //}

        public bool WaitForWarFileDeployment(string tomcatContainerName, string warFolderName, int maxWaitTimeMs = 15000, int checkIntervalMs = 500)
        {
            try
            {
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {
                    string checkCommand = $"docker exec {tomcatContainerName} bash -c \"[ -d /usr/local/tomcat/webapps/{warFolderName} ] && echo exists\"";
                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process process = new Process())
                    {
                        process.StartInfo = processInfo;
                        process.Start();

                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim() == "exists")
                        {
                            return true;
                        }
                    }
                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                }
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while waiting for deployment. Detail: {ex.Message}");
            }
        }
        #endregion


        #region NODEJS
        public bool WaitForNodeJsDeployment(string containerName, string appName, string appType = "webapp_webapi", int maxWaitTimeMs = 30000, int checkIntervalMs = 1000)
        {
            try
            {
                string checkCommand = "";
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {
                    if (appType == "webapi")
                    {
                        checkCommand = $"docker exec auto-grading-nodejs /bin/bash -c \"APP_NAME={appName}; " +
                        $"Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_WEB_API.port\\\"; " +
                        $"if [ ! -f $Port_FILE ]; " +
                        $"then " +
                        $"echo \\\"FAIL PORT FILE\\\";" +
                        $"exit 1; " +
                        $"else " +
                        $"echo \\\"OK PORT FILE\\\";" +
                        $"fi;" +
                        $" HOST=\\\"localhost\\\";" +
                        $"Port=$(cat $Port_FILE);" +
                        $"if [ -z \\\"$Port\\\" ]; " +
                        $"then " +
                        $"echo \\\" FAIL Empty Port\\\"; " +
                        $"exit 1; " +
                        $"else " +
                        $"echo \\\"OK PORT\\\"; " +
                        $"fi; " +
                        $"if  curl --silent --head \\\"http://${{HOST}}:${{Port}}\\\">/dev/null ; " +
                        $"then " +
                        $"echo \\\"OK CURL\\\"; " +
                        $"else " +
                        $"echo \\\"FAIL CURL\\\"; " +
                        $"exit 1; " +
                        $"fi; " +
                        $"grep -q \\\"location /$APP_NAME/ {{\\\" /etc/nginx/nginx.conf && " +
                        $"nginx -t >/dev/null 2>&1 &&" +
                        $" echo \\\"OK NGINX SYNTAX\\\" || " +
                        $"{{ echo \\\"FAIL NGINX SYNTAX\\\"; exit 1; }}; " +
                        $"echo \\\"APP ${{APP_NAME}} IS RUNNING\\\"\"";
                    }
                    else
                    {
                        checkCommand = $"docker exec auto-grading-nodejs /bin/bash -c \"APP_NAME={appName}; " +
                        $"FE_Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_FE.port\\\"; " +
                        $"BE_Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_BE.port\\\"; " +
                        $"if [ ! -f $FE_Port_FILE ] || [ ! -f $BE_Port_FILE ]; " +
                        $"then " +
                        $"echo \\\"FAIL PORT FILE\\\";" +
                        $"exit 1; " +
                        $"else " +
                        $"echo \\\"OK PORT FILE\\\";" +
                        $"fi;" +
                        $" HOST=\\\"localhost\\\";" +
                        $"FE_Port=$(cat $FE_Port_FILE); " +
                        $"BE_Port=$(cat $BE_Port_FILE);" +
                        $"if [ -z \\\"$FE_Port\\\" ] || [ -z \\\"$BE_Port\\\" ]; " +
                        $"then " +
                        $"echo \\\" FAIL Empty Port\\\"; " +
                        $"exit 1; " +
                        $"else " +
                        $"echo \\\"OK PORT\\\"; " +
                        $"fi; " +
                        $"if  curl --silent --head \\\"http://${{HOST}}:${{BE_Port}}\\\">/dev/null  &&  curl --silent --head \\\"http://${{HOST}}:${{FE_Port}}\\\">/dev/null ; " +
                        $"then " +
                        $"echo \\\"OK CURL\\\"; " +
                        $"else " +
                        $"echo \\\"FAIL CURL\\\"; " +
                        $"exit 1; " +
                        $"fi; " +
                        $"grep -q \\\"location /$APP_NAME/ {{\\\" /etc/nginx/nginx.conf && " +
                        $"grep -q \\\"location /$APP_NAME/api {{\\\" /etc/nginx/nginx.conf && " +
                        $"nginx -t >/dev/null 2>&1 &&" +
                        $" echo \\\"OK NGINX SYNTAX\\\" || " +
                        $"{{ echo \\\"FAIL NGINX SYNTAX\\\"; exit 1; }}; " +
                        $"echo \\\"APP ${{APP_NAME}} IS RUNNING\\\"\"";
                    }

                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains($"app {appName.ToLower()} is running"))
                        {
                            Console.WriteLine($"Application {appName} is deployed and running. Output: {output}");
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting for {appName} deployment... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Deployment timeout for {appName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while checking deployment of {appName}. Details: {ex.Message}", ex);
            }
        }

        public bool WaitForNodeJSDispose(string containerName, string appName, string appType, int maxWaitTimeMs = 30000, int checkIntervalMs = 1000)
        {
            try
            {
                string checkCommand = "";
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {
                    if (appType == "webapi")
                    {
                        checkCommand = $"docker exec auto-grading-nodejs /bin/bash -c \"APP_NAME={appName}; " +
                                           $"Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_WEB_API.port\\\"; " +
                                           $"if [ ! -f $Port_FILE ]; " +
                                           $"then " +
                                           $"echo \\\"PORT FILE IS REMOVED\\\"; " +
                                           $"else " +
                                           $"echo \\\"PORT FILE IS EXIST\\\";" +
                                           $"exit 1;" +
                                           $"fi; " +
                                           $"HOST=\\\"localhost\\\"; " +
                                           $"grep -q \\\"location /$APP_NAME/ {{\\\" /etc/nginx/nginx.conf && " +
                                           $"nginx -t >/dev/null 2>&1 && " +
                                           $"{{ echo \\\"${{APP_NAME}} NGINX EXIST\\\";  exit 1;}}  ||  " +
                                           $" {{ nginx -t >/dev/null 2>&1 && echo \\\"${{APP_NAME}} NGINX DISPOSED\\\";}};" +
                                           $"echo \\\" APP ${{APP_NAME}} IS DISPOSED\\\"\"";
                    }
                    else
                    {
                        checkCommand = $"docker exec auto-grading-nodejs /bin/bash -c \"APP_NAME={appName}; " +
                                             $"FE_Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_FE.port\\\"; " +
                                             $"BE_Port_FILE=\\\"/tmp/ports/${{APP_NAME}}_BE.port\\\"; " +
                                             $"if [ ! -f $FE_Port_FILE ] || [ ! -f $BE_Port_FILE ]; " +
                                             $"then " +
                                             $"echo \\\"PORT FILE IS REMOVED\\\"; " +
                                             $"else " +
                                             $"echo \\\"PORT FILE IS EXIST\\\";" +
                                             $"exit 1;" +
                                             $"fi; " +
                                             $"HOST=\\\"localhost\\\"; " +
                                             $"grep -q \\\"location /$APP_NAME/ {{\\\" /etc/nginx/nginx.conf && " +
                                             $"grep -q \\\"location /$APP_NAME/api {{\\\" /etc/nginx/nginx.conf && " +
                                             $"nginx -t >/dev/null 2>&1 && " +
                                             $"{{ echo \\\"${{APP_NAME}} NGINX EXIST\\\";  exit 1;}}  ||  " +
                                             $" {{ nginx -t >/dev/null 2>&1 && echo \\\"${{APP_NAME}} NGINX DISPOSED\\\";}};" +
                                             $"echo \\\" APP ${{APP_NAME}} IS DISPOSED\\\"\"";
                    }


                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains($"app {appName.ToLower()} is disposed"))
                        {
                            Console.WriteLine($"Application {appName} is deployed and running. Output: {output}");
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting for {appName} deployment... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Deployment timeout for {appName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while checking deployment of {appName}. Details: {ex.Message}", ex);
            }
        }


        public bool WaitForMongoDatabaseImported(string containerName, string appName, int maxWaitTimeMs = 10000, int checkIntervalMs = 1000)
        {
            try
            {
                string checkCommand = "";
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {

                    checkCommand = $"docker exec {containerName} /bin/bash -c \"" +
                        $"stable_count=0; " +
                        $"max_attempts=10; " +
                        $"last_size=0; " +
                        $"while [ $stable_count -lt 5 ] && [ $max_attempts -gt 0 ]; do " +
                        $"STATS=$(mongosh --eval 'db.getSiblingDB(\\\"{appName}\\\").stats().dataSize' --quiet); " +
                        $"if [ \\\"$STATS\\\" != \\\"0\\\" ] && [ \\\"$STATS\\\" == \\\"$last_size\\\" ]; " +
                        $"then " +
                        $"((stable_count++)); " +
                        $"else " +
                        $"stable_count=0; " +
                        $"fi; " +
                        $"last_size=$STATS; " +
                        $"sleep 0.2; " +
                        $"((max_attempts--)); " +
                        $"done; " +
                        $"if [ $stable_count -ge 5 ]; " +
                        $"then echo \\\"OK\\\"; " +
                        $"else echo \\\"FAIL\\\"; fi\"";

                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains($"ok"))
                        {
                            Console.WriteLine($"Application {appName} is deployed and running. Output: {output}");
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting for {appName} deployment... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Deployment timeout for {appName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while checking deployment of {appName}. Details: {ex.Message}", ex);
            }
        }


        #endregion

        #region MSSQL
        public void WaitForSqlServer(string server, string username, string password, string database, int retryIntervalMilliseconds = 1000, int timeoutInMilliseconds = 60000)
        {
            int maxRetries = timeoutInMilliseconds / retryIntervalMilliseconds;
            int retries = 0;

            while (retries < maxRetries)
            {
                if (IsSqlServerReady(server, username, password, database, timeoutInMilliseconds))
                {
                    Console.WriteLine("[INFO] SQL Server is now ready.");
                    return;
                }
                else
                {
                    retries++;
                    Console.WriteLine($"[INFO] SQL Server not ready, retrying in {retryIntervalMilliseconds / 1000} seconds...");
                    System.Threading.Thread.Sleep(retryIntervalMilliseconds);
                }
            }

            throw new Exception("SQL Server did not become ready within the expected time.");
        }

        private bool IsSqlServerReady(string server, string username, string password, string database, int timeoutInMilliseconds = 30000)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "sqlcmd",
                Arguments = $"-S {server} -U {username} -P {password} -d {database} -Q \"SELECT 1\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = processInfo })
                {
                    process.Start();

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(timeoutInMilliseconds))
                    {
                        process.Kill();
                        throw new TimeoutException("SQL Server is not ready within the timeout period.");
                    }

                    if (string.IsNullOrEmpty(error) && output.Contains("1"))
                    {
                        Console.WriteLine("SQL Server is ready.");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"Error: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred: {ex.Message}");
                throw;
            }

            return false;
        }
        #endregion

        #region JavaSpring
        public bool WaitForJarDeployment(string containerName, string appName, int maxWaitTimeMs = 70000, int checkIntervalMs = 1000)
        {
            try
            {
                int elapsedTime = 0;
                while (elapsedTime < maxWaitTimeMs)
                {
                    // Check both the PID file and the actual process
                    string checkCommand = $"docker exec {containerName} /bin/bash -c \"APP_NAME={appName}; if [ -f \\\"/tmp/$APP_NAME.pid\\\" ] && [ -f \\\"/tmp/$APP_NAME.port\\\" ]; then PID=$(cat \\\"/tmp/$APP_NAME.pid\\\"); PORT=$(cat \\\"/tmp/$APP_NAME.port\\\"); PROC_OK=$(kill -0 $PID 2>/dev/null && echo \\\"OK\\\" || echo \\\"FAIL\\\"); NGINX_OK=$(grep -q \\\"location /$APP_NAME/\\\" /etc/nginx/nginx.conf && nginx -t >/dev/null 2>&1 && echo \\\"OK\\\" || echo \\\"FAIL\\\"); APP_RESPONSE=$(curl -s -m 1 \\\"http://localhost:$PORT/\\\" -w \\\"%{{http_code}}\\\" -o /dev/null); if [ -n \\\"$APP_RESPONSE\\\" ]; then APP_OK=\\\"OK (HTTP $APP_RESPONSE)\\\"; else APP_OK=\\\"FAIL\\\"; fi; if [ \\\"$PROC_OK\\\" = \\\"OK\\\" ] && [ \\\"$NGINX_OK\\\" = \\\"OK\\\" ] && [ \\\"$APP_OK\\\" != \\\"FAIL\\\" ]; then echo \\\"FULLY DEPLOYED - Process:$PROC_OK, Port:$PORT, App:$APP_OK, Ngx:$NGINX_OK\\\"; else echo \\\"PARTIAL DEPLOYMENT - Process:$PROC_OK, Port:$PORT, App:$APP_OK, Ngx:$NGINX_OK\\\"; fi; else echo \\\"NOT DEPLOYED\\\"; fi\"";
                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains("fully"))
                        {
                            string port = output.Trim().Split(':')[1];
                            Console.WriteLine($"Application {appName} is deployed and running on port {port}. Output: {output}");
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting for {appName} deployment... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Deployment timeout for {appName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while checking deployment of {appName}. Details: {ex.Message}", ex);
            }
        }

        #endregion

        #region Python Django

        public bool WaitForPythonDjangoProjectInstallPackageAndDeploy(
            string containerName,
            string folderName,
            int maxWaitTimeMs = 120000,
            int checkIntervalMs = 2000)
        {
            try
            {
                int elapsedTime = 0;

                while (elapsedTime < maxWaitTimeMs)
                {
                    // Lệnh kiểm tra trạng thái triển khai
                    string checkCommand = $"docker exec {containerName} /bin/bash -c \"APP_NAME={folderName}; if [ -f \\\"/tmp/$APP_NAME.pid\\\" ] && [ -f \\\"/tmp/$APP_NAME.port\\\" ]; then PID=$(cat \\\"/tmp/$APP_NAME.pid\\\"); PORT=$(cat \\\"/tmp/$APP_NAME.port\\\"); PROC_OK=$(kill -0 $PID 2>/dev/null && echo \\\"OK\\\" || echo \\\"FAIL\\\"); NGINX_OK=$( [ -f \\\"/etc/nginx/conf.d/$APP_NAME.conf\\\" ] && nginx -t >/dev/null 2>&1 && echo \\\"OK\\\" || echo \\\"FAIL\\\" ); APP_RESPONSE=$(curl -s -m 1 \\\"http://localhost:$PORT/\\\" -w \\\"%{{http_code}}\\\" -o /dev/null); if [ -n \\\"$APP_RESPONSE\\\" ] && [ \\\"$APP_RESPONSE\\\" -ge 100 ] && [ \\\"$APP_RESPONSE\\\" -le 599 ]; then APP_OK=\\\"OK (HTTP $APP_RESPONSE)\\\"; else APP_OK=\\\"FAIL (HTTP $APP_RESPONSE)\\\"; fi; if [ \\\"$PROC_OK\\\" = \\\"OK\\\" ] && [ \\\"$NGINX_OK\\\" = \\\"OK\\\" ] && [ \\\"${{APP_OK%% *}}\\\" = \\\"OK\\\" ]; then echo \\\"FULLY DEPLOYED (webapp) - Process:$PROC_OK, Port:$PORT, App:$APP_OK, Nginx:$NGINX_OK\\\"; else echo \\\"PARTIAL DEPLOYMENT (webapp) - Process:$PROC_OK, Port:$PORT, App:$APP_OK, Nginx:$NGINX_OK\\\"; fi; else echo \\\"NOT DEPLOYED\\\"; fi\"";

                    ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", "/c " + checkCommand)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process process = Process.Start(processInfo))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (output.Trim().ToLower().Contains("fully"))
                        {
                            string port = output.Trim().Split(':')[1];
                            Console.WriteLine($"Application {folderName} is deployed and running on port {port}. Output: {output}");
                            Thread.Sleep(2000);
                            return true;
                        }
                    }

                    Thread.Sleep(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                    Console.WriteLine($"Waiting deploying {folderName}... ({elapsedTime}/{maxWaitTimeMs}ms)");
                }

                Console.WriteLine($"Timeout for  {folderName} after {maxWaitTimeMs}ms");
                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Lỗi khi kiểm tra triển khai của {folderName}. Chi tiết: {ex.Message}", ex);
            }
        }


        #endregion
    }
}
