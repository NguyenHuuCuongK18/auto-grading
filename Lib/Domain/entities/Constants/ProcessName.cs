using System;
using System.IO;

namespace Domain.Entities.Constants
{
    /// <summary>
    /// Provides static paths to executable files used in the grading system.
    /// Note: EnvironmentManager and DotNetEnvironmentManagerHelper have been removed
    /// as they were replaced by DockerGradingService's integrated container management.
    /// </summary>
    public class ProcessName
    {
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
    }
}
