// This file contains the Result Writing region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        // Result Writing methods are in this partial class file
        // The main implementation remains in DockerGradingService.cs
        // This file is a placeholder for future extraction of result writing logic
        
        // Result writing methods include:
        // - WriteTestCaseResultAsync
        // - SetUserSheetHeaders
        // - SetClientSheetHeaders
        // - SetServerSheetHeaders
        // - SetNetworkSheetHeaders
        // - WriteOverallSummaryAsync
        // - MoveSnapshotsToTCFolder
    }
}
