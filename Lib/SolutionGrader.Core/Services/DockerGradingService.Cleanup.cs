// This file contains the Cleanup region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        // Cleanup methods are in this partial class file
        // The main implementation remains in DockerGradingService.cs
        // This file is a placeholder for future extraction of cleanup logic
        
        // Cleanup methods include:
        // - KillDotnetProcessesInContainerAsync
        // - ForceKillDotnetProcessesInContainerAsync
        // - ParseDotnetPidsFromPsOutput
        // - ResetDatabaseContainerAsync
        // - SaveDockerLogsAsync
        // - ReadFileFromContainer
        // - ReadFileFromContainerIncremental
        // - ExportLogsFromUnifiedContainerAsync
        // - ExportStageLogsForTestCaseAsync
        // - ClearStageLogsInContainer
        // - StopAllProcessesForNewTestCaseAsync
        // - CleanupUnifiedContainerAsync
        // - SetupNetworkMonitorContainerAsync
        // - CleanupNetworkMonitorContainerAsync
        // - ResetNetworkMonitorForNewTestCaseAsync
        // - CleanupDatabaseInstanceAsync
    }
}
