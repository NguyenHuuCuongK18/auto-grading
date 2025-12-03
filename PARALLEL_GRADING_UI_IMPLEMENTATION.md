# Parallel Grading UI Implementation

This document describes the implementation of configurable parallel grading and index selection in the SolutionGrader.UI application.

## Overview

The UI now supports the same parallel grading capabilities as the CLI:
1. **Configurable Parallelism**: Grade multiple students simultaneously
2. **Index Range Selection**: Grade students from index A to index B (for incident recovery)
3. **Port Management**: Each parallel student gets unique incrementing ports
4. **Thread-Safe Execution**: Uses SemaphoreSlim for concurrency control

## UI Changes

### SetupWindow (Initial Configuration)

Added parallel grading configuration section with three inputs:
- **Parallel Students** (default: 1): Number of students to grade simultaneously
- **Start Index** (default: 0): 0-based index to start grading from
- **End Index** (default: -1): 0-based index to end grading at (-1 means all)

Validation ensures:
- Parallel students ≥ 1
- Start index ≥ 0
- End index ≥ -1 or ≥ start index

### GradingWindow (Runtime Configuration)

The grading window now displays parallel grading configuration at the top:
- Shows current values for MaxParallelStudents, StartIndex, EndIndex
- Allows runtime updates before starting grading
- Clear tooltips explain each setting

## Implementation Details

### ApplyIndexRange Method
```csharp
private List<StudentSolution> ApplyIndexRange(List<StudentSolution> students, int startIndex, int endIndex)
```
Filters the student list based on configured indices:
- Returns students from startIndex to endIndex (inclusive)
- Handles edge cases (out of bounds, -1 for "all remaining")
- Used before starting grading to select subset

### StartGradingAsync Method (Enhanced)

**Sequential Mode (MaxParallelStudents ≤ 1)**:
```csharp
foreach (var student in studentsToGrade)
{
    await GradeStudentAsync(student, portOffset: 0, ct);
}
```

**Parallel Mode (MaxParallelStudents > 1)**:
```csharp
var semaphore = new SemaphoreSlim(maxParallelStudents);
var tasks = students.Select(async (student, index) =>
{
    await semaphore.WaitAsync(ct);
    try
    {
        var portOffset = index % maxParallelStudents;
        await GradeStudentAsync(student, portOffset, ct);
    }
    finally
    {
        semaphore.Release();
    }
}).ToList();

await Task.WhenAll(tasks);
```

### Port Offset Calculation

For parallel grading, each student gets unique ports:
```csharp
var portOffset = index % maxParallelStudents;
CodeContainerInternalPort = basePort + portOffset;
CodeContainerHostPort = basePort + portOffset;
```

Example with basePort=8000, maxParallel=3:
- Student 0: ports 8000/8000
- Student 1: ports 8001/8001
- Student 2: ports 8002/8002
- Student 3: ports 8000/8000 (reuses after batch completes)

**CRITICAL**: Internal and external ports MUST match for libpcap/npcap network monitoring.

## Architecture Alignment

The UI implementation mirrors the CLI's proven pattern:

```
Both UI and CLI:
1. Read configuration (including parallel settings)
2. Discover students from Submit folder
3. Apply index range filtering (ApplyIndexRange)
4. Execute grading (sequential OR parallel)
   - Sequential: simple foreach loop
   - Parallel: SemaphoreSlim + Task.WhenAll
5. Calculate port offsets for each student
6. Delegate to shared DockerGradingService
```

This ensures **identical behavior** between UI and CLI modes.

## Usage Examples

### Example 1: Sequential Grading
- Parallel Students: 1
- Start Index: 0
- End Index: -1
Result: Grades all students one at a time (original behavior)

### Example 2: Parallel Grading
- Parallel Students: 3
- Start Index: 0
- End Index: -1
Result: Grades up to 3 students simultaneously

### Example 3: Resume After Incident
- Parallel Students: 2
- Start Index: 5
- End Index: 10
Result: Grades students [5, 6, 7, 8, 9, 10] with 2 in parallel

### Example 4: Test Single Student
- Parallel Students: 1
- Start Index: 2
- End Index: 2
Result: Grades only the student at index 2

## Thread Safety

Parallel execution uses multiple thread-safety mechanisms:
1. **SemaphoreSlim**: Limits concurrent grading operations
2. **lock** statement: Thread-safe result writing
3. **ObservableCollection** updates: Dispatcher.Invoke for UI updates
4. **CancellationToken**: Coordinated cancellation across threads

## Testing Considerations

When testing parallel grading:
1. **Run as sudo/admin**: Required for libpcap network monitoring
2. **Check port availability**: Ensure enough ports for maxParallel students
3. **Monitor resources**: Parallel grading uses more CPU/memory/network
4. **Verify container names**: Each student gets unique container names with student code suffix
5. **Check logs**: Parallel grading produces interleaved logs

## Error Handling

The implementation preserves original error handling:
- Individual student failures don't stop the batch
- Cancellation (pause/stop) works across all parallel tasks
- OperationCanceledException handled gracefully
- Results written incrementally (after each student)

## Performance

Parallel grading performance depends on:
- **System resources**: CPU cores, memory, network bandwidth
- **Test complexity**: More complex tests take longer
- **Container overhead**: Docker container startup time
- **Network monitoring**: libpcap capture overhead

Recommended maxParallel values:
- 1-2: Safe for most systems
- 3-4: Requires good hardware (4+ cores, 8GB+ RAM)
- 5+: Dedicated grading server recommended

## Compatibility

The implementation is compatible with:
- All existing test kits
- Both client/server and standalone projects
- Network monitoring with libpcap (Linux) or npcap (Windows)
- Database grading with shared SQL Server container
- All grading modes (Docker, local, etc.)

## Future Enhancements

Potential improvements:
1. **Auto-detect optimal parallelism**: Based on system resources
2. **Dynamic port allocation**: Avoid hardcoded port ranges
3. **Resource monitoring**: Show CPU/memory usage during grading
4. **Batch progress**: Overall progress bar for parallel batches
5. **Per-student logs**: Separate log tabs for each parallel student
