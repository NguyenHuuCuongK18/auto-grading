using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SolutionGrader.UI.Services;
using SolutionGrader.Core.Services;

namespace SolutionGrader.UI.Test.tests.services
{
    internal class TestKitDiscoveryServiceTests
    {
        private class FakeLogger : ILoggingService
        {
            public List<string> Infos { get; } = new();
            public List<string> Errors { get; } = new();
            public List<string> Warnings { get; } = new();
            public List<string> Debugs { get; } = new();

            public string? CurrentStudent { get; private set; }
            public string? CurrentPaper { get; private set; }

            public event EventHandler<LogEventArgs>? LogAdded;

            public void LogInfo(string message)
            { Infos.Add(message); LogAdded?.Invoke(this, new LogEventArgs { Level = LogLevel.Info, Message = message, Timestamp = DateTime.Now, StudentCode = CurrentStudent }); }
            public void LogError(string message)
            { Errors.Add(message); LogAdded?.Invoke(this, new LogEventArgs { Level = LogLevel.Error, Message = message, Timestamp = DateTime.Now, StudentCode = CurrentStudent }); }
            public void LogWarning(string message)
            { Warnings.Add(message); LogAdded?.Invoke(this, new LogEventArgs { Level = LogLevel.Warning, Message = message, Timestamp = DateTime.Now, StudentCode = CurrentStudent }); }
            public void LogDebug(string message)
            { Debugs.Add(message); LogAdded?.Invoke(this, new LogEventArgs { Level = LogLevel.Debug, Message = message, Timestamp = DateTime.Now, StudentCode = CurrentStudent }); }
            public void LogError(string message, Exception ex)
            { LogError(message + " Exception: " + ex.Message); }

            public void SetStudentContext(string? studentCode)
            { CurrentStudent = studentCode; CurrentPaper = null; }

            public void SetStudentContext(string? studentCode, string? paperNo)
            { CurrentStudent = studentCode; CurrentPaper = paperNo; }

            public string GetAllLogs()
            { return string.Join("\n", Debugs.Concat(Infos).Concat(Warnings).Concat(Errors)); }

            public string GetStudentResultFolder(string studentCode, string? paperNo = null)
            {
                // Return a temp folder path to avoid actual disk writes by logger
                var basePath = Path.Combine(Path.GetTempPath(), "TKDS_LoggerResults");
                var path = paperNo != null ? Path.Combine(basePath, paperNo, "student", studentCode) : Path.Combine(basePath, "student", studentCode);
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private string _rootDir = null!;
        private FakeLogger _logger = null!;
        private TestKitDiscoveryService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "TKDS_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootDir);
            _logger = new FakeLogger();
            _service = new TestKitDiscoveryService(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, true); } catch { }
        }

        // UT01_DiscoverTestKits_ValidHeaderFolders_ReturnsAllKits
        [Test]
        public void UT01_DiscoverTestKits_ValidHeaderFolders_ReturnsAllKits()
        {
            var kit1 = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit1);
            File.WriteAllText(Path.Combine(kit1, "Header.xlsx"), "dummy");
            var kit2 = Path.Combine(_rootDir, "Q2"); Directory.CreateDirectory(kit2);
            File.WriteAllText(Path.Combine(kit2, "Header.xlsx"), "dummy");
            var invalid = Path.Combine(_rootDir, "Q3"); Directory.CreateDirectory(invalid);

