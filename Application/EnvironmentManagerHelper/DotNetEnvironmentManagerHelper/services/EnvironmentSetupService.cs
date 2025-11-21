using Domain.Entities.Constants;
using Domain.Entities.Docker.DockerSupporter.Entity;
using EnvironmentManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using System.Media;
using System.Diagnostics;

namespace DotNetEnvironmentManagerHelper.Services
{
    public class EnvironmentSetupService : BaseEnvironmentSetupService
    {
        public override string EnvironmentType { get; } = "dotnet";

        #region TESTKIT
        #region SETUP
        public override void SetupContainerForTestKit(Domain.Entities.Main.Environment environment)
        {
            questionEnvironment = environment;
            questionConfigs = environment.Configs;

            try
            {
                string databaseContainerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseContainerName);
                string codeContainerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerName);

                if (!dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    SetupDatabaseContainer();
                }

                if (!dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    SetupCodeContainer();
                }

                Thread.Sleep(2000);

                // Ensure logs exist for grader to attach
                try
                {
                    dockerCommandExecutor.MakeDirectory(codeContainerName, "/logs");
                    dockerCommandExecutor.ExecDockerCommand($"{codeContainerName} sh -c \"touch /logs/client.log /logs/server.log\"");
                }
                catch { }

                // Prepare DB and essential files
                try { GenerateDbScript(); } catch { }
                try { ResetDatabase(); } catch { }
                try { CopyEssentialFilesAndFolders(); } catch { }
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
            questionEnvironment = environment;
            questionConfigs = environment.Configs;

            try
            {
                string databaseContainerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseContainerName);
                string codeContainerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerName);

