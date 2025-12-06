# Batch Grading Message Logger Fix

## Problem Statement

When grading multiple students in parallel batches, the system threw file access conflicts:
```
The process cannot access the file 'C:\...\GradingLogs\GradingMessages_20251207_054818.txt' 
because it is being used by another process.
```

This error occurred when multiple students were graded simultaneously, causing all parallel grading workers to fail.

## Root Cause Analysis

### Previous Architecture (BROKEN)

```
GradingWindow.StartGradingAsync()
  └─> Creates N parallel worker threads (e.g., 5 threads for batch size 5)
      │
      ├─> Worker-0: GradeStudentAsync(Student1)
      │   └─> _gradingService.StartGradingAsync([Student1], config, ...)
      │       └─> NEW GradingMessageLogger(resultPath)
      │           └─> Creates: GradingMessages_20251207_054818.txt ✓
      │
      ├─> Worker-1: GradeStudentAsync(Student2)
      │   └─> _gradingService.StartGradingAsync([Student2], config, ...)
      │       └─> NEW GradingMessageLogger(resultPath)
      │           └─> Creates: GradingMessages_20251207_054818.txt ❌ CONFLICT!
      │
      ├─> Worker-2: GradeStudentAsync(Student3)
      │   └─> _gradingService.StartGradingAsync([Student3], config, ...)
      │       └─> NEW GradingMessageLogger(resultPath)
      │           └─> Creates: GradingMessages_20251207_054818.txt ❌ CONFLICT!
      │
      └─> ... (all workers fail with file access error)
```

### The Issue

1. **Multiple Logger Instances**: Each call to `GradingOrchestrationService.StartGradingAsync` created a NEW `GradingMessageLogger` instance
2. **Same Timestamp**: All loggers created in the same second used the SAME filename pattern: `GradingMessages_yyyyMMdd_HHmmss.txt`
3. **File Access Conflict**: Multiple loggers tried to open/create the same file simultaneously, causing exceptions
4. **Cascading Failures**: All parallel students failed because they couldn't create their log files

### Why This Happened

The code was designed for **sequential grading** where only ONE student is graded at a time. When parallel/batch grading was added:
- Each worker thread independently called `StartGradingAsync` with a single student
- Each call created its own `GradingMessageLogger` 
- No shared logger existed at the session level
- Similar to how `PortAllocator` needed to be shared, but was overlooked

## Solution Architecture

### New Architecture (FIXED)

```
GradingWindow.StartGradingAsync()
  │
  ├─> Create SHARED GradingMessageLogger (ONE instance for entire session)
  │   └─> Creates: GradingMessages_20251207_054818.txt ✓ (once, at session start)
  │
  └─> Creates N parallel worker threads
      │
      ├─> Worker-0: GradeStudentAsync(Student1)
      │   └─> _gradingService.StartGradingAsync([Student1], config, ..., sharedLogger)
      │       └─> Uses SHARED logger (no new instance created) ✓
      │
      ├─> Worker-1: GradeStudentAsync(Student2)
      │   └─> _gradingService.StartGradingAsync([Student2], config, ..., sharedLogger)
      │       └─> Uses SHARED logger (no new instance created) ✓
      │
      ├─> Worker-2: GradeStudentAsync(Student3)
      │   └─> _gradingService.StartGradingAsync([Student3], config, ..., sharedLogger)
      │       └─> Uses SHARED logger (no new instance created) ✓
      │
      └─> ... (all workers succeed using shared logger)
  
  └─> Dispose SHARED logger (at session end, exports to Excel)
```

### Key Changes

1. **Shared Logger Pattern**: Similar to `PortAllocator`, created ONE `GradingMessageLogger` per grading session
2. **Ownership Tracking**: Added `_ownsMessageLogger` flag to prevent double disposal
3. **Optional Parameter**: Made `sharedMessageLogger` an optional parameter in `StartGradingAsync`
4. **Thread-Safe Logging**: `GradingMessageLogger` already had proper locking, so multiple threads can safely write to the same instance

## Implementation Details

### 1. GradingWindow.xaml.cs

