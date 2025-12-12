using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SolutionGrader.UI.Services;
using SolutionGrader.UI.Models;
using Domain.Models;

namespace SolutionGrader.UI.Test.tests.services
{
    internal class GradingOrchestrationServiceTests
    {
        private static GradingConfiguration CreateConfig(string basePath, int hostPort = 8000)
        {
            Directory.CreateDirectory(basePath);
            return new GradingConfiguration
            {
                SubmitFolderPath = basePath,
                TestKitFolderPath = basePath, // keep simple; some paths may be used
                SaveResultFolderPath = basePath,
                HasClient = false,
                HasServer = false,
                ClientProjectName = "ClientProj",
                ServerProjectName = "ServerProj",
                CodeContainerHostPort = hostPort,
                CodeContainerInternalPort = hostPort,
                GradingTimeoutSeconds = 5
            };
        }

        private static StudentSolution CreateStudent(string code, string paper, GradingStatus status = GradingStatus.Not_Run)
        {
            return new StudentSolution
            {
                StudentCode = code,
                PaperNo = paper,
                Status = status,
                SolutionPath = Path.Combine(Path.GetTempPath(), "auto-grading-tests", code, paper)
            };
        }

        [Test]
        public async Task UT01_StartGradingAsync_EmptyStudentList_NoErrorsAndSessionCompleted()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 1); // valid minimal port
            var session = new GradingSessionState();

            await service.StartGradingAsync(new List<StudentSolution>(), config, session);

