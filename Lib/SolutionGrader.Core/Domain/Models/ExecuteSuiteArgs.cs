namespace SolutionGrader.Core.Domain.Models;

public sealed class ExecuteSuiteArgs
{
    public required string SuitePath { get; init; }   // folder or Header.xlsx
    public required string ResultRoot { get; init; }  // output root

    public string Protocol { get; set; } = "HTTP";    // set from header

    // These may be references (Meta/Given) or overridden with student submission paths in paper mode
    public string? ClientExePath { get; init; }
    public string? ServerExePath { get; init; }

    // Optional: Use inner test case environment.xlsx files (default: false)
    // When true, test case-specific environment.xlsx will override suite-level environment
    // This allows different test cases to use different databases or configurations
    public bool UseInnerTestCaseEnvironment { get; init; } = false;

    // Docker monitoring (single container containing both client & server)
    public bool UseDockerContainers { get; init; } = false;
    public string? CodeContainerName { get; init; } // single container name
    public string? ClientLogPath { get; init; } // path inside container
    public string? ServerLogPath { get; init; } // path inside container
    public string? MiddlewareHost { get; init; } // host/ip for middleware (default localhost)

    // Student-specific overrides for EnvironmentManager (question folder naming & publish path)
    public string? OverrideCodeFilePath { get; init; }
    public string? OverrideStudentQuestionName { get; init; }
    public string? OverrideStudentQuestionPath { get; init; }
}
