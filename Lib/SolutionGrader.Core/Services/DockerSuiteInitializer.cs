using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities.Docker.DockerSupporter.Entity;
using EnvironmentBuilder.DockerCommand;
using FileHandler;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Initializes required docker environment before suite execution:
    /// - Parse environment.xlsx (outer) to get config values
    /// - Pull / build images (code + database)
    /// - Run database container and wait ready
    /// - Run code container
    /// - Copy runtimes/client/server folders into container if needed
    /// - Ensure log folder
    /// </summary>
    public sealed class DockerSuiteInitializer
    {
        private readonly DockerCommandExecutor _docker;

        public DockerSuiteInitializer()
        {
            _docker = new DockerCommandExecutor();
        }

        public async Task<(bool Success,string Message, DockerBase CodeContainer, DockerBase DbContainer)> InitializeAsync(string suiteRoot, ExecuteSuiteArgs args, CancellationToken ct)
        {
            try
            {
                var envXlsx = Path.Combine(suiteRoot, "environment.xlsx");
                if (!File.Exists(envXlsx)) return (false, "environment.xlsx not found", new DockerBase(), new DockerBase());

                var env = EnvFileHandler.LoadEnvironment(envXlsx);
                var codeDocker = EnvFileHandler.CreateCodeDocker(env);
                var dbDocker = EnvFileHandler.CreateDatabaseDocker(env);

                // Pull images if not present (only for remote image names)
                SafePull(dbDocker.ImageName);
                SafePull(codeDocker.ImageName);

                // Run database container first
                _docker.RunContainer(dbDocker);
                // Wait for DB port mapping accessible (simple retry)
                _docker.WaitForSqlServer("localhost," + dbDocker.HostPort, env.Configs.GetValueOrDefault("Database_Username", "sa"), env.Configs.GetValueOrDefault("Database_Password", "sa"), env.Configs.GetValueOrDefault("Default_Database_Name", "Library"));

                // Run code container
                _docker.RunContainer(codeDocker);

                // Prepare log paths inside container if not exist
                EnsureContainerFolder(codeDocker.ContainerName, "/logs");

                // Copy runtimes & given executables if paths present
                CopyIfExists(codeDocker.ContainerName, Path.Combine(suiteRoot, env.Configs.GetValueOrDefault("Runtimes_Folder", Path.Combine("Meta","runtimes"))), "/runtimes");
                CopyIfExists(codeDocker.ContainerName, Path.Combine(suiteRoot, env.Configs.GetValueOrDefault("Client", Path.Combine("Meta","Given","Client"))), "/app/client");
                CopyIfExists(codeDocker.ContainerName, Path.Combine(suiteRoot, env.Configs.GetValueOrDefault("Server", Path.Combine("Meta","Given","Server"))), "/app/server");

                return (true, "Docker environment initialized", codeDocker, dbDocker);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, new DockerBase(), new DockerBase());
            }
        }

        private void SafePull(string image)
        {
            if (string.IsNullOrWhiteSpace(image)) return;
            try { _docker.PullImage(image); } catch { }
        }

        private void EnsureContainerFolder(string container, string folder)
        {
            try { _docker.MakeDirectory(container, folder); } catch { }
        }

        private void CopyIfExists(string container, string hostPath, string containerDest)
        {
            try
            {
                if (Directory.Exists(hostPath))
                {
                    // docker cp supports copying entire folder
                    _docker.CopyFileToContainer(hostPath, $"{container}:{containerDest}");
                }
            }
            catch { }
        }
    }
}