**Added Shared Logger Field:**
```csharp
// CRITICAL: Shared GradingMessageLogger for batch/parallel grading
// Each grading session needs ONE shared GradingMessageLogger that all parallel students use
// This prevents file access conflicts when multiple students try to write to the same log file
// The logger creates one timestamped file per session, not per student
private GradingMessageLogger? _sharedMessageLogger;
```

**Initialization at Session Start:**
```csharp
// CRITICAL: Initialize shared GradingMessageLogger for THIS grading session
// All parallel students will use this SAME GradingMessageLogger instance
// to ensure thread-safe logging without file access conflicts
// The logger creates ONE log file per session with a unique timestamp
var resultPath = !string.IsNullOrEmpty(_configuration.SaveResultFolderPath) 
    ? _configuration.SaveResultFolderPath 
    : Path.Combine(_configuration.SubmitFolderPath, "Results");
_sharedMessageLogger = new GradingMessageLogger(resultPath);
_logger.LogInfo($"[Message Logger] Initialized SHARED GradingMessageLogger for batch grading session");
```

**Pass Shared Logger to Service:**
```csharp
await _gradingService.StartGradingAsync(
    new System.Collections.Generic.List<StudentSolution> { student },
    studentConfig,
    sessionState,
    ct,
    _sharedMessageLogger); // Pass shared logger
```

**Disposal at Session End:**
```csharp
// Dispose shared GradingMessageLogger when session ends
// This will export all messages to Excel and close the log file
_sharedMessageLogger?.LogInfo($"Grading session completed. Total students: {studentsToGrade.Count}");
_sharedMessageLogger?.Dispose();
_sharedMessageLogger = null;
_logger.LogInfo("[Message Logger] Shared GradingMessageLogger disposed and logs exported to Excel");
```

### 2. GradingOrchestrationService.cs

**Added Optional Parameter:**
```csharp
/// <param name="sharedMessageLogger">Optional shared GradingMessageLogger for batch grading. 
/// If provided, uses this instead of creating a new instance. 
/// This prevents file access conflicts in parallel grading scenarios.</param>
public async Task StartGradingAsync(
    List<StudentSolution> students, 
    GradingConfiguration config,
    GradingSessionState sessionState,
    CancellationToken ct = default,
    GradingMessageLogger? sharedMessageLogger = null)
```

**Ownership Tracking:**
```csharp
private GradingMessageLogger? _messageLogger;
private bool _ownsMessageLogger; // True if we created the logger, false if it was shared
```

**Conditional Initialization:**
```csharp
// Initialize centralized message logger for structured error/message logging
// Use shared logger if provided (for batch grading), otherwise create a new one
if (sharedMessageLogger != null)
{
    _messageLogger = sharedMessageLogger;
    _ownsMessageLogger = false; // We don't own this logger, so don't dispose it
    _logger.LogInfo($"[GradingOrchestrationService] Using shared GradingMessageLogger for batch grading");
}
else
{
    _messageLogger = new GradingMessageLogger(resultPath);
    _ownsMessageLogger = true; // We created this logger, so we must dispose it
    _messageLogger.LogInfo($"Starting grading session for {students.Count} students");
    _logger.LogInfo($"[GradingOrchestrationService] Created new GradingMessageLogger instance");
}
```

**Conditional Disposal:**
```csharp
// Dispose message logger only if we created it (not shared)
// If shared, the owner (GradingWindow) will dispose it
if (_ownsMessageLogger)
{
    _messageLogger?.LogInfo($"Grading session completed. Total students: {students.Count}");
    _messageLogger?.Dispose();
    _logger.LogInfo("[GradingOrchestrationService] Disposed owned GradingMessageLogger");
}
else
{
    _logger.LogInfo("[GradingOrchestrationService] Skipped disposal of shared GradingMessageLogger (owned by caller)");
}
```

## Thread Safety

The fix is thread-safe because:

1. **Single Instance**: Only ONE `GradingMessageLogger` instance exists per grading session
2. **Internal Locking**: `GradingMessageLogger` already has proper `lock (_fileLock)` for file writes
3. **ConcurrentBag**: Uses `ConcurrentBag<GradingMessage>` for thread-safe message collection
4. **StreamWriter AutoFlush**: Configured with `AutoFlush = true` for immediate write consistency

