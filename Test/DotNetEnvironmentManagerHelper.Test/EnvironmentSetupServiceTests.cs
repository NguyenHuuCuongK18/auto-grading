using Common.commons;
using DotNetEnvironmentManagerHelper.Services;
using DotNetEnvironmentManagerHelper.Test.helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.IO;
using System.Collections.Generic;

namespace DotNetEnvironmentManagerHelper.Test
{
    /// <summary>
    /// Unit tests for DotNetEnvironmentManagerHelper
    /// - Auth : NhatNM - 2025/01/12 
    ///
    /// Note: Test naming rule of thumb: [TestID]_[UnitOfWork]_[StateUnderTest]_[ExpectedBehavior]
    /// </summary>
    public class EnvironmentSetupServiceTests
    {
        private string _clientAppsettingsPath = null!;
        private string _serverAppsettingsPath = null!;
        private string _clientRoot = null!;
        private string _serverRoot = null!;

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            _clientAppsettingsPath = FileHelper.GetResourcePath(ResourceConstant.AppSettingsName, ResourceConstant.ClientResourceFolderName);
            _serverAppsettingsPath = FileHelper.GetResourcePath(ResourceConstant.AppSettingsName, ResourceConstant.ServerResourceFolderName);

            Assert.That(File.Exists(_clientAppsettingsPath), "Client appsettings.json must exist.");
            Assert.That(File.Exists(_serverAppsettingsPath), "Server appsettings.json must exist.");

            _clientRoot = Path.GetDirectoryName(_clientAppsettingsPath)!;
            _serverRoot = Path.GetDirectoryName(_serverAppsettingsPath)!;
        }

        [SetUp]
        public void Setup()
        {
            TestInitHelper.ResetAllResources(useDB: false);
        }

        private static void AssertIpPort(string path, string expectedIp, string expectedPort, string subject)
        {
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.That(json["IpAddress"]?.ToString(), Is.EqualTo(expectedIp), $"{subject} IpAddress should be {expectedIp}.");
            Assert.That(json["Port"]?.ToString(), Is.EqualTo(expectedPort), $"{subject} Port should be {expectedPort}.");
        }

        private Dictionary<string, string> CreateEnvConfigs(
            string port,
            bool includeServerPath,
            bool includeClientPath,
            bool includeDbSecrets = true)
        {
            var configs = new Dictionary<string, string>
            {
                { Domain.Entities.Constants.EnvironmentConfiguration.DatabaseContainerName, "sqlserver" },
                { Domain.Entities.Constants.EnvironmentConfiguration.DatabaseContainerInternalPort, "1433" },
                { Domain.Entities.Constants.EnvironmentConfiguration.DatabaseName, "Library" },
                { Domain.Entities.Constants.EnvironmentConfiguration.DatabaseUsername, "sa" },
                { Domain.Entities.Constants.EnvironmentConfiguration.CodeContainerInternalPort, port },
                { Domain.Entities.Constants.EnvironmentConfiguration.CodeContainerName, "server-app" },
                { Domain.Entities.Constants.EnvironmentConfiguration.EnvironmentType, "dotnet" }
            };

            if (includeDbSecrets)
            {
                configs[Domain.Entities.Constants.EnvironmentConfiguration.DatabasePassword] = "Pass@word1";
            }

            configs[Domain.Entities.Constants.EnvironmentConfiguration.CodeFilePath] = includeServerPath ? _serverRoot : "";
            configs[Domain.Entities.Constants.EnvironmentConfiguration.GivenConsolePath] = includeClientPath ? _clientRoot : "";

            return configs;
        }

        private static EnvironmentSetupService CreateService(Dictionary<string, string> configs)
        {
            var env = new Domain.Entities.Main.Environment { Configs = configs, Steps = new List<string>() };
            var service = new EnvironmentSetupService();
            service.SetupEnvironmentForQuestion(env);
            return service;
        }

        [Test]
        public void UT01_GenerateConnectionFile_WriteConnectionStringNonExistFile_FileModifiedWithCorrectInfo()
        {
            string originalServer = File.ReadAllText(_serverAppsettingsPath);
            string originalClient = File.ReadAllText(_clientAppsettingsPath);

            var configs = CreateEnvConfigs(port: "5000", includeServerPath: false, includeClientPath: false, includeDbSecrets: true);
            var service = CreateService(configs);
            service.GenerateConnectionFile();

            var serverJson = File.ReadAllText(_serverAppsettingsPath);
            var clientJson = File.ReadAllText(_clientAppsettingsPath);

            Assert.That(serverJson, Is.EqualTo(originalServer), "Server appsettings.json should remain unchanged.");
            Assert.That(clientJson, Is.EqualTo(originalClient), "Client appsettings.json should remain unchanged.");
        }

        [TestCase("5000")]
        [TestCase("5050")]
        public void UT02_GenerateConnectionFile_WriteConnectionStringToExistFile_FileModifiedWithCorrectInfo(string port)
        {
            var configs = CreateEnvConfigs(port: port, includeServerPath: true, includeClientPath: true, includeDbSecrets: true);
            var service = CreateService(configs);
            service.GenerateConnectionFile();

            AssertIpPort(_serverAppsettingsPath, "0.0.0.0", port, "Server");
            AssertIpPort(_clientAppsettingsPath, "host.docker.internal", port, "Client");
        }

