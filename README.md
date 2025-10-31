# Auto-Grading System for Client-Server Applications

## Overview

This is a comprehensive auto-grading system designed to test and grade client-server applications built with C#/.NET. It validates the entire communication flow between client and server applications, including HTTP traffic, console outputs, and data payloads.

## Features

### Comprehensive Validation
- ✅ **Client Console Output** - Validates text displayed to users
- ✅ **Server Console Output** - Validates server logs and messages
- ✅ **HTTP Methods** - Verifies correct HTTP verbs (GET, POST, etc.)
- ✅ **Status Codes** - Checks HTTP response status codes
- ✅ **Request Payloads** - Validates data sent from client to server
- ✅ **Response Payloads** - Validates data sent from server to client
- ✅ **Byte Sizes** - Checks payload sizes with configurable tolerance
- ✅ **Data Types** - Supports JSON, CSV, Text, and other formats

### Flexible Grading Modes
- **DEFAULT** - Full validation of all aspects
- **CLIENT** - Client-side only (output + responses)
- **SERVER** - Server-side only (output + requests)
- **CONSOLE** - Console outputs only
- **HTTP** - HTTP traffic only

### Enhanced Reporting
- **GradeDetail.xlsx** - Complete grading report with multiple sheets
- **TestRunData Sheet** - Shows actual runtime captures (no template duplication)
- **ErrorReport Sheet** - Consolidates ALL errors for easy debugging
- **OverallSummary.xlsx** - Summary of all test cases
- **FailedTestDetail.xlsx** - Compact report of failures only

## Quick Start

### Prerequisites
- .NET 8.0 SDK or later
- Client and Server applications to test

### Build
```bash
dotnet build --configuration Release
```

### Run Tests
```bash
# Full validation (all checks enabled)
./Application/SolutionGrader.Cli/bin/Release/net8.0/SolutionGrader.Cli \
    ExecuteSuite \
    --suite TestKitDemo \
    --out Results \
    --client path/to/client.exe \
    --server path/to/server.exe

# Client-only validation (for debugging)
./Application/SolutionGrader.Cli/bin/Release/net8.0/SolutionGrader.Cli \
    ExecuteSuite \
    --suite TestKitDemo \
    --out Results \
    --client path/to/client.exe \
    --server path/to/server.exe \
    --grading-mode CLIENT
```

## Test Kit Structure

Create a test suite with `Header.xlsx` and `Detail.xlsx` files:

### Header.xlsx
Defines test cases and their point values.

### Detail.xlsx
Contains three sheets defining the test flow:

1. **InputClients** - User inputs to send
2. **OutputClients** - Expected client behavior
3. **OutputServers** - Expected server behavior

See [GRADING_GUIDE.md](GRADING_GUIDE.md) for detailed documentation.

## Command Line Options

```bash
SolutionGrader.Cli ExecuteSuite [OPTIONS]

Required:
  --suite <path>          Path to test suite folder or Header.xlsx
  --out <path>           Output directory for results

Optional:
  --client <path>        Path to client executable
  --server <path>        Path to server executable
  --client-appsettings <path>   Client appsettings.json template
  --server-appsettings <path>   Server appsettings.json template
  --db-script <path>     Database reset script
  --timeout <seconds>    Timeout per test stage (default: 10)
  --grading-mode <mode>  Validation mode (DEFAULT, CLIENT, SERVER, CONSOLE, HTTP)
```

## Example Test Kit

See `TestKitDemo/` folder for sample test kits.

### Example: TC01/Detail.xlsx

**InputClients Sheet:**
| Stage | Input | DataType | Action |
|-------|-------|----------|--------|
| 1 | | | Connect |
| 2 | 1 | Integer | Client Input |

**OutputClients Sheet:**
| Stage | Method | DataResponse | StatusCode | Output | DataTypeMiddleWare | ByteSize |
|-------|--------|--------------|------------|--------|-------------------|----------|
| 1 | | | | Client started... | | |
| 2 | GET | [{"BookId":1,...}] | OK | BookId: 1... | JSON | 268 |

**OutputServers Sheet:**
| Stage | Method | DataRequest | Output | DataTypeMiddleware | ByteSize |
|-------|--------|-------------|--------|-------------------|----------|
| 1 | | | Server started... | | |
| 2 | GET | | GET /books/1 | Empty | 0 |

