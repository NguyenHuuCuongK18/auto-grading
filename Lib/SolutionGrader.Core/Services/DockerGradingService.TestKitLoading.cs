// This file contains the Test Kit Loading region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Domain.Models;
using Domain.Models.Configuration;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Test Kit Loading

        private TestKitConfig LoadTestKitConfig(string testKitPath, DockerGradingConfig config)
        {
            var tkConfig = new TestKitConfig();

            // Load Environment.xlsx
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (File.Exists(envPath))
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

            // Load Header.xlsx
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (File.Exists(headerPath))
            {
                using var wb = new XLWorkbook(headerPath);
                if (wb.TryGetWorksheet("QuestionMark", out var markSheet))
                {
                    foreach (var row in markSheet.RowsUsed().Skip(1))
                    {
                        var tcName = row.Cell(1).GetString()?.Trim();

                        // Safely parse mark value - handle both numeric and text cells
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
                                OnProgress($"[Warning] Cannot parse mark value '{markStr}' for test case '{tcName}' - defaulting to 0");
                                mark = 0.0;
                            }
                        }

                        if (!string.IsNullOrEmpty(tcName))
                            tkConfig.TestCaseMarks[tcName] = mark;
                    }
                }

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
                            // Store Grade_Content from test kit root Header.xlsx
                            // This determines whether students submit Server, Client, or Both
                            tkConfig.DefaultGradeContent = value ?? "Client/Server";
                            OnProgress($"[TestKit] Root Header.xlsx Grade_Content: {tkConfig.DefaultGradeContent}");
                        }
                    }
                }
            }

            // Discover test cases and read per-test-case configuration from Header.xlsx
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
                        // CRITICAL: Use DefaultGradeContent from outer Header.xlsx, NOT per-test-case
                        // Container is set up ONCE at the beginning with outer configuration
                        GradeContent = tkConfig.DefaultGradeContent
                    };
                })
                .OrderBy(tc => tc.Name)
                .ToList();

            // Discover given/golden server and client from Meta folder
            // These are used when student only provides one component (e.g., client only)
            var metaPath = Path.Combine(testKitPath, "Meta");
            if (Directory.Exists(metaPath))
            {
                // Look for given server in Meta/Given/Server
                var givenServerPath = Path.Combine(metaPath, "Given", "Server");
                if (Directory.Exists(givenServerPath))
                {
                    // Find Project11.dll or any main DLL in the server folder
                    var serverDll = Directory.GetFiles(givenServerPath, "Project11.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.GetFiles(givenServerPath, "*.dll", SearchOption.TopDirectoryOnly)
                            .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                            .FirstOrDefault();

                    if (serverDll != null)
                    {
                        tkConfig.GivenServerPath = serverDll;
                        OnProgress($"[TestKit] Found given server: {Path.GetFileName(serverDll)}");
                    }
                }

                // Look for given client in Meta/Given/Client
                var givenClientPath = Path.Combine(metaPath, "Given", "Client");
                if (Directory.Exists(givenClientPath))
                {
                    // Find Project12.dll or any main DLL in the client folder
                    var clientDll = Directory.GetFiles(givenClientPath, "Project12.dll", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.GetFiles(givenClientPath, "*.dll", SearchOption.TopDirectoryOnly)
                            .Where(f => !Path.GetFileName(f).StartsWith("Microsoft.") && !Path.GetFileName(f).StartsWith("System."))
                            .FirstOrDefault();

                    if (clientDll != null)
                    {
                        tkConfig.GivenClientPath = clientDll;
                        OnProgress($"[TestKit] Found given client: {Path.GetFileName(clientDll)}");
                    }
                }
            }

            // Apply config overrides
            // CRITICAL: Do NOT override with allocated port - all students use same internal port (4000)
            // Docker containers are isolated, so there's no port conflict
            // The allocated port is no longer needed since we removed port allocation logic
            OnProgress($"[Port Config] LoadTestKitConfig - TestKit default: tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}");
            OnProgress($"[Port Config] LoadTestKitConfig - Allocated port (IGNORED): config.CodeContainerInternalPort={config.CodeContainerInternalPort}");

            // Do NOT override - keep the testkit default (4000)
            // if (config.CodeContainerInternalPort > 0)
            //     tkConfig.CodeContainerInternalPort = config.CodeContainerInternalPort;
            // if (config.CodeContainerHostPort > 0)
            //     tkConfig.CodeContainerHostPort = config.CodeContainerHostPort;

            OnProgress($"[Port Config] LoadTestKitConfig - Final (using testkit default): tkConfig.CodeContainerInternalPort={tkConfig.CodeContainerInternalPort}");

            return tkConfig;
        }

        /// <summary>
        /// Reads the timeout configuration from the test case's Header.xlsx file.
        /// Looks for the Testcase_Property sheet and reads:
        /// - Timeout(Seconds): timeout in seconds
        /// 
        /// The effective timeout is the LONGER of:
        /// - The timeout from Header.xlsx (if specified)
        /// - The default timeout (DefaultTestCaseTimeoutSeconds = 2 minutes)
        /// 
        /// NOTE: Grade_Content is NOT read here because the container is set up ONCE at the beginning
        /// with the outer environment configuration. Grade_Content must come from the outer Header.xlsx.
        /// </summary>
        /// <param name="testCasePath">Path to the test case folder</param>
        /// <param name="defaultTimeout">Default timeout to use if not specified in Header.xlsx (uses DefaultTestCaseTimeoutSeconds if not provided)</param>
        /// <returns>Timeout in seconds (longer of configured or default)</returns>
        private static int ReadTestCaseTimeout(string testCasePath, int defaultTimeout = 0)
        {
            // Use our constant if no default provided
            if (defaultTimeout <= 0)
                defaultTimeout = DefaultTestCaseTimeoutSeconds;
            
            var headerPath = Path.Combine(testCasePath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return defaultTimeout;

            try
            {
                using var wb = new XLWorkbook(headerPath);
                if (wb.TryGetWorksheet("Testcase_Property", out var ws))
                {
                    int configuredTimeout = 0;

                    foreach (var row in ws.RowsUsed())
                    {
                        var key = row.Cell(1).GetValue<string>()?.Trim() ?? "";
                        var value = row.Cell(2).GetValue<string>()?.Trim() ?? "";

                        // Read Timeout
                        if ((key.Equals("Timeout(Seconds)", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("Timeout", StringComparison.OrdinalIgnoreCase)) &&
                            int.TryParse(value, out var parsedTimeout) && parsedTimeout > 0)
                        {
                            configuredTimeout = parsedTimeout;
                            // NOTE: Cannot use OnProgress here - this is a static context
                            // Logging moved to instance method context where OnProgress is available
                        }
                    }

                    // Prioritize the LONGER timeout (per user requirement)
                    // This ensures students get enough time even if test case specifies less
                    return configuredTimeout > 0 
                        ? Math.Max(configuredTimeout, defaultTimeout) 
                        : defaultTimeout;
                }
            }
            catch
            {
                // Silently use defaults if header cannot be read
            }

            return defaultTimeout;
        }

        private List<(int Stage, string Input, string Action)> ReadActions(string detailPath)
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

        private Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
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

        private List<ExpectedNetworkFlow> ReadExpectedNetwork(string detailPath)
        {
            var flows = new List<ExpectedNetworkFlow>();

            // DIAGNOSTIC LOGGING - Written to GradingLogs files via OnProgress
            OnProgress($"[ReadExpectedNetwork] Called with Detail.xlsx path: {detailPath}");
            OnProgress($"[ReadExpectedNetwork] File exists: {File.Exists(detailPath)}");

            using var wb = new XLWorkbook(detailPath);

            OnProgress($"[ReadExpectedNetwork] Workbook loaded, checking for 'Network' worksheet");

            if (wb.TryGetWorksheet("Network", out var ws))
            {
                var rowCount = ws.RowsUsed().Count();
                OnProgress($"[ReadExpectedNetwork] 'Network' worksheet found with {rowCount} rows");

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetValue<string>();
                    var timeCell = row.Cell(2).GetValue<string>();

                    // CRITICAL FIX: Skip rows marked as "(Not validated by this test case)"
                    // These rows appear in Detail.xlsx but should NOT be used for network validation
                    if (timeCell != null && timeCell.Contains("Not validated"))
                    {
                        continue; // Skip this row
                    }

                    var flags = row.Cell(6).GetValue<string>();
                    var state = row.Cell(7).GetValue<string>();
                    var data = row.Cell(8).GetValue<string>();  // Column H: Data payload (for TCP)
                    var sourceRole = row.Cell(9).GetValue<string>();
                    var destRole = row.Cell(10).GetValue<string>();

                    // HTTP-specific fields (columns 11-15, if present)
                    // For HTTP protocol: URI, Method, Status, HttpVersion, HttpBody
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
                            // HTTP fields
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
                OnProgress($"[ReadExpectedNetwork] WARNING: 'Network' worksheet NOT FOUND in Detail.xlsx!");
                OnProgress($"[ReadExpectedNetwork] Available worksheets: {string.Join(", ", wb.Worksheets.Select(w => w.Name))}");
            }

            OnProgress($"[ReadExpectedNetwork] Returning {flows.Count} flows");

            return flows;
        }

        #endregion
    }
}
