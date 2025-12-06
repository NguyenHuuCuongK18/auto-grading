# Multi-Threading Architecture and CPU Core Utilization

This document explains how the auto-grading system uses multi-threading and multiple CPU cores to optimize grading performance.

## Current Multi-Threading Implementation

### ✅ Already Implemented: Parallel Student Grading

The system **already uses multi-threading** extensively through parallel student grading:

```csharp
// Configure parallelism (example: 10 students in parallel)
MaxParallelStudents = 10;

// System automatically creates 10 worker threads
// Each thread grades one student concurrently
// When a thread finishes, it immediately picks up the next student (continuous batch processing)
```

### Thread Architecture

```
CPU Cores: [Core 1] [Core 2] [Core 3] [Core 4] [Core 5] [Core 6] [Core 7] [Core 8]
           ↓        ↓        ↓        ↓        ↓        ↓        ↓        ↓
Workers:   Thread1  Thread2  Thread3  Thread4  Thread5  Thread6  Thread7  Thread8
           ↓        ↓        ↓        ↓        ↓        ↓        ↓        ↓
Students:  Student1 Student2 Student3 Student4 Student5 Student6 Student7 Student8
           
Queue:     [Student9] [Student10] [Student11] ... [Student100]
           ↑
           Producer thread feeds students into queue
           Workers pull from queue as they finish current student
```

### How It Works

1. **Producer Thread:** Feeds students into a Channel (queue)
2. **Worker Threads:** MaxParallelStudents workers continuously pull from queue
3. **OS Thread Scheduler:** Distributes worker threads across available CPU cores
4. **Continuous Processing:** When a worker finishes, it immediately grabs next student

### Example: 8-Core System Grading 100 Students

```
Configuration: MaxParallelStudents = 10
CPU Cores: 8 physical cores
Thread Distribution: OS scheduler maps 10 threads across 8 cores

Time 0:00  - Students 1-10 start grading (10 threads active)
Time 0:02  - Student 1 finishes → Thread picks up Student 11 immediately
Time 0:03  - Student 2 finishes → Thread picks up Student 12 immediately
Time 0:05  - Student 3 finishes → Thread picks up Student 13 immediately
...
Time 2:00  - All 100 students complete
```

**Key Point:** The system continuously keeps 10 students being graded at all times until the queue is empty.

---

## CPU Core Utilization Strategy

### Determining Optimal MaxParallelStudents

The optimal parallelism depends on workload characteristics:

#### 1. CPU-Bound Workload (e.g., code compilation, complex computation)
```
Optimal = Number of CPU cores
Example: 8-core system → MaxParallelStudents = 8
```

#### 2. I/O-Bound Workload (e.g., Docker operations, database queries, network traffic)
```
Optimal = Number of CPU cores × 2 to 4
Example: 8-core system → MaxParallelStudents = 16 to 32
```

#### 3. Mixed Workload (typical for auto-grading)
```
Optimal = Number of CPU cores × 1.5 to 2
Example: 8-core system → MaxParallelStudents = 12 to 16
```

### Auto-Grading Workload Characteristics

Auto-grading is **primarily I/O-bound**:

| Phase | Type | CPU Usage | Duration |
|-------|------|-----------|----------|
| Docker container startup | I/O-bound | Low (10-20%) | 1-3 seconds |
| File copying to container | I/O-bound | Low (10-30%) | 0.5-2 seconds |
| Test execution (in container) | Mixed | Medium (30-60%) | 5-30 seconds |
| Network monitoring | I/O-bound | Low (5-15%) | Continuous |
| Database operations | I/O-bound | Low (10-20%) | 1-5 seconds |
| Result writing | I/O-bound | Low (5-10%) | 0.5-1 second |

**Conclusion:** For most systems, set `MaxParallelStudents = CPU_cores × 1.5 to 2`

### Recommended Settings by System

