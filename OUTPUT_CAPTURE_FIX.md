# Output Capture Fix - Handling Prompts Without Trailing Newlines

## Problem

When grading a solution that was used to create a test kit, the grading system was failing with errors like:
```
[Step] Result: FAIL - Text Content Mismatch: Content differs at position 62
```

The issue was that prompts without trailing newlines (like `"Please enter integer number : "`) were not being captured correctly.

### Root Cause

The original implementation used `ReadLineAsync()` to read process output line-by-line:

```csharp
while ((line = await reader.ReadLineAsync()) != null)
{
    // Process line
}
```

**Problem**: `ReadLineAsync()` only returns when a newline character is encountered. If a program writes a prompt without a trailing newline (e.g., `Console.Write("Enter input: ")`), the output is buffered but never captured until the next line is written.

### Example Scenario

**Test Kit Generator** uses a loop that reads until no newline is left:
```csharp
while (reader.Peek() >= 0)
{
    var line = reader.ReadLine();
    output += line + "\n";
}
```

**Auto-Grader** (before fix) used `ReadLineAsync()`:
```csharp
while ((line = await reader.ReadLineAsync()) != null)
{
    buffer.AppendLine(line);
}
```

**Result**: 
- Test kit expected: `"Client started...\nPlease enter integer number : "`
- Grader captured: `"Client started...\n"`
- The prompt was missing!

## Solution

Modified `ExecutableManager.PumpAsync()` to use **time-based flushing** with `ReadAsync()`:

### Key Changes

1. **Read in chunks** instead of line-by-line:
   ```csharp
   var charBuffer = new char[ReadBufferSize];
   var charsRead = await reader.ReadAsync(charBuffer, 0, charBuffer.Length);
   ```

2. **Flush on two conditions**:
   - When a newline is encountered (normal behavior)
   - When 100ms has elapsed since last flush (captures prompts)

3. **Smart timeout handling**:
   ```csharp
   // Wait for either data or timeout
   var delayTask = lineBuffer.Length > 0 
       ? Task.Delay(FlushIntervalMs) 
       : Task.Delay(Timeout.Infinite);
   var completedTask = await Task.WhenAny(pendingReadTask, delayTask);
   ```

### How It Works

```
Time 0ms:   Process writes "Please enter: " (no newline)
Time 0ms:   ReadAsync() reads characters into buffer
Time 0ms:   No newline found, continue reading
Time 100ms: Flush interval elapsed, flush partial output
Time 100ms: "Please enter: " is now captured!
```

## Test Results

### Unit Test
```csharp
[Fact]
public async Task CapturesPromptWithoutNewline()
{
    // Client outputs: "Please enter integer number : " (no newline)
    var output = manager.GetClientOutput();
    Assert.Contains("Please enter integer number :", output); // ✅ PASS
}
```

### Integration Test
```
=== Output Capture Integration Test ===

1. Starting client...
2. Sending input '42'...
3. Checking captured output...

--- Captured Output ---
Client started, connecting to Server at http://localhost:5000/
Please enter integer number : You entered: 42

--- End Output ---

✓ Contains 'Client started': True
✓ Contains 'Please enter integer number :': True
✓ Contains 'You entered: 42': True

✅ Integration test PASSED - All prompts captured correctly!
```

## Comparison/Normalization

The existing comparison system already handles all requirements:

### Normalize() Function
- ✅ **BOM removal**: Strips Unicode BOM (`\uFEFF`)
- ✅ **Case insensitivity**: Configurable (default: true)
- ✅ **Line endings**: CRLF/CR → LF
- ✅ **Unicode whitespace**: Non-breaking space, em space, etc. → regular space
- ✅ **Whitespace normalization**: Trim lines, collapse multiple spaces
- ✅ **Blank lines**: Remove excessive blank lines

### Multi-Level Comparison
1. **Exact match**: After normalization
2. **Contains match**: Actual contains expected (for console output)
3. **Aggressive match**: Strip ALL whitespace and punctuation
4. **Aggressive contains**: After aggressive stripping

Example:
```csharp
Expected: "Please enter integer number : "
Actual:   "  Please  enter   integer number:  \n"

After normalization:
Expected: "please enter integer number :"
Actual:   "please enter integer number:"

Aggressive normalization:
Expected: "pleaseenterintegernumber"
Actual:   "pleaseenterintegernumber"

Result: ✅ MATCH (aggressive normalization)
```

## Performance Impact

- **Flush interval**: 100ms (configurable constant)
- **Read buffer**: 4096 characters
- **CPU overhead**: Minimal - only creates delay task when needed
- **Memory overhead**: Minimal - reuses buffer

The 100ms delay ensures:
- Prompts are captured quickly (within 100ms)
- CPU usage is not significantly increased
- No blocking on console read operations

## Configuration

The flush interval is defined as a constant in `ExecutableManager.cs`:

```csharp
const int FlushIntervalMs = 100; // Flush partial lines every 100ms
```

If you need different timing:
1. Increase for better performance (less frequent checks)
2. Decrease for faster prompt capture (more frequent checks)

## Security

✅ **CodeQL Analysis**: No security vulnerabilities detected
- No buffer overflows
- No race conditions
- No deadlocks
- Safe memory handling

## Backward Compatibility

✅ **Fully backward compatible**:
- Existing test suites continue to work
- No breaking changes to public APIs
- Line-based output still works as before
- Only adds support for partial-line output

## Verification

To verify the fix is working:

1. **Check logs**: Look for prompts in captured output
2. **Run tests**: Unit and integration tests should pass
3. **Grade test kit**: The solution used to create the test kit should now pass its own tests

Example log output:
```
[ClientInput] Sent: 1
[ClientInput] Client produced output (284 bytes)
```

The "284 bytes" should include the prompt text now.

## Summary

**Before**: Prompts without newlines were never captured → Tests failed
**After**: Prompts are captured within 100ms → Tests pass

The fix ensures that the auto-grader now captures output the same way as the test kit generator, making the grading accurate and reliable.