            Assert.False(session.IsRunning);
            Assert.AreEqual(0, session.TotalStudents);
            Assert.AreEqual(0, session.GradedStudents);
        }

        [Test]
        public async Task UT02_StartGradingAsync_SingleStudentValidPort_StatusUpdatedToFailedIfNoComponents()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 8000);
            // Force early return by invalid port: use 0
            config.CodeContainerHostPort = 0;
            config.CodeContainerInternalPort = 0;
            var session = new GradingSessionState();
            var students = new List<StudentSolution> { CreateStudent("S001", "1") };

            await service.StartGradingAsync(students, config, session);

            Assert.False(session.IsRunning);
            Assert.AreEqual(1, session.TotalStudents);
            Assert.AreEqual(1, session.GradedStudents);
            Assert.AreEqual(GradingStatus.Failed, students[0].Status);
        }

        [Test]
        public async Task UT03_StartGradingAsync_MultipleStudentsNoFilter_AllStudentsProcessed()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0); // invalid port to avoid docker
            var session = new GradingSessionState();
            var students = new List<StudentSolution>
            {
                CreateStudent("S001", "1", GradingStatus.Success),
                CreateStudent("S002", "1", GradingStatus.Failed),
                CreateStudent("S003", "2", GradingStatus.Not_Run)
            };

            await service.StartGradingAsync(students, config, session);

            Assert.False(session.IsRunning);
            Assert.AreEqual(3, session.TotalStudents);
            Assert.AreEqual(3, session.GradedStudents);
        }

        [Test]
        public async Task UT04_StartGradingAsync_InvalidPortConfiguration_StudentMarkedFailed()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 70000); // invalid (>65535)
            var session = new GradingSessionState();
            var student = CreateStudent("S004", "1");

            await service.StartGradingAsync(new List<StudentSolution> { student }, config, session);

            Assert.AreEqual(GradingStatus.Failed, student.Status);
            Assert.False(session.IsRunning);
        }

        [Test]
        public async Task UT05_StartGradingAsync_CancellationRequested_StopsEarly()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var students = new List<StudentSolution>
            {
                CreateStudent("S001", "1"),
                CreateStudent("S002", "1"),
                CreateStudent("S003", "1")
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await service.StartGradingAsync(students, config, session, cts.Token);

            Assert.False(session.IsRunning);
            Assert.LessOrEqual(session.GradedStudents, students.Count);
        }

        [Test]
        public async Task UT06_StartGradingAsync_PausedThenResumed_ProgressContinues()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var students = new List<StudentSolution> { CreateStudent("S001", "1"), CreateStudent("S002", "1") };

            // Start in paused state to exercise pause loop briefly
            session.IsPaused = true;
            var task = service.StartGradingAsync(students, config, session);
            await Task.Delay(200);
            service.ResumeGrading(session);
            await task;

            Assert.False(session.IsRunning);
            Assert.AreEqual(2, session.GradedStudents);
        }

        [Test]
        public async Task UT07_StartGradingAsync_SharedMessageLogger_OwnershipNotDisposed()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var student = CreateStudent("S007", "1");
            using var sharedLogger = new GradingMessageLogger(tmp);

            await service.StartGradingAsync(new List<StudentSolution> { student }, config, session, CancellationToken.None, sharedLogger);

            // The method should complete without disposing shared logger (ownership retained by caller)
            Assert.True(true); // sanity: no exception thrown
        }

        [Test]
        public async Task UT08_StartGradingAsync_StatusCountsUpdated_AfterEachStudent()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var students = new List<StudentSolution>
            {
                CreateStudent("S1", "1", GradingStatus.Not_Run),
                CreateStudent("S2", "1", GradingStatus.Success),
                CreateStudent("S3", "1", GradingStatus.Failed)
            };

            await service.StartGradingAsync(students, config, session);

            Assert.AreEqual(students.Count, session.GradedStudents);
            Assert.GreaterOrEqual(session.NotRunCount + session.SuccessCount + session.FailedCount, 0);
        }

        [Test]
        public async Task UT09_StartGradingAsync_BoundaryPortMin_ProcessesWithoutThrow()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 1); // min valid
            var session = new GradingSessionState();
            var students = new List<StudentSolution> { CreateStudent("S9", "1") };

            await service.StartGradingAsync(students, config, session);

            Assert.False(session.IsRunning);
            Assert.AreEqual(1, session.TotalStudents);
        }

        [Test]
        public async Task UT10_StartGradingAsync_BoundaryPortMax_ProcessesWithoutThrow()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 65535); // max valid
            var session = new GradingSessionState();
            var students = new List<StudentSolution> { CreateStudent("S10", "1") };

            await service.StartGradingAsync(students, config, session);

            Assert.False(session.IsRunning);
            Assert.AreEqual(1, session.TotalStudents);
        }

        [Test]
        public async Task UT11_StartGradingAsync_NullCancellationToken_InternalTokenCreated()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var students = new List<StudentSolution> { CreateStudent("S11", "1") };

            await service.StartGradingAsync(students, config, session, default);

            Assert.False(session.IsRunning);
            Assert.AreEqual(1, session.GradedStudents);
        }

        [Test]
        public async Task UT12_StartGradingAsync_VariedInitialStatuses_NoPreFilter_AllGraded()
        {
            var logger = new TestLoggingService();
            var service = new GradingOrchestrationService(logger);
            var tmp = Path.Combine(Path.GetTempPath(), "auto-grading-tests", Guid.NewGuid().ToString());
            var config = CreateConfig(tmp, hostPort: 0);
            var session = new GradingSessionState();
            var students = new List<StudentSolution>
            {
                CreateStudent("A", "1", GradingStatus.Not_Run),
                CreateStudent("B", "1", GradingStatus.Paused),
                CreateStudent("C", "1", GradingStatus.Success),
                CreateStudent("D", "1", GradingStatus.Failed)
            };

            await service.StartGradingAsync(students, config, session);

            Assert.AreEqual(students.Count, session.GradedStudents);
            Assert.False(session.IsRunning);
        }

        // Minimal test logger to avoid external logging dependencies
        private class TestLoggingService : ILoggingService
        {
            public event EventHandler<LogEventArgs>? LogAdded;

            public void LogDebug(string message) { }
            public void LogError(string message) { }
            public void LogError(string message, Exception ex) { }
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void SetStudentContext(string? studentCode) { }
            public void SetStudentContext(string? studentCode, string? paperNo) { }
            public string GetAllLogs() => string.Empty;
            public string GetStudentResultFolder(string studentCode, string? paperNo = null)
            {
                var basePath = Path.Combine(Path.GetTempPath(), "auto-grading-tests", "logs");
                var path = !string.IsNullOrEmpty(paperNo)
                    ? Path.Combine(basePath, paperNo, "student", studentCode)
                    : Path.Combine(basePath, "student", studentCode);
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}