| System | CPU Cores | Recommended MaxParallelStudents | Expected Throughput |
|--------|-----------|--------------------------------|---------------------|
| Laptop | 4 cores | 6-8 students | 30-40 students/hour |
| Desktop | 8 cores | 12-16 students | 60-80 students/hour |
| Workstation | 16 cores | 24-32 students | 120-160 students/hour |
| Server | 32+ cores | 48-64 students | 240-320 students/hour |

---

## Current Multi-Threading Components

### 1. ✅ Parallel Student Grading (Student-Level Parallelism)
**Status:** Fully implemented with continuous batch processing

```csharp
// CLI: Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs
MaxParallelStudents = 10;  // Grade 10 students concurrently

// Creates:
// - 1 producer thread (feeds students into queue)
// - 10 worker threads (grade students)
// - Total: 11 threads for student-level parallelism
```

### 2. ✅ Async I/O Operations (Non-Blocking I/O)
**Status:** Already implemented throughout codebase

```csharp
// Docker operations use async I/O (don't block threads)
await _dockerExecutor.StartContainerAsync(containerName);
await _dockerExecutor.CopyFileToContainerAsync(sourcePath, containerPath);

// File operations use async I/O
await File.ReadAllTextAsync(path);
await File.WriteAllTextAsync(path, content);

// Network operations use async I/O
await _networkMonitor.StartAsync();
```

**Benefit:** Threads don't block during I/O, allowing OS to schedule other work

### 3. ✅ Thread-Safe Data Structures
**Status:** Already implemented

```csharp
// ConcurrentBag for thread-safe result collection
var results = new ConcurrentBag<StudentGradingResult>();

// Channel for thread-safe producer-consumer queue
var channel = Channel.CreateBounded<StudentInfo>(...);

// Mutex for system-wide port allocation synchronization
var mutex = new Mutex(false, "AutoGrading_PortAllocator");
```

### 4. ✅ Parallel-Safe Services
**Status:** Each student gets isolated instances

```csharp
// Each worker thread creates its own:
IRunContext runContext = new RunContext();  // Thread-local
INetworkMonitorService networkMonitor = new NetworkMonitorService(runContext);  // Thread-local
DockerGradingService gradingService = new DockerGradingService(networkMonitor, runContext);  // Thread-local

// No shared state between students = no contention
```

---

## Potential Additional Optimizations

### 🔧 Option 1: Parallel Test Case Execution (Within Student)

**Current:** Test cases run sequentially within each student
```
Student 1 Thread:
  ├─ Test Case 1 (5 seconds)
  ├─ Test Case 2 (5 seconds)  ← Waits for TC1 to finish
  └─ Test Case 3 (5 seconds)  ← Waits for TC2 to finish
  Total: 15 seconds
```

**Potential:** Run independent test cases in parallel
```
Student 1 Thread spawns 3 sub-threads:
  ├─ Test Case 1 (5 seconds) ─┐
  ├─ Test Case 2 (5 seconds) ─┼─ All run concurrently
  └─ Test Case 3 (5 seconds) ─┘
  Total: 5 seconds
```

**Challenges:**
- Port conflicts: Each test case needs unique ports
- Container conflicts: Multiple containers per student
- Resource contention: Increased Docker load
- Complexity: Significant code changes required

**Recommendation:** ⚠️ **High complexity, uncertain benefit** - Only implement if profiling shows test case execution is the bottleneck

### 🔧 Option 2: Parallel DLL Scanning (During Discovery)

**Current:** Scan student DLL files sequentially
```
Discovery Phase:
  Student 1 → Scan for DLLs (0.1s)
  Student 2 → Scan for DLLs (0.1s)
  Student 3 → Scan for DLLs (0.1s)
  ...
  Total: N × 0.1s
```

**Potential:** Parallel DLL scanning with `Parallel.ForEachAsync`
```csharp
await Parallel.ForEachAsync(students, new ParallelOptions 
{ 
    MaxDegreeOfParallelism = Environment.ProcessorCount 
}, async (student, ct) =>
{
    student.ServerDllPath = await Task.Run(() => FindDll(student.SolutionPath, "Server"));
    student.ClientDllPath = await Task.Run(() => FindDll(student.SolutionPath, "Client"));
});
```

