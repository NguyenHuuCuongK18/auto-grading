using System;
using System.IO;
using NUnit.Framework;
using SolutionGrader.UI.Services;
using SolutionGrader.UI.Models;

namespace SolutionGrader.UI.Test.services
{
    internal class StudentDiscoveryServiceTests
    {
        private string _resourceRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "resources", "dummy_dll");

        private class TestLogger : ILoggingService
        {
            public event EventHandler<LogEventArgs>? LogAdded;
            public void LogInfo(string message) { }
            public void LogDebug(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) { }
            public void LogError(string message, Exception ex) { }
            public void SetStudentContext(string? studentCode) { }
            public void SetStudentContext(string? studentCode, string? paperNo) { }
            public string GetAllLogs() => string.Empty;
            public string GetStudentResultFolder(string studentCode, string? paperNo = null) => string.Empty;
        }

        [SetUp]
        public void Setup()
        {
            Assert.That(Directory.Exists(_resourceRoot), "Dummy DLL resource folder not found: " + _resourceRoot);
        }

        [Test]
        public void UT01_FindDllPath_ExactMatchDllExists_ReturnsPath()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var targetName = "Project11";
                var targetDll = Path.Combine(_resourceRoot, "Project11.dll");
                var dest = Path.Combine(tempDir, "bin", targetName + ".dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(targetDll, dest, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, targetName);

                Assert.That(result, Is.Not.Null);
                Assert.That(Path.GetFileName(result!), Is.EqualTo(targetName + ".dll"));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT02_FindDllPath_CaseInsensitiveName_ReturnsPath()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var targetName = "project11"; // lower case on purpose
                var targetDll = Path.Combine(_resourceRoot, "Project11.dll");
                var dest = Path.Combine(tempDir, "out", "Release", "Project11.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(targetDll, dest, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, targetName);

                Assert.That(result, Is.Not.Null);
                Assert.That(result, Is.EqualTo(dest));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT03_FindDllPath_MultipleCandidates_PrefersPublishThenReleaseThenDebug()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var targetName = "Project11";
                var src = Path.Combine(_resourceRoot, "Project11.dll");

                var pathPublish = Path.Combine(tempDir, "Q11", "publish", targetName + ".dll");
                var pathRelease = Path.Combine(tempDir, "Q11", "bin", "Release", targetName + ".dll");
                var pathDebug = Path.Combine(tempDir, "Q11", "bin", "Debug", targetName + ".dll");
                foreach (var p in new[] { pathPublish, pathRelease, pathDebug })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    File.Copy(src, p, true);
                }

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, targetName);

