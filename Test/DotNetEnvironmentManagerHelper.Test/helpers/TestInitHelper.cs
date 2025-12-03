using Common.commons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetEnvironmentManagerHelper.Test.helpers
{
    internal class TestInitHelper
    {
        private TestInitHelper() { }

        public static void ResetAllResources(bool useDB)
        {
            string clientAppSettingsPath = FileHelper.GetResourcePath(ResourceConstant.ClientAppSettingsName);
            string serverAppSettingsPath = FileHelper.GetResourcePath(ResourceConstant.ServerAppSettingsName);

            if (string.IsNullOrEmpty(clientAppSettingsPath) || string.IsNullOrEmpty(serverAppSettingsPath))
            {
                clientAppSettingsPath = FileHelper.CreateResourcePath(ResourceConstant.ClientAppSettingsName);
                serverAppSettingsPath = FileHelper.CreateResourcePath(ResourceConstant.ServerAppSettingsName);
            }

            if (useDB)
            {
                FileHelper.WriteToFile(clientAppSettingsPath, ResouceTemplate.AppSettingsTemplate);
                FileHelper.WriteToFile(serverAppSettingsPath, ResouceTemplate.AppSettingsWithDBTemplate);
            }
            else
            {
                FileHelper.WriteToFile(clientAppSettingsPath, ResouceTemplate.AppSettingsTemplate);
                FileHelper.WriteToFile(serverAppSettingsPath, ResouceTemplate.AppSettingsTemplate);
            }
        }


    }
}
