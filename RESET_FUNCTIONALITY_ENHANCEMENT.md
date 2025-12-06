# Reset Functionality Enhancement

## Overview

Enhanced the reset functionality to comprehensively clean up all result folders when a student is reset after a canceled or paused grading operation. This prevents interference from partial results when re-grading.

## Problem Statement

When grading is canceled or paused (either intentionally via the Pause button or due to an unexpected interruption), partial result files may be left behind in various locations:

1. **Result folders** in the SaveResultFolderPath
2. **Log folders** specific to the student
3. **Temporary files** created during grading
4. **Partial Excel files** with incomplete data

These leftover files can interfere with subsequent grading attempts, potentially causing:
- Incorrect result aggregation
- Conflicting data in Excel files
- Confusion about which results are current
- Inconsistent grading outcomes

## Solution

The `ResetStudent()` method has been enhanced to perform comprehensive cleanup of all student-related files and folders.

### Cleanup Locations

The reset operation now cleans up files in the following locations:

#### 1. Paper-Organized Result Folder (Current Structure)
**Location**: `{SaveResultFolderPath}/{PaperNo}/student/{StudentCode}/`

This is the primary location where grading results are stored, organized by paper number.

**Contains**:
- `OverallSummary.xlsx` - Student's overall grading summary
- `{TestCase}/` - Folders for each test case
  - `GradeDetail.xlsx` - Detailed grading information per test case
  - Other test case specific files

#### 2. Legacy Result Folder (Old Structure)
**Location**: `{SaveResultFolderPath}/student/{StudentCode}/`

Supports older folder structures that may exist from previous versions.

#### 3. Student-Specific Log Folders
**Location**: `{SaveResultFolderPath}/Logs/Log_{StudentCode}_{Date}_Paper{PaperNo}/`

Log folders created during grading that contain:
- Grading execution logs
- Debug information
- Error messages
- Timing information

**Pattern Matching**: Uses wildcard pattern `Log_{StudentCode}_*` to find all log folders for the student across different grading dates.

#### 4. Temporary Files
**Location**: Anywhere under `{SaveResultFolderPath}` (recursive search)

**Pattern**: `*{StudentCode}*.tmp`

Temporary files that may be created during:
- Excel file writes
- Result aggregation
- Intermediate computations

### User Experience Enhancements

#### Confirmation Dialogs

Both "Reset All" and "Reset Selected" now show confirmation dialogs:

**Reset All Dialog**:
```
This will reset all {N} student(s) and DELETE their result folders.

This ensures a clean re-grade without interference from previous attempts.

Are you sure you want to continue?
```

**Reset Selected Dialog**:
```
This will reset {N} selected student(s) and DELETE their result folders.

This ensures a clean re-grade without interference from previous attempts.

Are you sure you want to continue?
```

#### Completion Feedback

After successful reset, users see a confirmation message:
```
Reset complete!

{N} student(s) are ready for re-grading.
```

#### Detailed Logging

The reset operation provides comprehensive logging:

```
Resetting {N} selected students and deleting result folders...
Deleted paper-organized result folder for {StudentCode} (Paper {PaperNo})
Deleted legacy result folder for {StudentCode}
Deleted log folder: Log_{StudentCode}_20231206_Paper1
Deleted temp file: {StudentCode}_intermediate.tmp
Reset complete for {StudentCode}: Deleted 3 folder(s). Student is ready for re-grading.
{N} selected student statuses reset and result folders deleted
```

### Implementation Details

#### Error Handling

The reset operation is designed to be resilient:

1. **Individual failures don't abort the process**: If one folder fails to delete, the reset continues with other folders
2. **All exceptions are logged**: Users can see what succeeded and what failed
3. **Graceful degradation**: Missing folders are not treated as errors

#### Thread Safety

The reset operation is safe to call from the UI thread and:
- Uses try-catch blocks around each deletion
- Doesn't hold locks on directories
- Can handle files in use (logs warning instead of failing)

#### Performance

The reset operation is efficient:
- **Paper-organized folder**: Direct path lookup (fast)
- **Legacy folder**: Direct path lookup (fast)
- **Log folders**: Pattern-based search in single directory (fast)
- **Temp files**: Recursive search (slower, but temp files are rare)

### Code Structure

```csharp
private void ResetStudent(StudentSolution student)
{
    // 1. Reset in-memory student state
    student.Status = GradingStatus.Not_Run;
    student.Mark = 0;
    // ... other properties
    
    int foldersDeleted = 0;
    
    // 2. Delete paper-organized result folder
    if (Directory.Exists(paperResultFolder))
    {
        Directory.Delete(paperResultFolder, true);
        foldersDeleted++;
    }
    
    // 3. Delete legacy result folder
    if (Directory.Exists(legacyResultFolder))
    {
        Directory.Delete(legacyResultFolder, true);
        foldersDeleted++;
    }
    
    // 4. Delete student log folders (pattern matching)
    var studentLogFolders = Directory.GetDirectories(logsFolder, $"Log_{student.StudentCode}_*");
    foreach (var logFolder in studentLogFolders)
    {
        Directory.Delete(logFolder, true);
        foldersDeleted++;
    }
    
    // 5. Delete temp files (recursive search)
    var tempFiles = Directory.GetFiles(saveFolder, $"*{student.StudentCode}*.tmp", SearchOption.AllDirectories);
    foreach (var tempFile in tempFiles)
    {
        File.Delete(tempFile);
    }
    
    // 6. Log completion status
    _logger.LogInfo($"Reset complete: Deleted {foldersDeleted} folder(s)");
}
```