                if (dockerCommandExecutor.IsContainerRunning(codeContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(codeContainerName);
                }

                if (dockerCommandExecutor.IsContainerRunning(databaseContainerName))
                {
                    dockerCommandExecutor.RemoveContainer(databaseContainerName);
                }

                if (!string.IsNullOrEmpty(questionConfigs[EnvironmentConfiguration.GivenApiContainerName]))
                {
                    string givenApiContainerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.GivenApiContainerName);

                    if (dockerCommandExecutor.IsContainerRunning(givenApiContainerName))
                    {
                        dockerCommandExecutor.RemoveContainer(givenApiContainerName);
                    }
                }
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
                string imageName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeImageName);
                string networkName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DockerNetwork);
                string containerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerName);
                int containerPort = int.Parse(TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerInternalPort));
                int hostPort = int.Parse(TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerHostPort));

                var envVars = GetEnvironmentVariablesForQuestion();
                if (envVars.ContainsKey(EnvironmentConfiguration.AppType))
                {
                    var v = envVars[EnvironmentConfiguration.AppType];
                    envVars["APP_TYPE"] = v;
                }

                string studentQuestionPath = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionPath);
                string studentQuestionName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionName);
                string configuredVolume = TryGetValueOrDefault(questionConfigs, "Code_Container_Volume");
                if (string.IsNullOrWhiteSpace(configuredVolume) && !string.IsNullOrWhiteSpace(studentQuestionPath) && Directory.Exists(studentQuestionPath))
                {
                    configuredVolume = $"{studentQuestionPath}:/apps/{studentQuestionName}";
                }
                Console.WriteLine($"[EnvSetup] Code container volume mapping: {(string.IsNullOrWhiteSpace(configuredVolume)?"<none>" : configuredVolume)}");
                if (!string.IsNullOrWhiteSpace(studentQuestionPath) && Directory.Exists(studentQuestionPath))
                {
                    Console.WriteLine("[EnvSetup] Host student question path contents before run:");
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(studentQuestionPath, "*", SearchOption.TopDirectoryOnly))
                            Console.WriteLine("[EnvSetup]   " + Path.GetFileName(f));
                    }
                    catch { }
                }

                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    ContainerPort = containerPort,
                    HostPort = hostPort,
                    EnvironmentVariables = envVars,
                    DockerVolume = configuredVolume
                };

                dockerCommandExecutor.RunContainer(dockerBase);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while setting up dotnet container. Details: {ex.Message}");
            }
        }

        public override void SetupDatabaseContainer()
        {
            try
            {
                string imageName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseImageName);
                string networkName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DockerNetwork);
                string containerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseContainerName);
                int containerPort = int.Parse(TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseContainerInternalPort));
                int hostPort = int.Parse(TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseContainerHostPort));
                string databaseUsername = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabaseUsername);
                string databasePassword = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DatabasePassword);

                var envs = new Dictionary<string, string>
                {
                    { "ACCEPT_EULA", "Y" },
                    // For MSSQL official image, SA_PASSWORD is required
                    { "SA_PASSWORD", databasePassword }
                };

                DockerBase dockerBase = new DockerBase
                {
                    ImageName = imageName,
                    DockerNetwork = networkName,
                    ContainerName = containerName,
                    ContainerPort = containerPort,
                    HostPort = hostPort,
                    EnvironmentVariables = envs
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
        public override void SetupEnvironmentForQuestion(Domain.Entities.Main.Environment environment)
        {
            // Enhanced setup: derive and normalize key configuration values (Given API setup removed per request)
            questionEnvironment = environment;
            questionConfigs = environment.Configs;

            try
            {
                var resourceRoot = TryGetValueOrDefault(questionConfigs, "Resource_Root", TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionPath));
                var questionPath = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionPath);
                if (!string.IsNullOrWhiteSpace(questionPath) && Directory.Exists(questionPath))
                {
                    var questionName = Path.GetFileName(questionPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
                    if (string.IsNullOrWhiteSpace(TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionName)))
                        questionConfigs[EnvironmentConfiguration.StudentQuestionName] = questionName;
                    questionConfigs[EnvironmentConfiguration.DatabaseName] = questionName;
                }

                var defaultDbName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DefaultDatabaseName);
                if (string.IsNullOrWhiteSpace(defaultDbName) && questionConfigs.TryGetValue(EnvironmentConfiguration.StudentQuestionName, out var qn) && !string.IsNullOrWhiteSpace(qn))
                {
                    questionConfigs[EnvironmentConfiguration.DefaultDatabaseName] = qn;
                }

                var sqlScriptRel = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.DefaultDatabaseFilePath);
                if (!string.IsNullOrWhiteSpace(sqlScriptRel) && !Path.IsPathRooted(sqlScriptRel))
                {
                    questionConfigs[EnvironmentConfiguration.DefaultDatabaseFilePath] = Path.Combine(resourceRoot ?? string.Empty, sqlScriptRel);
                }

                var connectFileRel = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.ConnectDbFile);
                if (!string.IsNullOrWhiteSpace(connectFileRel) && !Path.IsPathRooted(connectFileRel))
                {
                    questionConfigs[EnvironmentConfiguration.ConnectDbFile] = Path.Combine(resourceRoot ?? string.Empty, connectFileRel);
                }

                var runtimesRel = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.RuntimesFolder);
                if (!string.IsNullOrWhiteSpace(runtimesRel) && !Path.IsPathRooted(runtimesRel))
                {
                    questionConfigs[EnvironmentConfiguration.RuntimesFolder] = Path.Combine(resourceRoot ?? string.Empty, runtimesRel);
                }

                var codePath = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeFilePath);
                if (string.IsNullOrWhiteSpace(codePath) || Directory.Exists(codePath))
                {
                    if (!string.IsNullOrWhiteSpace(questionPath) && Directory.Exists(questionPath))
                    {
                        var dll = Directory.GetFiles(questionPath, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
                        var exe = Directory.GetFiles(questionPath, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                        var chosen = dll ?? exe;
                        if (!string.IsNullOrWhiteSpace(chosen))
                        {
                            questionConfigs[EnvironmentConfiguration.CodeFilePath] = chosen;
                        }
                    }
                }

                // Given API setup intentionally ignored.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EnvSetup] Warning: SetupEnvironmentForQuestion enhancements failed: {ex.Message}");
            }
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
                        break;
                    case EnvironmentQAction.GenerateDatabaseScript:
                        GenerateDbScript();
                        break;
                    case EnvironmentQAction.GenerateConnectionFile:
                        GenerateConnectionFile();
                        break;
                    case EnvironmentQAction.ModifyGivenIp:
                        ModifyGivenIpPort();
                        break;
                    default:
                        throw new Exception($"Action named '{step}' is not defined!");
                }
            }
        }
        #endregion

        #region DISPOSE
        public override void DisposeEnvironmentForQuestion()
        {
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
            try
            {
                // must copy runtimes folder to student question first
                CopyRuntimesToStudentQuestion();
                // then copy publish file into container
                CopyPublishFolder();
            }
            catch (Exception ex)
            {
                File.AppendAllText("log.txt", ex.ToString());
            }
        }

        private void CopyPublishFolder()
        {
            try
            {
                var codePath = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeFilePath);
                var containerName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.CodeContainerName);
                var appName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionName);
                if (string.IsNullOrWhiteSpace(codePath) || string.IsNullOrWhiteSpace(containerName) || string.IsNullOrWhiteSpace(appName))
                {
                    Console.WriteLine("[EnvSetup] CopyPublishFolder skipped: missing CodeFilePath/ContainerName/StudentQuestionName");
                    return;
                }

                string publishDir = Directory.Exists(codePath) ? codePath : (File.Exists(codePath) ? Path.GetDirectoryName(codePath)! : string.Empty);
                if (string.IsNullOrWhiteSpace(publishDir) || !Directory.Exists(publishDir))
                {
                    Console.WriteLine($"[EnvSetup] Publish directory not found: {publishDir}");
                    return;
                }

                // Ensure target directories
                try { dockerCommandExecutor.MakeDirectory(containerName, "/apps"); } catch { }
                try { dockerCommandExecutor.MakeDirectory(containerName, $"/apps/{appName}"); } catch { }

                // Stage then copy entire publish folder (no app start logic here)
                var stagingRoot = Path.Combine(Path.GetTempPath(), "dotnet_publish_stage", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingRoot);
                var stagingApp = Path.Combine(stagingRoot, appName);
                Directory.CreateDirectory(stagingApp);
                CopyDirectoryRecursive(publishDir, stagingApp);

                // Copy staged publish folder into container using docker cp (CopyFolderToContainer)
                dockerCommandExecutor.CopyFolderToContainer(stagingApp, containerName, $"/apps/{appName}");

                // List contents for diagnostics only
                try
                {
                    dockerCommandExecutor.ExecDockerCommand($"{containerName} sh -c \"echo '[EnvSetup] Contents of /apps/{appName}:'; ls -1 /apps/{appName} || echo 'Listing failed'\"");
                }
                catch { }

                // Touch trigger file for external watchers (still allowed)
                try
                {
                    dockerCommandExecutor.ExecDockerCommand($"{containerName} sh -c \"APP_NAME={appName}; touch /apps/$APP_NAME/restart.trigger && rm /apps/$APP_NAME/restart.trigger\"");
                }
                catch { }

                try { Directory.Delete(stagingRoot, true); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EnvSetup] Error copying publish folder: " + ex.Message);
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destDir, relative));
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(file, targetPath, overwrite: true);
            }
        }

        public override void GenerateDbScript()
        {
            try
            {
                string sqlFilePath = questionConfigs[EnvironmentConfiguration.DefaultDatabaseFilePath];
                string oldDatabaseName = questionConfigs[EnvironmentConfiguration.DefaultDatabaseName];
                string studentQuestionPath = questionConfigs[EnvironmentConfiguration.StudentQuestionPath];
                string newDatabaseName = questionConfigs[EnvironmentConfiguration.StudentQuestionName];
                string containerName = questionConfigs[EnvironmentConfiguration.DatabaseContainerName];

                string sqlFileName = $"{newDatabaseName}.sql";
                string sqlScriptContent = File.ReadAllText(sqlFilePath).Replace(oldDatabaseName, newDatabaseName);

                string newSqlFilePath = Path.Combine(studentQuestionPath, sqlFileName);
                File.WriteAllText(newSqlFilePath, sqlScriptContent);

                dockerCommandExecutor.CopyFileToContainer(newSqlFilePath, $"{containerName}:/var/opt/mssql/{sqlFileName}");
            }
            catch (Exception)
            {
                return;
            }
        }

        public override void GenerateConnectionFile()
        {
            string newConnectionString =
                $"Server={questionConfigs[EnvironmentConfiguration.DatabaseContainerName]}," +
                $"{questionConfigs[EnvironmentConfiguration.DatabaseContainerInternalPort]};" +
                $"database={questionConfigs[EnvironmentConfiguration.DatabaseName]};" +
                $"uid={questionConfigs[EnvironmentConfiguration.DatabaseUsername]};" +
                $"Password={questionConfigs[EnvironmentConfiguration.DatabasePassword]};" +
                $"Encrypt=false;TrustServerCertificate=true";

            string appsettingsPath = Path.Combine(questionConfigs[EnvironmentConfiguration.StudentQuestionPath], "appsettings.json");
            if (!File.Exists(appsettingsPath)) return;

            string appsettingsContent = File.ReadAllText(appsettingsPath);
            var appsettingsJson = JObject.Parse(appsettingsContent);
            var connectionStringsSection = appsettingsJson["ConnectionStrings"];
            if (connectionStringsSection != null)
                connectionStringsSection["MyCnn"] = newConnectionString;

            string updatedContent = appsettingsJson.ToString();
            File.WriteAllText(appsettingsPath, updatedContent);
        }

        public void ModifyGivenIpPort()
        {
            throw new NotImplementedException();
        }
        #endregion
        #endregion

        #region TC
        #region SETUP
        public override void SetupEnvironmentForTestCase(Domain.Entities.Main.Environment environment)
        {
            testCaseEnvironment = environment;
            testCaseConfigs = environment.Configs;

            try
            {
                // Inherit student question identity from question environment if missing
                if (questionConfigs != null)
                {
                    if (!testCaseConfigs.ContainsKey(EnvironmentConfiguration.StudentQuestionName) && questionConfigs.TryGetValue(EnvironmentConfiguration.StudentQuestionName, out var qn))
                        testCaseConfigs[EnvironmentConfiguration.StudentQuestionName] = qn;
                    if (!testCaseConfigs.ContainsKey(EnvironmentConfiguration.StudentQuestionPath) && questionConfigs.TryGetValue(EnvironmentConfiguration.StudentQuestionPath, out var qp))
                        testCaseConfigs[EnvironmentConfiguration.StudentQuestionPath] = qp;
                }

                // Reuse existing ports (no dynamic randomization here)
                var codeInternal = TryGetValueOrDefault(questionConfigs ?? testCaseConfigs, EnvironmentConfiguration.CodeContainerInternalPort);
                var codeHost = TryGetValueOrDefault(questionConfigs ?? testCaseConfigs, EnvironmentConfiguration.CodeContainerHostPort);
                if (!string.IsNullOrWhiteSpace(codeInternal))
                    SetOrReplaceConfig(testCaseConfigs, EnvironmentConfiguration.CodeContainerInternalPort, codeInternal);
                if (!string.IsNullOrWhiteSpace(codeHost))
                    SetOrReplaceConfig(testCaseConfigs, EnvironmentConfiguration.CodeContainerHostPort, codeHost);

                // Database reuse
                foreach (var k in new[] { EnvironmentConfiguration.DatabaseName, EnvironmentConfiguration.DatabaseUsername, EnvironmentConfiguration.DatabasePassword, EnvironmentConfiguration.DatabaseContainerName })
                {
                    var val = TryGetValueOrDefault(questionConfigs ?? testCaseConfigs, k);
                    if (!string.IsNullOrWhiteSpace(val)) SetOrReplaceConfig(testCaseConfigs, k, val);
                }

                // Connection string synthesis (MyCnn) for mssql/postgres/mysql
                var dbms = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseManagementSystem).ToLowerInvariant();
                var databaseName = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseName);
                var sqlUsername = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabaseUsername);
                var sqlPassword = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.DatabasePassword);
                var dbHostPort = TryGetValueOrDefault(questionConfigs ?? testCaseConfigs, EnvironmentConfiguration.DatabaseContainerHostPort);
                string connectionString = string.Empty;
                if (!string.IsNullOrWhiteSpace(dbms))
                {
                    if (dbms.Contains("mssql"))
                        connectionString = $"Server=localhost,{dbHostPort};Database={databaseName};User Id={sqlUsername};Password={sqlPassword};Encrypt=false;TrustServerCertificate=true";
                    else if (dbms.Contains("postgres"))
                        connectionString = $"Host=localhost;Port={dbHostPort};Database={databaseName};Username={sqlUsername};Password={sqlPassword};SSL Mode=Disable;Trust Server Certificate=true";
                    else if (dbms.Contains("mysql"))
                        connectionString = $"Server=localhost;Port={dbHostPort};Database={databaseName};Uid={sqlUsername};Pwd={sqlPassword};SslMode=none;";
                }
                if (!string.IsNullOrWhiteSpace(connectionString))
                    SetOrReplaceConfig(testCaseConfigs, "MyCnn", connectionString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EnvSetup] Warning: SetupEnvironmentForTestCase enhancements failed: {ex.Message}");
            }
        }
        #endregion

        #region DISPOSE
        public override void DisposeEnvironmentForTestCase()
        {
            // no-op for now
        }

        public void DisposeGivenAPI()
        {
            string givenApiContainerName = TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.CodeContainerName);
            string publishFileName = $"/apps/{TryGetValueOrDefault(testCaseConfigs, EnvironmentConfiguration.StudentQuestionName)}";
            try
            {
                dockerCommandExecutor.RemoveFolder(givenApiContainerName, publishFileName);
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
                    TryGetValueOrDefault(testCaseConfigs ?? questionConfigs, EnvironmentConfiguration.DatabaseContainerName),
                    TryGetValueOrDefault(testCaseConfigs ?? questionConfigs, EnvironmentConfiguration.DatabaseUsername),
                    TryGetValueOrDefault(testCaseConfigs ?? questionConfigs, EnvironmentConfiguration.DatabasePassword),
                    TryGetValueOrDefault(testCaseConfigs ?? questionConfigs, EnvironmentConfiguration.DatabaseName)
                );
            }
            catch (Exception)
            {
                return;
            }
        }

        public void RestartGivenApi()
        {
            string givenApiAppName = testCaseConfigs[EnvironmentConfiguration.GivenApiAppName];
            string givenApiContainerName = testCaseConfigs[EnvironmentConfiguration.GivenApiContainerName];

            string restartCommand = $"{givenApiContainerName} sh -c \"APP_NAME={givenApiAppName} && if [ -f /tmp/$APP_NAME.pid ]; then kill `cat /tmp/$APP_NAME.pid` 2>/dev/null; rm -f /tmp/$APP_NAME.pid /tmp/$APP_NAME.port; fi && touch /apps/$APP_NAME/tempfile && rm /apps/$APP_NAME/tempfile\"";

            dockerCommandExecutor.ExecDockerCommand(restartCommand, 30000);
            dockerCommandExecutor.WaitForPublishFileDeployment(givenApiContainerName, givenApiAppName);
        }
        #endregion
        #endregion

        #region UTILITIES
        private void ResetSqlDatabase(string sqlContainerName, string sqlUsername, string sqlPassword, string databaseName)
        {
            try
            {
                string dropDatabaseQuery = $@"USE master; IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;";
                string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
                dockerCommandExecutor.ExecDockerCommand(command);

                command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U {sqlUsername} -P {sqlPassword} -i /var/opt/mssql/{databaseName}.sql";
                dockerCommandExecutor.ExecDockerCommand(command);
            }
            catch (Exception)
            {
                return;
            }
        }

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

        private void CopyRuntimesToStudentQuestion()
        {
            try
            {
                string runtimesFolder = TryGetValueOrDefault(questionEnvironment.Configs, EnvironmentConfiguration.RuntimesFolder);
                string studentQuestionPath = TryGetValueOrDefault(questionEnvironment.Configs, EnvironmentConfiguration.StudentQuestionPath);

                string destinationPath = Path.Combine(studentQuestionPath, "runtimes");
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

        private Dictionary<string, string> GetEnvironmentVariablesForQuestion()
        {
            string appType = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.AppType);
            string studentQuestionName = TryGetValueOrDefault(questionConfigs, EnvironmentConfiguration.StudentQuestionName);
            var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(appType)) environmentVariables[EnvironmentConfiguration.AppType] = appType;
            if (!string.IsNullOrWhiteSpace(studentQuestionName)) environmentVariables["APP_NAME"] = studentQuestionName;
            return environmentVariables;
        }

        private static void SetOrReplaceConfig(Dictionary<string, string> configs, string key, string value)
        {
            if (configs.ContainsKey(key)) configs[key] = value; else configs[key] = value;
        }
        #endregion
    }
}
