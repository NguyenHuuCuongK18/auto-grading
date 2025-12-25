using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using EnvironmentManager.Services;

namespace DotNetEnvironmentManagerHelper.Services
{
    /// <summary>
    /// Service responsible for loading test kit configurations from Excel files.
    /// Handles parsing Environment.xlsx, Header.xlsx, and Detail.xlsx files.
    /// </summary>
    public class TestKitLoaderService
    {
        private readonly Action<string>? _progressCallback;

        /// <summary>
        /// Creates a new instance of the test kit loader service.
        /// </summary>
        public TestKitLoaderService(Action<string>? progressCallback = null)
        {
            _progressCallback = progressCallback;
        }

        /// <summary>
        /// Reports progress to the callback if available.
        /// </summary>
        protected void OnProgress(string message)
        {
            _progressCallback?.Invoke(message);
        }

        /// <summary>
        /// Loads the test kit configuration from the specified path.
        /// </summary>
        public TestKitConfiguration LoadTestKitConfig(string testKitPath, int defaultTimeoutSeconds = 120)
        {
            var tkConfig = new TestKitConfiguration();

            LoadEnvironmentConfig(testKitPath, tkConfig);
            LoadHeaderConfig(testKitPath, tkConfig);
            DiscoverTestCases(testKitPath, tkConfig, defaultTimeoutSeconds);
            LoadGivenPaths(testKitPath, tkConfig);

            return tkConfig;
        }

        /// <summary>
        /// Loads configuration from Environment.xlsx.
        /// </summary>
        private void LoadEnvironmentConfig(string testKitPath, TestKitConfiguration tkConfig)
        {
            var envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (!File.Exists(envPath))
                return;

            using var wb = new XLWorkbook(envPath);
            if (!wb.TryGetWorksheet("Config", out var ws))
                return;

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

        /// <summary>
        /// Loads configuration from Header.xlsx.
        /// </summary>
        private void LoadHeaderConfig(string testKitPath, TestKitConfiguration tkConfig)
        {
            var headerPath = Path.Combine(testKitPath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return;

            using var wb = new XLWorkbook(headerPath);
            
            // Load QuestionMark sheet for test case marks
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
                            OnProgress($"[Warning] Cannot parse mark value '{markStr}' for test case '{tcName}'");
                            mark = 0.0;
                        }
                    }

                    if (!string.IsNullOrEmpty(tcName))
                        tkConfig.TestCaseMarks[tcName] = mark;
                }
            }

            // Load Config sheet for protocol and grade content
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

