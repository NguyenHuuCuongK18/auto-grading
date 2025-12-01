using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GradingServices
{
    /// <summary>
    /// Represents the result of grading a single student submission.
    /// </summary>
    public class StudentGradingResult
    {
        public string StudentCode { get; set; } = string.Empty;
        public string PaperNo { get; set; } = string.Empty;
        public bool Success { get; set; }
        public double TotalPointsAwarded { get; set; }
        public double TotalPointsPossible { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<TestCaseResult> TestCaseResults { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Represents the result of a single test case.
    /// </summary>
    public class TestCaseResult
    {
        public string TestCaseName { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public double PointsAwarded { get; set; }
        public double PointsPossible { get; set; }
        public List<StageResult> StageResults { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Represents the result of a single stage within a test case.
    /// </summary>
    public class StageResult
    {
        public int StageNumber { get; set; }
        public string Action { get; set; } = string.Empty;
        public bool ClientOutputMatches { get; set; }
        public bool ServerOutputMatches { get; set; }
        public bool NetworkMatches { get; set; }
        public string? ExpectedClientOutput { get; set; }
        public string? ActualClientOutput { get; set; }
        public string? ExpectedServerOutput { get; set; }
        public string? ActualServerOutput { get; set; }
        public List<NetworkPacketResult> NetworkPackets { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Represents a captured network packet result.
    /// </summary>
    public class NetworkPacketResult
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string Flags { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? Data { get; set; }
        public string SourceRole { get; set; } = string.Empty;
        public string DestinationRole { get; set; } = string.Empty;
        public bool Matches { get; set; }
    }

    /// <summary>
    /// Configuration for grading a paper.
    /// </summary>
    public class GradingConfiguration
    {
        public string SubmitFolderPath { get; set; } = string.Empty;
        public string TestKitFolderPath { get; set; } = string.Empty;
        public string SaveResultFolderPath { get; set; } = string.Empty;
        
        /// <summary>
        /// If true, student provides the client code. If false, use golden client from testkit.
        /// </summary>
        public bool HasClient { get; set; } = true;
        
        /// <summary>
        /// If true, student provides the server code. If false, use golden server from testkit.
        /// </summary>
        public bool HasServer { get; set; } = true;
        
        /// <summary>
        /// Student's client project folder name (e.g., "Q12").
        /// </summary>
        public string ClientProjectName { get; set; } = "Q12";
        
        /// <summary>
        /// Student's server project folder name (e.g., "Q11").
        /// </summary>
        public string ServerProjectName { get; set; } = "Q11";
        
        /// <summary>
        /// Server port from Environment.xlsx (Code_Container_Host_Port).
        /// </summary>
        public int ServerPort { get; set; } = 8000;
        
        /// <summary>
        /// Maps paper numbers to testkit folder names.
        /// </summary>
        public Dictionary<string, string> PaperToTestKitMapping { get; set; } = new();
        
        /// <summary>
        /// Path to golden client in testkit (e.g., "Meta/Given/Client").
        /// Used when HasClient is false.
        /// </summary>
        public string GoldenClientPath { get; set; } = "Meta/Given/Client";
        
        /// <summary>
        /// Path to golden server in testkit (e.g., "Meta/Given/Server").
        /// Used when HasServer is false.
        /// </summary>
        public string GoldenServerPath { get; set; } = "Meta/Given/Server";
    }

    /// <summary>
    /// Interface for the main grading service.
    /// </summary>
    public interface IGradingService
    {
        /// <summary>
        /// Grades all students in a paper.
        /// </summary>
        Task<List<StudentGradingResult>> GradeAllStudentsAsync(
            string paperNo,
            GradingConfiguration config,
            IProgress<string>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// Grades a single student submission.
        /// </summary>
        Task<StudentGradingResult> GradeStudentAsync(
            string studentCode,
            string paperNo,
            GradingConfiguration config,
            IProgress<string>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// Validates configuration before grading.
        /// </summary>
        (bool IsValid, string? ErrorMessage) ValidateConfiguration(GradingConfiguration config);

        /// <summary>
        /// Gets available students for a paper.
        /// </summary>
        List<string> GetStudentsForPaper(string paperNo, string submitFolderPath);

        /// <summary>
        /// Gets available test cases for a testkit.
        /// </summary>
        List<string> GetTestCasesForTestKit(string testKitPath);
    }

    /// <summary>
    /// Interface for Docker container management during grading.
    /// </summary>
    public interface IDockerContainerService
    {
        /// <summary>
        /// Creates and starts the database container.
        /// </summary>
        Task<bool> StartDatabaseContainerAsync(CancellationToken ct = default);

        /// <summary>
        /// Creates the client container (without starting the application).
        /// </summary>
        Task<bool> CreateClientContainerAsync(string studentCode, string clientPath, CancellationToken ct = default);

        /// <summary>
        /// Creates the server container (without starting the application).
        /// </summary>
        Task<bool> CreateServerContainerAsync(string studentCode, string serverPath, CancellationToken ct = default);

        /// <summary>
        /// Starts the client application inside the container.
        /// </summary>
        Task<bool> StartClientApplicationAsync(CancellationToken ct = default);

        /// <summary>
        /// Starts the server application inside the container.
        /// </summary>
        Task<bool> StartServerApplicationAsync(CancellationToken ct = default);

        /// <summary>
        /// Sends input to the client container via named pipe.
        /// </summary>
        Task<bool> SendClientInputAsync(string input, CancellationToken ct = default);

        /// <summary>
        /// Attaches to the client container console and reads output.
        /// </summary>
        Task<string> GetClientConsoleOutputAsync(CancellationToken ct = default);

        /// <summary>
        /// Attaches to the server container console and reads output.
        /// </summary>
        Task<string> GetServerConsoleOutputAsync(CancellationToken ct = default);

        /// <summary>
        /// Stops and removes the client container.
        /// </summary>
        Task StopClientContainerAsync(CancellationToken ct = default);

        /// <summary>
        /// Stops and removes the server container.
        /// </summary>
        Task StopServerContainerAsync(CancellationToken ct = default);

        /// <summary>
        /// Stops and removes the database container.
        /// </summary>
        Task StopDatabaseContainerAsync(CancellationToken ct = default);

        /// <summary>
        /// Disposes all containers.
        /// </summary>
        Task DisposeAllContainersAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for stage-based console capture.
    /// </summary>
    public interface IStageConsoleService
    {
        /// <summary>
        /// Gets the current stage number.
        /// </summary>
        int CurrentStage { get; }

        /// <summary>
        /// Increments the stage after an action.
        /// </summary>
        void IncrementStage();

        /// <summary>
        /// Captures the current console output and associates it with the current stage.
        /// </summary>
        Task CaptureStageOutputAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets the client console output for a specific stage.
        /// </summary>
        string? GetClientOutputForStage(int stage);

        /// <summary>
        /// Gets the server console output for a specific stage.
        /// </summary>
        string? GetServerOutputForStage(int stage);

        /// <summary>
        /// Clears all captured stage data.
        /// </summary>
        void Clear();
    }
}
