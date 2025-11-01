# Comprehensive Grading System Guide

## Overview

The auto-grading system now provides comprehensive validation of client-server applications by grading ALL aspects of the communication flow, not just console outputs.

## What Gets Validated

### 1. Client-Side Validations
- **Client Console Output** - Text displayed to the user
- **Data Response** - HTTP response payload received from server
- **HTTP Status Code** - Status code of server response
- **Response Byte Size** - Size of response payload

### 2. Server-Side Validations
- **Server Console Output** - Server logs and debug messages
- **Data Request** - HTTP request payload received from client
- **HTTP Method** - HTTP verb used (GET, POST, PUT, DELETE, etc.)
- **Request Byte Size** - Size of request payload

### 3. Complete Flow Example
```
User Input -> Client
   |
   v
Client sends HTTP Request -> Middleware Proxy
   |                            (captures: method, request payload, byte size)
   v
Server receives request
   |
   v
Server processes & logs to console
   |
   v
Server sends HTTP Response -> Middleware Proxy
   |                            (captures: status code, response payload, byte size)
   v
Client receives response
   |
   v
Client displays output to console
```

## Test Kit Structure

### Detail.xlsx Sheets

#### 1. InputClients Sheet
Defines the test inputs to send to the client application.

| Column | Description |
|--------|-------------|
| Stage | Stage number (1, 2, 3, ...) |
| Input | Data to send to client |
| DataType | Type of input (Integer, String, etc.) |
| Action | Action to perform (Connect, Client Input) |

#### 2. OutputClients Sheet
Defines expected outputs from the client side.

| Column | Description |
|--------|-------------|
| Stage | Stage number matching input |
| Method | Expected HTTP method (GET, POST, etc.) |
| DataResponse | Expected HTTP response payload |
| StatusCode | Expected HTTP status code (OK, 200, etc.) |
| Output | Expected client console output |
| DataTypeMiddleWare | Type of data (JSON, Text, CSV) |
| ByteSize | Expected response size in bytes |

#### 3. OutputServers Sheet
Defines expected outputs from the server side.

| Column | Description |
|--------|-------------|
| Stage | Stage number matching input |
| Method | Expected HTTP method |
| DataRequest | Expected HTTP request payload |
| Output | Expected server console output |
| DataTypeMiddleware | Type of data (JSON, Text, Empty) |
| ByteSize | Expected request size in bytes |

## Grading Modes

Control which validations to run using the `--grading-mode` parameter:

### DEFAULT (Full Validation)
Validates everything - all HTTP traffic and console outputs.
```bash
SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results
```

### CLIENT Mode
Validates only client-side:
- Client console output
- Data responses from server
```bash
SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results --grading-mode CLIENT
```

### SERVER Mode
Validates only server-side:
- Server console output
- Data requests from client
```bash
SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results --grading-mode SERVER
```

### CONSOLE Mode
Validates only console outputs (no HTTP validation):
- Client console output
- Server console output
```bash
SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results --grading-mode CONSOLE
```

### HTTP Mode
Validates only HTTP traffic (no console outputs):
- HTTP methods
- Status codes
- Request/Response payloads
- Byte sizes
```bash
SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results --grading-mode HTTP
```

## Output Files

### 1. GradeDetail.xlsx
The main grading report with multiple sheets:

#### Original Sheets (with results)
- **InputClients** - Shows inputs sent + validation results
- **OutputClients** - Shows expected vs actual client outputs + results
- **OutputServers** - Shows expected vs actual server outputs + results

#### New Enhanced Sheets

**TestRunData Sheet** - Shows ONLY actual runtime data
| Column | Description |
|--------|-------------|
| Stage | Test stage number |
| StepId | Unique step identifier |
| ValidationType | Type of validation (HTTP_METHOD, CLIENT_OUTPUT, etc.) |
| Action | Action performed |
| Result | PASS or FAIL |
| Message | Result message |
| DurationMs | Time taken in milliseconds |
| ActualOutput | Captured actual output |
| HttpMethod | Captured HTTP method |
| StatusCode | Captured status code |
| ByteSize | Captured byte size |

**ErrorReport Sheet** - Consolidates ALL errors
| Column | Description |
|--------|-------------|
| Stage | Stage where error occurred |
| StepId | Step identifier |
| ValidationType | Validation that failed |
| ErrorCode | Error code (TEXT_MISMATCH, HTTP_METHOD_MISMATCH, etc.) |
| ErrorCategory | Error category (Compare, Network, Process, etc.) |
| Message | Error description |
| ExpectedValue | What was expected |
| ActualValue | What was received |
| PointsLost | Points lost for this error |

### 2. FailedTestDetail.xlsx
Compact report showing only failed tests (created only when there are failures).

### 3. OverallSummary.xlsx
Summary of all test cases with total points.

## Error Codes

