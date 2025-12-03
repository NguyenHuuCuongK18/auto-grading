# Network Monitoring Fix for Parallel Grading

## Problem
The grading system was failing because network monitoring was not properly initialized for parallel grading:
1. **Race condition**: All parallel students shared the same `_configuration` object, causing port conflicts
2. **One monitor for all**: Only one NetworkMonitorService instance was monitoring one port, missing traffic from other students
3. **Network logs not matching TestKit**: Each student on different ports wasn't being monitored correctly

## Solution

### 1. Fixed Race Condition in GradingWindow.xaml.cs
**Before (WRONG)**:
```csharp
// Shared _configuration modified by all parallel students - RACE CONDITION!
_configuration.CodeContainerInternalPort = testKitConfig.CodeContainerInternalPort + portOffset;
_configuration.CodeContainerHostPort = testKitConfig.CodeContainerHostPort + portOffset;
await _gradingService.StartGradingAsync(
    new List<StudentSolution> { student },
    _configuration,  // ❌ Shared object!
    sessionState,
    ct);
```

**After (CORRECT)**:
```csharp
// Each student gets their own configuration copy with unique ports
var studentConfig = new GradingConfiguration
{
    // ... copy all settings ...
    CodeContainerInternalPort = testKitConfig.CodeContainerInternalPort + portOffset,
    CodeContainerHostPort = testKitConfig.CodeContainerHostPort + portOffset,
    // ... database and other settings ...
};

await _gradingService.StartGradingAsync(
    new List<StudentSolution> { student },
    studentConfig,  // ✅ Student-specific configuration!
    sessionState,
    ct);
```

### 2. One NetworkMonitorService Per Student
Each call to `LibGradingService.ExecuteDockerGradingAsync` creates its own NetworkMonitorService:
```csharp
// In LibGradingService.ExecuteDockerGradingAsync:
IRunContext runctx = new RunContext();
INetworkMonitorService networkMonitor = new NetworkMonitorService(runctx);  // ✅ New instance per student

// In DockerGradingService.GradeStudentAsync:
_networkMonitor.MonitorPort = config.CodeContainerHostPort;  // ✅ Port includes offset
_networkMonitor.ProtocolType = testKitConfig.Protocol;
await _networkMonitor.StartAsync(ct);
```

### 3. Enhanced Logging
Added detailed logging to track network monitor lifecycle:
```csharp
Console.WriteLine($"[NetworkMonitor] Starting monitor for student {studentCode} on host port {config.CodeContainerHostPort}");
Console.WriteLine($"[NetworkMonitor] Monitor active for student {studentCode} - ready to capture packets");
// ... grading happens ...
Console.WriteLine($"[NetworkMonitor] Stopping monitor for student {studentCode}...");
Console.WriteLine($"[NetworkMonitor] Monitor stopped for student {studentCode}");
```

## How Parallel Grading Works Now

### Example: 3 Students Grading in Parallel (MaxParallelStudents=3)
| Student | Port Offset | Container Port | Network Monitor Port | Status |
|---------|-------------|----------------|---------------------|--------|
| Student1 | 0 | 8000 | 8000 | ✅ Monitored |
| Student2 | 1 | 8001 | 8001 | ✅ Monitored |
| Student3 | 2 | 8002 | 8002 | ✅ Monitored |

Each student:
1. Gets their own `GradingConfiguration` with unique port offset
2. Gets their own `NetworkMonitorService` instance
3. NetworkMonitor captures traffic on their specific port
4. Network logs match TestKit test cases for that student

## Testing Instructions

### Before Running Tests
1. **Install libpcap** (Linux):
   ```bash
   sudo apt-get install libpcap-dev
   ```
2. **Run with sudo** (required for network capture):
   ```bash
   sudo dotnet run --project Application/SolutionGrader.UI/SolutionGrader.UI.csproj
   ```

### Test Parallel Grading
1. Open the UI application
2. Load students from Submit folder
3. Set **Batch Size** to 2 or 3 (test parallel grading)
4. Select 3-5 students
5. Click **Start Selected**
6. **Verify in logs**:
   ```
   [NetworkMonitor] Starting monitor for student Student1 on host port 8000
   [NetworkMonitor] Starting monitor for student Student2 on host port 8001
   [NetworkMonitor] Starting monitor for student Student3 on host port 8002
   ```

### Verify Network Logs Match TestKit
1. After grading completes, check result folder structure:
   ```
   Results/
     └── {PaperNo}/
         └── student/
             └── {StudentCode}/
                 ├── OverallSummary.xlsx
                 ├── TC1/
                 │   ├── TC1_Result.xlsx    ← Check network flow validation
                 │   └── GradeDetail.xlsx
                 ├── TC2/
                 └── TC3/
   ```

2. Open `TC1_Result.xlsx` and verify network flow entries:
   ```
   NETWORK-FLOW-3-1  |  3  |  COMPARE_NETWORK_FLOW  |  True  |  Network flow validation passed for packet 1: [SYN] Client->Server
   NETWORK-FLOW-3-2  |  3  |  COMPARE_NETWORK_FLOW  |  True  |  Network flow validation passed for packet 2: [SYN, ACK] Server->Client
   ```

3. Compare with TestKit expectations in `TestKit/TestKit/Q1/TC1/Detail.xlsx`

## UI Improvements
**Batch Selection** moved next to **Start All** button for more compact layout:
- Before: 3 sections (Batch Config, Student Selection, Grading Actions)
- After: 2 sections (Student Selection, Grading Actions with Batch Size)
- Saves ~50 pixels of vertical space
- Clearer visual grouping of related controls

## Files Changed
1. `Application/SolutionGrader.UI/GradingWindow.xaml.cs` - Fixed race condition, created per-student config
2. `Lib/SolutionGrader.Core/Services/DockerGradingService.cs` - Added detailed network monitor logging
3. `Application/SolutionGrader.UI/GradingWindow.xaml` - UI layout improvements

## Expected Behavior After Fix
✅ Each parallel student has their own network monitor on their unique port
✅ No race conditions when modifying configuration
✅ Network logs correctly reflect TestKit test cases
✅ Parallel grading works reliably with 2+ students
✅ UI is more compact and intuitive
