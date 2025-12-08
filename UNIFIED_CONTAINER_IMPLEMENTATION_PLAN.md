# Unified Container Implementation Plan

## Overview
Implement "unified container" approach where client and server run in the SAME container, communicating via localhost, with a shared network monitor on the loopback interface.

## Architecture Comparison

### Current (Sidecar)
```
Student 1:
  - Container: ag-server-student1 (server process)
  - Container: ag-client-student1 (client process)
  - Container: ag-monitor-student1 (sidecar on eth0)

Student 2:
  - Container: ag-server-student2
  - Container: ag-client-student2
  - Container: ag-monitor-student2

Total: 6 containers for 2 students
```

### Proposed (Unified)
```
Student 1:
  - Container: ag-unified-student1 
    ├─ Server process (localhost:8000)
    └─ Client process → localhost:8000

Student 2:
  - Container: ag-unified-student2
    ├─ Server process (localhost:8001)
    └─ Client process → localhost:8001

Shared Monitor:
  - Container: ag-shared-monitor (sidecar on lo)
    └─ Filters: port 8000 OR port 8001

Total: 3 containers for 2 students (50% reduction)
```

## Implementation Components

### 1. Configuration Changes

**File**: `DockerGradingService.cs` - `DockerGradingConfig` class

```csharp
// NEW: Default approach
public bool UseUnifiedContainer { get; set; } = true;

// LEGACY: Only used when UseUnifiedContainer = false
public bool UseDockerInternalNetworking { get; set; } = true;
```

### 2. Unified Container Setup

**New Method**: `SetupUnifiedContainerAsync`

```csharp
private async Task SetupUnifiedContainerAsync(
    string? serverDllPath,
    string? clientDllPath,
    DockerGradingConfig config,
    TestKitConfig testKitConfig,
    string unifiedContainer)
{
    // 1. Create startup script that runs both processes
    // 2. Copy both DLLs to container
    // 3. Configure appsettings.json with localhost
    // 4. Start unified container with entrypoint script
}
```

**Startup Script** (`startup.sh`):
```bash
#!/bin/bash
# Start server in background
dotnet /apps/server/Server.dll &
SERVER_PID=$!

# Wait for server to bind to port
sleep 2

# Start client in foreground
dotnet /apps/client/Client.dll

# Cleanup
kill $SERVER_PID 2>/dev/null || true
```

### 3. Shared Network Monitor

**Enhancement**: `SharedNetworkMonitorService`

Current: Monitors host network interfaces (eth0, vEthernet)
Needed: Monitor loopback interface (`lo`) when attached via sidecar

```csharp
// In FindCandidateDevices():
if (unifiedContainerMode) {
    // Look for loopback interface
    // Typically named "lo" on Linux, "lo0" on Mac
    var loopbackDevice = devices.FirstOrDefault(d => 
        d.Name.ToLower().Contains("lo") && !d.Name.Contains("loopback"));
}
```

**Attachment**:
```bash
# Attach shared monitor to first unified container
docker run -d --name ag-shared-monitor \
  --net=container:ag-unified-student1 \
  --cap-add=NET_ADMIN --cap-add=NET_RAW \
  fptuxaes/network-monitor:latest \
  tcpdump -i lo -w /capture/unified.pcap "tcp port 8000 or tcp port 8001 or tcp port 8002"
```

**Port-based filtering**: Already implemented in `SharedNetworkMonitorService._portToStudentCode`

### 4. Modified Grading Flow

**File**: `DockerGradingService.cs` - `GradeStudentAsync`

```csharp
if (config.UseUnifiedContainer) {
    // NEW PATH
    var unifiedContainer = $"ag-unified-{studentCode}";
    await SetupUnifiedContainerAsync(..., unifiedContainer);
    
    // Shared monitor lifecycle (singleton pattern)
    if (!_sharedMonitorStarted) {
        await StartSharedNetworkMonitorAsync(...);
        _sharedMonitorStarted = true;
    }
    
    // Register this student with shared monitor
    _networkMonitor?.RegisterStudent(studentCode, port, ...);
    
} else {
    // LEGACY PATH (existing code)
    await SetupContainersAsync(serverContainer, clientContainer, ...);
    
    if (config.UseDockerInternalNetworking) {
        // Sidecar on eth0
    } else {
        // Port mapping mode
    }
}
```

### 5. Test Execution Changes

**File**: `DockerGradingService.cs` - Test execution methods

**Current**: Separate commands for server container and client container
**Needed**: Single container with process management

