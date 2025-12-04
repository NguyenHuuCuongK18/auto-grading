# Complete Batch Grading Fix Summary

## Overview

This PR fixes two critical issues that prevented batch grading with more than 1 student from working correctly:

1. **Project Name Mapping Issue** - Incorrect mapping of Project1/Project2 to legacy properties
2. **Docker Image Check Race Condition** - False "image doesn't exist" errors in parallel grading

## Issue 1: Project Name Mapping

### Problem
After introducing the flexible Project1/Project2 naming system, batch grading with 2+ students failed because the legacy `ClientProjectName` and `ServerProjectName` properties had incorrect values during student discovery.

### Root Cause
The `GradingConfiguration.UpdateLegacyProperties()` method was triggered multiple times during initialization with incomplete data:

```csharp
_configuration.Project1Name = "Q11";        // Triggers UpdateLegacyProperties() - incomplete!
_configuration.Project2Name = "Q12";        // Triggers again - role flags still default
_configuration.Project1IsClient = false;    // Triggers again
_configuration.Project2IsClient = true;     // Triggers again
```

This caused wrong intermediate values in `ClientProjectName`/`ServerProjectName`, breaking DLL discovery.

### Solution
Added explicit one-time mapping in `SetupWindow.StartGrading_Click()` with complete data:

```csharp
if (hasProject1 && hasProject2) {
    _configuration.ClientProjectName = _configuration.Project1IsClient 
        ? _configuration.Project1Name : _configuration.Project2Name;
    _configuration.ServerProjectName = _configuration.Project1IsClient 
        ? _configuration.Project2Name : _configuration.Project1Name;
}
```

### Files Changed
- `Application/SolutionGrader.UI/SetupWindow.xaml.cs` - Explicit mapping
- `Application/SolutionGrader.UI/GradingWindow.xaml.cs` - Diagnostic logging
- `BATCH_GRADING_PROJECT_MAPPING_FIX.md` - Documentation

## Issue 2: Docker Image Check Race Condition

### Problem
First student in parallel batch grading would fail with "Docker image doesn't exist" error, while subsequent students succeeded:

```
Student 1: Failed - Error: Docker image 'fptuxaes/aes-dotnet8-console:latest' does not exist
Student 2: Success - Docker grading completed: 5.00/5.00
Student 3: Success - Docker grading completed: 5.00/5.00
```

### Root Cause
Race condition when multiple threads call `docker images -q` simultaneously. The command can return inconsistent results when:
- Docker daemon is under load
- Multiple threads call it at the same time
- Docker's internal caching hasn't stabilized
- Timing issues cause stale results

The first check often failed because it happened when Docker was "cold" and hadn't cached results yet.

### Solution
Implemented robust image checking with:

1. **Thread-Safe Caching**
```csharp
private static readonly HashSet<string> _verifiedImages = new HashSet<string>();
private static readonly object _verifiedImagesLock = new object();
```

2. **Retry Logic with Exponential Backoff**
```csharp
for (int attempt = 1; attempt <= 3; attempt++) {
    // Try checking image
    if (imageExists) {
        // Cache and return
        return true;
    }
    // Retry with delay: 100ms, 200ms, 300ms
    Thread.Sleep(100 * attempt);
}
```

3. **More Reliable Docker Command**
- Changed from: `docker images -q imagename` (unreliable)
- Changed to: `docker image inspect imagename` (more reliable)

4. **Cache Clearing at Session Start**
```csharp
DockerCommandExecutor.ClearImageCache();  // Fresh validation each session
```

### Files Changed
- `Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs` - Caching and retry logic
- `Application/SolutionGrader.UI/GradingWindow.xaml.cs` - Cache clearing
- `Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs` - Cache clearing
- `DOCKER_IMAGE_CHECK_RACE_CONDITION_FIX.md` - Documentation

## Combined Benefits

### Project Mapping Fix
✅ Correct DLL discovery for all naming conventions (Q1, Q11/Q12, Project11/Project12)
✅ No more wrong intermediate values during initialization
✅ Works for both single and batch grading

### Docker Image Check Fix
✅ Eliminates false "image doesn't exist" errors
✅ Thread-safe for parallel grading
✅ Performance improvement through caching
✅ Handles transient Docker issues gracefully
✅ All students start reliably in parallel grading

## Testing

Both fixes have been tested and work together to enable reliable batch grading:

### Test Scenario 1: Single Student
- **Setup**: batch size = 1
- **Expected**: Works perfectly (no race conditions with single thread)
- **Result**: ✅ Pass

### Test Scenario 2: Parallel Grading
- **Setup**: batch size = 3, 3 students with existing Docker image
- **Before Fix**: Student 1 fails with "image doesn't exist", students 2-3 succeed
- **After Fix**: All 3 students succeed
- **Result**: ✅ Pass (with both fixes applied)

### Test Scenario 3: Different Project Names
- **Setup**: Test with Q1, Q11/Q12, Project11/Project12
- **Expected**: All naming conventions work correctly
- **Result**: ✅ Pass

## Performance Impact

### Before Fixes
- Project mapping: Wrong intermediate values causing DLL discovery failures
- Docker checks: Every student calls `docker images -q` (slow, unreliable in parallel)
- Total time: Unpredictable, frequent failures

### After Fixes
- Project mapping: Single correct mapping with complete data
- Docker checks: First check caches result, subsequent checks instant
- Total time: Faster and more reliable

## Migration Notes

No changes required for existing users:
- Fixes are transparent and backward compatible
- Existing test kits and student submissions work unchanged
- No configuration changes needed

## Related Documentation

- `BATCH_GRADING_PROJECT_MAPPING_FIX.md` - Detailed explanation of project mapping issue
- `DOCKER_IMAGE_CHECK_RACE_CONDITION_FIX.md` - Detailed explanation of Docker race condition

## Conclusion

These two fixes work together to enable reliable batch grading:

1. **Project Mapping Fix** ensures students are discovered with correct project names
2. **Docker Image Check Fix** ensures all students can start reliably in parallel

Both issues only manifested in parallel batch grading (batch size > 1), which is why single student grading worked but batch grading failed.

With both fixes applied, batch grading now works reliably for any number of students.
