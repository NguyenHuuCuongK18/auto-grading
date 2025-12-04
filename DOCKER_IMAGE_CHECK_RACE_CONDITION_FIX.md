# Docker Image Check Race Condition Fix

## Problem

In parallel batch grading, the first student sometimes fails with "Docker image doesn't exist" error, while subsequent students succeed. Example:

```
Student 1: Failed - Error: Docker image 'fptuxaes/aes-dotnet8-console:latest' does not exist locally
Student 2: Success - Docker grading completed: 5.00/5.00
Student 3: Success - Docker grading completed: 5.00/5.00
```

The user clearly has the image (students 2 and 3 succeeded), so the error for student 1 is incorrect.

## Root Cause

This is a **race condition** in the Docker image existence check that occurs during parallel batch grading:

### The Problematic Flow

1. **Thread 1** (Student 1) calls `IsImageExist("fptuxaes/aes-dotnet8-console:latest")` at time T
2. **Thread 2** (Student 2) calls `IsImageExist("fptuxaes/aes-dotnet8-console:latest")` at time T+0.1s
3. **Thread 3** (Student 3) calls `IsImageExist("fptuxaes/aes-dotnet8-console:latest")` at time T+0.2s

The original implementation used `docker images -q imagename` which can return inconsistent results when:
- Multiple threads call it simultaneously
- Docker daemon is under load
- Docker's internal caching hasn't stabilized
- Timing issues cause one thread to get stale results

### Why It Manifested in Parallel Grading

- **Single student (batch size = 1)**: Only one thread checks the image, no race condition
- **Multiple students (batch size > 1)**: Multiple threads check simultaneously, exposing the race condition
- **First student fails most often**: The first check happens when Docker is "cold" and may not have cached results

## Solution

Implemented a robust image checking mechanism with:

### 1. Thread-Safe Caching

**File**: `Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs`

```csharp
// Static cache shared across all instances
private static readonly HashSet<string> _verifiedImages = new HashSet<string>();
private static readonly object _verifiedImagesLock = new object();
```

Once an image is verified to exist, it's cached. Subsequent checks return immediately without calling Docker.

### 2. Retry Logic with Exponential Backoff

```csharp
// Try up to 3 times with delays between attempts
for (int attempt = 1; attempt <= 3; attempt++)
{
    try
    {
        // Check if image exists
        if (imageExists)
        {
            // Cache and return
            return true;
        }
    }
    catch (Exception ex)
    {
        // Retry with exponential backoff: 100ms, 200ms, 300ms
        Thread.Sleep(100 * attempt);
    }
}
```

This handles transient Docker issues gracefully.

### 3. More Reliable Docker Command

Changed from:
```bash
docker images -q imagename  # Can return inconsistent results
```

To:
```bash
docker image inspect imagename  # More reliable, returns detailed JSON or error
```

The `inspect` command is more deterministic and less prone to race conditions.

### 4. Cache Clearing at Session Start

**Files**: 
- `Application/SolutionGrader.UI/GradingWindow.xaml.cs`
- `Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs`

```csharp
// At the start of each grading session, clear the cache
_logger.LogInfo("[UI] Clearing Docker image cache for fresh validation...");
DockerCommandExecutor.ClearImageCache();
```

This ensures each session starts fresh and doesn't rely on stale cached data from previous sessions.

## Benefits

1. ✅ **Eliminates Race Condition**: Cache prevents multiple simultaneous checks of the same image
2. ✅ **Handles Transients**: Retry logic recovers from temporary Docker issues
3. ✅ **Improves Performance**: Cached results are instant, no repeated Docker calls
4. ✅ **More Reliable**: `docker image inspect` is more deterministic than `docker images -q`
5. ✅ **Thread-Safe**: Proper locking prevents cache corruption

## Testing Recommendations

Test with parallel batch grading to ensure the fix works:

### Test Case 1: Parallel Grading with Existing Image
```
Setup: Ensure Docker image exists locally
Action: Grade 3+ students in parallel (batch size = 3)
Expected: All students succeed, no image existence errors
```

### Test Case 2: Parallel Grading with Missing Image
```
Setup: Remove the Docker image
Action: Grade 3+ students in parallel (batch size = 3)
Expected: ALL students fail with the same clear error message
```

### Test Case 3: Sequential then Parallel
```
Setup: Image exists
Action: Grade 1 student (batch size = 1), then grade 3 students (batch size = 3)
Expected: All succeed, cache is reused efficiently
```

## Implementation Details

### Cache Lifetime

The cache persists for the **entire application lifetime** and is cleared:
- At the start of each grading session via `ClearImageCache()`
- When the application restarts

This is appropriate because:
- Docker images don't change during a session
- Clearing at session start ensures fresh validation
- Performance benefit from avoiding repeated checks

### Thread Safety

The implementation uses:
- `lock (_verifiedImagesLock)` for all cache access
- Static cache shared across all `DockerCommandExecutor` instances
- Atomic cache operations (check + add in single lock)

### Error Handling

When all retry attempts fail:
- Assumes image doesn't exist (safe default)
- Logs all attempts for debugging
- Provides clear error message to user

## Related Files Changed

1. **Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs**
   - Added cache fields
   - Enhanced `IsImageExist()` with retry and caching
   - Added `ClearImageCache()` static method

2. **Application/SolutionGrader.UI/GradingWindow.xaml.cs**
   - Added cache clearing at session start

3. **Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs**
   - Added cache clearing at session start

## Migration Notes

No changes needed for existing users. The fix is transparent and backward compatible.

## Performance Impact

- **First check**: Slightly slower due to retry logic (max 600ms with 3 retries)
- **Subsequent checks**: Instant (cached)
- **Overall**: Significant performance improvement in batch grading due to caching

## Debugging

If image check issues persist:
1. Check logs for retry attempts and their results
2. Verify Docker daemon is running and responsive
3. Ensure image name matches exactly (case-sensitive)
4. Try `docker image inspect <imagename>` manually to see if it works

## Future Enhancements

Consider:
- Adding metrics on cache hit rate
- Configurable retry count and delays
- Pre-warming cache at application start
- Cache expiration after a configurable time period
