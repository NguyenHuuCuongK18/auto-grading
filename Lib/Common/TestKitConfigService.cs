using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace Common.Services
{
    /// <summary>
    /// Handles test kit configuration parsing and loading.
    /// Responsible for reading Header.xlsx and Detail.xlsx files.
    /// </summary>
    public class TestKitConfigService
    {
        private readonly Action<string>? _onProgress;

        public TestKitConfigService(Action<string>? onProgress = null)
        {
            _onProgress = onProgress;
        }

        /// <summary>
        /// Reads test case configuration from Detail.xlsx Config sheet.
        /// Returns (timeout, gradeContent) tuple.
        /// </summary>
        public (int timeout, string gradeContent) ReadTestCaseConfig(string testCasePath, int defaultTimeout)
        {
            var configPath = Path.Combine(testCasePath, "Detail.xlsx");
            if (!File.Exists(configPath))
            {
                _onProgress?.Invoke($"[CONFIG] Detail.xlsx not found, using defaults");
                return (defaultTimeout, "Client/Server");
            }

            try
            {
                using var workbook = new XLWorkbook(configPath);
                if (!workbook.Worksheets.Contains("Config"))
                {
                    _onProgress?.Invoke($"[CONFIG] Config sheet not found in Detail.xlsx");
                    return (defaultTimeout, "Client/Server");
                }

                var ws = workbook.Worksheet("Config");
                int timeout = defaultTimeout;
                string gradeContent = "Client/Server";

                // Read configuration rows
                var row = 2;
                while (row <= 100)
                {
                    var key = ws.Cell(row, 1).GetValue<string>()?.Trim();
                    if (string.IsNullOrEmpty(key))
                    {
                        row++;
                        continue;
                    }

                    var value = ws.Cell(row, 2).GetValue<string>()?.Trim();

                    if (key.Equals("Timeout", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int parsedTimeout))
                    {
                        timeout = parsedTimeout;
                    }
                    else if (key.Equals("Grade_Content", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                    {
                        gradeContent = value;
                    }

                    row++;
                }

                _onProgress?.Invoke($"[CONFIG] Test case config: Timeout={timeout}s, Grade_Content={gradeContent}");
                return (timeout, gradeContent);
            }
            catch (Exception ex)
            {
                _onProgress?.Invoke($"[CONFIG] Error reading test case config: {ex.Message}");
                return (defaultTimeout, "Client/Server");
            }
        }

        /// <summary>
        /// Reads actions from Detail.xlsx Action sheet.
        /// Returns list of (Stage, Input, Action) tuples.
        /// </summary>
        public List<(int Stage, string Input, string Action)> ReadActions(string detailPath)
        {
            var actions = new List<(int Stage, string Input, string Action)>();

            if (!File.Exists(detailPath))
            {
                _onProgress?.Invoke($"[CONFIG] Detail.xlsx not found at {detailPath}");
                return actions;
            }

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                if (!workbook.Worksheets.Contains("Action"))
                {
                    _onProgress?.Invoke($"[CONFIG] Action sheet not found in Detail.xlsx");
                    return actions;
                }

                var ws = workbook.Worksheet("Action");
                var row = 2; // Start from row 2 (skip header)

                while (row <= 1000)
                {
                    var stageStr = ws.Cell(row, 1).GetValue<string>();
                    if (string.IsNullOrWhiteSpace(stageStr))
                        break;

                    if (!int.TryParse(stageStr, out int stage))
                    {
                        row++;
                        continue;
                    }

                    var input = ws.Cell(row, 2).GetValue<string>() ?? "";
                    var action = ws.Cell(row, 3).GetValue<string>() ?? "";

                    actions.Add((stage, input, action));
                    row++;
                }

                _onProgress?.Invoke($"[CONFIG] Loaded {actions.Count} actions from Detail.xlsx");
            }
            catch (Exception ex)
            {
                _onProgress?.Invoke($"[CONFIG] Error reading actions: {ex.Message}");
            }

            return actions;
        }

        /// <summary>
        /// Reads expected console outputs from Detail.xlsx Expected_Output sheet.
        /// Returns dictionary: Stage -> (ClientConsole, ServerConsole)
        /// </summary>
        public Dictionary<int, (string? ClientConsole, string? ServerConsole)> ReadExpectedOutputs(string detailPath)
        {
            var outputs = new Dictionary<int, (string?, string?)>();

            if (!File.Exists(detailPath))
            {
                _onProgress?.Invoke($"[CONFIG] Detail.xlsx not found at {detailPath}");
                return outputs;
            }

            try
            {
                using var workbook = new XLWorkbook(detailPath);
                if (!workbook.Worksheets.Contains("Expected_Output"))
                {
                    _onProgress?.Invoke($"[CONFIG] Expected_Output sheet not found in Detail.xlsx");
                    return outputs;
                }

                var ws = workbook.Worksheet("Expected_Output");
                var row = 2; // Start from row 2 (skip header)

                while (row <= 1000)
                {
                    var stageStr = ws.Cell(row, 1).GetValue<string>();
                    if (string.IsNullOrWhiteSpace(stageStr))
                        break;

                    if (!int.TryParse(stageStr, out int stage))
                    {
                        row++;
                        continue;
                    }

                    var clientConsole = ws.Cell(row, 2).GetValue<string>();
                    var serverConsole = ws.Cell(row, 3).GetValue<string>();

                    outputs[stage] = (clientConsole, serverConsole);
                    row++;
                }

                _onProgress?.Invoke($"[CONFIG] Loaded expected outputs for {outputs.Count} stages");
            }
            catch (Exception ex)
            {
                _onProgress?.Invoke($"[CONFIG] Error reading expected outputs: {ex.Message}");
            }

            return outputs;
        }
    }
}