From `GradingMessageLogger.cs`:
```csharp
private readonly ConcurrentBag<GradingMessage> _messages = new();
private readonly object _fileLock = new object();

// Write to text log file with disposal guard
lock (_fileLock)
{
    if (_disposed || _textLogWriter == null)
        return;
    
    try
    {
        _textLogWriter.WriteLine(msg.ToString());
        // ... additional writes
    }
    catch (ObjectDisposedException) { /* handled */ }
    catch (IOException) { /* handled */ }
}
```

## Benefits

1. **No File Conflicts**: All students in a session write to the same log file safely
2. **Better Log Organization**: One consolidated log file per session, easier to review
3. **Consistent with PortAllocator Pattern**: Follows established shared resource pattern
4. **Backward Compatible**: Optional parameter means existing sequential grading still works
5. **Resource Efficient**: ONE log file instead of attempting to create N identical files

## Testing Recommendations

### Manual Testing

1. **Batch Grading Test**:
   - Set `MaxParallelStudents = 5`
   - Grade 5+ students simultaneously
   - Verify: No file access errors
   - Verify: Single `GradingMessages_*.txt` file created
   - Verify: All student messages present in the log

2. **Sequential Grading Test**:
   - Set `MaxParallelStudents = 1`
   - Grade students one at a time
   - Verify: Logger still works correctly
   - Verify: No regressions

3. **Large Batch Test**:
   - Set `MaxParallelStudents = 10`
   - Grade 50+ students
   - Verify: No file conflicts across multiple batches
   - Verify: All messages logged correctly

### Expected Log Output

**Console Log (Success):**
```
[Message Logger] Initialized SHARED GradingMessageLogger for batch grading session
[Worker-0] [1/5] Starting grading for: Student1 (Paper 1)
[Worker-1] [2/5] Starting grading for: Student2 (Paper 1)
[Worker-2] [3/5] Starting grading for: Student3 (Paper 1)
[Worker-3] [4/5] Starting grading for: Student4 (Paper 1)
[Worker-4] [5/5] Starting grading for: Student5 (Paper 1)
... (parallel grading proceeds)
[Message Logger] Shared GradingMessageLogger disposed and logs exported to Excel
```

**File System (Success):**
```
Results/
  GradingLogs/
    GradingMessages_20251207_120000.txt     (single text log)
    GradingMessages_20251207_120000.xlsx    (Excel export with all messages)
  1/
    student/
      Student1/...
      Student2/...
      ...
```

## Comparison with Other Shared Resources

This fix follows the same pattern as other shared resources in the system:

| Resource | Shared? | Purpose | Created By | Disposed By |
|----------|---------|---------|------------|-------------|
| `PortAllocator` | ✓ | Unique port allocation | GradingWindow | GradingWindow |
| `GradingMessageLogger` | ✓ | Centralized logging | GradingWindow | GradingWindow |
| `ExcelLogCoordinator` | ✗ | Excel updates | GradingOrchestrationService | GradingOrchestrationService |
| `SharedNetworkMonitor` | ✓ | Network traffic capture | SharedNetworkMonitorManager | SharedNetworkMonitorManager |

Note: `ExcelLogCoordinator` is NOT shared because each call to `StartGradingAsync` processes different students and needs independent Excel coordination. However, `GradingMessageLogger` MUST be shared because all students write to the same log file.

## Related Documentation

- `BATCH_GRADING_FIX.md` - Original batch grading implementation
- `PARALLEL_GRADING_FIX.md` - Parallel execution without serialization
- `UI_BATCH_GRADING_PORT_FIX.md` - Port allocation fix (similar pattern)
- `MULTI_THREADING_ARCHITECTURE.md` - Overall parallel architecture

## Conclusion

This fix resolves the critical file access conflict issue in batch grading by:
1. Creating a single shared `GradingMessageLogger` instance per session
2. Passing it to all parallel workers via optional parameter
3. Properly managing ownership and disposal
4. Following established patterns for shared resources

The solution is thread-safe, backward compatible, and provides better log organization by consolidating all messages into a single file per grading session.
