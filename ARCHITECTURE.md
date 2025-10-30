# Architecture Overview

## Project Restructuring Summary

This document explains how the auto-grading project has been restructured to meet the requirements:
1. **Structure from test-grader**: Lib/ and Application/ folder organization
2. **Functionality from SolutionGrader**: Excel-based grading logic
3. **CLI from current project**: Preserved argument parsing

## Structure Comparison

### Before (Flat Structure)
```
├── SolutionGrader.Cli/
│   └── Program.cs
├── SolutionGrader.Core/
│   ├── Abstractions/
│   ├── Domain/
│   ├── Keywords/
│   └── Services/
└── SolutionGrader.sln
```

### After (test-grader Structure)
```
├── Lib/                           # Reusable libraries (like test-grader)
│   └── SolutionGrader.Core/      # Core grading functionality
│       ├── Abstractions/         # Interfaces
│       ├── Domain/               # Models
│       ├── Keywords/             # Constants
│       └── Services/             # Implementations
├── Application/                   # Executable apps (like test-grader)
│   └── SolutionGrader.Cli/       # CLI interface
└── SolutionGrader.sln            # Updated with folder structure
```

## Functionality Mapping

### From SolutionGrader (WPF)
The following core functionality has been preserved from the SolutionGrader WPF application:

1. **Test Suite Loading**: 
   - `TestSuiteLoader.Load()` - Reads Header.xlsx and identifies test cases
   - Supports QuestionMark sheet for test case marks

2. **Test Case Execution**:
   - `TestCaseLoader.Load()` - Reads Detail.xlsx for each test case
   - Step-by-step execution with actions: SERVERSTART, CLIENTSTART, COMPARE_TEXT, etc.
   - Process management for client/server applications

3. **Grading Logic**:
   - All-or-nothing grading system
   - Points distributed evenly across comparison steps
   - 0 points if any comparison fails

4. **Report Generation**:
   - `GradeDetail.xlsx` - Detailed results per test case
   - `FailedTestDetail.xlsx` - Only generated on failures
   - `OverallSummary.xlsx` - Aggregated results

5. **Middleware Proxy**:
   - HTTP/TCP traffic monitoring
   - Port 5000 proxy to 5001

### From test-grader (Architecture)
The following structural patterns have been adopted:

1. **Lib/ Folder**: Contains reusable libraries
   - Currently: SolutionGrader.Core
   - Future: Can add FileMaster, LogMaster, Domain, etc. as separate libraries

2. **Application/ Folder**: Contains executable applications
   - Currently: SolutionGrader.Cli
   - Future: Can add EnvironmentManager, ReportCollector, etc. as needed

3. **Solution Structure**: 
   - Folder-based organization in .sln file
   - Clear separation of concerns

## CLI Interface

The CLI has been preserved with the exact same interface:

```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite <path> \
  --out <path> \
  [--client <exe>] \
  [--server <exe>] \
  [--client-appsettings <path>] \
  [--server-appsettings <path>] \
  [--db-script <sql>] \
  [--timeout <seconds>]
```

## Key Services

### SuiteRunner
Main orchestrator that:
- Loads test suite
- Iterates through test cases
- Manages environment reset
- Coordinates all services
- Generates reports

### Executor
Executes individual test steps:
- Action routing (SERVERSTART, CLIENTSTART, WAIT, etc.)
- Process management
- Middleware control
- Output comparison

### ExcelDetailLogService
Handles grading and reporting:
- Tracks all step results
- Implements all-or-nothing grading
- Generates Excel reports
- Creates FailedTestDetail.xlsx on failures

### DataComparisonService
Compares actual vs expected outputs:
- Text comparison
- JSON comparison
- CSV comparison
- File comparison

## Benefits of New Structure

1. **Modularity**: Clear separation between libraries and applications
2. **Extensibility**: Easy to add new libraries or applications
3. **Maintainability**: Follows established patterns from test-grader
4. **Functionality**: Preserves all working features from SolutionGrader
5. **CLI Compatibility**: No breaking changes to command-line interface

## Future Enhancements

The structure now supports adding:
- **Lib/Domain**: Shared entities and constants
- **Lib/FileMaster**: File operations (Excel, JSON, XML)
- **Lib/LogMaster**: Centralized logging
- **Lib/ProcessLauncher**: Process management utilities
- **Application/EnvironmentManager**: Environment setup/teardown
- **Application/ReportCollector**: Result aggregation
- **Application/GradingSolution**: Post-processing and scoring