## Usage Scenarios

### Scenario 1: Paused Grading

**Situation**: User pauses grading after 10 students have completed.

**Action**: 
1. Click "Reset Selected" on remaining students
2. Confirm the dialog
3. Click "Start Selected" to re-grade with fresh state

**Result**: Remaining students are re-graded without any leftover data from the paused attempt.

### Scenario 2: Abrupt Cancellation

**Situation**: Application crashes or user closes window during grading.

**Action**:
1. Restart application
2. Select affected students (those showing "Paused" or "InProgress")
3. Click "Reset Selected"
4. Confirm and re-grade

**Result**: All partial results cleaned up, students ready for clean re-grading.

### Scenario 3: Retry Failed Students

**Situation**: Some students failed due to configuration issues.

**Action**:
1. Fix configuration issues
2. Select failed students
3. Click "Reset Selected"
4. Re-grade after reset

**Result**: Fresh grading attempt without old failure data.

### Scenario 4: Clean Slate

**Situation**: Want to re-grade all students from scratch (e.g., after test kit changes).

**Action**:
1. Click "Reset All"
2. Confirm the dialog
3. All students ready for fresh grading

**Result**: Complete cleanup of all result folders, ready for new grading session.

## Safety Considerations

### When Reset is ALLOWED
- ✅ No grading is currently in progress (`_isRunning == false`)
- ✅ User explicitly confirms the action
- ✅ Files can be deleted (no permission issues)

### When Reset is BLOCKED
- ❌ Grading is currently running (`_isRunning == true`)
  - Shows message: "Cannot reset while grading is in progress."
  - User must pause or wait for completion first

### Data Safety
- **No undo**: Once reset is confirmed, folder deletion is permanent
- **Confirmation required**: Two-step process (button + dialog) prevents accidental resets
- **Selective reset**: Can reset individual students without affecting others
- **Grading logic untouched**: Reset only affects stored results, not grading algorithms

## Testing Recommendations

### Test Case 1: Reset After Normal Completion
1. Grade a student to completion
2. Reset the student
3. Verify all result folders deleted
4. Re-grade and verify results are identical

### Test Case 2: Reset After Pause
1. Start grading multiple students
2. Click "Pause" mid-way
3. Reset paused students
4. Verify partial results deleted
5. Re-grade and verify clean execution

### Test Case 3: Reset After Cancel
1. Start grading
2. Close window (cancel)
3. Reopen application
4. Reset affected students
5. Verify all folders cleaned
6. Re-grade successfully

### Test Case 4: Reset Multiple Students
1. Grade 10 students
2. Select 5 students
3. Click "Reset Selected"
4. Verify only those 5 have folders deleted
5. Other 5 remain intact

### Test Case 5: Reset with Missing Folders
1. Manually delete some result folders
2. Click "Reset All"
3. Verify operation completes without errors
4. Check logs show "No existing result folders found"

## Troubleshooting

### Issue: "Failed to delete result folder"

**Possible Causes**:
- Folder is locked by another process (Excel file open)
- Insufficient permissions
- Folder doesn't exist (not an issue, just logged)

**Solution**:
- Close any Excel files from the result folder
- Run application with appropriate permissions
- Check logs to see specific error message

### Issue: Some files remain after reset

**Possible Causes**:
- Files in use by another process
- Non-standard file naming (doesn't match patterns)

**Solution**:
- Close other applications using the files
- Manually delete remaining files if needed
- Files won't interfere if they don't match student code pattern

### Issue: Reset button disabled

**Possible Causes**:
- Grading is currently in progress

**Solution**:
- Click "Pause" to stop grading
- Wait for current student to complete
- Then reset will be enabled

## Benefits

1. **Clean Re-grading**: Ensures no interference from previous attempts
2. **User Confidence**: Clear feedback about what's being deleted
3. **Robust Operation**: Handles edge cases gracefully
4. **Comprehensive Cleanup**: Removes all traces of previous grading attempts
5. **Safe by Default**: Requires confirmation to prevent accidental data loss
6. **Detailed Logging**: Full visibility into reset operations

## Conclusion

The enhanced reset functionality provides a reliable way to clean up after canceled or failed grading operations, ensuring that re-grading attempts start with a completely clean slate. The comprehensive cleanup, combined with user-friendly confirmations and detailed logging, makes the reset operation both powerful and safe to use.
