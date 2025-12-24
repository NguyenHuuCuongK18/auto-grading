using Domain.Entities.Constants;
using Domain.Entities.Docker.DockerSupporter.Entity;
using EnvironmentManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace DotNetEnvironmentManagerHelper.Services
{
    /// <summary>
    /// Environment setup service for .NET Console Networking Applications.
    /// Handles Docker container lifecycle for server, client, and database containers.
    /// </summary>
    public class EnvironmentSetupService : BaseEnvironmentSetupService
    {
        public override string EnvironmentType { get; } = "dotnet";

        #region TESTKIT
        #region SETUP
        /// <summary>
        /// Sets up Docker containers for a .NET test kit, including database, code, and given console containers.
        /// </summary>
        public override void SetupContainerForTestKit(Domain.Entities.Main.Environment environment)
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

                string givenConsoleContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerName
                );

                if (!dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    SetupDatabaseContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    SetupCodeContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(givenConsoleContainerName))
                {
                    SetupGivenConsoleContainer();
                }

                Thread.Sleep(3000);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed setting up .NET container for testkit. Details: {ex.Message}");
            }
        }
        #endregion

        #region DISPOSE
        /// <summary>
        /// Disposes all Docker containers for the test kit.
        /// </summary>
        public override void DisposeContainerForTestKit(Domain.Entities.Main.Environment environment)
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

                string givenConsoleContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerName
                );

                if (dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(codeContainerName);
                }

                if (dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(databaseContainerName);
                }

                if (dockerCommandExecutor.IsContainerRunning(givenConsoleContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(givenConsoleContainerName);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed disposing containers. Details: {ex.Message}");
            }
        }
        #endregion

        #region CORE
        /// <summary>
        /// Sets up the main .NET code container for running student solutions.
        /// </summary>
        public override void SetupCodeContainer()
        {
            try
            {
                string imageName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeImageName
                );

                string networkName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DockerNetwork
                );

                string containerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeContainerName
                );

                int containerPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeContainerInternalPort
                ));

                int hostPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.CodeContainerHostPort
                ));

                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    ContainerPort = containerPort,
                    HostPort = hostPort,
                    EnvironmentVariables = GetEnvironmentVariablesForQuestion()
                };

                dockerCommandExecutor.RunContainer(dockerBase);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while setting up dotnet container. Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets up the given console container (client or server depending on test configuration).
        /// </summary>
        private void SetupGivenConsoleContainer()
        {
            try
            {
                string imageName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleImageName
                );

                string networkName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DockerNetwork
                );

                string containerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerName
                );

                int containerPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerInternalPort
                ));

                int hostPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerHostPort
                ));

                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    EnvironmentVariables = GetEnvironmentVariablesForQuestion()
                };

                dockerCommandExecutor.RunContainer(dockerBase);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while setting up dotnet given api container. Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets up the SQL Server database container.
        /// </summary>
        public override void SetupDatabaseContainer()
        {
            try
            {
                string imageName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseImageName
                );

                string networkName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DockerNetwork
                );

                string containerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseContainerName
                );

                int containerPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseContainerInternalPort
                ));

                int hostPort = int.Parse(TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseContainerHostPort
                ));

                string databaseUsername = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabaseUsername
                );

                string databasePassword = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.DatabasePassword
                );

                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    ContainerPort = containerPort,
                    HostPort = hostPort,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        {
                            "ACCEPT_EULA",
                            "Y"
                        },
                        {
                            $"MSSQL_{databaseUsername.ToUpper()}_PASSWORD",
                            databasePassword
                        }
                    }
                };

                dockerCommandExecutor.RunContainer(dockerBase, 3000);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while setting up database container. Details: {ex.Message}");
            }
        }
        #endregion
        #endregion

        #region Q
        #region SETUP
        /// <summary>
        /// Initializes environment for a specific question.
        /// </summary>
        public override void SetupEnvironmentForQuestion(Domain.Entities.Main.Environment environment)
        {
            questionEnvironment = environment;
            questionConfigs = environment.Configs;
        }

        /// <summary>
        /// Executes the setup steps defined for a question.
        /// </summary>
        public override void ExecuteSetupEnvironmentForQuestionBySteps()
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
        #endregion

        #region DISPOSE
        /// <summary>
        /// Disposes the environment for a question by removing deployed files and dropping database.
        /// </summary>
        public override void DisposeEnvironmentForQuestion()
        {
            // Remove publish file from code container
            dockerCommandExecutor.RemoveFolder(
                questionConfigs[EnvironmentConfiguration.CodeContainerName],
                $"/apps/{questionConfigs[EnvironmentConfiguration.StudentQuestionName]}"
            );

            // Remove SQL file from database container
            dockerCommandExecutor.RemoveFile(
                questionConfigs[EnvironmentConfiguration.DatabaseContainerName],
                $"/var/opt/mssql/{questionConfigs[EnvironmentConfiguration.StudentQuestionName]}.sql"
            );

            // Drop database
            DropSqlDatabase(
                questionConfigs[EnvironmentConfiguration.DatabaseContainerName],
                questionConfigs[EnvironmentConfiguration.DatabaseUsername],
                questionConfigs[EnvironmentConfiguration.DatabasePassword],
                questionConfigs[EnvironmentConfiguration.DatabaseName]
            );
        }
        #endregion

        #region CORE
        /// <summary>
        /// Copies essential files and folders including runtimes and deploys server/client code.
        /// </summary>
        public override void CopyEssentialFilesAndFolders()
        {
            // Copy runtimes folder to client and server
            CopyRuntimesToClient();
            CopyRuntimesToServer();

            // Deploy server
            CopyPublishFolder();
            // Deploy client
            CopyGivenConsolePublishFolder();
        }

        /// <summary>
        /// Generates a database initialization script and copies it to the database container.
        /// </summary>
        public override void GenerateDbScript()
        {
            try
            {
                string sqlFilePath = questionConfigs[EnvironmentConfiguration.DefaultDatabaseFilePath];
                string oldDatabaseName = questionConfigs[EnvironmentConfiguration.DefaultDatabaseName];
                string studentQuestionPath = questionConfigs[EnvironmentConfiguration.CodeFilePath];
                string newDatabaseName = questionConfigs[EnvironmentConfiguration.DatabaseName];
                string containerName = questionConfigs[EnvironmentConfiguration.DatabaseContainerName];

                // Read SQL script and replace database name
                string sqlFileName = $"{newDatabaseName}.sql";
                string sqlScriptContent = File.ReadAllText(sqlFilePath).Replace(oldDatabaseName, newDatabaseName);

                // Create new SQL script with the new database name
                string newSqlFilePath = Path.Combine(studentQuestionPath, sqlFileName);
                File.WriteAllText(newSqlFilePath, sqlScriptContent);

                // Copy to container
                dockerCommandExecutor.CopyFileToContainer(newSqlFilePath, $"{containerName}:/var/opt/mssql/{sqlFileName}");
            }
            catch (Exception)
            {
                // Silent return on failure - database script generation is optional
                return;
            }
        }

        /// <summary>
        /// Generates appsettings.json connection files for server and client.
        /// </summary>
        public override void GenerateConnectionFile()
        {
            string dbConn = $"Server={questionConfigs[EnvironmentConfiguration.DatabaseContainerName]},{questionConfigs[EnvironmentConfiguration.DatabaseContainerInternalPort]};database={questionConfigs[EnvironmentConfiguration.DatabaseName]};uid={questionConfigs[EnvironmentConfiguration.DatabaseUsername]};Password={questionConfigs[EnvironmentConfiguration.DatabasePassword]};Encrypt=false;TrustServerCertificate=true";

            string port = questionConfigs[EnvironmentConfiguration.CodeContainerInternalPort];
            string serverName = questionConfigs[EnvironmentConfiguration.CodeContainerName];

            void UpdateFile(string pathKey, string ipOverride)
            {
                if (!questionConfigs.TryGetValue(pathKey, out var rootPath)) return;

                string filePath = Path.Combine(rootPath, "appsettings.json");
                if (!File.Exists(filePath)) return;

                var json = JObject.Parse(File.ReadAllText(filePath));

                // Update database connection
                if (json["ConnectionStrings"] != null) json["ConnectionStrings"]["MyCnn"] = dbConn;

                // Update networking settings
                json["IpAddress"] = ipOverride;
                json["Port"] = port;

                File.WriteAllText(filePath, json.ToString());
            }

            UpdateFile(EnvironmentConfiguration.CodeFilePath, "0.0.0.0");
            UpdateFile(EnvironmentConfiguration.GivenConsolePath, "host.docker.internal");
        }

        /// <summary>
        /// Modifies IP and port settings in the given API configuration.
        /// </summary>
        public void ModifyGivenIpAndPort()
        {
            try
            {
                string appsettingsPath = Path.Combine(questionConfigs[EnvironmentConfiguration.StudentQuestionPath], "appsettings.json");
                if (!File.Exists(appsettingsPath))
                {
                    return;
                }
                string appsettingsContent = File.ReadAllText(appsettingsPath);

                var appsettingsJson = JObject.Parse(appsettingsContent);

                var givenApiUrlSection = appsettingsJson["GivenAPIBaseUrl"];
                if (givenApiUrlSection == null)
                    return;
                appsettingsJson["GivenAPIBaseUrl"] = questionConfigs[EnvironmentConfiguration.GivenApiUrl];
                string updatedContent = appsettingsJson.ToString();
                File.WriteAllText(appsettingsPath, updatedContent);
            }
            catch (Exception)
            {
                return;
            }
        }
        #endregion
        #endregion

        #region TC
        #region SETUP
        /// <summary>
        /// Initializes environment for a specific test case.
        /// </summary>
        public override void SetupEnvironmentForTestCase(Domain.Entities.Main.Environment environment)
        {
            testCaseEnvironment = environment;
            testCaseConfigs = environment.Configs;
        }

        /// <summary>
        /// Executes the setup steps defined for a test case.
        /// </summary>
        public override void ExecuteSetupEnvironmentForTestCaseBySteps()
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

                    case EnvironmentTcAction.ResetGivenConsole:
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
        /// Disposes the environment for a test case. Currently no-op.
        /// </summary>
        public override void DisposeEnvironmentForTestCase()
        {
        }

        /// <summary>
        /// Disposes the given API container.
        /// </summary>
        public void DisposeGivenAPI()
        {
            string givenConsoleContainerName = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.CodeContainerName);
            string publishFileName = $"/apps/{TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.StudentQuestionName)}";
            try
            {
                dockerCommandExecutor.RemoveFolder(givenConsoleContainerName, publishFileName);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while disposing question. Detail: {ex.Message}");
            }
        }
        #endregion

        #region CORE
        /// <summary>
        /// Resets the database by dropping and recreating it from the SQL script.
        /// </summary>
        public override void ResetDatabase()
        {
            try
            {
                ResetSqlDatabase(
                    TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseContainerName),
                    TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseUsername),
                    TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabasePassword),
                    TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseName)
                );
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>
        /// Restarts the given console application.
        /// </summary>
        public void RestartGivenConsole()
        {
            string givenConsoleAppName = testCaseConfigs[EnvironmentConfiguration.GivenConsoleAppName];
            string givenConsoleContainerName = testCaseConfigs[EnvironmentConfiguration.GivenConsoleContainerName];

            string restartCommand = $"{givenConsoleContainerName} sh -c \"APP_NAME={givenConsoleAppName} && if [ -f /tmp/$APP_NAME.pid ]; then kill `cat /tmp/$APP_NAME.pid` 2>/dev/null; rm -f /tmp/$APP_NAME.pid /tmp/$APP_NAME.port; fi && touch /apps/$APP_NAME/tempfile && rm /apps/$APP_NAME/tempfile\"";

            dockerCommandExecutor.ExecDockerCommand(restartCommand, 30000);
            dockerCommandExecutor.WaitForPublishFileDeployment(givenConsoleContainerName, givenConsoleAppName);
        }
        #endregion
        #endregion

        #region UTILITIES
        /// <summary>
        /// Resets the SQL database by dropping and recreating it from the SQL script.
        /// </summary>
        private void ResetSqlDatabase(string sqlContainerName, string sqlUsername, string sqlPassword, string databaseName)
        {
            try
            {
                // Drop database
                string dropDatabaseQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;";
                string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
                dockerCommandExecutor.ExecDockerCommand(command);

                // Create new database
                command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -i /var/opt/mssql/{databaseName}.sql";
                dockerCommandExecutor.ExecDockerCommand(command);
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>
        /// Drops a SQL database from the container.
        /// </summary>
        private void DropSqlDatabase(string sqlContainerName, string sqlUsername, string sqlPassword, string databaseName)
        {
            try
            {
                string dropDatabaseQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;";
                string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
                dockerCommandExecutor.ExecDockerCommand(command);
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>
        /// Copies runtimes folder to the client solution folder.
        /// </summary>
        private void CopyRuntimesToClient()
        {
            try
            {
                string runtimesFolder = TryGetValueOrDefault(
                    questionEnvironment.Configs,
                    EnvironmentConfiguration.RuntimesFolder
                );

                string clientQuestionPath = TryGetValueOrDefault(
                    questionEnvironment.Configs,
                    EnvironmentConfiguration.GivenConsolePath
                );

                string destinationPath = Path.Combine(clientQuestionPath, "runtimes");
                if (!Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                    dockerCommandExecutor.CopyFolder(runtimesFolder, destinationPath);
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Copies runtimes folder to the server solution folder.
        /// </summary>
        private void CopyRuntimesToServer()
        {
            try
            {
                string runtimesFolder = TryGetValueOrDefault(
                    questionEnvironment.Configs,
                    EnvironmentConfiguration.RuntimesFolder
                );

                string serverQuestionPath = TryGetValueOrDefault(
                    questionEnvironment.Configs,
                    EnvironmentConfiguration.CodeFilePath
                );

                string destinationPath = Path.Combine(serverQuestionPath, "runtimes");
                if (!Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                    dockerCommandExecutor.CopyFolder(runtimesFolder, destinationPath);
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// Copies server publish folder to the code container and waits for deployment.
        /// </summary>
        private void CopyPublishFolder()
        {
            try
            {
                dockerCommandExecutor.CopyFileToContainer(
                    questionConfigs[EnvironmentConfiguration.CodeFilePath],
                    questionConfigs[EnvironmentConfiguration.CodeContainerName] + ":/apps"
                );

                dockerCommandExecutor.WaitForPublishConsoleFileDeployment(
                    questionConfigs[EnvironmentConfiguration.CodeContainerName],
                    questionConfigs[EnvironmentConfiguration.StudentQuestionName],
                    questionConfigs[EnvironmentConfiguration.DockerServerPath],
                    questionConfigs[EnvironmentConfiguration.CodeContainerInternalPort]
                );
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>
        /// Copies client publish folder to the given console container and waits for deployment.
        /// </summary>
        private void CopyGivenConsolePublishFolder()
        {
            try
            {
                dockerCommandExecutor.CopyFileToContainer(
                    questionConfigs[EnvironmentConfiguration.GivenConsolePath],
                    questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName] + ":/apps"
                );

                dockerCommandExecutor.WaitForPublishConsoleFileDeployment(
                    questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName],
                    questionConfigs[EnvironmentConfiguration.GivenConsoleAppName],
                    questionConfigs[EnvironmentConfiguration.DockerClientPath],
                    "-1"
                );
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>
        /// Gets environment variables for the question container.
        /// </summary>
        private Dictionary<string, string> GetEnvironmentVariablesForQuestion()
        {
            string appType = TryGetValueOrDefault(
                questionConfigs,
                EnvironmentConfiguration.AppType
            );

            string signalrHub = TryGetValueOrDefault(
                questionConfigs,
                EnvironmentConfiguration.SignalRHub
            );

            Dictionary<string, string> environmentVariables = new Dictionary<string, string>();

            environmentVariables.Add(EnvironmentConfiguration.AppType, appType);

            if (!string.IsNullOrEmpty(signalrHub))
                environmentVariables.Add(EnvironmentConfiguration.SignalRHub, signalrHub);

            return environmentVariables;
        }
        #endregion
    }
}