### HTTP/Network Validation Errors
- **HTTP_METHOD_MISMATCH** - HTTP method doesn't match (e.g., expected GET, got POST)
- **STATUS_CODE_MISMATCH** - Status code doesn't match (e.g., expected 200, got 404)
- **BYTE_SIZE_MISMATCH** - Byte size outside tolerance range
- **DATA_REQUEST_MISMATCH** - Request payload doesn't match expected
- **DATA_RESPONSE_MISMATCH** - Response payload doesn't match expected
- **DATA_TYPE_MISMATCH** - Data type doesn't match (e.g., expected JSON, got Text)

### Other Errors
- **TEXT_MISMATCH** - Text content doesn't match
- **JSON_MISMATCH** - JSON structure/content doesn't match
- **PROCESS_CRASHED** - Client or server process crashed
- **TIMEOUT** - Operation exceeded timeout
- **ACTUAL_FILE_MISSING** - Expected output not generated

## Configuring Validation Tolerance

### Byte Size Tolerance
By default, byte size comparisons allow:
- ±10 bytes absolute difference, OR
- ±5% relative difference

This can be adjusted in `GradingKeywords.cs`:
```csharp
public const int ByteSizeTolerance = 10;
public const double ByteSizeTolerancePercent = 0.05;
```

### Status Code Normalization
Status codes are normalized for comparison:
- "200", "OK", "200 OK" all match
- "404", "NotFound", "404 NotFound" all match

## Debugging Tips

### 1. Use Grading Modes
Start with specific modes to isolate issues:
```bash
# Test only client output first
--grading-mode CLIENT

# Then test only server output
--grading-mode SERVER

# Finally test HTTP traffic
--grading-mode HTTP
```

### 2. Check TestRunData Sheet
This sheet shows what was actually captured during execution:
- Verify data is being captured correctly
- Check HTTP metadata (method, status, size)
- Review actual outputs vs expected

### 3. Review ErrorReport Sheet
All errors are consolidated here:
- See all failures at once
- Identify patterns (e.g., all byte size mismatches)
- Focus on high-point-value errors first

### 4. Examine Original Sheets
The InputClients, OutputClients, and OutputServers sheets show:
- Full expected vs actual comparison
- Diff excerpts for mismatches
- Colored highlighting of failures

## Code Customization

### Adding Custom Validation Types

1. **Add to GradingKeywords.cs**:
```csharp
public const string Validation_CustomCheck = "CUSTOM_CHECK";
```

2. **Add to GradingConfig.cs**:
```csharp
public bool ValidateCustomCheck { get; set; } = true;
```

3. **Add error code to ErrorCodes.cs**:
```csharp
public const string CUSTOM_CHECK_MISMATCH = "CUSTOM_CHECK_MISMATCH";
```

4. **Implement in DataComparisonService.cs**:
```csharp
public (bool, string) CompareCustomCheck(string? expected, string? actual)
{
    // Your validation logic here
}
```

5. **Add to ValidateStep method** in DataComparisonService.cs:
```csharp
if (validationType == "CUSTOM_CHECK")
{
    return CompareCustomCheck(step.Target, actualPath);
}
```

### Modifying Grading Modes

Edit `GradingConfig.cs` to create custom modes:
```csharp
public static GradingConfig MyCustomMode => new GradingConfig
{
    ValidateClientOutput = true,
    ValidateServerOutput = false,
    ValidateDataResponse = true,
    // ... configure as needed
};
```

Then use in `Program.cs`:
```csharp
"CUSTOM" => GradingConfig.MyCustomMode,
```

## Best Practices

### 1. Start Simple
- Begin with console output validation only (`--grading-mode CONSOLE`)
- Then add HTTP validation incrementally

### 2. Use Appropriate Data Types
- Mark JSON responses as "JSON" in DataTypeMiddleWare column
- Use "Empty" for endpoints that return no body
- Use "Text" for plain text responses

### 3. Define Realistic Byte Sizes
- Don't specify byte size if content can vary
- Leave byte size empty to skip that validation
- Account for variations in JSON formatting (whitespace)

### 4. Test Incrementally
- Test one stage at a time
- Verify each stage before moving to next
- Use grading modes to isolate issues

### 5. Review All Sheets
- Check original sheets for detailed comparisons
- Use TestRunData for runtime verification
- Use ErrorReport for quick debugging

## Example Workflow

1. **Create Test Kit**:
   - Define InputClients (user inputs)
   - Define OutputClients (expected client behavior)
   - Define OutputServers (expected server behavior)

2. **Run Initial Test**:
   ```bash
   SolutionGrader.Cli ExecuteSuite --suite TestKitDemo --out Results
   ```

3. **Debug Failures**:
   - Open ErrorReport sheet - see all failures
   - Open TestRunData sheet - verify actual captures
   - Use grading modes to isolate:
     ```bash
     --grading-mode CLIENT  # Test client only
     --grading-mode SERVER  # Test server only
     ```

4. **Fix Issues**:
   - Update application code based on errors
   - Re-run tests to verify fixes
   - Iterate until all tests pass

5. **Review Results**:
   - Check OverallSummary.xlsx for total score
   - Verify all validations passed
   - Review GradeDetail.xlsx for complete report
