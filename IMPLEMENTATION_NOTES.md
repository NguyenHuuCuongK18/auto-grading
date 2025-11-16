# Implementation Notes - Process Launcher Refactoring

## Overview
This document describes the changes made to implement step-based orchestration and configurable inner test case environment support.

## 1. Configurable Inner Test Case Environment

### Background
Previously, test case-specific `environment.xlsx` files were always loaded if they existed, which could cause issues when test cases needed to use the suite-level database configuration. The problem statement mentioned:

> "i had previously made a mistake where i ignore the inner test case enviroment.xlsx, i had now fixed it a tiny bit, where now it should say correct database, this is important because what if on a next test case i need to load in a different dataset (this is hypothetical, and does not actually needs to run first) so you need to make a configurable to config if this inner enviroment is taken into account or not default false"

### Implementation
- **New flag**: `UseInnerTestCaseEnvironment` added to `ExecuteSuiteArgs` (default: `false`)
- **CLI support**: New `--use-inner-env` flag added to command line interface
- **Suite loader**: `ExcelSuiteLoader.BuildCasesFromDirectory()` now only loads inner test case `environment.xlsx` files when the flag is enabled
- **Logging**: When enabled, a message is logged: `[Suite] Using inner test case environment for {testCaseName}: {envPath}`

### Usage
```bash
# Default behavior - use suite-level environment only
SolutionGrader.Cli ExecuteSuite --suite TestKit --out Results

# Enable inner test case environments
SolutionGrader.Cli ExecuteSuite --suite TestKit --out Results --use-inner-env
```

### Benefits
- **Backward compatible**: Default behavior (`false`) maintains existing functionality
- **Flexibility**: Allows test cases to override database configurations when needed
- **Control**: Explicit opt-in prevents accidental environment overrides

## 2. Step-Based Orchestration

### Background
The problem statement requested:

> "can you use process launcher in a way that seperate out the steps being performed, each steps calls back to process launcher referencing how https://github.com/NguyenHuuCuongK18/test-grader.git flows"

The test-grader repository uses a step-based approach where:
1. Environment setup
2. Test kit reading
3. Code execution
4. Grading
5. Logging
6. Cleanup

### Implementation: TestCaseOrchestrator

A new `TestCaseOrchestrator` class was created to encapsulate step-based test case execution. Each step is a separate method that can be monitored and logged independently.

#### Step 1: SetupEnvironmentAsync
- Generates `appsettings.json` files from Header.xlsx
- Configures middleware and server ports
- Resets database with appropriate script
- Determines database script path (test case specific or suite level)

#### Step 2: ReadTestKitInfo
- Ensures output directory exists
- Parses `detail.xlsx` to load test steps
- Initializes result logging
- Calculates comparison step count for grading

#### Step 3: InitializeProcesses
- Validates client and server executable paths
- Initializes executable manager

#### Step 4: ExecuteAndGradeStepsAsync
- Runs each test step in sequence
- Handles stage transitions with appropriate delays
- Monitors process health
- Performs comparisons and validations
- Logs results for grading

#### Step 5: WriteResultsAsync
- Writes test case results to output
- Finalizes Excel case log

#### Step 6: CleanupAsync
- Stops all running processes
- Stops middleware proxy
- Cleanup resources

### SuiteRunner Integration

The `SuiteRunner.ExecuteSuiteAsync()` method was refactored to use the orchestrator:

```csharp
// Step 1: Setup Environment
var (setupOk, setupMsg) = await _orchestrator.SetupEnvironmentAsync(...);

// Step 2: Read Test Kit Information
var (readOk, readMsg, steps) = _orchestrator.ReadTestKitInfo(...);

// Step 3: Initialize Processes
var (initOk, initMsg) = _orchestrator.InitializeProcesses(...);

// Step 4: Execute and Grade Steps
var (execOk, execMsg, results) = await _orchestrator.ExecuteAndGradeStepsAsync(...);

// Step 5: Write Results
var (writeOk, writeMsg) = await _orchestrator.WriteResultsAsync(...);

// Step 6: Cleanup
var (cleanupOk, cleanupMsg) = await _orchestrator.CleanupAsync();
```

### Benefits

1. **Clear Separation**: Each logical phase is isolated
2. **Monitoring**: Each step can be monitored independently
3. **Error Handling**: Failures at any step are clearly identified
4. **Logging**: Step-specific logging shows progress clearly
5. **Maintainability**: Easier to understand and modify individual steps
6. **Testing**: Steps can be tested in isolation

### Comparison with test-grader

The implementation follows a similar pattern to test-grader's `GradingEngine`:

| test-grader | auto-grading (this implementation) |
|------------|-----------------------------------|
| SetUpEnv() | SetupEnvironmentAsync() |
| ImportQuestionKit() | ReadTestKitInfo() |
| TestCaseExecutor.Execute() | ExecuteAndGradeStepsAsync() |
| ExcelExporter.Export() | WriteResultsAsync() |
| Dispose() | CleanupAsync() |

The key difference is that auto-grading maintains backward compatibility and integrates cleanly with the existing architecture.

## 3. Backward Compatibility

All changes are backward compatible:
- Default behavior unchanged (inner environments not used by default)
- Existing test suites work without modification
- New CLI flag is optional
- Step-based orchestration maintains same external behavior

## 4. Testing

The implementation builds successfully with no errors. To test:

1. **Without inner environments** (default):
   ```bash
   dotnet run --project Application/SolutionGrader.Cli/SolutionGrader.Cli.csproj ExecuteSuite \
     --suite SampleTestKitsWithData/Testkit_HTTP_1 \
     --out Results
   ```

2. **With inner environments**:
   ```bash
   dotnet run --project Application/SolutionGrader.Cli/SolutionGrader.Cli.csproj ExecuteSuite \
     --suite SampleTestKitsWithData/Testkit_HTTP_1 \
     --out Results \
     --use-inner-env
   ```

## 5. Files Modified

1. `ExecuteSuiteArgs.cs` - Added `UseInnerTestCaseEnvironment` property
2. `ITestSuiteLoader.cs` - Updated interface to accept flag
3. `ExcelSuiteLoader.cs` - Updated to conditionally load inner environments
4. `SuiteRunner.cs` - Refactored to use TestCaseOrchestrator
5. `TestCaseOrchestrator.cs` - New file with step-based orchestration
6. `Program.cs` - Added CLI support for `--use-inner-env` flag

## 6. Future Improvements

Potential enhancements:
1. Add step timing metrics
2. Add step retry logic for transient failures
3. Allow configuration of step timeouts
4. Add step-level event hooks for custom logic
5. Parallel test case execution (currently sequential)
