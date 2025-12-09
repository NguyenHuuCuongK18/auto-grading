using Common.commons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetEnvironmentManagerHelper.Test.helpers
{
    public class TestInitHelper
    {
        private TestInitHelper() { }

        public static void ResetAllResources(bool useDB)
        {
            string clientAppSettingsPath = FileHelper.GetResourcePath(ResourceConstant.AppSettingsName, ResourceConstant.ClientResourceFolderName);
            string serverAppSettingsPath = FileHelper.GetResourcePath(ResourceConstant.AppSettingsName, ResourceConstant.ServerResourceFolderName);

            if (useDB)
            {
                FileHelper.WriteToFile(serverAppSettingsPath, ResouceTemplate.AppSettingsWithDBTemplate);
            }
            else
            {
                FileHelper.WriteToFile(serverAppSettingsPath, ResouceTemplate.AppSettingsTemplate);
            }

            FileHelper.WriteToFile(clientAppSettingsPath, ResouceTemplate.AppSettingsTemplate);
        }
    }
}
