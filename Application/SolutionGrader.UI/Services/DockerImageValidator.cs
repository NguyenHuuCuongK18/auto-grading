using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SolutionGrader.UI.Services
{
    /// <summary>
    /// Service responsible for validating Docker images before grading.
    /// 
    /// CRITICAL: This service detects when users have an OLD version of the Docker image
    /// that doesn't include the unified-entrypoint.sh script. This causes the error:
    /// "exec /scripts/unified-entrypoint.sh: no such file or directory"
    /// 
    /// The auto-grading system requires:
    /// - Image: fptuxaes/aes-dotnet8-console:latest
    /// - Built from: DockerImage/Dockerfile.unified (NOT DockerImage/Dockerfile)
    /// - Must contain: /scripts/unified-entrypoint.sh as ENTRYPOINT
    /// 
    /// If validation fails, users need to rebuild the image using:
    ///   cd DockerImage && bash build.sh
    /// OR manually:
    ///   docker build -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified DockerImage/
    /// </summary>
    public class DockerImageValidator
    {
        private const string RequiredImageName = "fptuxaes/aes-dotnet8-console:latest";
        private const string RequiredMonitorImageName = "fptuxaes/network-monitor:latest";
        private const string ExpectedEntrypoint = "/scripts/unified-entrypoint.sh";
        
        /// <summary>
        /// Validates that all required Docker images exist and are correctly configured.
        /// </summary>
        /// <returns>Validation result with success status and detailed message</returns>
        public async Task<ValidationResult> ValidateImagesAsync()
        {
            // Check if unified container image exists
            var unifiedExists = await CheckImageExistsAsync(RequiredImageName);
            if (!unifiedExists)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"❌ Docker image '{RequiredImageName}' not found.\n\n" +
                             "This image is required for running student code.\n\n" +
                             "To build the required images, run:\n" +
                             "  cd DockerImage\n" +
                             "  bash build.sh\n\n" +
                             "Or manually:\n" +
                             $"  docker build -t {RequiredImageName} -f DockerImage/Dockerfile.unified DockerImage/",
                    RequiresRebuild = true
                };
            }
            
            // Check if the image has the correct entrypoint
            var entrypointCheck = await ValidateImageEntrypointAsync(RequiredImageName);
            if (!entrypointCheck.IsValid)
            {
                return entrypointCheck;
            }
            
            // Check if network monitor image exists
            var monitorExists = await CheckImageExistsAsync(RequiredMonitorImageName);
            if (!monitorExists)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"❌ Docker image '{RequiredMonitorImageName}' not found.\n\n" +
                             "This image is required for network packet capture.\n\n" +
                             "To build the required images, run:\n" +
                             "  cd DockerImage\n" +
                             "  bash build.sh\n\n" +
                             "Or manually:\n" +
                             $"  docker build -t {RequiredMonitorImageName} -f DockerImage/NetworkMonitor.Dockerfile DockerImage/",
                    RequiresRebuild = true
                };
            }
            
            // All images exist and are valid
            return new ValidationResult
            {
                IsValid = true,
                Message = "✅ All Docker images are correctly configured and ready for grading.",
                RequiresRebuild = false
            };
        }
        
        /// <summary>
        /// Checks if a Docker image exists locally.
        /// </summary>
        private async Task<bool> CheckImageExistsAsync(string imageName)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"images -q {imageName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                return !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Validates that the Docker image has the correct entrypoint configuration.
        /// This detects when users have built with the old Dockerfile instead of Dockerfile.unified.
        /// </summary>
        private async Task<ValidationResult> ValidateImageEntrypointAsync(string imageName)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"image inspect {imageName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (process.ExitCode != 0)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Message = $"❌ Failed to inspect Docker image '{imageName}'.\n\nError: Unable to read image configuration.",
                        RequiresRebuild = true
                    };
                }
                
                // Parse JSON output
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var imageInfo = root[0];
                    
                    // Check Config.Entrypoint
                    if (imageInfo.TryGetProperty("Config", out var config))
                    {
                        if (config.TryGetProperty("Entrypoint", out var entrypoint))
                        {
                            // Entrypoint should be an array with "/scripts/unified-entrypoint.sh"
                            if (entrypoint.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in entrypoint.EnumerateArray())
                                {
                                    var value = item.GetString();
                                    if (value != null && value.Contains("unified-entrypoint.sh"))
                                    {
                                        // Correct entrypoint found
                                        return new ValidationResult
                                        {
                                            IsValid = true,
                                            Message = "✅ Docker image has correct entrypoint configuration.",
                                            RequiresRebuild = false
                                        };
                                    }
                                }
                            }
                            
                            // Entrypoint exists but doesn't have unified-entrypoint.sh
                            // Format the entrypoint array for display
                            var entrypointDisplay = "Unknown";
                            try
                            {
                                var entrypointList = new System.Collections.Generic.List<string>();
                                foreach (var item in entrypoint.EnumerateArray())
                                {
                                    var value = item.GetString();
                                    if (value != null) entrypointList.Add(value);
                                }
                                entrypointDisplay = string.Join(", ", entrypointList);
                            }
                            catch { }
                            
                            return new ValidationResult
                            {
                                IsValid = false,
                                Message = $"❌ WRONG DOCKER IMAGE DETECTED!\n\n" +
                                         $"Your '{imageName}' image was built with the OLD Dockerfile.\n" +
                                         $"The current system requires an image built with Dockerfile.unified.\n\n" +
                                         $"Current entrypoint: {entrypointDisplay}\n" +
                                         $"Required entrypoint: {ExpectedEntrypoint}\n\n" +
                                         $"This is why you're getting the error:\n" +
                                         $"  'exec /scripts/unified-entrypoint.sh: no such file or directory'\n\n" +
                                         "TO FIX THIS ISSUE:\n" +
                                         "1. Delete the old image:\n" +
                                         $"   docker rmi {imageName}\n\n" +
                                         "2. Rebuild with the correct Dockerfile:\n" +
                                         "   cd DockerImage\n" +
                                         "   bash build.sh\n\n" +
                                         "   OR manually:\n" +
                                         $"   docker build -t {imageName} -f DockerImage/Dockerfile.unified DockerImage/\n\n" +
                                         "3. Restart the application and try grading again.",
                                RequiresRebuild = true
                            };
                        }
                    }
                }
                
                // Could not find entrypoint in config
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"❌ Docker image '{imageName}' has invalid configuration.\n\n" +
                             "The image exists but doesn't have the required entrypoint.\n" +
                             "Please rebuild the image using:\n" +
                             "  cd DockerImage\n" +
                             "  bash build.sh",
                    RequiresRebuild = true
                };
            }
            catch (Exception ex)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = $"❌ Error validating Docker image: {ex.Message}\n\n" +
                             "Please ensure Docker is running and try again.",
                    RequiresRebuild = false
                };
            }
        }
        
        /// <summary>
        /// Gets the DockerImage directory path for building images.
        /// </summary>
        public string? GetDockerImageDirectory()
        {
            var currentDir = Directory.GetCurrentDirectory();
            
            // Search up to 5 levels up
            var dir = new DirectoryInfo(currentDir);
            for (int i = 0; i < 5 && dir != null; i++)
            {
                var dockerImagePath = Path.Combine(dir.FullName, "DockerImage");
                if (Directory.Exists(dockerImagePath))
                {
                    return dockerImagePath;
                }
                
                dir = dir.Parent;
            }
            
            return null;
        }
    }
    
    /// <summary>
    /// Result of Docker image validation.
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// Whether the validation passed (all images exist and are correctly configured).
        /// </summary>
        public bool IsValid { get; set; }
        
        /// <summary>
        /// User-friendly message explaining the validation result.
        /// </summary>
        public string Message { get; set; } = string.Empty;
        
        /// <summary>
        /// Whether the user needs to rebuild the Docker images.
        /// </summary>
        public bool RequiresRebuild { get; set; }
    }
}
