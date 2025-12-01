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
    /// This shit is re-written for .NET Console Networking Application
    /// 
    /// - Auth : NhatNM -
    /// </summary>
    public class EnvironmentSetupService : BaseEnvironmentSetupService
    {
        public override string EnvironmentType { get; } = "dotnet";

        #region TESTKIT
        #region SETUP
        public override void SetupContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
            //logger.LogInfo("Setting up .NET essential containers...");

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

                // use as either client or server
                string givenConsoleContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerName
                );

                if (!dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    //logger.LogInfo("MSSQL container is not running, try starting...");
                    SetupDatabaseContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    //logger.LogInfo(".NET container is not running, try starting...");
                    SetupCodeContainer();
                }

                //if (!string.IsNullOrEmpty(questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName]))
                //{
                //    string givenConsoleContainerName = TryGetValueOrDefault(
                //        questionConfigs,
                //        EnvironmentConfiguration.GivenConsoleContainerName
                //    );

                if (!dockerCommandExecutor.IsContainerRunning(givenConsoleContainerName))
                {
                    //logger.LogInfo("Given API container is not running, try starting...");
                    SetupGivenConsoleContainer();
                }
                //}

                Thread.Sleep(3000);

                //logger.LogInfo("Finished setting up .NET essential containers.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed setting up .NET container for testkit. Details: {ex.Message}");
            }
        }
        #endregion

        #region DISPOSE
        public override void DisposeContainerForTestKit(Domain.Entities.Main.Environment environment)
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

                string givenConsoleContainerName = TryGetValueOrDefault(
                    questionConfigs,
                    EnvironmentConfiguration.GivenConsoleContainerName
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

                //if (!string.IsNullOrEmpty(questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName]))
                //{
                //    string givenConsoleContainerName = TryGetValueOrDefault(
                //        questionConfigs,
                //        EnvironmentConfiguration.GivenConsoleContainerName
                //    );

                if (dockerCommandExecutor.IsContainerRunning(givenConsoleContainerName))
                {
                    //logger.LogInfo("Given API container is running, try disposing...");

                    dockerCommandExecutor.RemoveContainer(givenConsoleContainerName);
                }
                //}

                //logger.LogInfo("Finished disposeing essential containers.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed disposing containers. Details: {ex.Message}");
            }
        }
        #endregion

        #region CORE
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
                // log
                //logger.LogErr("Error while setting up dotnet container");
                // throw
                throw new Exception($"Error while setting up dotnet container. Details: {ex.Message}");
            }
        }

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

                // setting up dotnet given console container
                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    //ContainerPort = containerPort,
                    //HostPort = hostPort,
                    EnvironmentVariables = GetEnvironmentVariablesForQuestion()
                };

                dockerCommandExecutor.RunContainer(dockerBase);
            }
            catch (Exception ex)
            {
                // log
                //logger.LogErr("Error while setting up dotnet given api container");
                // throw
                throw new Exception($"Error while setting up dotnet given api container. Details: {ex.Message}");
            }
        }

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
                // log
                //logger.LogInfo("Error while setting up database container");
                // throw
                throw new Exception($"Error while setting up database container. Details: {ex.Message}");
            }
        }
        #endregion
        #endregion

        #region Q
        #region SETUP
        public override void SetupEnvironmentForQuestion(Domain.Entities.Main.Environment environment)
        {
            //logger.LogInfo("Setting up .NET environment for question...");

            questionEnvironment = environment;
            questionConfigs = environment.Configs;
        }

        public override void ExecuteSetupEnvironmentForQuestionBySteps()
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

                    //case EnvironmentQAction.ModifyGivenApiUrl:
                    //    ModifyGivenIpAndPort();
                    //    //logger.LogInfo("Try modifying api url");
                    //    break;

                    //case EnvironmentQAction.CopyGivenConsolePublish:
                    //    CopyGivenConsolePublishFolder();
                    //    //logger.LogInfo("Try copying given console publish file");
                    //    break;

                    default:
                        //logger.LogErr($"Action named '{step}' is not defined!");
                        throw new Exception($"Action named '{step}' is not defined!");
                }
            }
        }
        #endregion

        #region DISPOSE
        public override void DisposeEnvironmentForQuestion()
        {
            //logger.LogInfo("Disposing .NET environment for question...");

            // remove publish file
            dockerCommandExecutor.RemoveFolder(
                questionConfigs[EnvironmentConfiguration.CodeContainerName],
                $"/apps/{questionConfigs[EnvironmentConfiguration.StudentQuestionName]}"
            );

            // remove sql file in sqlserver volume
            dockerCommandExecutor.RemoveFile(
                questionConfigs[EnvironmentConfiguration.DatabaseContainerName],
                $"/var/opt/mssql/{questionConfigs[EnvironmentConfiguration.StudentQuestionName]}.sql"
            );

            // drop database
            DropSqlDatabase(
                questionConfigs[EnvironmentConfiguration.DatabaseContainerName],
                questionConfigs[EnvironmentConfiguration.DatabaseUsername],
                questionConfigs[EnvironmentConfiguration.DatabasePassword],
                questionConfigs[EnvironmentConfiguration.DatabaseName]
            );
        }
        #endregion

        #region CORE
        public override void CopyEssentialFilesAndFolders()
        {
            // must copy runtimes folder to student question first
            CopyRuntimesToClient();
            CopyRuntimesToServer();

            // deployt server
            CopyPublishFolder();
            // deploy client
            CopyGivenConsolePublishFolder();
        }

        public override void GenerateDbScript()
        {
            try
            {
                string sqlFilePath = questionConfigs[EnvironmentConfiguration.DefaultDatabaseFilePath];
                string oldDatabaseName = questionConfigs[EnvironmentConfiguration.DefaultDatabaseName];

                // Change to code file path since currently the server and only server need DB
                string studentQuestionPath = questionConfigs[EnvironmentConfiguration.CodeFilePath];
                string newDatabaseName = questionConfigs[EnvironmentConfiguration.DatabaseName];
                string containerName = questionConfigs[EnvironmentConfiguration.DatabaseContainerName];

                // read sql script and change db name
                string sqlFileName = $"{newDatabaseName}.sql";
                string sqlScriptContent = File.ReadAllText(sqlFilePath).Replace(oldDatabaseName, newDatabaseName);

                // create a new sql script contains only new db name
                string newSqlFilePath = Path.Combine(studentQuestionPath, sqlFileName);
                File.WriteAllText(newSqlFilePath, sqlScriptContent);

                // and copy to container
                dockerCommandExecutor.CopyFileToContainer(newSqlFilePath, $"{containerName}:/var/opt/mssql/{sqlFileName}");
            }
            catch (Exception ex)
            {
                // log
                //logger.LogErr($"Error while generating sql script. Details: {ex.Message}");
                return;
                // throw
                //throw new Exception($"Error while generating sql script. Details: {ex.Message}");
            }
        }

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

                // Update DB
                if (json["ConnectionStrings"] != null) json["ConnectionStrings"]["MyCnn"] = dbConn;

                // Update Networking
                json["IpAddress"] = ipOverride;
                json["Port"] = port;

                File.WriteAllText(filePath, json.ToString());
            }

            UpdateFile(EnvironmentConfiguration.CodeFilePath, "0.0.0.0");

            UpdateFile(EnvironmentConfiguration.GivenConsolePath, "host.docker.internal");
        }


        // TODO: Configure port
        public void ModifyGivenIpAndPort()
        {
            try
            {
                //thay đổi api url trước khi copy file given vào
                string appsettingsPath = Path.Combine(questionConfigs[EnvironmentConfiguration.StudentQuestionPath], "appsettings.json");
                if (!File.Exists(appsettingsPath))
                {
                    //logger.LogErr("appsettings.json not found!");
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
            catch (Exception ex)
            {
                // log
                //logger.LogErr("Error in appsettings.json: " + ex.Message);
                return;
            }

        }
        #endregion
        #endregion

        #region TC
        #region SETUP
        public override void SetupEnvironmentForTestCase(Domain.Entities.Main.Environment environment)
        {
            //logger.LogInfo("Setting up .NET environment for test case...");

            testCaseEnvironment = environment;
            testCaseConfigs = environment.Configs;
        }

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
        public override void DisposeEnvironmentForTestCase()
        {
            //logger.LogInfo("Disposing .NET environment for test case...");

            //throw new NotImplementedException();
        }

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
            catch (Exception ex)
            {
                // log
                //logger.LogErr("Error while reseting database: " + ex.Message);
                return;
                // throw
                //throw new Exception($"Error while reseting database. Details: {ex.Message}");
            }
        }

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
        private void ResetSqlDatabase(string sqlContainerName, string sqlUsername, string sqlPassword, string databaseName)
        {
            try
            {
                // drop database
                string dropDatabaseQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;";
                string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
                dockerCommandExecutor.ExecDockerCommand(command);

                // create new database
                command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -i /var/opt/mssql/{databaseName}.sql";
                dockerCommandExecutor.ExecDockerCommand(command);
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error while reset database. Detail: {ex.Message}");
                return;
            }
        }

        private void DropSqlDatabase(string sqlContainerName, string sqlUsername, string sqlPassword, string databaseName)
        {
            try
            {
                // drop database
                string dropDatabaseQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;";
                string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
                dockerCommandExecutor.ExecDockerCommand(command);
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error while reset database. Detail: {ex.Message}");
                return;
            }
        }

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
                //check if exist runtimes folder, if not copy
                if (!Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                    dockerCommandExecutor.CopyFolder(runtimesFolder, destinationPath);
                }
            }
            catch (Exception ex)
            {
                //logger.LogErr("Error adding runtimes folder: " + ex.Message);
                throw;
            }
        }

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
                //check if exist runtimes folder, if not copy
                if (!Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                    dockerCommandExecutor.CopyFolder(runtimesFolder, destinationPath);
                }
            }
            catch (Exception ex)
            {
                //logger.LogErr("Error adding runtimes folder: " + ex.Message);
                throw;
            }
        }

        // This is server - copy files only without starting
        private void CopyPublishFolder()
        {
            CopyServerFilesOnly();
            StartServerApplication();
        }

        /// <summary>
        /// Copies server publish files to the container without starting the application.
        /// Call StartServerApplication() separately to start the server.
        /// </summary>
        public void CopyServerFilesOnly()
        {
            try
            {
                // copy all publish files to container:/apps
                dockerCommandExecutor.CopyFileToContainer(
                    questionConfigs[EnvironmentConfiguration.CodeFilePath],
                    questionConfigs[EnvironmentConfiguration.CodeContainerName] + ":/apps"
                );
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error copying server files. Details: {ex.Message}");
                return;
            }
        }

        /// <summary>
        /// Starts the server application inside its container.
        /// Must be called after CopyServerFilesOnly().
        /// </summary>
        public void StartServerApplication()
        {
            try
            {
                // wait for .net deployment (this starts the application)
                dockerCommandExecutor.WaitForPublishConsoleFileDeployment(
                    questionConfigs[EnvironmentConfiguration.CodeContainerName],
                    questionConfigs[EnvironmentConfiguration.StudentQuestionName],
                    questionConfigs[EnvironmentConfiguration.DockerServerPath],
                    questionConfigs[EnvironmentConfiguration.CodeContainerInternalPort]
                );
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error starting server. Details: {ex.Message}");
                return;
            }
        }

        // This is client - copy files only without starting
        private void CopyGivenConsolePublishFolder()
        {
            CopyClientFilesOnly();
            StartClientApplication();
        }

        /// <summary>
        /// Copies client (given console) publish files to the container without starting the application.
        /// Call StartClientApplication() separately to start the client.
        /// </summary>
        public void CopyClientFilesOnly()
        {
            try
            {
                // copy all publish files to container:/apps
                dockerCommandExecutor.CopyFileToContainer(
                    questionConfigs[EnvironmentConfiguration.GivenConsolePath],
                    questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName] + ":/apps"
                );
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error copying client files. Details: {ex.Message}");
                return;
            }
        }

        /// <summary>
        /// Starts the client (given console) application inside its container.
        /// Must be called after CopyClientFilesOnly().
        /// </summary>
        public void StartClientApplication()
        {
            try
            {
                // wait for .net deployment (this starts the application)
                dockerCommandExecutor.WaitForPublishConsoleFileDeployment(
                    questionConfigs[EnvironmentConfiguration.GivenConsoleContainerName],
                    questionConfigs[EnvironmentConfiguration.GivenConsoleAppName],
                    questionConfigs[EnvironmentConfiguration.DockerClientPath],
                    "-1"
                );
            }
            catch (Exception ex)
            {
                //logger.LogErr($"Error starting client. Details: {ex.Message}");
                return;
            }
        }


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