        /// <summary>
        /// Discovers test cases in the test kit directory.
        /// </summary>
        private void DiscoverTestCases(string testKitPath, TestKitConfiguration tkConfig, int defaultTimeoutSeconds)
        {
            tkConfig.TestCases = Directory.GetDirectories(testKitPath)
                .Where(d => !Path.GetFileName(d).Equals("Meta", StringComparison.OrdinalIgnoreCase))
                .Where(d => File.Exists(Path.Combine(d, "Detail.xlsx")))
                .Select(d =>
                {
                    var timeout = ReadTestCaseTimeout(d, defaultTimeoutSeconds);
                    var tcName = Path.GetFileName(d);
                    return new TestCaseConfiguration
                    {
                        Name = tcName,
                        Path = d,
                        MaxMark = tkConfig.TestCaseMarks.TryGetValue(tcName, out var m) ? m : 1.0,
                        TimeoutSeconds = timeout,
                        GradeContent = ReadTestCaseGradeContent(d) ?? tkConfig.DefaultGradeContent
                    };
                })
                .OrderBy(tc => tc.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Loads golden client/server paths from Meta/Given folder.
        /// </summary>
        private void LoadGivenPaths(string testKitPath, TestKitConfiguration tkConfig)
        {
            var metaGivenPath = Path.Combine(testKitPath, "Meta", "Given");
            
            var serverPath = Path.Combine(metaGivenPath, "Server");
            if (Directory.Exists(serverPath))
            {
                var serverDll = Directory.GetFiles(serverPath, "*.dll", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                tkConfig.GivenServerPath = serverDll;
            }

            var clientPath = Path.Combine(metaGivenPath, "Client");
            if (Directory.Exists(clientPath))
            {
                var clientDll = Directory.GetFiles(clientPath, "*.dll", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                tkConfig.GivenClientPath = clientDll;
            }
        }

        /// <summary>
        /// Reads timeout from test case Header.xlsx.
        /// </summary>
        private int ReadTestCaseTimeout(string testCasePath, int defaultTimeout)
        {
            var headerPath = Path.Combine(testCasePath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return defaultTimeout;

            try
            {
                using var wb = new XLWorkbook(headerPath);
                if (!wb.TryGetWorksheet("Config", out var ws))
                    return defaultTimeout;

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var key = row.Cell(1).GetString()?.Trim();
                    if (key?.Equals("Timeout", StringComparison.OrdinalIgnoreCase) == true ||
                        key?.Equals("TimeoutSeconds", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var value = row.Cell(2).GetString()?.Trim();
                        if (int.TryParse(value, out var timeout))
                            return timeout;
                    }
                }
            }
            catch { }

            return defaultTimeout;
        }

        /// <summary>
        /// Reads Grade_Content from test case Header.xlsx.
        /// </summary>
        private string? ReadTestCaseGradeContent(string testCasePath)
        {
            var headerPath = Path.Combine(testCasePath, "Header.xlsx");
            if (!File.Exists(headerPath))
                return null;

            try
            {
                using var wb = new XLWorkbook(headerPath);
                if (!wb.TryGetWorksheet("Config", out var ws))
                    return null;

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var key = row.Cell(1).GetString()?.Trim();
                    if (key?.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return row.Cell(2).GetString()?.Trim();
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Reads actions from Detail.xlsx for a test case.
        /// </summary>
        public List<ActionData> ReadActions(string detailPath)
        {
            var actions = new List<ActionData>();

            if (!File.Exists(detailPath))
                return actions;

            try
            {
                using var wb = new XLWorkbook(detailPath);
                if (!wb.TryGetWorksheet("Action", out var ws))
                    return actions;

                foreach (var row in ws.RowsUsed().Skip(1))
                {
                    var stageStr = row.Cell(1).GetString()?.Trim();
                    if (!int.TryParse(stageStr, out var stage))
                        continue;

                    actions.Add(new ActionData
                    {
                        Stage = stage,
                        ActionType = row.Cell(2).GetString()?.Trim(),
                        Input = row.Cell(3).GetString()?.Trim()
                    });
                }
            }
            catch { }

            return actions;
        }

        /// <summary>
        /// Reads expected outputs from Detail.xlsx for a test case.
        /// </summary>
        public Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
        {
            var outputs = new Dictionary<int, (string? ClientConsole, string? ServerConsole)>();

            if (!File.Exists(detailPath))
                return outputs;

            try
            {
                using var wb = new XLWorkbook(detailPath);
                
                if (wb.TryGetWorksheet("Client", out var clientWs))
                {
                    foreach (var row in clientWs.RowsUsed().Skip(1))
                    {
                        var stageStr = row.Cell(1).GetString()?.Trim();
                        if (!int.TryParse(stageStr, out var stage))
                            continue;

                        var console = row.Cell(2).GetString()?.Trim();
                        if (!string.IsNullOrEmpty(console))
                        {
                            if (outputs.TryGetValue(stage, out var existing))
                            {
                                outputs[stage] = (console, existing.ServerConsole);
                            }
                            else
                            {
                                outputs[stage] = (console, null);
                            }
                        }
                    }
                }

                if (wb.TryGetWorksheet("Server", out var serverWs))
                {
                    foreach (var row in serverWs.RowsUsed().Skip(1))
                    {
                        var stageStr = row.Cell(1).GetString()?.Trim();
                        if (!int.TryParse(stageStr, out var stage))
                            continue;

                        var console = row.Cell(2).GetString()?.Trim();
                        if (!string.IsNullOrEmpty(console))
                        {
                            if (outputs.TryGetValue(stage, out var existing))
                            {
                                outputs[stage] = (existing.ClientConsole, console);
                            }
                            else
                            {
                                outputs[stage] = (null, console);
                            }
                        }
                    }
                }
            }
            catch { }

            return outputs;
        }
    }

    /// <summary>
    /// Configuration for a test kit.
    /// </summary>
    public class TestKitConfiguration
    {
        /// <summary>Internal port for code container</summary>
        public int CodeContainerInternalPort { get; set; } = 8000;
        
        /// <summary>Host port for code container</summary>
        public int CodeContainerHostPort { get; set; } = 8000;
        
        /// <summary>Docker image name for code containers</summary>
        public string CodeImageName { get; set; } = "dotnet-grader:latest";
        
        /// <summary>Docker network name</summary>
        public string DockerNetwork { get; set; } = "grading-network";
        
        /// <summary>Default database name</summary>
        public string DatabaseName { get; set; } = "Library";
        
        /// <summary>Database password</summary>
        public string DatabasePassword { get; set; } = "";
        
        /// <summary>Network protocol (TCP or HTTP)</summary>
        public string Protocol { get; set; } = "TCP";
        
        /// <summary>Default Grade_Content from root Header.xlsx</summary>
        public string DefaultGradeContent { get; set; } = "Client/Server";
        
        /// <summary>Path to golden server DLL</summary>
        public string? GivenServerPath { get; set; }
        
        /// <summary>Path to golden client DLL</summary>
        public string? GivenClientPath { get; set; }
        
        /// <summary>Test case marks from QuestionMark sheet</summary>
        public Dictionary<string, double> TestCaseMarks { get; set; } = new();
        
        /// <summary>Discovered test cases</summary>
        public List<TestCaseConfiguration> TestCases { get; set; } = new();
    }

    /// <summary>
    /// Configuration for a test case.
    /// </summary>
    public class TestCaseConfiguration
    {
        /// <summary>Name of the test case (directory name)</summary>
        public string Name { get; set; } = "";
        
        /// <summary>Full path to the test case directory</summary>
        public string Path { get; set; } = "";
        
        /// <summary>Maximum marks for this test case</summary>
        public double MaxMark { get; set; } = 1.0;
        
        /// <summary>Timeout in seconds for this test case</summary>
        public int TimeoutSeconds { get; set; } = 120;
        
        /// <summary>What to grade: "Client", "Server", or "Client/Server"</summary>
        public string GradeContent { get; set; } = "Client/Server";
    }
}
