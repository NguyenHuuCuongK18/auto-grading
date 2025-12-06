# Manual Verification Guide for Batch Grading Message Logger Fix

This guide explains how to manually verify that the file access conflict issue has been resolved.

## Prerequisites

1. Build the solution:
   ```bash
   cd /home/runner/work/auto-grading/auto-grading
   dotnet build SolutionGrader.sln --configuration Release
   ```

2. Prepare test data:
   - At least 5 student submissions
   - Valid test kit(s)
   - Proper configuration

## Test Scenarios

### Scenario 1: Parallel Batch Grading (Primary Test)

**Purpose**: Verify no file access conflicts when multiple students are graded simultaneously.

**Steps**:
1. Open `SolutionGrader.UI`
2. Set `Max Parallel Students` to `5` or higher
3. Load at least 5 students
4. Click "Start All" to begin grading
5. Monitor the console log and grading progress

**Expected Results**:
- ✅ Grading starts successfully for all students
- ✅ No "file is being used by another process" errors
- ✅ All workers log messages without conflicts
- ✅ Single log file created: `GradingLogs/GradingMessages_YYYYMMDD_HHMMSS.txt`
- ✅ Single Excel file created: `GradingLogs/GradingMessages_YYYYMMDD_HHMMSS.xlsx`
- ✅ All student messages present in both files

**Console Log Indicators (Success)**:
```
[Message Logger] Initialized SHARED GradingMessageLogger for batch grading session
[Worker-0] [1/5] Starting grading for: Student1 (Paper 1)
[Worker-1] [2/5] Starting grading for: Student2 (Paper 1)
[Worker-2] [3/5] Starting grading for: Student3 (Paper 1)
[GradingOrchestrationService] Using shared GradingMessageLogger for batch grading
[GradingOrchestrationService] Using shared GradingMessageLogger for batch grading
...
[Message Logger] Shared GradingMessageLogger disposed and logs exported to Excel
```

**Failure Indicators (If Fix Doesn't Work)**:
```
System.IO.IOException: The process cannot access the file 
'...\GradingLogs\GradingMessages_20251207_054818.txt' 
because it is being used by another process.
```

### Scenario 2: Sequential Grading (Backward Compatibility)

**Purpose**: Verify the fix doesn't break sequential grading.

**Steps**:
1. Set `Max Parallel Students` to `1`
2. Load students
3. Click "Start All"

**Expected Results**:
- ✅ Grading works as before
- ✅ Single log file created
- ✅ All messages logged correctly
- ✅ No regressions

### Scenario 3: Large Batch Test (Stress Test)

**Purpose**: Verify the fix scales to large batches.

**Steps**:
1. Set `Max Parallel Students` to `10` or higher
2. Load 50+ students (or as many as available)
3. Click "Start All"

**Expected Results**:
- ✅ No file access conflicts across multiple batches
- ✅ All messages logged correctly
- ✅ Single consolidated log file per session
- ✅ Excel export contains all messages

### Scenario 4: Multiple Sessions Test

**Purpose**: Verify each session creates its own log file.

**Steps**:
1. Complete a grading session (any number of students)
2. Note the log file timestamp
3. Wait at least 1 second
4. Start a new grading session
5. Check the log files

**Expected Results**:
- ✅ Two separate log files with different timestamps
- ✅ Each session has its own messages
- ✅ No cross-contamination between sessions

## Verification Checklist

### File System Verification

After a successful grading session, verify:

```
Results/
  GradingLogs/
    ✅ GradingMessages_YYYYMMDD_HHMMSS.txt     (ONE file per session)
    ✅ GradingMessages_YYYYMMDD_HHMMSS.xlsx    (ONE Excel export per session)
  1/  (Paper number)
    student/
      Student1/
        ✅ (Student-specific results)
      Student2/
        ✅ (Student-specific results)
      ...
```

### Log File Content Verification

Open the text log file and verify:

1. **Header Present**:
   ```
   ====================================================================================================
   GRADING SESSION LOG - 2025-12-07 05:48:18
   ====================================================================================================
   ```

2. **All Students Logged**: Search for each student code in the file
   ```
   [INFO] Student1: Starting grading...
   [INFO] Student2: Starting grading...
   [INFO] Student3: Starting grading...
   ```

3. **No Error Messages**: No file access errors in the log

4. **Session Completion**: Footer present
   ```
   ====================================================================================================
   SESSION ENDED - 2025-12-07 05:52:30
   Total messages: 150
   ====================================================================================================
   ```

### Excel File Verification

Open the Excel file (`GradingMessages_*.xlsx`) and verify:

1. **Summary Sheet**: Contains correct message counts
2. **All Messages Sheet**: All students' messages present
3. **Student Errors Sheet**: Student-specific errors logged
4. **Grader Errors Sheet**: System errors (if any)
5. **TestKit Errors Sheet**: Test kit issues (if any)

## Troubleshooting

### If File Access Errors Still Occur

1. **Check Code Changes**:
   ```bash
   git status
   git diff HEAD
   ```

2. **Verify Shared Logger Creation**:
   - Search console for: `[Message Logger] Initialized SHARED GradingMessageLogger`
   - Should appear ONCE per session

3. **Verify Shared Logger Usage**:
   - Search console for: `[GradingOrchestrationService] Using shared GradingMessageLogger`
   - Should appear for EACH student

4. **Check for Multiple Logger Instances**:
   - Search console for: `[GradingOrchestrationService] Created new GradingMessageLogger`
   - Should NOT appear when using parallel grading

### If Logs Are Missing Messages

1. Check if logger was disposed prematurely
2. Verify `AutoFlush = true` in GradingMessageLogger
3. Check for exceptions during logging

## Performance Impact

The fix should have **no negative performance impact**. In fact, it may be slightly more efficient:

- **Before**: N logger instances, N file handles, N StreamWriters
- **After**: 1 logger instance, 1 file handle, 1 StreamWriter with locking

## Rollback Instructions

If the fix causes issues, you can revert by:

1. Remove the `sharedMessageLogger` parameter from `StartGradingAsync` calls
2. Remove shared logger creation in `GradingWindow.StartGradingAsync`
3. Revert to creating logger in `GradingOrchestrationService.StartGradingAsync`

However, this will bring back the file access conflict issue in parallel grading.

## Success Criteria

The fix is considered successful if:

1. ✅ **No File Access Errors**: Zero "file is being used by another process" errors
2. ✅ **All Messages Logged**: Every student's messages appear in the log
3. ✅ **Single Log File**: One consolidated log per session
4. ✅ **Parallel Grading Works**: Multiple students can be graded simultaneously
5. ✅ **Backward Compatible**: Sequential grading still works
6. ✅ **Proper Cleanup**: Logger disposed and Excel exported at session end

## Related Documentation

- `BATCH_GRADING_MESSAGE_LOGGER_FIX.md` - Technical implementation details
- `BATCH_GRADING_FIX.md` - Original batch grading implementation
- `PARALLEL_GRADING_FIX.md` - Parallel execution architecture

## Support

If you encounter any issues during verification:

1. Check console logs for error messages
2. Review the log files in `Results/GradingLogs/`
3. Verify the build completed successfully
4. Ensure test data is valid and accessible