```csharp
// Unified mode: Execute in unified container
docker exec ag-unified-student1 /scripts/run-test.sh test-case-1

// run-test.sh:
# Kill any existing server
pkill -f Server.dll
# Start server
dotnet /apps/server/Server.dll &
SERVER_PID=$!
# Run client with inputs
echo "input1\ninput2" | dotnet /apps/client/Client.dll
# Kill server
kill $SERVER_PID
```

### 6. Cleanup Changes

**Method**: `CleanupContainersAsync`

```csharp
if (config.UseUnifiedContainer) {
    // Remove unified container only
    _dockerExecutor.RemoveContainer(unifiedContainer);
    
    // Unregister student from shared monitor
    _networkMonitor?.UnregisterStudent(studentCode);
    
    // Don't stop shared monitor (other students may be using it)
    
} else {
    // Legacy: Remove server and client containers
    _dockerExecutor.RemoveContainer(serverContainer);
    _dockerExecutor.RemoveContainer(clientContainer);
    
    // Stop per-student monitor
    await StopNetworkMonitorContainerAsync(...);
}
```

### 7. Shared Monitor Lifecycle

**Initialization**: In batch grading startup (GradingWindow or CLI)
```csharp
// Start shared monitor ONCE before grading any students
var sharedMonitor = new SharedNetworkMonitorService(startPort, endPort);
await sharedMonitor.StartAsync();
```

**Per-Student**: Register/Unregister
```csharp
// Before grading student
sharedMonitor.RegisterStudent(studentCode, port, "TCP", runContext);

// After grading student
sharedMonitor.UnregisterStudent(studentCode);
```

**Shutdown**: After all students graded
```csharp
await sharedMonitor.StopAsync();
sharedMonitor.Dispose();
```

## File Changes Summary

### Modified Files
1. `Lib/SolutionGrader.Core/Services/DockerGradingService.cs`
   - Add `UseUnifiedContainer` property
   - Add `SetupUnifiedContainerAsync` method
   - Modify `GradeStudentAsync` to branch on mode
   - Update `CleanupContainersAsync` for unified mode
   - Update test execution logic

2. `Lib/SolutionGrader.Core/Services/SharedNetworkMonitorService.cs`
   - Support loopback interface detection
   - Ensure port-based filtering works correctly

3. `Application/SolutionGrader.UI/GradingWindow.xaml.cs`
   - Initialize shared monitor once before batch grading
   - Pass shared monitor to grading service

4. `Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs`
   - Similar shared monitor initialization for CLI

### New Files
1. `DockerImage/startup-unified.sh` (or inline script)
   - Manages server and client processes in unified container

### Removed/Deprecated
1. Per-student monitor container logic (replaced by shared monitor)
2. Legacy port mapping code (if user confirms removal)

## Migration Path

### Phase 1: Add Configuration (Backwards Compatible)
- Add `UseUnifiedContainer = false` (default off)
- Existing code paths unchanged
- Users can opt-in to test

### Phase 2: Implement Unified Logic (Parallel)
- Add new methods alongside existing ones
- No breaking changes to existing paths

### Phase 3: Switch Default (Breaking)
- Set `UseUnifiedContainer = true` (default on)
- Existing code becomes "legacy mode"

### Phase 4: Cleanup (Optional)
- Remove deprecated code paths if user confirms
- Simplify codebase

## Open Questions for User

1. **Process Management**: 
   - Use simple shell script with `&` and `kill`?
   - Or install supervisord in container?

2. **Port Allocation**:
   - Sequential: 8000, 8001, 8002... (one per student)?
   - Or reuse ports between students (monitor filters by student code)?

3. **Shared Monitor Lifecycle**:
   - Start with first student, persist until last?
   - Or separate initialization phase?

4. **Backwards Compatibility**:
   - Keep all 3 modes (unified, sidecar, port-mapping)?
   - Or remove port-mapping entirely?

5. **Testing Strategy**:
   - Prototype with single student first?
   - Or full implementation immediately?

## Estimated Effort

- **Configuration**: 30 minutes
- **Unified Container Setup**: 4 hours
- **Shared Monitor Integration**: 2 hours
- **Test Execution Refactor**: 3 hours
- **Cleanup Logic**: 1 hour
- **Testing & Debugging**: 4 hours
- **Documentation**: 1 hour

**Total**: ~15 hours (2 days)

## Risks

1. **Process Management Complexity**: Managing 2 processes in 1 container
2. **Port Conflicts**: If server doesn't exit cleanly between tests
3. **Loopback Monitoring**: May need different tcpdump filters
4. **Shared Monitor State**: Race conditions in concurrent grading
5. **Backwards Compatibility**: Breaking existing workflows

## Next Steps

Awaiting user confirmation on:
- Proceed with full implementation?
- Preferred process management approach?
- Port allocation strategy?
- Cleanup scope (keep legacy or remove)?
