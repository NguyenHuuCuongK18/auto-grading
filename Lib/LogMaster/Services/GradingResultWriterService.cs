using System;
using System.IO;
using System.Threading.Tasks;
using ClosedXML.Excel;
using LogMaster.Models;

namespace LogMaster.Services
{
    /// <summary>
    /// Service responsible for writing grading results to Excel files.
    /// Creates GradeDetail.xlsx files in the SampleLogging format.
    /// </summary>
    public class GradingResultWriterService
    {
        private readonly Action<string>? _progressCallback;

        /// <summary>
        /// Creates a new instance of the grading result writer service.
        /// </summary>
        public GradingResultWriterService(Action<string>? progressCallback = null)
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
        /// Writes test case result to GradeDetail.xlsx in the SampleLogging format.
        /// Creates User, Client, Server, Database, and Network sheets.
        /// </summary>
        public async Task WriteTestCaseResultAsync(string tcResultPath, TestCaseResultData result)
        {
            var detailPath = Path.Combine(tcResultPath, "GradeDetail.xlsx");
            using var wb = new XLWorkbook();

            // === User Sheet ===
            var userWs = wb.Worksheets.Add("User");
            SetUserSheetHeaders(userWs);
            int userRow = 2;
            foreach (var action in result.Actions)
            {
                userWs.Cell(userRow, 1).Value = action.Stage;
                userWs.Cell(userRow, 2).Value = action.Input ?? "";
                userWs.Cell(userRow, 3).Value = action.ActionType ?? "";
                userRow++;
            }
            userWs.Columns().AdjustToContents();

            // === Client Sheet ===
            var clientWs = wb.Worksheets.Add("Client");
            SetClientSheetHeaders(clientWs);
            int clientRow = 2;
            foreach (var comp in result.ClientComparisons)
            {
                clientWs.Cell(clientRow, 1).Value = comp.Stage;
                clientWs.Cell(clientRow, 2).Value = comp.Expected ?? "";
                clientWs.Cell(clientRow, 6).Value = comp.Passed ? "PASS" : "FAIL";
                clientWs.Cell(clientRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";
                clientWs.Cell(clientRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";
                clientWs.Cell(clientRow, 9).Value = comp.PointsAwarded;
                clientWs.Cell(clientRow, 10).Value = comp.PointsPossible;
                clientWs.Cell(clientRow, 11).Value = comp.DurationMs;
                clientWs.Cell(clientRow, 13).Value = comp.Passed ? "Text comparison passed" : "Text comparison failed";
                clientWs.Cell(clientRow, 19).Value = comp.Actual ?? "";
                clientRow++;
            }
            clientWs.Columns().AdjustToContents();

            // === Server Sheet ===
            var serverWs = wb.Worksheets.Add("Server");
            SetServerSheetHeaders(serverWs);
            int serverRow = 2;
            foreach (var comp in result.ServerComparisons)
            {
                serverWs.Cell(serverRow, 1).Value = comp.Stage;
                serverWs.Cell(serverRow, 2).Value = comp.Expected ?? "";
                serverWs.Cell(serverRow, 6).Value = comp.Passed ? "PASS" : "FAIL";
                serverWs.Cell(serverRow, 7).Value = comp.Passed ? "NONE" : "COMPARE_FAIL";
                serverWs.Cell(serverRow, 8).Value = comp.Passed ? "None" : "OutputMismatch";
                serverWs.Cell(serverRow, 9).Value = comp.PointsAwarded;
                serverWs.Cell(serverRow, 10).Value = comp.PointsPossible;
                serverWs.Cell(serverRow, 11).Value = comp.DurationMs;
                serverWs.Cell(serverRow, 13).Value = comp.Passed ? "Text comparison passed" : "Text comparison failed";
                serverWs.Cell(serverRow, 19).Value = comp.Actual ?? "";
                serverRow++;
            }
            serverWs.Columns().AdjustToContents();

            // === Database Sheet ===
            wb.Worksheets.Add("Database");

            // === Network Sheet ===
            var netWs = wb.Worksheets.Add("Network");
            SetNetworkSheetHeaders(netWs);
            int netRow = 2;
            foreach (var netResult in result.NetworkComparisons)
            {
                netWs.Cell(netRow, 1).Value = netResult.Stage;
                netWs.Cell(netRow, 16).Value = netResult.Passed ? "PASS" : "FAIL";
                netRow++;
            }
            netWs.Columns().AdjustToContents();

            wb.SaveAs(detailPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Sets headers for the User sheet.
        /// </summary>
        private void SetUserSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Input", "Action", "DataType", "Result", "ErrorCode", 
                                   "ErrorCategory", "PointsAwarded", "PointsPossible", "DurationMs", 
                                   "DetailPath", "Message", "DiffIndex", "ExpectedOutput", "ActualOutput", 
                                   "ExpectedExcerpt", "ActualExcerpt" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
                ws.Cell(1, i + 1).Style.Font.Bold = true;
            }
        }

        /// <summary>
        /// Sets headers for the Client sheet.
        /// </summary>
        private void SetClientSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", 
                                   "ErrorCode", "ErrorCategory", "PointsAwarded", "PointsPossible", 
                                   "DurationMs", "DetailPath", "Message", "DiffIndex", "ExpectedOutput", 
                                   "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ClientStdout" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
                ws.Cell(1, i + 1).Style.Font.Bold = true;
            }
        }

        /// <summary>
        /// Sets headers for the Server sheet.
        /// </summary>
        private void SetServerSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Console", "Input", "DataType", "Action", "Result", 
                                   "ErrorCode", "ErrorCategory", "PointsAwarded", "PointsPossible", 
                                   "DurationMs", "DetailPath", "Message", "DiffIndex", "ExpectedOutput", 
                                   "ActualOutput", "ExpectedExcerpt", "ActualExcerpt", "ServerStdout" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
                ws.Cell(1, i + 1).Style.Font.Bold = true;
            }
        }

        /// <summary>
        /// Sets headers for the Network sheet.
        /// </summary>
        private void SetNetworkSheetHeaders(IXLWorksheet ws)
        {
            var headers = new[] { "Stage", "Time", "Info", "Source", "Destination", "Flags", 
                                   "State", "Data", "SourceRole", "DestinationRole", "ActualFlags", 
                                   "ActualState", "ActualSourceRole", "ActualDestRole", "ActualData", "NetworkResult" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
                ws.Cell(1, i + 1).Style.Font.Bold = true;
            }
        }
    }
}