**Benefit:** Faster student discovery (useful for 100+ students)

**Recommendation:** ✅ **Low complexity, proven benefit** - Good optimization for large student counts

### 🔧 Option 3: ThreadPool Tuning

**Current:** Uses default .NET ThreadPool settings

**Potential:** Tune ThreadPool for high concurrency
```csharp
// Increase minimum threads to reduce spin-up latency
ThreadPool.SetMinThreads(
    workerThreads: MaxParallelStudents * 2,
    completionPortThreads: MaxParallelStudents * 2
);

// Increase maximum threads for I/O-bound workload
ThreadPool.SetMaxThreads(
    workerThreads: MaxParallelStudents * 4,
    completionPortThreads: MaxParallelStudents * 4
);
```

**Benefit:** Reduces thread creation overhead during parallel grading

**Recommendation:** ✅ **Low complexity, measurable benefit** - Good for systems with high MaxParallelStudents

### 🔧 Option 4: Parallel Result Writing (Batched)

**Current:** Write results after each student completes
```
Student 1 finishes → Write to Excel (blocks thread for 0.5s)
Student 2 finishes → Write to Excel (blocks thread for 0.5s)
```

**Potential:** Batch writes on background thread
```csharp
// Dedicated writer thread
var writeChannel = Channel.CreateUnbounded<StudentResult>();
Task.Run(async () => {
    var batch = new List<StudentResult>();
    await foreach (var result in writeChannel.Reader.ReadAllAsync())
    {
        batch.Add(result);
        if (batch.Count >= 10 || /* timeout */)
        {
            await WriteBatchToExcel(batch);  // Single write for multiple students
            batch.Clear();
        }
    }
});
```

**Benefit:** Reduces Excel I/O overhead, worker threads return to grading faster

**Recommendation:** ✅ **Medium complexity, good benefit** - Worthwhile for batch grading

---

## Implementation Priority

### ✅ Already Implemented (Priority 0 - Done)
1. **Parallel student grading** - Fully implemented with continuous batch processing
2. **Async I/O operations** - All I/O uses async/await
3. **Thread-safe data structures** - ConcurrentBag, Channels, Mutex
4. **Isolated services per student** - No shared state contention

### 🎯 Recommended for Implementation (Priority 1 - High Value)

#### A. Parallel DLL Scanning During Discovery
```csharp
// Implement in StudentDiscoveryService
public async Task<List<StudentSolution>> DiscoverStudentsParallelAsync(...)
{
    var students = GetStudentFolders(...);
    
    await Parallel.ForEachAsync(students, new ParallelOptions 
    { 
        MaxDegreeOfParallelism = Environment.ProcessorCount 
    }, async (student, ct) =>
    {
        // Parallel DLL discovery
        student.ServerDllPath = await Task.Run(() => FindDll(...), ct);
        student.ClientDllPath = await Task.Run(() => FindDll(...), ct);
    });
    
    return students;
}
```

**Estimated Time:** 2-3 hours
**Expected Benefit:** 2-5x faster discovery for 100+ students

#### B. ThreadPool Tuning
```csharp
// Add to Program.cs startup
public static void ConfigureThreadPool(int maxParallelStudents)
{
    // Set minimum threads to avoid spin-up latency
    ThreadPool.SetMinThreads(
        workerThreads: Math.Max(maxParallelStudents * 2, Environment.ProcessorCount * 2),
        completionPortThreads: Math.Max(maxParallelStudents * 2, Environment.ProcessorCount * 2)
    );
    
    Console.WriteLine($"[ThreadPool] Configured for {maxParallelStudents} parallel students");
    Console.WriteLine($"[ThreadPool] Minimum threads: {maxParallelStudents * 2}");
}
```

**Estimated Time:** 1 hour
**Expected Benefit:** 5-10% reduction in thread creation overhead