                Assert.That(result, Is.EqualTo(pathPublish));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT04_FindDllPath_OnlySystemDlls_Excluded_ReturnsNull()
        {
            var tempDir = CreateTempSolution();
            try
            {
                // put only Microsoft/System dlls matching name pattern but should be excluded
                var msDll = Path.Combine(_resourceRoot, "Microsoft.Extensions.Configuration.dll");
                var sysDll = Path.Combine(_resourceRoot, "Microsoft.Extensions.Primitives.dll");

                var d1 = Path.Combine(tempDir, "lib", "Microsoft.Extensions.Configuration.dll");
                var d2 = Path.Combine(tempDir, "lib", "System.Fake.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(d1)!);
                File.Copy(msDll, d1, true);
                File.WriteAllBytes(d2, new byte[] { 0x00 });

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, "Project11");

                Assert.That(result, Is.Null);
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT05_FindDllPath_NonExistingSolutionPath_ReturnsNull()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var nonExisting = Path.Combine(Path.GetTempPath(), "NotExist_" + Guid.NewGuid());
            var result = svc.FindDllPath(nonExisting, "Project11");
            Assert.That(result, Is.Null);
        }

        [Test]
        public void UT06_FindDllPath_EmptyProjectName_ReturnsNull()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, string.Empty);
                Assert.That(result, Is.Null);
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT07_FindDllPath_ProjectNameWithSpaces_ReturnsPath()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var src = Path.Combine(_resourceRoot, "Project11.dll");
                var dest = Path.Combine(tempDir, "bin", "Project11.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, "Project 11"); // space in name
                Assert.That(result, Is.EqualTo(dest));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT08_FindDllPath_OnlyRuntimesCandidate_StillReturnsPathDueToFallback()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var src = Path.Combine(_resourceRoot, "Project11.dll");
                var dest = Path.Combine(tempDir, "runtimes", "win", "lib", "net8.0", "Project11.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, "Project11");
                // Because filteredDlls would be empty, method falls back to original list and returns this path
                Assert.That(result, Is.EqualTo(dest));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        [Test]
        public void UT09_FindDllPath_MultipleNonPreferredShorterPath_ReturnsShorterPath()
        {
            var tempDir = CreateTempSolution();
            try
            {
                var target = "Project11";
                var src = Path.Combine(_resourceRoot, "Project11.dll");
                // Two candidates without publish/Release/Debug keywords
                var longPath = Path.Combine(tempDir, "a", "very", "long", "nested", "folder", target + ".dll");
                var shortPath = Path.Combine(tempDir, "x", target + ".dll");
                Directory.CreateDirectory(Path.GetDirectoryName(longPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(shortPath)!);
                File.Copy(src, longPath, true);
                File.Copy(src, shortPath, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var result = svc.FindDllPath(tempDir, target);
                Assert.That(result, Is.EqualTo(shortPath));
            }
            finally
            {
                SafeDelete(tempDir);
            }
        }

        private static string CreateTempSolution()
        {
            var dir = Path.Combine(Path.GetTempPath(), "FindDllPathTests_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void SafeDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        [Test]
        public void UT10_GetDockerPath_RelativeUnixPathReturned()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution");
            var dllPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution", "bin", "Release", "Project11.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var dockerPath = svc.GetDockerPath(solutionPath, dllPath);
            Assert.That(dockerPath.StartsWith("/apps/"));
            Assert.That(dockerPath.Contains("bin/Release/Project11.dll"));
        }

        [Test]
        public void UT11_GetDockerPath_NullDllPath_ReturnsEmptyString()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution");
            var dockerPath = svc.GetDockerPath(solutionPath, null);
            Assert.That(dockerPath, Is.EqualTo(string.Empty));
        }

        [Test]
        public void UT12_GetDockerPath_EmptyDllPath_ReturnsEmptyString()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution");
            var dockerPath = svc.GetDockerPath(solutionPath, string.Empty);
            Assert.That(dockerPath, Is.EqualTo(string.Empty));
        }

        [Test]
        public void UT13_GetDockerPath_SolutionPathWithoutDirectory_ThrowsArgumentNullException()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionPath = "Solution.csproj"; // Path.GetDirectoryName returns null
            var dllPath = Path.Combine(Path.GetTempPath(), "bin", "Project11.dll");

            Assert.Throws<ArgumentException>(() => svc.GetDockerPath(solutionPath, dllPath));
        }

        [Test]
        public void UT14_GetDockerPath_CustomContainerRoot_AppliedToOutput()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution");
            var dllPath = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution", "bin", "Debug", "Project11.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

            var dockerPath = svc.GetDockerPath(solutionPath, dllPath, "/custom");
            Assert.That(dockerPath.StartsWith("/custom/"));
            Assert.That(dockerPath.Contains("bin/Debug/Project11.dll"));
        }

        [Test]
        public void UT15_GetDockerPath_DllPathEqualsSolutionDirectory_ReturnsContainerRootSlash()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var solutionDir = Path.Combine(Path.GetTempPath(), "solution", "Q11", "solution");
            Directory.CreateDirectory(solutionDir);
            // dllPath equals the folder itself; relative path becomes '.'
            var dockerPath = svc.GetDockerPath(solutionDir, solutionDir);
            // Path.GetRelativePath returns '.'; ensuring conversion keeps it reasonable
            Assert.That(dockerPath, Is.EqualTo("/apps/solution"));
        }

        // ===================== DiscoverStudents tests =====================

        [Test]
        public void UT16_DiscoverStudents_SubmitFolderMissing_ReturnsEmpty()
        {
            var svc = new StudentDiscoveryService(new TestLogger());
            var missing = Path.Combine(Path.GetTempPath(), "SubmitMissing_" + Guid.NewGuid());
            var config = new GradingConfiguration
            {
                HasClient = true,
                HasServer = true,
                ClientProjectName = "Project11",
                ServerProjectName = "Project11"
            };

            var students = svc.DiscoverStudents(missing, config);
            Assert.That(students, Is.Empty);
        }