## Output Files

### GradeDetail.xlsx
Main grading report with:
- Original sheets (InputClients, OutputClients, OutputServers) with results
- **TestRunData** sheet - Actual runtime data only
- **ErrorReport** sheet - All errors consolidated

### OverallSummary.xlsx
Summary table showing:
- Test case name
- Pass/Fail status
- Points awarded
- Points possible

### FailedTestDetail.xlsx
Created only when tests fail, contains compact failure information.

## Architecture

### Key Components

- **SuiteRunner** - Orchestrates test execution
- **ExcelDetailParser** - Parses test kits and creates validation steps
- **Executor** - Executes test steps (start processes, send input, etc.)
- **MiddlewareProxyService** - Intercepts HTTP traffic between client/server
- **DataComparisonService** - Validates outputs against expected values
- **ExcelDetailLogService** - Generates grading reports

### Validation Flow

```
1. Load test suite (Header.xlsx + Detail.xlsx)
2. Start server application
3. Start middleware proxy (intercepts HTTP traffic)
4. Start client application
5. For each test stage:
   - Send input to client
   - Capture console outputs
   - Capture HTTP traffic (method, status, payloads, sizes)
   - Wait for processing
6. Validate all captures against expected values
7. Generate comprehensive reports
```

## Customization

### Adding New Validation Types

1. Add validation constant to `GradingKeywords.cs`
2. Add config property to `GradingConfig.cs`
3. Add error code to `ErrorCodes.cs`
4. Implement validation in `DataComparisonService.cs`
5. Update `ValidateStep` method to handle new type

### Creating Custom Grading Modes

Edit `GradingConfig.cs` to define new modes:

```csharp
public static GradingConfig MyCustomMode => new GradingConfig
{
    ValidateClientOutput = true,
    ValidateServerOutput = false,
    ValidateDataResponse = true,
    ValidateDataRequest = false,
    ValidateHttpMethod = true,
    ValidateStatusCode = true,
    ValidateByteSize = false,
    ValidateDataType = false
};
```

Then add to `Program.cs`:
```csharp
"MYMODE" => GradingConfig.MyCustomMode,
```

## Debugging

### Common Issues

**1. Byte Size Mismatches**
- JSON formatting differences (whitespace)
- Solution: Leave ByteSize empty in test kit to skip validation

**2. Timeout Errors**
- Application takes too long to respond
- Solution: Increase `--timeout` parameter

**3. HTTP Method Not Captured**
- Middleware proxy not intercepting traffic
- Solution: Verify proxy is running on port 5000

**4. Process Crashes**
- Check client/server logs in TestRunData sheet
- Review ActualOutput column for error messages

### Debug with Grading Modes

```bash
# Step 1: Test console outputs only
--grading-mode CONSOLE

# Step 2: Test HTTP traffic only
--grading-mode HTTP

# Step 3: Test client-side only
--grading-mode CLIENT

# Step 4: Test server-side only
--grading-mode SERVER

# Step 5: Full validation
# (omit --grading-mode or use DEFAULT)
```

## Error Codes Reference

| Code | Description |
|------|-------------|
| HTTP_METHOD_MISMATCH | HTTP method doesn't match expected |
| STATUS_CODE_MISMATCH | Status code doesn't match expected |
| BYTE_SIZE_MISMATCH | Byte size outside tolerance range |
| DATA_REQUEST_MISMATCH | Request payload doesn't match |
| DATA_RESPONSE_MISMATCH | Response payload doesn't match |
| TEXT_MISMATCH | Text content doesn't match |
| JSON_MISMATCH | JSON structure doesn't match |
| PROCESS_CRASHED | Application crashed |
| TIMEOUT | Operation exceeded timeout |

## Contributing

When adding new features:
1. Update relevant services in `Lib/SolutionGrader.Core/`
2. Add keywords to `Keywords/` directory
3. Update error codes in `Domain/Errors/ErrorCodes.cs`
4. Update documentation (this README and GRADING_GUIDE.md)
5. Build and test with sample project

## License

[Add your license here]

## For More Information

See [GRADING_GUIDE.md](GRADING_GUIDE.md) for comprehensive documentation including:
- Detailed validation explanations
- Test kit creation guide
- Complete error code reference
- Customization examples
- Best practices