#### C. Batched Result Writing
```csharp
// Implement in ResultWriterService
private readonly Channel<StudentResult> _writeChannel;
private readonly Task _writerTask;

public ResultWriterService(...)
{
    _writeChannel = Channel.CreateUnbounded<StudentResult>();
    _writerTask = Task.Run(() => BatchWriterLoop());
}

private async Task BatchWriterLoop()
{
    var batch = new List<StudentResult>();
    var timer = new Timer(500); // Flush every 500ms
    
    await foreach (var result in _writeChannel.Reader.ReadAllAsync())
    {
        batch.Add(result);
        
        if (batch.Count >= 10 || timer.Elapsed)
        {
            await WriteBatchToExcelOptimized(batch);
            batch.Clear();
            timer.Reset();
        }
    }
}
```

**Estimated Time:** 4-6 hours
**Expected Benefit:** 10-20% faster result writing, less thread blocking

### ⚠️ Consider Carefully (Priority 2 - Complex)

#### D. Parallel Test Case Execution (Within Student)
**Complexity:** Very High
**Risk:** Resource contention, port conflicts, container conflicts
**Benefit:** Potentially 2-3x faster per student (if test cases are independent)
**Recommendation:** Only implement if profiling shows test execution is the bottleneck (not Docker/I/O)

---

## Performance Monitoring

### CPU Core Utilization Metrics

Add these metrics to track multi-threading efficiency:

```csharp
public class GradingMetrics
{
    public int MaxParallelStudents { get; set; }
    public int AvailableCpuCores { get; set; }
    public int ActiveWorkerThreads { get; set; }
    public double CpuUtilizationPercent { get; set; }
    public double ThreadPoolUtilizationPercent { get; set; }
    public TimeSpan AverageStudentGradingTime { get; set; }
    public int StudentsGradedPerHour { get; set; }
}
```

### Recommended Monitoring

```csharp
// At start of grading session
Console.WriteLine($"[System] CPU Cores: {Environment.ProcessorCount}");
Console.WriteLine($"[System] MaxParallelStudents: {config.MaxParallelStudents}");
Console.WriteLine($"[System] Parallelism Ratio: {config.MaxParallelStudents / (double)Environment.ProcessorCount:F2}x");

// During grading
ThreadPool.GetAvailableThreads(out int availableWorker, out int availableIO);
ThreadPool.GetMaxThreads(out int maxWorker, out int maxIO);
int busyWorkers = maxWorker - availableWorker;
Console.WriteLine($"[ThreadPool] Active workers: {busyWorkers}/{maxWorker}");
```

---

## Summary

### ✅ What's Already Optimized

The system **already uses multi-threading extensively**:
- ✅ Parallel student grading (10+ students graded simultaneously)
- ✅ Continuous batch processing (no idle time)
- ✅ Async I/O operations (threads don't block on I/O)
- ✅ Thread-safe data structures (ConcurrentBag, Channels, Mutex)
- ✅ Isolated services per student (no contention)

### 🎯 Recommended Next Steps

1. **Parallel DLL scanning** during discovery (2-5x faster for 100+ students)
2. **ThreadPool tuning** for high concurrency (5-10% improvement)
3. **Batched result writing** (10-20% less blocking)

### ⚙️ Configuration Guidelines

```
System        CPU Cores    Recommended MaxParallelStudents
-------       ---------    -------------------------------
Laptop        4            6-8
Desktop       8            12-16
Workstation   16           24-32
Server        32+          48-64
```

### 📊 Expected Performance

With current optimizations + recommended additions:

| Students | MaxParallel | Time (Before) | Time (After) | Improvement |
|----------|-------------|---------------|--------------|-------------|
| 100      | 10          | 200 min       | 120 min      | 40% faster  |
| 100      | 20          | 100 min       | 70 min       | 30% faster  |
| 500      | 32          | 500 min       | 300 min      | 40% faster  |

**The system already maximally utilizes CPU cores through parallel student grading. Additional optimizations provide incremental improvements.**
