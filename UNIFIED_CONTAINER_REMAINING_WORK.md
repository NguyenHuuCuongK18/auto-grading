# Unified Container - Remaining Implementation Work

## Completed So Far ✅

### Phase 1: Infrastructure (Commit 5f47a73)
- ✅ Dockerfile.unified with supervisord
- ✅ supervisord-unified.conf for process management
- ✅ unified-control.sh for supervisorctl commands

### Phase 2: Core Setup Methods (Commit a4fda2e)
- ✅ Removed legacy configuration options
- ✅ Added UnifiedContainerImage property
- ✅ Modified GradeStudentAsync to use unified approach
- ✅ Added SetupUnifiedContainerAsync method
- ✅ Added CopyFilesToUnifiedContainerAsync method
- ✅ Integrated with shared network monitor

## Remaining Work 🚧

### Phase 3: Test Case Action Execution
**File**: `DockerGradingService.cs` - `ExecuteActionsAsync` method

**Current Implementation** (lines ~1500-1700):
- Uses separate `docker exec` commands for server and client containers
- Calls STARTSERVER, STARTCLIENT, SENDINPUT, WAITFOR commands

**Required Changes**:
```csharp
private async Task<(List<string> clientOutputs, List<string> serverOutputs)> ExecuteActionsAsync(
    List<ActionRecord> actions,
    DockerGradingConfig config,
    TestKitConfig testKitConfig,
    string? serverDllPath,
    string? clientDllPath,
    string unifiedContainer,  // Changed from serverContainer, clientContainer
    CancellationToken ct)
{
    foreach (var action in actions)
    {
        switch (action.Action.ToUpperInvariant())
        {
            case "STARTSERVER":
                // OLD: docker exec ag-server-{student} dotnet /apps/Server.dll
                // NEW: docker exec ag-unified-{student} /scripts/unified-control.sh StartServer {stage}
                var startServerCmd = $"docker exec {unifiedContainer} " +
                                    $"/scripts/unified-control.sh StartServer {action.Stage}";
                _commandExecutor.RunCommand(startServerCmd, ...);
                break;
                
            case "STARTCLIENT":
                // NEW: docker exec ag-unified-{student} /scripts/unified-control.sh StartClient {stage}
                break;
                
            case "CLOSESERVER":
                // NEW: docker exec ag-unified-{student} /scripts/unified-control.sh CloseServer {stage}
                break;
                
            case "CLOSECLIENT":
                // NEW: docker exec ag-unified-{student} /scripts/unified-control.sh CloseClient {stage}
                break;
                
            case "SENDINPUT":
                // Still use docker exec but target the unified container
                // Input goes to supervisord-managed client process
                break;
        }
    }
}
```

### Phase 4: Appsettings Generation
**File**: `DockerGradingService.cs` - Add new method

**Required Method**:
```csharp
private void GenerateAppsettingsInUnifiedContainer(
    DockerGradingConfig config,
    TestKitConfig testKitConfig,
    string unifiedContainer)
{
    // Generate appsettings.json for SERVER (/apps/server/)
    var serverAppsettings = new
    {
        ConnectionStrings = new { ... },
        ServerConfig = new {
            IpAddress = "127.0.0.1",  // Bind to localhost
            Port = config.CodeContainerInternalPort
        }
    };
    var serverJson = JsonSerializer.Serialize(serverAppsettings, ...);
    
    // Write to temp file, copy to container
    var tempServerFile = Path.GetTempFileName();
    File.WriteAllText(tempServerFile, serverJson);
    _dockerExecutor.CopyFileToContainer(tempServerFile, $"{unifiedContainer}:/apps/server/appsettings.json");
    
    // Generate appsettings.json for CLIENT (/apps/client/)
    var clientAppsettings = new
    {
        ConnectionStrings = new { ... },
        ClientConfig = new {
            ServerAddress = "127.0.0.1",  // Connect to localhost
            ServerPort = config.CodeContainerInternalPort
        }
    };
    var clientJson = JsonSerializer.Serialize(clientAppsettings, ...);
    
    var tempClientFile = Path.GetTempFileName();
    File.WriteAllText(tempClientFile, clientJson);
    _dockerExecutor.CopyFileToContainer(tempClientFile, $"{unifiedContainer}:/apps/client/appsettings.json");
}
```

### Phase 5: Cleanup Logic
**File**: `DockerGradingService.cs` - `CleanupContainersAsync` method

**Current Implementation** (lines ~2608):
- Removes separate server and client containers
- Stops per-student monitor containers

**Required Changes**:
```csharp
private async Task CleanupUnifiedContainerAsync(string unifiedContainer, string studentCode)
{
    _consoleManager.RemoveAllAttachments();
    
    OnProgress($"[Docker Cleanup] Starting cleanup for unified container {unifiedContainer}");
    
    // Remove unified container
    try 
    { 
        _dockerExecutor.RemoveContainer(unifiedContainer); 
        OnProgress($"[Docker Cleanup] Removed {unifiedContainer}");
    } 
    catch (Exception ex)
    { 
        OnProgress($"[Docker Cleanup] Warning: Failed to remove {unifiedContainer}: {ex.Message}");
    }
    
    // Unregister student from shared monitor
    if (_networkMonitor != null)
    {
        _networkMonitor.UnregisterStudent(studentCode);
        OnProgress($"[NetworkMonitor] Unregistered student {studentCode}");
    }
    
    await Task.CompletedTask;
}
```