        [Test]
        public void UT17_DiscoverStudents_SingleStudentWithSolutionAndDlls_FindsStudentAndDlls()
        {
            var submit = CreateTempSolution();
            try
            {
                // Submit/1/studentA/1/solution
                var paperDir = Path.Combine(submit, "1");
                var studentDir = Path.Combine(paperDir, "studentA");
                var questionDir = Path.Combine(studentDir, "1");
                var solutionDir = Path.Combine(questionDir, "solution");
                Directory.CreateDirectory(solutionDir);

                // place dummy dlls
                var dll = Path.Combine(_resourceRoot, "Project11.dll");
                var clientOut = Path.Combine(solutionDir, "bin", "Release", "Project11.dll");
                var serverOut = Path.Combine(solutionDir, "publish", "Project11.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(clientOut)!);
                Directory.CreateDirectory(Path.GetDirectoryName(serverOut)!);
                File.Copy(dll, clientOut, true);
                File.Copy(dll, serverOut, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var config = new GradingConfiguration
                {
                    HasClient = true,
                    HasServer = true,
                    ClientProjectName = "Project11",
                    ServerProjectName = "Project11"
                };

                var students = svc.DiscoverStudents(submit, config);
                Assert.That(students.Count, Is.EqualTo(1));
                var s = students[0];
                Assert.That(s.StudentCode, Is.EqualTo("studentA"));
                Assert.That(s.PaperNo, Is.EqualTo("1"));
                Assert.That(s.ClientDllPath, Is.Not.Null);
                Assert.That(s.ServerDllPath, Is.Not.Null);
            }
            finally
            {
                SafeDelete(submit);
            }
        }

        [Test]
        public void UT18_DiscoverStudents_SkipEntriesWithDotInName_IgnoresSystemFileFolders()
        {
            var submit = CreateTempSolution();
            try
            {
                var paperDir = Path.Combine(submit, "1");
                var studentDirValid = Path.Combine(paperDir, "studentA");
                var studentDirInvalid = Path.Combine(paperDir, "Sinh_vien.txt");
                var q1Valid = Path.Combine(studentDirValid, "1");
                var solValid = Path.Combine(q1Valid, "solution");
                Directory.CreateDirectory(solValid);
                Directory.CreateDirectory(studentDirInvalid); // folder with dot in name

                // put dll in valid student
                var dll = Path.Combine(_resourceRoot, "Project11.dll");
                var outDll = Path.Combine(solValid, "publish", "Project11.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(outDll)!);
                File.Copy(dll, outDll, true);

                var svc = new StudentDiscoveryService(new TestLogger());
                var config = new GradingConfiguration { HasClient = true, ClientProjectName = "Project11" };
                var students = svc.DiscoverStudents(submit, config);

                Assert.That(students.Count, Is.EqualTo(1));
                Assert.That(students[0].StudentCode, Is.EqualTo("studentA"));
            }
            finally
            {
                SafeDelete(submit);
            }
        }

        [Test]
        public void UT19_DiscoverStudents_QuestionFolderMissing_ReturnsEmpty()
        {
            var submit = CreateTempSolution();
            try
            {
                var paperDir = Path.Combine(submit, "1");
                var studentDir = Path.Combine(paperDir, "studentA");
                Directory.CreateDirectory(studentDir);

                var svc = new StudentDiscoveryService(new TestLogger());
                var config = new GradingConfiguration { HasClient = true, ClientProjectName = "Project11" };
                var students = svc.DiscoverStudents(submit, config);
                Assert.That(students, Is.Empty);
            }
            finally
            {
                SafeDelete(submit);
            }
        }

        [Test]
        public void UT20_DiscoverStudents_SolutionMissingZipPresent_ExtractsAndFindsDll()
        {
            var submit = CreateTempSolution();
            try
            {
                var paperDir = Path.Combine(submit, "1");
                var studentDir = Path.Combine(paperDir, "studentA");
                var q1Dir = Path.Combine(studentDir, "1");
                Directory.CreateDirectory(q1Dir);

                // Create a zip file containing a solution folder with dll
                var tempSolution = Path.Combine(Path.GetTempPath(), "zipsrc_" + Guid.NewGuid());
                var sol = Path.Combine(tempSolution, "solution");
                var publishDir = Path.Combine(sol, "publish");
                Directory.CreateDirectory(publishDir);
                File.Copy(Path.Combine(_resourceRoot, "Project11.dll"), Path.Combine(publishDir, "Project11.dll"), true);

                var zipPath = Path.Combine(q1Dir, "solution.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(tempSolution, zipPath);
                SafeDelete(tempSolution);

                var svc = new StudentDiscoveryService(new TestLogger());
                var config = new GradingConfiguration { HasClient = true, ClientProjectName = "Project11" };
                var students = svc.DiscoverStudents(submit, config);

                Assert.That(students.Count, Is.EqualTo(1));
                Assert.That(students[0].ClientDllPath, Is.Not.Null);
            }
            finally
            {
                SafeDelete(submit);
            }
        }
    }
}
