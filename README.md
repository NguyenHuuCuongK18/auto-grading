# SolutionGrader CLI

A command-line auto-grading tool for testing client-server applications with Excel-based test suites.

## Overview

This is a CLI version of the SolutionGrader tool that allows you to run automated tests on client-server applications using test suites defined in Excel files. The tool:

- Manages server and client processes
- Proxies HTTP/TCP traffic for monitoring
- Validates outputs against expected results
- Generates detailed test reports

## Project Structure

The project follows a library-based architecture inspired by test-grader:

```
├── Lib/                           # Reusable libraries
│   └── SolutionGrader.Core/      # Core grading functionality
├── Application/                   # Executable applications
│   └── SolutionGrader.Cli/       # Command-line interface
└── SampleTestKit/                # Sample test suite
```

## Usage

```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite <suiteFolder|Header.xlsx> \
  --out <resultRoot> \
  [--client <client.exe>] \
  [--server <server.exe>] \
  [--client-appsettings <path>] \
  [--server-appsettings <path>] \
  [--db-script <sql>] \
  [--timeout <sec>]
```

### Parameters

- `--suite`: Path to test suite folder or Header.xlsx file (required)
- `--out`: Output directory for test results (required)
- `--client`: Path to client executable
- `--server`: Path to server executable
- `--client-appsettings`: Path to client appsettings.json template
- `--server-appsettings`: Path to server appsettings.json template
- `--db-script`: Path to database reset script
- `--timeout`: Timeout in seconds for each test step (default: 10)

### Example

```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "C:\Tests\TestSuite" \
  --out "C:\Results" \
  --client "C:\Apps\Client\Client.exe" \
  --server "C:\Apps\Server\Server.exe" \
  --client-appsettings "C:\Config\client-appsettings.json" \
  --server-appsettings "C:\Config\server-appsettings.json" \
  --db-script "C:\SQL\reset.sql" \
  --timeout 15
```

## How It Works

1. **Test Suite Loading**: Reads test cases from Excel files in the suite folder
2. **Environment Setup**: Replaces appsettings files and resets database if needed
3. **Process Management**: Starts server and client applications as needed
4. **Middleware Proxy**: Creates an HTTP/TCP proxy on port 5000 that forwards to the real server on port 5001
5. **Test Execution**: Runs test steps defined in the Excel files
6. **Result Validation**: Compares actual outputs with expected results
7. **Report Generation**: Creates detailed test result reports in the output folder

## Recent Improvements

### Grading Logic Update (v2.0)

The grading system has been significantly improved to meet academic grading requirements:

1. **All-or-Nothing Grading**: Points are now awarded only if ALL test steps in a test case pass. If any step fails, no points are awarded for that test case.
2. **Global Header Marks**: Test case marks are now read from the global `Header.xlsx` file's `QuestionMark` sheet, supporting arbitrary double values (e.g., 1.0, 2.5, 10.0).
3. **Excel-Only Output**: Removed CSV output files. All results are now saved in Excel format for better readability and formatting.
4. **Failed Test Detail Report**: Added `FailedTestDetail.xlsx` file that provides detailed information about failed test steps, including:
   - Sheet name where the failure occurred
   - Stage number
   - Result status
   - Detailed error message
   - Path to diff files (for mismatch details)
5. **Improved Cell Formatting**: Excel cells now properly wrap text and auto-fit content, with specific width settings for long columns like Message, DetailPath, and Output.

#### Output Files

For each test case, the following files are generated:
- `GradeDetail.xlsx` - Detailed step-by-step results with pass/fail status and points
- `FailedTestDetail.xlsx` - Only created if there are failures; shows detailed failure information
- `<TestCase>_Result.xlsx` - Summary of step execution results

Overall summary:
- `OverallSummary.xlsx` - Aggregated results across all test cases with total points

### Fixed Hanging Issue (v1.1)

The CLI version previously hung during execution. The following fixes were implemented:

1. **Improved Shutdown**: Middleware now properly waits for background tasks to complete during shutdown
2. **Port Availability Check**: Server readiness is now verified by checking if the port is actually listening, not just if the process exists
3. **Timeout Protection**: Added timeouts to prevent indefinite waits on slow or unresponsive servers
4. **Enhanced Logging**: Added comprehensive console output to track execution progress

### Console Output

The tool now provides detailed console output showing:
- Test suite loading progress
- Test case execution status
- Step-by-step action logging
- Success/failure results for each step
- Timing information

Example output:
```
[Suite] Loading test suite from: C:\Tests\TestSuite
[Suite] Protocol: HTTP
[Suite] Found 2 test case(s)

[TestCase] Starting: Question1
[TestCase] Loaded 5 step(s)
[Step] Executing: SERVERSTART (Stage: SETUP, ID: IC-SERVER-1)
[Action] ServerStart: Starting server application...
[Proxy] HTTP proxy listening on http://127.0.0.1:5000/ -> http://127.0.0.1:5001/
[Step] Result: PASS - Server started (150ms)
...
```

## Test Suite Format

Test suites are defined using Excel files with specific sheet structures:
- **Header**: Contains suite metadata and protocol type
- **InputClients**: Defines client input actions
- **OutputClients**: Defines expected client outputs
- **OutputServers**: Defines expected server outputs

See the original SolutionGrader repository for detailed Excel format documentation.

## Building

```bash
dotnet build
```

## Requirements

- .NET 8.0 or higher
- ClosedXML (for Excel file reading)
- Test applications must listen on port 5001 (proxy forwards from 5000 to 5001)

## Troubleshooting

### Server Not Ready Warning

If you see "Server not fully initialized after 5s wait", it means:
- The server executable path is incorrect
- The server is taking longer than 5 seconds to start listening
- The server has a configuration error and exits immediately

The tool will continue execution but tests may fail if the server isn't actually running.

### Port Already in Use

If port 5000 is already in use, the middleware proxy will fail to start. Make sure no other applications are using port 5000 or 5001.

## License

See LICENSE file for details.