### Phase 6: Log Export
**File**: `DockerGradingService.cs` - After test execution

**Required Logic**:
```csharp
// After all test cases complete, export logs from container to student directory
private async Task ExportLogsFromUnifiedContainerAsync(
    string unifiedContainer,
    string logOutputDir,
    string studentResultPath)
{
    OnProgress($"[Unified] Exporting logs from container...");
    
    // Logs are already in /logs inside container (mounted volume)
    // They were written by supervisord per stage
    
    // Copy logs from mounted volume to student result directory
    var sourceLogDir = logOutputDir;  // Already mapped to /logs
    var destLogDir = Path.Combine(studentResultPath, "ProcessLogs");
    Directory.CreateDirectory(destLogDir);
    
    // Logs are already on host filesystem via volume mount
    // Just need to organize them
    var logFiles = Directory.GetFiles(sourceLogDir, "*.log");
    foreach (var logFile in logFiles)
    {
        var fileName = Path.GetFileName(logFile);
        var destFile = Path.Combine(destLogDir, fileName);
        File.Copy(logFile, destFile, true);
        OnProgress($"[Unified] Exported log: {fileName}");
    }
}
```

### Phase 7: Remove Legacy Code

**Files to Clean Up**:
1. `DockerGradingService.cs`:
   - Remove `SetupContainersAsync` method (lines 518-624)
   - Remove `StartNetworkMonitorContainerAsync` method (lines 3053-3115)
   - Remove `StopNetworkMonitorContainerAsync` method (lines 3117-3175)
   - Remove `CopyFilesToContainersAsync` method (lines 911-1107)
   - Remove `GenerateAppsettingsInContainers` method (lines 1268-1400+)
   - Remove legacy network monitor logic

2. Update call sites:
   - Find all references to removed methods
   - Update to use unified container equivalents

### Phase 8: Testing

**Test Cases**:
1. **Single Student**:
   ```bash
   # Run grading for one student
   # Verify:
   #   - Unified container created
   #   - Processes start/stop per test case actions
   #   - Logs exported correctly
   #   - Network monitor captures traffic
   #   - Container cleaned up
   ```

2. **Batch Grading**:
   ```bash
   # Run grading for multiple students
   # Verify:
   #   - Shared monitor handles all students
   #   - Port filtering works correctly
   #   - No container conflicts
   #   - All cleaned up after completion
   ```

3. **Error Handling**:
   ```bash
   # Test with student code that crashes
   # Verify:
   #   - Cleanup still happens
   #   - Logs still exported
   #   - Shared monitor still functional
   ```

## Implementation Sequence

1. **Phase 3** - Modify ExecuteActionsAsync (2-3 hours)
2. **Phase 4** - Add GenerateAppsettingsInUnifiedContainer (1 hour)
3. **Phase 5** - Update cleanup logic (1 hour)
4. **Phase 6** - Add log export (30 minutes)
5. **Phase 7** - Remove legacy code (2 hours)
6. **Phase 8** - Testing (3-4 hours)

**Total Estimated Time**: ~10-12 hours remaining

## Key Considerations

1. **Supervisord Environment Variables**:
   - Need to pass stage, DLL names to supervisord
   - May need to regenerate supervisord config per test case
   - OR use environment file approach

2. **Process Lifecycle**:
   - Server/client may need multiple restarts per grading session
   - Logs must be cumulative or stage-specific

3. **Shared Monitor**:
   - Must be started BEFORE any student grading
   - Must stay running for entire batch
   - Must be stopped AFTER all students complete

4. **Port Allocation**:
   - Each student gets unique port from Environment.xlsx
   - Shared monitor filters by port
   - No port conflicts between students

5. **Backwards Compatibility**:
   - REMOVE entirely per user request
   - No fallback to legacy modes

## Questions for User

1. **Supervisord Environment**: 
   - Should stage/DLL info be passed via environment variables?
   - Or regenerate config file per stage?

2. **Log Organization**:
   - One log file per stage per process (server-stage-0.log, server-stage-1.log)?
   - Or cumulative (server.log with stage markers)?

3. **Shared Monitor Initialization**:
   - Should it be in GradingWindow (UI) or DockerGradingService?
   - Who owns the lifecycle?

4. **Error Recovery**:
   - If supervisord crashes, should we restart container?
   - Or fail the grading?

## Next Steps

User should review this plan and confirm:
- Approach for supervisord environment management
- Log organization strategy
- Shared monitor initialization location
- Error handling strategy

Then implementation can proceed phase by phase with commits after each phase.
