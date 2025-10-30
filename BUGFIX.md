# Bug Fixes: Output Capture and Comparison Issues

## Summary

Fixed multiple critical bugs causing test failures:
1. **HTTP middleware overwriting server console output** - Fixed by using distinct memory storage locations
2. **Timing issues causing output to wrong stage** - Fixed by using cumulative output approach as primary method
3. **Excel stage whitespace causing key mismatches** - Fixed by trimming all Excel cell values
4. **Unicode whitespace not being normalized** - Fixed by adding normalization for special space characters

## Problem Description

### Symptoms
- Tests fail with "Text Content Mismatch: Content differs at position 0"
- Affects both client and server output comparisons in stages where HTTP traffic occurs
- Same solution that created the test kit fails when graded against that test kit

### Root Cause
When grading client-server applications that use HTTP communication:

1. **ExecutableManager** captures server process console output (stdout/stderr) using `AppendServerOutput()` to progressively accumulate output lines
   - Example: "Server started at http://localhost:5001/", "GET /books/1"

2. **MiddlewareProxyService** intercepts HTTP traffic and calls `SetServerOutput()` to store request/response data
   - Example: "=== REQUEST ===\n\n=== RESPONSE ===\n[{...}]"

3. `SetServerOutput()` **completely replaces** the StringBuilder content, overwriting all accumulated console output

4. When test comparison runs:
   - Expected: Server console output like "GET /books/1"
   - Actual: HTTP traffic like "=== REQUEST ===..."
   - Result: Mismatch at character 0 because content is completely different

### Technical Details

The issue was in how memory-based output capture worked:

**Before Fix:**
```csharp
// ExecutableManager.cs - Appends console output
_run.AppendServerOutput(question, stage, line + "\n");  // Accumulates

// MiddlewareProxyService.cs - Captures HTTP traffic
_run.SetServerOutput(question, stage, httpTraffic);     // OVERWRITES!
```

Both used the same memory key: `memory://servers/{question}/{stage}`

**Memory Key Conflict:**
```
Stage 2 memory key: memory://servers/TC01/2
  - First contains: "GET /books/1\n" (from console)
  - Then replaced with: "=== REQUEST ===\n..." (from HTTP traffic)
  - Console output lost!
```

## Solution

### Architecture Change

Introduced **separate memory namespaces** for different output types:

| Output Type | Memory Key Pattern | Source | Usage |
|-------------|-------------------|--------|-------|
| Server Console | `memory://servers/{q}/{s}` | ExecutableManager | OS-OUT-* steps |
| HTTP Request | `memory://servers-req/{q}/{s}` | MiddlewareProxyService | OS-REQ-* steps |
| HTTP Response | `memory://servers-resp/{q}/{s}` | MiddlewareProxyService | Future use |

### Code Changes

#### 1. FileKeywords.cs - Added Constants
```csharp
public const string Folder_ServersRequest = "servers-req";
public const string Folder_ServersResponse = "servers-resp";
```

#### 2. IRunContext.cs - Extended Interface
```csharp
string GetServerRequestCaptureKey(string questionCode, string stage);
string GetServerResponseCaptureKey(string questionCode, string stage);
void SetServerRequest(string questionCode, string stage, string content);
void SetServerResponse(string questionCode, string stage, string content);
```

#### 3. RunContext.cs - Implemented New Methods
```csharp
public string GetServerRequestCaptureKey(string questionCode, string stage)
    => BuildKey(FileKeywords.Folder_ServersRequest, questionCode, stage);

public string GetServerResponseCaptureKey(string questionCode, string stage)
    => BuildKey(FileKeywords.Folder_ServersResponse, questionCode, stage);

public void SetServerRequest(string questionCode, string stage, string content)
    => SetCapture(FileKeywords.Folder_ServersRequest, questionCode, stage, content);

public void SetServerResponse(string questionCode, string stage, string content)
    => SetCapture(FileKeywords.Folder_ServersResponse, questionCode, stage, content);
```

#### 4. MiddlewareProxyService.cs - Use Separate Storage
**Before:**
```csharp
_run.SetServerOutput(question, stage, sb.ToString());  // Overwrites console!
```

**After:**
```csharp
_run.SetServerRequest(question, stage, requestBody ?? "");
_run.SetServerResponse(question, stage, respText);
// Console output in memory://servers/* remains intact!
```

#### 5. Executor.cs - Route to Correct Memory Location
**Before:**
```csharp
if (step.Id.StartsWith("OS-", StringComparison.OrdinalIgnoreCase))
    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);
```

**After:**
```csharp
if (step.Id.StartsWith("OS-REQ-", StringComparison.OrdinalIgnoreCase))
    return _run.GetServerRequestCaptureKey(step.QuestionCode, stageLabel);

if (step.Id.StartsWith("OS-OUT-", StringComparison.OrdinalIgnoreCase))
    return _run.GetServerCaptureKey(step.QuestionCode, stageLabel);
```

### Behavior After Fix

**Stage 1: Server Start**
```
Console Output → memory://servers/TC01/1
Content: " Server started at http://localhost:5001/\n"
```

**Stage 2: HTTP Request**
```
Console Output → memory://servers/TC01/2
Content: "GET /books/1\n"

HTTP Request → memory://servers-req/TC01/2
Content: ""

HTTP Response → memory://servers-resp/TC01/2
Content: "[{\"BookId\":1,...}]"
```

**Test Comparisons:**
- `OS-OUT-2`: Compares expected "GET /books/1" with `memory://servers/TC01/2` ✓ PASS
- `OS-REQ-2`: Compares expected "" with `memory://servers-req/TC01/2` ✓ PASS

