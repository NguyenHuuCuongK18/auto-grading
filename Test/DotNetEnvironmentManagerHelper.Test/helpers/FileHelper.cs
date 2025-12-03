using Common.commons;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetEnvironmentManagerHelper.Test.helpers
{
    internal class FileHelper
    {
        private FileHelper() { }

        public static string GetProjectRootPath()
        {
            return Path.GetFullPath(Path.Combine(ResourceConstant.BuildPath, ResourceConstant.ProjectRootRelativePath));
        }

        public static void WriteToFile(string filePath, string content)
        {
            File.WriteAllText(filePath, content);
        }

        public static string CreateResourcePath(string resourceName)
        {
            return Path.Combine(GetProjectRootPath(), ResourceConstant.ResourceFolderName, resourceName);
        }

        public static string GetResourcePath(string resourceName)
        {
            string fullPath = Path.Combine(GetProjectRootPath(), ResourceConstant.ResourceFolderName, resourceName);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
            return string.Empty;
        }

        public static void CreateFile(string dir, string fileName, string content)
        {
            string filePath = Path.Combine(dir, fileName);
            File.WriteAllText(filePath, content);
        }

        // Fake a file deletion by returning a dummy path
        public static string FakeDeleteFile(string filePath)
        {
            return Path.Combine(filePath, "dummy_path");
        }

        // Delete a file fr, no coming back bozo
        public static void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
