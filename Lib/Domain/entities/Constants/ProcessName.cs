using System;
using System.IO;

namespace Domain.Entities.Constants
{
    /// <summary>
    /// Provides static paths to executable files and helper DLLs used in the grading system.
    /// </summary>
    public class ProcessName
    {
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));

        /// <summary>
        /// Path to the Environment Manager executable for managing Docker containers.
        /// </summary>
        public static readonly string EnvironmentManager = Path.Combine(ProjectRoot, @"Application\EnvironmentManager\bin\Debug\net8.0") + @"\EnvironmentManager.exe";

        /// <summary>
        /// Path to the .NET Environment Manager Helper DLL for setting up .NET-based containers.
        /// </summary>
        public static readonly string DotNetEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\DotNetEnvironmentManagerHelper\bin\Debug\net8.0") + @"\DotNetEnvironmentManagerHelper.dll";
    }
}
