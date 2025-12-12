using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using SolutionGrader.UI.Services;

namespace SolutionGrader.UI.Test.tests.services
{
    [TestFixture]
    public class LibGradingServiceTests
    {
        private string _root = null!;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "LibGradingServiceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void Teardown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // UT01_ExecuteDockerGradingAsync_LoggerThrowsOperationCanceled_ReturnsCancelled
        [Test]
        public async Task UT01_ExecuteDockerGradingAsync_LoggerThrowsOperationCanceled_ReturnsCancelled()
        {
            var logger = new helpers.ThrowingLoggerCanceled();
            var svc = new LibGradingService(logger);
            var result = await svc.ExecuteDockerGradingAsync("tk", Path.Combine(_root, "r1"), null, null, "S001");
            Assert.That(result.ErrorMessage, Is.EqualTo("Cancelled"));
            Assert.That(result.StudentCode, Is.EqualTo("S001"));
        }

        // UT02_ExecuteDockerGradingAsync_LoggerThrowsGeneralException_ReturnsErrorMessage
        [Test]
        public async Task UT02_ExecuteDockerGradingAsync_LoggerThrowsGeneralException_ReturnsErrorMessage()
        {
            var logger = new helpers.ThrowingLoggerGeneral("boom");
            var svc = new LibGradingService(logger);
            var result = await svc.ExecuteDockerGradingAsync("tk", Path.Combine(_root, "r2"), null, null, "S002");
            Assert.That(result.ErrorMessage, Does.Contain("boom"));
            Assert.That(result.StudentCode, Is.EqualTo("S002"));
        }

        // UT03_ExecuteDockerGradingAsync_ResultRootInvalidCharacters_ReturnsErrorMessage
        [Test]
        public async Task UT03_ExecuteDockerGradingAsync_ResultRootInvalidCharacters_ReturnsErrorMessage()
        {
            var logger = new helpers.TestLogger();
            var svc = new LibGradingService(logger);
            var invalid = Path.Combine(_root, "inva<lid>:");
            var result = await svc.ExecuteDockerGradingAsync("tk", invalid, null, null, "S003");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(result.StudentCode, Is.EqualTo("S003"));
        }

        // UT04_ExecuteDockerGradingAsync_ResultRootIsExistingFile_ReturnsErrorMessage
        [Test]
        public async Task UT04_ExecuteDockerGradingAsync_ResultRootIsExistingFile_ReturnsErrorMessage()
        {
            var logger = new helpers.TestLogger();
            var svc = new LibGradingService(logger);
            var filePath = Path.Combine(_root, "existing.txt");
            await File.WriteAllTextAsync(filePath, "x");
            var result = await svc.ExecuteDockerGradingAsync("tk", filePath, null, null, "S004");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(result.StudentCode, Is.EqualTo("S004"));
        }

        // UT05_ExecuteDockerGradingAsync_ResultRootPathTooLong_ReturnsErrorMessage
        [Test]
        public async Task UT05_ExecuteDockerGradingAsync_ResultRootPathTooLong_ReturnsErrorMessage()
        {
            var logger = new helpers.TestLogger();
            var svc = new LibGradingService(logger);
            var tooLong = Path.Combine(_root, new string('a', 260));
            var result = await svc.ExecuteDockerGradingAsync("tk", tooLong, null, null, "S005");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
        }

        // UT06_ExecuteDockerGradingAsync_EmptyStudentCode_ReturnsSameStudentCode
        [Test]
        public async Task UT06_ExecuteDockerGradingAsync_EmptyStudentCode_ReturnsSameStudentCode()
        {
            var logger = new helpers.ThrowingLoggerGeneral("early");
            var svc = new LibGradingService(logger);
            var result = await svc.ExecuteDockerGradingAsync("tk", Path.Combine(_root, "r6"), null, null, string.Empty);
            Assert.That(result.StudentCode, Is.EqualTo(string.Empty));
            Assert.That(result.ErrorMessage, Does.Contain("early"));
        }

        // UT07_ExecuteDockerGradingAsync_NullDlls_NoCrashAndErrorHandled
        [Test]
        public async Task UT07_ExecuteDockerGradingAsync_NullDlls_NoCrashAndErrorHandled()
        {
            var logger = new helpers.ThrowingLoggerGeneral("stop");
            var svc = new LibGradingService(logger);
            var result = await svc.ExecuteDockerGradingAsync("tk", Path.Combine(_root, "r7"), null, null, "S007");
            Assert.That(result.StudentCode, Is.EqualTo("S007"));
            Assert.That(result.ErrorMessage, Does.Contain("stop"));
        }

        // UT08_ExecuteDockerGradingAsync_LogsShowContext_BeforeFailure
        [Test]
        public async Task UT08_ExecuteDockerGradingAsync_LogsShowContext_BeforeFailure()
        {
            var logger = new helpers.TestLogger();
            var svc = new LibGradingService(logger);
            var invalid = Path.Combine(_root, "*invalid*");
            var result = await svc.ExecuteDockerGradingAsync("TK1", invalid, null, null, "S008");
            var logs = logger.GetAllLogs();
            Assert.That(logs, Does.Contain("Test kit: TK1"));
            Assert.That(logs, Does.Contain("Student: S008"));
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
        }

        // UT09_ExecuteDockerGradingAsync_ResultFolderCreated_WhenValid
        [Test]
        public async Task UT09_ExecuteDockerGradingAsync_ResultFolderCreated_WhenValid()
        {
            var logger = new helpers.ThrowingLoggerGeneral("after-create");
            var svc = new LibGradingService(logger);
            var resultRoot = Path.Combine(_root, "r9");
            var _ = await svc.ExecuteDockerGradingAsync("tk", resultRoot, null, null, "S009");
            Assert.That(Directory.Exists(resultRoot), Is.True);
        }
    }
}
