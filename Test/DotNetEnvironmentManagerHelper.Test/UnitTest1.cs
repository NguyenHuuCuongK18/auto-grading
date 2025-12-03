using Common.commons;
using Domain.Entities.Main;
using DotNetEnvironmentManagerHelper.Services;
using DotNetEnvironmentManagerHelper.Test.helpers;
using EnvironmentManager.Services;
using ProcessLauncher.ProcessLauncher;

namespace DotNetEnvironmentManagerHelper.Test
{
    /// <summary>
    /// Unit tests for DotNetEnvironmentManagerHelper
    /// - Auth : NhatNM - 2025/01/12 
    /// </summary>
    public class Tests
    {
        // Test naming rule of thumb: [UnitOfWork]_[StateUnderTest]_[ExpectedBehavior]

        [SetUp]
        public void Setup()
        {
            TestInitHelper.ResetAllResources(useDB: false);
        }

        [Test]
        public void GenerateConnectionFile_WriteConnectionStringToNonExistFile_ReturnWithNoSideEffect()
        {
            string clientAppsettingPath = FileHelper.GetResourcePath(ResourceConstant.ClientAppSettingsName);
            string serverAppsettingPath = FileHelper.GetResourcePath(ResourceConstant.ServerAppSettingsName);

            if (File.Exists(clientAppsettingPath) && File.Exists(serverAppsettingPath))
            {
                var service = new EnvironmentSetupService();
                service.GenerateConnectionFile();
            }
            else
            {
                Assert.Fail("Pre-condition failed: Appsettings files do not exist.");
            }

        }

        [Test]
        public void GenerateConnectionFile_WriteConnectionStringToExistFile_OverrideFileContent()
        {
            throw new NotImplementedException();
        }

        [Test]
        public void GenerateConnectionFile_WriteIpAndPortForServerToExistFile_OverrideFileContent()
        {
            throw new NotImplementedException();
        }

        [Test]
        public void GenerateConnectionFile_WriteIpAndPortForClientToExistFile_OverrideFileContent()
        {
            throw new NotImplementedException();
        }
    }
}