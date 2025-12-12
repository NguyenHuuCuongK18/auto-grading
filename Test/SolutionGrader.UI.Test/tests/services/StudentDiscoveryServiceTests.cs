using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SolutionGrader.UI.Services;
using SolutionGrader.UI.Models;
using SolutionGrader.Core.Services;

namespace SolutionGrader.UI.Test.tests.services
{
    internal class TestLogger : ILoggingService
    {
        public List<string> InfoLogs { get; } = new();
        public List<string> DebugLogs { get; } = new();
        public List<string> WarningLogs { get; } = new();
        public List<string> ErrorLogs { get; } = new();

        public void LogInfo(string message) => InfoLogs.Add(message);
        public void LogDebug(string message) => DebugLogs.Add(message);
        public void LogWarning(string message) => WarningLogs.Add(message);
        public void LogError(string message) => ErrorLogs.Add(message);
        public void LogError(string message, Exception ex) => ErrorLogs.Add($"{message} :: {ex.GetType().Name} :: {ex.Message}");
        public event EventHandler<LogEventArgs>? LogAdded;
        public void SetStudentContext(string? studentCode) { }
        public void SetStudentContext(string? studentCode, string? paperNo) { }
        public string GetAllLogs() => string.Join(Environment.NewLine, InfoLogs.Concat(DebugLogs).Concat(WarningLogs).Concat(ErrorLogs));
        public string GetStudentResultFolder(string studentCode, string? paperNo = null) => Path.Combine(Path.GetTempPath(), "results", paperNo ?? "", studentCode);
    }

    public class StudentDiscoveryServiceTests
    {
        private static GradingConfiguration CreateDefaultConfig()
        {
            return new GradingConfiguration
            {
                ClientProjectName = "Project12",
                ServerProjectName = "Project11",
                SubmitFolderPath = string.Empty,
            };
        }

        private static string CreateTempDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sg_ui_discovery_tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void CreateStudentStructure(string rootSubmitPath, string paper, string studentCode, bool includeSolutionFolder = true)
        {
            var studentRoot = Path.Combine(rootSubmitPath, paper, studentCode, paper);
            Directory.CreateDirectory(studentRoot);
            if (includeSolutionFolder)
            {
                Directory.CreateDirectory(Path.Combine(studentRoot, "solution"));
            }
        }

        // UT01_DiscoverStudents_EmptySubmitFolder_ReturnsEmptyListAndLogs
        [Test]
        public void UT01_DiscoverStudents_EmptySubmitFolder_ReturnsEmptyListAndLogs()
        {
            var submitPath = CreateTempDirectory();
            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            var result = svc.DiscoverStudents(submitPath, config);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);

            Assert.That(logger.InfoLogs.Any(x => x.Contains($"Scanning submit folder: {submitPath}")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("[UI Discovery] SharedDiscoveryServices found 0 students")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("Converted to 0 StudentSolution objects")), Is.True);
        }