        [TestCase("5050", true, false)]
        [TestCase("5050", false, true)]
        public void UT03_GenerateConnectionFile_MissingOnePathKey_NoChangesAndNoException_WhileOtherSideModifiedCorrectly(string port, bool includeServerPath, bool includeClientPath)
        {
            var configs = CreateEnvConfigs(port: port, includeServerPath: includeServerPath, includeClientPath: includeClientPath, includeDbSecrets: true);
            var originalServer = File.ReadAllText(_serverAppsettingsPath);
            var originalClient = File.ReadAllText(_clientAppsettingsPath);

            var service = CreateService(configs);
            Assert.DoesNotThrow(() => service.GenerateConnectionFile());

            if (includeServerPath)
                AssertIpPort(_serverAppsettingsPath, "0.0.0.0", port, "Server");
            else
                Assert.That(File.ReadAllText(_serverAppsettingsPath), Is.EqualTo(originalServer), "Server appsettings.json should remain unchanged.");

            if (includeClientPath)
                AssertIpPort(_clientAppsettingsPath, "host.docker.internal", port, "Client");
            else
                Assert.That(File.ReadAllText(_clientAppsettingsPath), Is.EqualTo(originalClient), "Client appsettings.json should remain unchanged.");
        }

        [Test]
        public void UT04_GenerateConnectionFile_MissingBothPathKey_NoChangesAndNoException()
        {
            var originalClient = File.ReadAllText(_clientAppsettingsPath);
            var originalServer = File.ReadAllText(_serverAppsettingsPath);

            var configs = CreateEnvConfigs(port: "5050", includeServerPath: false, includeClientPath: false, includeDbSecrets: true);
            var service = CreateService(configs);
            Assert.DoesNotThrow(() => service.GenerateConnectionFile());

            var newServer = File.ReadAllText(_clientAppsettingsPath);
            var newClient = File.ReadAllText(_serverAppsettingsPath);
            Assert.That(newClient, Is.EqualTo(originalClient), "Client appsettings.json should remain unchanged.");
            Assert.That(newServer, Is.EqualTo(originalServer), "Server appsettings.json should remain unchanged.");
        }

        [TestCase("6001")]
        [TestCase("7000")]
        public void UT05_GenerateConnectionFile_WriteConnectionStringToExistFileWithDB_FileModifiedWithCorrectInfo(string port)
        {
            TestInitHelper.ResetAllResources(useDB: true);

            var configs = CreateEnvConfigs(port: port, includeServerPath: true, includeClientPath: true, includeDbSecrets: true);
            var service = CreateService(configs);
            service.GenerateConnectionFile();

            var expectedConnStr = "Server=sqlserver,1433;database=Library;uid=sa;Password=Pass@word1;Encrypt=false;TrustServerCertificate=true";

            AssertIpPort(_serverAppsettingsPath, "0.0.0.0", port, "Server");
            var serverJson = JObject.Parse(File.ReadAllText(_serverAppsettingsPath));
            var serverCnn = DataHelper.ReadConnString(serverJson);
            if (serverCnn is not null) Assert.That(serverCnn, Is.EqualTo(expectedConnStr), "Server connection string should be updated.");

            AssertIpPort(_clientAppsettingsPath, "host.docker.internal", port, "Client");
            var clientJson = JObject.Parse(File.ReadAllText(_clientAppsettingsPath));
            var clientCnn = DataHelper.ReadConnString(clientJson);
            if (clientCnn is not null) Assert.That(clientCnn, Is.EqualTo(expectedConnStr), "Client connection string should be updated.");
        }

        [Test]
        public void UT06_GenerateConnectionFile_MissingDatabaseKey_ThrowsAndNoFileChanges()
        {
            TestInitHelper.ResetAllResources(useDB: true);
            var originalClient = File.ReadAllText(_clientAppsettingsPath);
            var originalServer = File.ReadAllText(_serverAppsettingsPath);

            var configs = CreateEnvConfigs(port: "7000", includeServerPath: true, includeClientPath: true, includeDbSecrets: false);
            // Deliberately omit DatabasePassword
            configs.Remove(Domain.Entities.Constants.EnvironmentConfiguration.DatabasePassword);

            var service = CreateService(configs);

            Assert.Throws<KeyNotFoundException>(() => service.GenerateConnectionFile(), "Expected KeyNotFoundException when a DB key is missing.");

            var newClient = File.ReadAllText(_clientAppsettingsPath);
            var newServer = File.ReadAllText(_serverAppsettingsPath);
            Assert.That(newClient, Is.EqualTo(originalClient), "Client appsettings.json should remain unchanged on failure.");
            Assert.That(newServer, Is.EqualTo(originalServer), "Server appsettings.json should remain unchanged on failure.");
        }

        [Test]
        public void UT07_GenerateConnectionFile_MissingConnectionStringNameForServerInAppsettings_CreateKeyAndModifyCorrectly()
        {
            TestInitHelper.ResetAllResources(useDB: true);
            FileHelper.WriteToFile(_serverAppsettingsPath, ResouceTemplate.AppSettingsWithDBTemplateNoCnn);

            var configs = CreateEnvConfigs(port: "6001", includeServerPath: true, includeClientPath: true, includeDbSecrets: true);
            var service = CreateService(configs);
            service.GenerateConnectionFile();

            var expectedConnStr = "Server=sqlserver,1433;database=Library;uid=sa;Password=Pass@word1;Encrypt=false;TrustServerCertificate=true";

            AssertIpPort(_serverAppsettingsPath, "0.0.0.0", "6001", "Server");
            var serverJson = JObject.Parse(File.ReadAllText(_serverAppsettingsPath));
            var serverCnn = DataHelper.ReadConnString(serverJson);
            if (serverCnn is not null) Assert.That(serverCnn, Is.EqualTo(expectedConnStr), "Server connection string should be updated.");
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            TestInitHelper.ResetAllResources(useDB: false);
        }
    }
}