using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;

namespace SolutionGrader.Core.Services.Docker
{
    /// <summary>
    /// Service responsible for loading test kit configuration from Excel files.
    /// Handles:
    /// - Environment.xlsx parsing
    /// - Header.xlsx parsing
    /// - Test case discovery
    /// - Detail.xlsx parsing (actions, expected outputs, network flows)
    /// - Golden client/server discovery
    /// </summary>
    public sealed class DockerTestKitLoaderService
    {
        /// <summary>
        /// Event raised when progress is updated.
        /// </summary>
        public event EventHandler<string>? ProgressUpdated;
        
        /// <summary>
        /// Loads the complete test kit configuration from the specified path.
        /// </summary>
        public TestKitConfig LoadTestKitConfig(string testKitPath, DockerGradingConfig config)
        {
            var tkConfig = new TestKitConfig();
            
            // Load Environment.xlsx
            LoadEnvironmentConfig(testKitPath, tkConfig);
            
            // Load Header.xlsx
            LoadHeaderConfig(testKitPath, tkConfig);
            
            // Discover test cases
            DiscoverTestCases(testKitPath, config, tkConfig);
            
            // Discover golden client/server
            DiscoverGoldenFiles(testKitPath, tkConfig);
            
            // Log port configuration
            OnProgress($"[Port Config] TestKit default: {tkConfig.CodeContainerInternalPort}");
            
            return tkConfig;
        }
        
