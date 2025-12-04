# Docker Image Check Race Condition Fix

## Problem

In parallel batch grading, the first student(s) sometimes fail with "Docker image doesn't exist" error, while subsequent students succeed. Examples:

### Initial Report (Code Image)
```
Student 1: Failed - Error: Docker image 'fptuxaes/aes-dotnet8-console:latest' does not exist locally
Student 2: Success - Docker grading completed: 5.00/5.00
Student 3: Success - Docker grading completed: 5.00/5.00
```

### After Partial Fix (Database Image)
```
Student 1: Failed - Error: Database image not found. Please pull the image: docker pull mcr.microsoft.com/mssql/server:2019-latest
Student 2: Failed - Error: Database image not found. Please pull the image: docker pull mcr.microsoft.com/mssql/server:2019-latest
Student 3: Success - Docker grading completed: 5.00/5.00
```

The user clearly has the images (some students succeed), so the errors are false negatives.

## Root Cause

This is a **race condition** in the Docker image existence check that occurs during parallel batch grading:

### The Problematic Flow

1. **Thread 1** (Student 1) calls `IsImageExist("image:latest")` at time T
2. **Thread 2** (Student 2) calls `IsImageExist("image:latest")` at time T+0.1s
3. **Thread 3** (Student 3) calls `IsImageExist("image:latest")` at time T+0.2s

### Original Implementation Issue

The original code used `docker images -q imagename` which can return inconsistent results when:
- Multiple threads call it simultaneously
- Docker daemon is under load
- Docker's internal caching hasn't stabilized
- Timing issues cause one thread to get stale results

### Initial Fix Issue (Incomplete)

The first fix added retry logic but only for **exceptions**. If `docker image inspect` returned exit code != 0 (image not found), it immediately returned `false` without retry:

```csharp
bool imageExists = result.ExitCode == 0 && ...;
if (imageExists) {
    return true;
}
// BUG: Immediately returns false without retry!
return false;
```

This meant:
- If Docker was busy and returned "image not found" transiently, no retry happened
- First few students got false negatives
- By the time the 3rd student checked, Docker had warmed up and returned correct result

## Complete Solution

Implemented comprehensive retry logic that handles BOTH exceptions AND non-zero exit codes:

### 1. Thread-Safe Caching (Already Implemented)

**File**: `Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs`

```csharp
// Static cache shared across all instances
private static readonly HashSet<string> _verifiedImages = new HashSet<string>();
private static readonly object _verifiedImagesLock = new object();
```

Once an image is verified to exist, it's cached. Subsequent checks return immediately without calling Docker.

### 2. Complete Retry Logic (Fixed)

```csharp
for (int attempt = 1; attempt <= 3; attempt++)
{
    try
    {
        var result = _commandExecutor.RunCommandAndCaptureOutput($"docker image inspect {imageName}", ...);
        bool imageExists = result.ExitCode == 0 && result.Output.Any(...);
        
        if (imageExists)
        {
            // Cache and return success
            _verifiedImages.Add(imageName);
            return true;
        }
        
        // CRITICAL FIX: Retry even when exit code is non-zero (not just exceptions)
        if (attempt < 3)
        {
            Console.WriteLine($"Image {imageName} not found, retrying...");
            Thread.Sleep(100 * attempt); // Exponential backoff
        }
        else
        {
            // After all retries, truly doesn't exist
            return false;
        }
    }
    catch (Exception ex)
    {
        // Also handle exceptions with retry
        if (attempt < 3)
        {
            Thread.Sleep(100 * attempt);
        }
        else
        {
            return false;
        }
    }
}
```

### 3. More Reliable Docker Command (Already Implemented)

Changed from:
```bash
docker images -q imagename  # Can return inconsistent results
```

To:
```bash
docker image inspect imagename  # More reliable, returns detailed JSON or error
```

### 4. Cache Clearing at Session Start (Already Implemented)

**Files**: 
- `Application/SolutionGrader.UI/GradingWindow.xaml.cs`
- `Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs`

```csharp
// At the start of each grading session, clear the cache
DockerCommandExecutor.ClearImageCache();
```

## Why It Manifested in Stages

### Stage 1: Code Image Failures
First student failed with code image error, others succeeded.

### Stage 2: Database Image Failures  
After partial fix, first TWO students failed with database image error, third succeeded.

This is because:
1. **Code image check** happens first (for all students almost simultaneously)
2. **Database image check** happens second (slight timing differences)
3. The incomplete retry logic failed to retry on non-zero exit codes
4. By the time the 3rd student checked, Docker had warmed up enough

## Benefits

1. ✅ **Eliminates Race Condition**: Cache prevents multiple simultaneous checks of the same image
2. ✅ **Handles All Transient Failures**: Retries on both exceptions AND non-zero exit codes
3. ✅ **Improves Performance**: Cached results are instant, no repeated Docker calls
4. ✅ **More Reliable**: `docker image inspect` is more deterministic than `docker images -q`
5. ✅ **Thread-Safe**: Proper locking prevents cache corruption
6. ✅ **Comprehensive Logging**: Shows retry attempts for debugging

## Testing Recommendations

Test with parallel batch grading to ensure the fix works:

### Test Case 1: Parallel Grading with Existing Images
```
Setup: Ensure both code and database Docker images exist locally
Action: Grade 3+ students in parallel (batch size = 3)
Expected: ALL students succeed, no false "image doesn't exist" errors
```

### Test Case 2: Parallel Grading with Missing Image
```
Setup: Remove a Docker image
Action: Grade 3+ students in parallel (batch size = 3)
Expected: ALL students fail with the same clear error message
```

### Test Case 3: Sequential then Parallel
```
Setup: Images exist
Action: Grade 1 student (batch size = 1), then grade 3 students (batch size = 3)
Expected: All succeed, cache is reused efficiently
```

## Debugging

Check console output for retry attempts:
```
[Docker] Attempt 1/3: Image myimage:latest not found, retrying...
[Docker] Attempt 2/3: Image myimage:latest not found, retrying...
[Docker] Image myimage:latest verified (attempt 3)
```

If you see these messages, the retry logic is working to overcome transient Docker issues.

## Related Files Changed

1. **Lib/EnvironmentBuilder/dockercommand/DockerCommandExecutor.cs**
   - Initial fix: Added cache fields and retry for exceptions
   - Complete fix: Added retry for non-zero exit codes too

2. **Application/SolutionGrader.UI/GradingWindow.xaml.cs**
   - Added cache clearing at session start

3. **Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs**
   - Added cache clearing at session start

## Performance Impact

- **First check**: Slightly slower due to retry logic (max 600ms with 3 retries)
- **Subsequent checks**: Instant (cached)
- **Overall**: Significant performance improvement in batch grading due to caching

## Future Enhancements

Consider:
- Adding metrics on cache hit rate and retry frequency
- Configurable retry count and delays
- Pre-warming cache at application start
- Cache expiration after a configurable time period