            var result = _service.DiscoverTestKits(_rootDir);

            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result.ContainsKey("Q1"), Is.True);
            Assert.That(result.ContainsKey("Q2"), Is.True);
            Assert.That(_logger.Warnings.Any(w => w.Contains("does not contain Header.xlsx")), Is.True);
        }

        // UT02_DiscoverTestKits_MissingFolder_ReturnsEmptyAndLogsError
        [Test]
        public void UT02_DiscoverTestKits_MissingFolder_ReturnsEmptyAndLogsError()
        {
            var missingPath = Path.Combine(_rootDir, "missing");
            var result = _service.DiscoverTestKits(missingPath);
            Assert.That(result, Is.Empty);
            Assert.That(_logger.Errors.Any(e => e.Contains("Test kit folder not found")), Is.True);
        }

        // UT03_GetTestCases_ValidDetailFolders_ReturnsSortedCases
        [Test]
        public void UT03_GetTestCases_ValidDetailFolders_ReturnsSortedCases()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var meta = Path.Combine(kit, "Meta"); Directory.CreateDirectory(meta);
            var c2 = Path.Combine(kit, "Case02"); Directory.CreateDirectory(c2);
            var c1 = Path.Combine(kit, "Case01"); Directory.CreateDirectory(c1);
            var cInvalid = Path.Combine(kit, "CaseNoDetail"); Directory.CreateDirectory(cInvalid);

            File.WriteAllText(Path.Combine(c1, "Detail.xlsx"), "d");
            File.WriteAllText(Path.Combine(c2, "Detail.xlsx"), "d");

            var cases = _service.GetTestCases(kit);

            Assert.That(cases.Count, Is.EqualTo(2));
            Assert.That(Path.GetFileName(cases[0]), Is.EqualTo("Case01"));
            Assert.That(Path.GetFileName(cases[1]), Is.EqualTo("Case02"));
        }

        // UT04_GetTestCases_MissingKitDir_ReturnsEmpty
        [Test]
        public void UT04_GetTestCases_MissingKitDir_ReturnsEmpty()
        {
            var cases = _service.GetTestCases(Path.Combine(_rootDir, "noKit"));
            Assert.That(cases, Is.Empty);
        }

        // UT05_GetEnvironmentPath_EnvFileExists_ReturnsPath
        [Test]
        public void UT05_GetEnvironmentPath_EnvFileExists_ReturnsPath()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var env = Path.Combine(kit, "Environment.xlsx");
            File.WriteAllText(env, "d");

            var path = _service.GetEnvironmentPath(kit);
            Assert.That(path, Is.EqualTo(env));
        }

        // UT06_GetEnvironmentPath_EnvFileMissing_ReturnsNull
        [Test]
        public void UT06_GetEnvironmentPath_EnvFileMissing_ReturnsNull()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var path = _service.GetEnvironmentPath(kit);
            Assert.That(path, Is.Null);
        }

        // UT07_GetHeaderPath_HeaderExists_ReturnsPath
        [Test]
        public void UT07_GetHeaderPath_HeaderExists_ReturnsPath()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var header = Path.Combine(kit, "Header.xlsx");
            File.WriteAllText(header, "d");

            var path = _service.GetHeaderPath(kit);
            Assert.That(path, Is.EqualTo(header));
        }

        // UT08_GetHeaderPath_HeaderMissing_ReturnsNull
        [Test]
        public void UT08_GetHeaderPath_HeaderMissing_ReturnsNull()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var path = _service.GetHeaderPath(kit);
            Assert.That(path, Is.Null);
        }

        // UT09_GetGivenExecutables_ServerAndClientDlls_ReturnsMainDlls
        [Test]
        public void UT09_GetGivenExecutables_ServerAndClientDlls_ReturnsMainDlls()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var given = Path.Combine(kit, "Meta", "Given"); Directory.CreateDirectory(given);
            var server = Path.Combine(given, "Server"); Directory.CreateDirectory(server);
            var client = Path.Combine(given, "Client"); Directory.CreateDirectory(client);

            // Add dependency-like dlls and one main dll
            var sDep = Path.Combine(server, "Microsoft.Dependency.dll"); File.WriteAllText(sDep, "d");
            var sMain = Path.Combine(server, "ServerMain.dll"); File.WriteAllText(sMain, "d");
            var cDep = Path.Combine(client, "Microsoft.Dependency.dll"); File.WriteAllText(cDep, "d");
            var cMain = Path.Combine(client, "ClientMain.dll"); File.WriteAllText(cMain, "d");

            (string? serverPath, string? clientPath) = _service.GetGivenExecutables(kit);
            Assert.That(serverPath, Is.EqualTo(sMain));
            Assert.That(clientPath, Is.EqualTo(cMain));
        }

        // UT10_GetGivenExecutables_GivenFolderMissing_ReturnsNulls
        [Test]
        public void UT10_GetGivenExecutables_GivenFolderMissing_ReturnsNulls()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            (string? serverPath, string? clientPath) = _service.GetGivenExecutables(kit);
            Assert.That(serverPath, Is.Null);
            Assert.That(clientPath, Is.Null);
        }

        // UT11_GetGivenExecutables_NoDlls_ReturnsNulls
        [Test]
        public void UT11_GetGivenExecutables_NoDlls_ReturnsNulls()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var given = Path.Combine(kit, "Meta", "Given"); Directory.CreateDirectory(given);
            Directory.CreateDirectory(Path.Combine(given, "Server"));
            Directory.CreateDirectory(Path.Combine(given, "Client"));

            (string? serverPath, string? clientPath) = _service.GetGivenExecutables(kit);
            Assert.That(serverPath, Is.Null);
            Assert.That(clientPath, Is.Null);
        }

        // UT12_GetTestKitMaxMark_ValidQuestionMarkSheet_ReturnsSum
        [Test]
        public void UT12_GetTestKitMaxMark_ValidQuestionMarkSheet_ReturnsSum()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var header = Path.Combine(kit, "Header.xlsx");

            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.AddWorksheet("QuestionMark");
                ws.Cell(1, 1).Value = "Question"; ws.Cell(1, 2).Value = "Mark";
                ws.Cell(2, 1).Value = "Q1"; ws.Cell(2, 2).Value = 5.5;
                ws.Cell(3, 1).Value = "Q2"; ws.Cell(3, 2).Value = "4.5"; // string parseable
                ws.Cell(4, 1).Value = "Q3"; ws.Cell(4, 2).Value = "abc"; // unparseable -> ignored
                wb.SaveAs(header);
            }

            var total = _service.GetTestKitMaxMark(kit);
            Assert.That(total, Is.EqualTo(10.0).Within(0.0001));
            Assert.That(_logger.Warnings.Any(w => w.Contains("Cannot parse mark value")), Is.True);
        }

        // UT13_GetTestKitMaxMark_HeaderMissing_ReturnsZeroAndWarn
        [Test]
        public void UT13_GetTestKitMaxMark_HeaderMissing_ReturnsZeroAndWarn()
        {
            var kit = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit);
            var total = _service.GetTestKitMaxMark(kit);
            Assert.That(total, Is.EqualTo(0.0));
            Assert.That(_logger.Warnings.Any(w => w.Contains("Header.xlsx not found")), Is.True);
        }

        // UT14_GetTestKitForPaper_ValidMapping_ReturnsKitPath
        [Test]
        public void UT14_GetTestKitForPaper_ValidMapping_ReturnsKitPath()
        {
            // Create kits with names resembling paper numbers mapping
            var kit1 = Path.Combine(_rootDir, "Q1"); Directory.CreateDirectory(kit1);
            File.WriteAllText(Path.Combine(kit1, "Header.xlsx"), "d");
            var kit2 = Path.Combine(_rootDir, "Q2"); Directory.CreateDirectory(kit2);
            File.WriteAllText(Path.Combine(kit2, "Header.xlsx"), "d");

            var path = _service.GetTestKitForPaper(_rootDir, "1");
            Assert.That(path, Is.EqualTo(kit1));

            var path2 = _service.GetTestKitForPaper(_rootDir, "2");
            Assert.That(path2, Is.EqualTo(kit2));
        }

        // UT15_GetTestKitForPaper_MissingOrUnmatched_ReturnsNull
        [Test]
        public void UT15_GetTestKitForPaper_MissingOrUnmatched_ReturnsNull()
        {
            var kitX = Path.Combine(_rootDir, "QX"); Directory.CreateDirectory(kitX);
            File.WriteAllText(Path.Combine(kitX, "Header.xlsx"), "d");

            var path = _service.GetTestKitForPaper(_rootDir, "3");
            Assert.That(path, Is.Null);
        }
    }
}

// Test Types:
// - Normal cases: UT01, UT03, UT05, UT07, UT09, UT12, UT14
// - Abnormal cases: UT02, UT10, UT11, UT13, UT15
// - Edge/Boundary cases: UT04 (missing directory), UT08 (missing header), UT06 (missing env)