        /// <summary>
        /// Loads configuration from Environment.xlsx.
        /// </summary>
        private void LoadEnvironmentConfig(string testKitPath, TestKitConfig tkConfig)
        {
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (!File.Exists(envPath))
            {
                return;
            }
            
            try
            {
                using var wb = new XLWorkbook(envPath);
                if (wb.TryGetWorksheet("Config", out var ws))
                {
                    foreach (var row in ws.RowsUsed().Skip(1))
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim()?.ToLowerInvariant()?.Replace("_", "");
                        var value = row.Cell(2).GetValue<string>()?.Trim();
                        
                        switch (key)
                        {
                            case "codecontainerinternalport":
                                if (int.TryParse(value, out var ip)) tkConfig.CodeContainerInternalPort = ip;
                                break;
                            case "codecontainerhostport":
                                if (int.TryParse(value, out var hp)) tkConfig.CodeContainerHostPort = hp;
                                break;
                            case "codeimagename":
                                tkConfig.CodeImageName = value ?? tkConfig.CodeImageName;
                                break;
                            case "dockernetwork":
                                tkConfig.DockerNetwork = value ?? tkConfig.DockerNetwork;
                                break;
                            case "defaultdatabasename":
                                tkConfig.DatabaseName = value ?? tkConfig.DatabaseName;
                                break;
                            case "databasepassword":
                                tkConfig.DatabasePassword = value ?? "";
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[TestKit] WARNING: Failed to read Environment.xlsx: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads configuration from Header.xlsx.
        /// </summary>
        private void LoadHeaderConfig(string testKitPath, TestKitConfig tkConfig)
        {
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (!File.Exists(headerPath))
            {
                return;
            }
            
            try
            {
                using var wb = new XLWorkbook(headerPath);
                
                // Load question marks
                if (wb.TryGetWorksheet("QuestionMark", out var markSheet))
                {
                    foreach (var row in markSheet.RowsUsed().Skip(1))
                    {
                        var tcName = row.Cell(1).GetString()?.Trim();
                        
                        double mark = 0.0;
                        if (row.Cell(2).TryGetValue<double>(out var directValue))
                        {
                            mark = directValue;
                        }
                        else
                        {
                            var markStr = row.Cell(2).GetString().Trim();
                            if (!double.TryParse(markStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out mark))
                            {
                                OnProgress($"[Warning] Cannot parse mark value '{markStr}' for '{tcName}'");
                                mark = 0.0;
                            }
                        }
                        
                        if (!string.IsNullOrEmpty(tcName))
                            tkConfig.TestCaseMarks[tcName] = mark;
                    }
                }
                
                // Load config
                if (wb.TryGetWorksheet("Config", out var configSheet))
                {
                    foreach (var row in configSheet.RowsUsed().Skip(1))
                    {
                        var key = row.Cell(1).GetString()?.Trim();
                        var value = row.Cell(2).GetString()?.Trim();
                        
                        if (key?.Equals("Protocol", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            tkConfig.Protocol = value ?? "TCP";
                        }
                        else if (key?.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            tkConfig.DefaultGradeContent = value ?? "Client/Server";
                            OnProgress($"[TestKit] Root Header.xlsx Grade_Content: {tkConfig.DefaultGradeContent}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[TestKit] WARNING: Failed to read Header.xlsx: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Discovers test cases in the test kit folder.
        /// </summary>
        private void DiscoverTestCases(string testKitPath, DockerGradingConfig config, TestKitConfig tkConfig)
        {
            tkConfig.TestCases = Directory.GetDirectories(testKitPath)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "Detail.xlsx")))
                .Select(d =>
                {
                    var timeout = ReadTestCaseTimeout(d, config.TestCaseTimeoutSeconds);
                    return new TestCaseInfo
                    {
                        Name = Path.GetFileName(d),
                        Path = d,
                        MaxMark = tkConfig.TestCaseMarks.TryGetValue(Path.GetFileName(d), out var m) ? m : 0,
                        TimeoutSeconds = timeout,
                        GradeContent = tkConfig.DefaultGradeContent
                    };
                })
                .OrderBy(tc => tc.Name)
                .ToList();
            
            OnProgress($"[TestKit] Discovered {tkConfig.TestCases.Count} test cases");
        }
        
        /// <summary>
        /// Discovers golden client/server from Meta folder.
        /// </summary>
        private void DiscoverGoldenFiles(string testKitPath, TestKitConfig tkConfig)
        {
            var metaPath = Path.Combine(testKitPath, "Meta");
            if (!Directory.Exists(metaPath))
            {
                return;
            }
            
            // Look for given server
            var givenServerPath = Path.Combine(metaPath, "Given", "Server");
            if (Directory.Exists(givenServerPath))
            {
                var serverDll = Directory.GetFiles(givenServerPath, "Project11.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    ?? Directory.GetFiles(givenServerPath, "*.dll", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                        .FirstOrDefault();
                
                if (serverDll != null)
                {
                    tkConfig.GivenServerPath = serverDll;
                    OnProgress($"[TestKit] Found golden server: {Path.GetFileName(serverDll)}");
                }
            }
            
            // Look for given client
            var givenClientPath = Path.Combine(metaPath, "Given", "Client");
            if (Directory.Exists(givenClientPath))
            {
                var clientDll = Directory.GetFiles(givenClientPath, "Project12.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    ?? Directory.GetFiles(givenClientPath, "*.dll", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                        .FirstOrDefault();
                
                if (clientDll != null)
                {
                    tkConfig.GivenClientPath = clientDll;
                    OnProgress($"[TestKit] Found golden client: {Path.GetFileName(clientDll)}");
                }
            }
        }
        
        /// <summary>
        /// Reads the timeout configuration from the test case's Header.xlsx file.
        /// </summary>
        public static int ReadTestCaseTimeout(string testCasePath, int defaultTimeout)
        {
            var headerPath = Path.Combine(testCasePath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return defaultTimeout;
            
            try
            {
                using var wb = new XLWorkbook(headerPath);
                if (wb.TryGetWorksheet("Testcase_Property", out var ws))
                {
                    foreach (var row in ws.RowsUsed())
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim() ?? "";
                        var value = row.Cell(2).GetValue<string>()?.Trim() ?? "";
                        
                        if ((key.Equals("Timeout(Seconds)", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Timeout", StringComparison.OrdinalIgnoreCase)) &&
                            int.TryParse(value, out var parsedTimeout) && parsedTimeout > 0)
                        {
                            return parsedTimeout;
                        }
                    }
                }
            }
            catch
            {
                // Use defaults
            }
            
            return defaultTimeout;
        }
        
        /// <summary>
        /// Reads actions from Detail.xlsx User sheet.
        /// </summary>
        public List<(int Stage, string Input, string Action)> ReadActions(string detailPath)
        {
            var actions = new List<(int Stage, string Input, string Action)>();
            
            using var wb = new XLWorkbook(detailPath);
            if (wb.TryGetWorksheet("User", out var ws))
            {
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var input = row.Cell(2).GetValue<string>() ?? "";
                    var action = row.Cell(3).GetValue<string>() ?? "";
                    
                    if (int.TryParse(stageStr, out var stage) && !string.IsNullOrEmpty(action))
                        actions.Add((stage, input, action));
                }
            }
            
            return actions;
        }
        
        /// <summary>
        /// Reads expected outputs from Detail.xlsx Client and Server sheets.
        /// </summary>
        public Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
        {
            var outputs = new Dictionary<int, (string? ClientConsole, string? ServerConsole)>();
            
            using var wb = new XLWorkbook(detailPath);
            
            if (wb.TryGetWorksheet("Client", out var clientWs))
            {
                foreach (var row in clientWs.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var console = row.Cell(2).GetValue<string>();
                    if (int.TryParse(stageStr, out var stage))
                    {
                        if (!outputs.ContainsKey(stage))
                            outputs[stage] = (null, null);
                        var current = outputs[stage];
                        outputs[stage] = (console, current.ServerConsole);
                    }
                }
            }
            
            if (wb.TryGetWorksheet("Server", out var serverWs))
            {
                foreach (var row in serverWs.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var console = row.Cell(2).GetValue<string>();
                    if (int.TryParse(stageStr, out var stage))
                    {
                        if (!outputs.ContainsKey(stage))
                            outputs[stage] = (null, null);
                        var current = outputs[stage];
                        outputs[stage] = (current.ClientConsole, console);
                    }
                }
            }
            
            return outputs;
        }
        
        /// <summary>
        /// Reads expected network flows from Detail.xlsx Network sheet.
        /// </summary>
        public List<ExpectedNetworkFlow> ReadExpectedNetwork(string detailPath)
        {
            var flows = new List<ExpectedNetworkFlow>();
            
            OnProgress($"[ReadExpectedNetwork] Loading from: {detailPath}");
            
            using var wb = new XLWorkbook(detailPath);
            
            if (wb.TryGetWorksheet("Network", out var ws))
            {
                var rowCount = ws.RowsUsed().Count();
                OnProgress($"[ReadExpectedNetwork] Found {rowCount} rows");
                
                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var timeCell = row.Cell(2).GetValue<string>();
                    
                    // Skip rows marked as not validated
                    if (timeCell != null && timeCell.Contains("Not validated"))
                    {
                        continue;
                    }
                    
                    var flags = row.Cell(6).GetValue<string>();
                    var state = row.Cell(7).GetValue<string>();
                    var data = row.Cell(8).GetValue<string>();
                    var sourceRole = row.Cell(9).GetValue<string>();
                    var destRole = row.Cell(10).GetValue<string>();
                    
                    // HTTP-specific fields
                    var uri = row.Cell(11).GetValue<string>();
                    var method = row.Cell(12).GetValue<string>();
                    var status = row.Cell(13).GetValue<string>();
                    var httpVersion = row.Cell(14).GetValue<string>();
                    var httpBody = row.Cell(15).GetValue<string>();
                    
                    if (int.TryParse(stageStr, out var stage))
                    {
                        flows.Add(new ExpectedNetworkFlow
                        {
                            Stage = stage,
                            Flags = flags,
                            State = state,
                            Data = data,
                            SourceRole = sourceRole,
                            DestinationRole = destRole,
                            URI = uri,
                            Method = method,
                            Status = status,
                            HttpVersion = httpVersion,
                            HttpBody = httpBody
                        });
                    }
                }
            }
            else
            {
                OnProgress($"[ReadExpectedNetwork] WARNING: 'Network' worksheet NOT FOUND");
            }
            
            OnProgress($"[ReadExpectedNetwork] Returning {flows.Count} flows");
            
            return flows;
        }
        
        private void OnProgress(string message)
        {
            ProgressUpdated?.Invoke(this, message);
        }
    }
}