## Testing

### Unit Tests
Created unit tests to verify:
1. Console output is preserved after HTTP traffic capture
2. HTTP request/response are stored in separate locations
3. Both outputs remain accessible and independent

**Test Results:**
```
Stage 1:
  Output: ' Server started\n'

Stage 2:
  Console: 'GET /books/1\n'
  Request: ''

✓ PASS
```

### Integration Testing
The fix has been tested with:
- ✓ Build verification (no compilation errors)
- ✓ Unit tests for output capture
- ✓ CodeQL security analysis (no vulnerabilities)

## Impact

### Fixed Issues
- ✅ Server console output no longer overwritten by HTTP traffic
- ✅ Test comparisons work correctly for both console and HTTP data
- ✅ OS-OUT-* steps compare against console output as expected
- ✅ OS-REQ-* steps compare against HTTP requests as expected

### Compatibility
- ✅ Backward compatible - existing tests continue to work
- ✅ No breaking changes to public APIs
- ✅ Test kit format unchanged

### Performance
- No performance impact - same number of memory operations
- Slightly more memory usage (separate storage for HTTP traffic)
- Better separation of concerns improves maintainability

## Related Files

### Modified Files
1. `Lib/SolutionGrader.Core/Abstractions/IRunContext.cs`
2. `Lib/SolutionGrader.Core/Keywords/FileKeywords.cs`
3. `Lib/SolutionGrader.Core/Services/RunContext.cs`
4. `Lib/SolutionGrader.Core/Services/MiddlewareProxyService.cs`
5. `Lib/SolutionGrader.Core/Services/Executor.cs`

### Test Files
- `/tmp/TestOutputCapture.csproj` (unit test project)

## Future Improvements

### Potential Enhancements
1. Use HTTP response memory for comparing actual responses in future test scenarios
2. Add timestamps to captured output for better debugging
3. Consider adding output capture for client HTTP requests as well

### Monitoring
Watch for:
- Any remaining "mismatch at character 0" errors (should not occur)
- Memory usage growth if test suites become very large
- Performance with high-frequency HTTP traffic

## Security

**CodeQL Analysis:** No security vulnerabilities detected
- Memory access patterns are safe
- No sensitive data exposure
- Proper encapsulation maintained

## Additional Fixes (v1.1 - 2025-10-30)

### Bug #2: Timing Issues and Stage Mismatches

**Problem:** Even after fixing the HTTP overwrite issue, tests were still failing with:
- "Content differs at position 0" - suggesting empty or wrong actual output
- "Actual Output Missing" - memory key not found

**Root Cause:**
Server console output was being written to the wrong stage due to async timing:
1. Server outputs "GET /books/1" during async HTTP request processing
2. The output gets captured to stage 1 (during setup) instead of stage 2 (where comparison expects it)
3. When comparison runs for stage 2, the memory key `memory://servers/TC01/2` is empty
4. The original cumulative approach was only used as a FALLBACK after trying current stage, so it never ran

**Solution:**
Changed cumulative output to be the PRIMARY method for memory:// paths:

```csharp
// Before: Try current stage first, use cumulative as fallback
if (!TryReadContent(actualPath, out var actualRaw))
    return error;  // Never reaches cumulative!
actualRaw = TryGetCumulativeOutput(actualPath, actualRaw);  // Too late

// After: Use cumulative as primary for memory:// paths
if (actualPath.StartsWith("memory://"))
    actualRaw = TryGetCumulativeOutput(actualPath, "");  // Accumulates stages 1-N
```

**Additional Improvements:**
- Limited cumulative iteration to current stage (not hardcoded 1-10)
- Added safety limit of 50 stages max to prevent excessive loops
- Extracted memory path parsing into reusable helper method

### Bug #3: Excel Whitespace in Stage Values

**Problem:** Stage values from Excel might have leading/trailing whitespace (e.g., " 2 " instead of "2"), causing memory key mismatches.

**Solution:** Added `.Trim()` to all Excel cell value reads:

```csharp
// ExcelDetailParser.cs
private static string Get(IXLRangeRow row, Dictionary<string, int> map, string key)
    => map.TryGetValue(key, out var c) ? row.Cell(c).GetString().Trim() : "";
```

### Bug #4: Unicode Whitespace Normalization

**Problem:** Expected output might contain non-breaking spaces (U+00A0) or other Unicode whitespace that don't match regular spaces.

**Solution:** Added normalization for Unicode whitespace characters:

```csharp
// DataComparisonService.cs Normalize()
s = s.Replace("\u00A0", " "); // Non-breaking space
s = s.Replace("\u2002", " "); // En space  
s = s.Replace("\u2003", " "); // Em space
s = s.Replace("\u2009", " "); // Thin space
```

### Testing Results

**Build:** ✅ Successful
**Code Review:** ✅ All feedback addressed
**Security Scan:** ✅ No vulnerabilities (CodeQL)

### Impact

These fixes ensure:
- ✅ Output captured to wrong stage (due to timing) is still found via cumulative approach
- ✅ Whitespace in Excel cells doesn't break stage key matching
- ✅ Unicode whitespace in expected output is normalized correctly
- ✅ Better error messages showing which stage/key failed

## Credits

- Issue identified by: NguyenHuuCuongK18
- Fix implemented by: GitHub Copilot Agent
- Code review: Automated review system

---
**Date:** 2025-10-30  
**Version:** 1.1  
**Status:** ✅ Complete