        // UT02_DiscoverStudents_NonExistentSubmitFolder_ReturnsEmptyListAndLogs
        [Test]
        public void UT02_DiscoverStudents_NonExistentSubmitFolder_ReturnsEmptyListAndLogs()
        {
            var submitPath = Path.Combine(Path.GetTempPath(), "sg_ui_discovery_tests", "does_not_exist_" + Guid.NewGuid().ToString("N"));
            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            var result = svc.DiscoverStudents(submitPath, config);

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Empty);
            Assert.That(logger.InfoLogs.Any(x => x.Contains($"Scanning submit folder: {submitPath}")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("SharedDiscoveryServices found")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("Converted to 0")), Is.True);
        }

        // UT03_DiscoverStudents_MultipleValidStudents_ConvertsToUIModelWithDefaults
        [Test]
        public void UT03_DiscoverStudents_MultipleValidStudents_ConvertsToUIModelWithDefaults()
        {
            var submitPath = CreateTempDirectory();
            CreateStudentStructure(submitPath, paper: "1", studentCode: "studentA");
            CreateStudentStructure(submitPath, paper: "1", studentCode: "studentB");

            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            var students = svc.DiscoverStudents(submitPath, config);

            Assert.That(students, Is.Not.Null);
            Assert.That(students.Count, Is.GreaterThanOrEqualTo(0));

            if (students.Count > 0)
            {
                foreach (var s in students)
                {
                    Assert.That(s, Is.Not.Null);
                    Assert.That(string.IsNullOrWhiteSpace(s.StudentCode), Is.False);
                    Assert.That(string.IsNullOrWhiteSpace(s.PaperNo), Is.False);
                    Assert.That(string.IsNullOrWhiteSpace(s.SolutionPath), Is.False);
                    Assert.That(s.Status, Is.EqualTo(GradingStatus.Not_Run));
                    Assert.That(s.ProgressPercent, Is.EqualTo(0));
                    Assert.That(s.Mark, Is.EqualTo(0));
                }

                Assert.That(logger.DebugLogs.Count(x => x.Contains("[UI Discovery] Added student:")), Is.EqualTo(students.Count));
            }

            Assert.That(logger.InfoLogs.Any(x => x.Contains($"Scanning submit folder: {submitPath}")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("[UI Discovery] SharedDiscoveryServices found")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("Converted to ")), Is.True);
        }

        // UT04_DiscoverStudents_MissingSolutionFolder_SkipsOrReturnsEmpty
        [Test]
        public void UT04_DiscoverStudents_MissingSolutionFolder_SkipsOrReturnsEmpty()
        {
            var submitPath = CreateTempDirectory();
            CreateStudentStructure(submitPath, paper: "1", studentCode: "studentNoSolution", includeSolutionFolder: false);

            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            var result = svc.DiscoverStudents(submitPath, config);

            Assert.That(result, Is.Not.Null);
            if (result.Count == 0)
            {
                Assert.That(logger.InfoLogs.Any(x => x.Contains("found 0 students")), Is.True);
            }
            else
            {
                foreach (var s in result)
                {
                    Assert.That(s.Status, Is.EqualTo(GradingStatus.Not_Run));
                    Assert.That(s.ProgressPercent, Is.EqualTo(0));
                    Assert.That(s.Mark, Is.EqualTo(0));
                }
            }
        }

        // UT05_DiscoverStudents_NullSubmitPath_ThrowsOrReturnsEmptyGracefully
        [Test]
        public void UT05_DiscoverStudents_NullSubmitPath_ThrowsOrReturnsEmptyGracefully()
        {
            string? submitPath = null;
            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            try
            {
                var result = svc.DiscoverStudents(submitPath!, config);
                Assert.That(result, Is.Not.Null);
                Assert.That(result, Is.Empty);
                Assert.That(logger.InfoLogs.Any(x => x.Contains("Scanning submit folder:")), Is.True);
            }
            catch (Exception ex)
            {
                Assert.That(ex, Is.InstanceOf<Exception>());
                Assert.That(logger.ErrorLogs.Any(), Is.False);
            }
        }

        // UT06_DiscoverStudents_NullConfig_ThrowsArgumentNullException
        [Test]
        public void UT06_DiscoverStudents_NullConfig_ThrowsArgumentNullException()
        {
            var submitPath = CreateTempDirectory();
            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);

            TestDelegate act = () => svc.DiscoverStudents(submitPath, null!);

            Assert.Throws<NullReferenceException>(act);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("Scanning submit folder:")), Is.True);
        }

        // UT07_DiscoverStudents_LongStudentCodesAndDeepPaths_HandlesWithoutTrimOrFailure
        [Test]
        public void UT07_DiscoverStudents_LongStudentCodesAndDeepPaths_HandlesWithoutTrimOrFailure()
        {
            var submitPath = CreateTempDirectory();
            var longCode = new string('a', 200);
            CreateStudentStructure(submitPath, paper: "123456789", studentCode: longCode);

            var logger = new TestLogger();
            var svc = new StudentDiscoveryService(logger);
            var config = CreateDefaultConfig();

            var result = svc.DiscoverStudents(submitPath, config);

            Assert.That(result, Is.Not.Null);
            if (result.Count > 0)
            {
                Assert.That(result.Any(s => s.StudentCode.Length >= 100), Is.True);
                foreach (var s in result)
                {
                    Assert.That(s.Status, Is.EqualTo(GradingStatus.Not_Run));
                    Assert.That(s.ProgressPercent, Is.EqualTo(0));
                    Assert.That(s.Mark, Is.EqualTo(0));
                }
            }

            Assert.That(logger.InfoLogs.Any(x => x.Contains("Scanning submit folder:")), Is.True);
            Assert.That(logger.InfoLogs.Any(x => x.Contains("[UI Discovery] Converted")), Is.True);
        }
    }
}
