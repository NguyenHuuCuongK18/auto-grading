# Stage Separation and ProcessLogs Organization - Implementation Summary

## Problem Statement

Client and server console outputs were not being properly separated by stage, and ProcessLogs were not organized by test case.

### Issues Identified

1. **No Stage Separation**: When a client/server process continued running across multiple stages, all output was accumulated in one log file instead of being separated by stage
2. **Poor ProcessLogs Organization**: All logs were exported to a single `ProcessLogs/` directory instead of being organized by test case
3. **Incorrect File Naming**: Log files didn't follow the expected naming convention

### Expected Behavior (from problem statement)

```
Stage 1: StartClient  → capture initial client console output
Stage 2: StartServer  → capture server console output  
Stage 3: Input "1+2"  → capture ONLY the NEW client output from this stage

Expected client stage 3 output:
"3
Enter operation (format as A X B):"
```

Expected directory structure:
```
ProcessLogs/
├── TC1/
│   ├── client-TC1-stage-1.log
│   ├── client-TC1-stage-3.log
│   ├── server-TC1-stage-2.log
│   └── server-TC1-stage-3.log
├── TC2/
│   └── ...
```

## Solution Architecture

### Before (OLD Approach)

**Wrapper Scripts:**
- Wrote to per-stage log files: `server-stage-1.log`, `server-stage-2.log`, etc.
- Used `>>` (append) which accumulated all output in one file

**C# Code:**
- Read logs AFTER all actions completed
- Could not separate output when processes continued running
- Exported all logs to single `ProcessLogs/` directory

**Problem:**
When client started at stage 1 and received input at stage 3, all output went into `client-stage-1.log` because the client process never stopped.

### After (NEW Approach)

**Wrapper Scripts:**
- Write to unified log files: `server.log`, `client.log`
- All output from a process goes to one file throughout the test case

**C# Code:**
- Reads logs incrementally AFTER EACH ACTION
- Tracks file byte positions to capture only new output
- Separates output by stage using incremental reading
- Exports logs organized by test case

**How it works:**
1. Action: StartClient (stage 1)
   - Client starts and outputs "Enter operation..."
   - C# reads log file → captures 34 bytes → saves to stage 1
   
2. Action: StartServer (stage 2)
   - Server starts and outputs "Waiting for connection..."
   - C# reads log file from position 37 → captures server output → saves to stage 2
   
3. Action: Input "1+2" (stage 3)
   - Client processes input, prints "3\nEnter operation..."
   - C# reads log file from position 34 → captures NEW output → saves to stage 3

## Implementation Details

### Files Changed

#### Shell Scripts
1. **client-wrapper.sh**
   - Changed: `exec dotnet "$DLL" < /tmp/client_input >> "/apps/client/client.log" 2>&1`
   - Removed: Stage file reading logic
   - All output goes to unified `client.log`

2. **server-wrapper.sh**
   - Changed: `exec dotnet "$DLL" >> "/apps/server/server.log" 2>&1`
   - Removed: Stage file reading logic
   - All output goes to unified `server.log`

3. **unified-control.sh**
   - Removed: `echo "$STAGE" > /tmp/server_stage` and `echo "$STAGE" > /tmp/client_stage`
   - No longer needs to write stage files

4. **supervisord-unified.conf**
   - Updated comments to reflect unified logging

#### C# Code (DockerGradingService.cs)

**New Method: ReadFileFromContainerIncremental()**
```csharp
private (string newContent, long newPosition) ReadFileFromContainerIncremental(
    string containerName, 
    string filePath, 
    long startPosition)
{
    // Uses 'tail -c +N' to read from specific byte position
    // Returns new content and updated position
}
```

**Modified Method: ExecuteActionsForUnifiedContainerAsync()**
```csharp
// Track file positions
long clientLogPosition = 0;
long serverLogPosition = 0;

foreach (var (stage, input, action) in actions)
{
    // Execute action (StartClient, StartServer, Input, etc.)
    
    // Read logs incrementally AFTER each action
    var (newServerOutput, newServerPosition) = ReadFileFromContainerIncremental(
        unifiedContainer, "/apps/server/server.log", serverLogPosition);
    if (!string.IsNullOrEmpty(newServerOutput))
    {
        serverOutputs[stage] = newServerOutput;
        serverLogPosition = newServerPosition;
    }
    
    // Same for client logs
}

// Store outputs for later export
_lastTestCaseClientOutputs = new Dictionary<int, string>(clientOutputs);
_lastTestCaseServerOutputs = new Dictionary<int, string>(serverOutputs);
```

**New Method: ExportStageLogsForTestCaseAsync()**
```csharp
private async Task ExportStageLogsForTestCaseAsync(
    string unifiedContainer, 
    string studentResultPath, 
    string testCaseName)
{
    // Create ProcessLogs/TC# subdirectory
    var tcLogsDir = Path.Combine(studentResultPath, "ProcessLogs", testCaseName);
    
    // Export client stage logs
    foreach (var (stage, output) in _lastTestCaseClientOutputs)
    {
        var logFileName = $"client-{testCaseName}-stage-{stage}.log";
        await File.WriteAllTextAsync(Path.Combine(tcLogsDir, logFileName), output);
    }
    
    // Same for server logs
}
```

**Modified: Test Case Loop**
```csharp
foreach (var testCase in testKitConfig.TestCases)
{
    // Execute test case
    var tcResult = await ExecuteTestCaseAsync(...);
    
    // Move PCAP snapshots to TC folder
    MoveSnapshotsToTCFolder(studentResultPath, tcResultPath, testCase.Name);
    
    // NEW: Export stage logs for this test case
    await ExportStageLogsForTestCaseAsync(unifiedContainer, studentResultPath, testCase.Name);
}
```

## Test Results

### Student: dongnvhe172649
**Score: 5.00/5.00** ✅

**ProcessLogs Structure:**
```
ProcessLogs/
├── TC1/
│   ├── client-TC1-stage-1.log  (34 chars: "Enter operation...")
│   ├── client-TC1-stage-3.log  (36 chars: "3\nEnter operation...")
│   ├── server-TC1-stage-2.log  (37 chars: "Waiting for connection...")
│   └── server-TC1-stage-3.log  (37 chars: "Client connected\nClient disconnected...")
├── TC2_Send/
│   ├── client-TC2_Send-stage-1.log
│   ├── client-TC2_Send-stage-3.log
│   ├── server-TC2_Send-stage-2.log
│   └── server-TC2_Send-stage-3.log
├── TC3_ReqResNotC/
│   ├── client-TC3_ReqResNotC-stage-1.log
│   ├── client-TC3_ReqResNotC-stage-3.log
│   ├── server-TC3_ReqResNotC-stage-2.log
│   └── server-TC3_ReqResNotC-stage-3.log
└── TC4_Full/
    ├── client-TC4_Full-stage-1.log
    ├── client-TC4_Full-stage-3.log
    ├── server-TC4_Full-stage-2.log
    └── server-TC4_Full-stage-3.log
```

**Verification:**
- ✅ Stage separation working: Client stage 3 contains ONLY `"3\nEnter operation..."` (36 chars)
- ✅ ProcessLogs organized by test case
- ✅ Correct file naming: `client-TC#-stage-#.log` and `server-TC#-stage-#.log`
- ✅ PCAP snapshots also in TC folders: `TC1/snapshot_TC1_stage1.pcap`, etc.

### Student: cuongnhhe186494
**Score: 2.00/5.00** ✅

This student's client doesn't print output after the first prompt (implementation-specific behavior).
- ✅ ProcessLogs still organized correctly
- ✅ Stage 1 client log contains initial prompt only
- ✅ No stage 3 client log (no output produced)
- ✅ System correctly handles this edge case

## Key Benefits

1. **Accurate Stage Separation**: Incremental reading captures only new output per stage
2. **Better Organization**: Logs organized by test case for easier debugging
3. **Consistent Naming**: Predictable file naming convention
4. **Handles Edge Cases**: Works correctly even when client/server don't produce output
5. **Minimal Delays**: Uses existing 2s delay, no extra waiting needed
6. **Clean Architecture**: Unified log files + incremental reading = simple and robust

## Troubleshooting

### If stage output is empty when it shouldn't be:

1. **Check if process is producing output**: Some student implementations may not print to console
2. **Check timing**: The 2s delay (`InputProcessingDelayMs`) should be sufficient for most cases
3. **Check buffering**: `DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1` is set in wrapper scripts
4. **Check log position tracking**: Verify byte positions are being updated correctly

### If logs are not organized by test case:

1. **Check ExportStageLogsForTestCaseAsync is called**: Should be called after each test case in the loop
2. **Check _lastTestCaseClientOutputs/ServerOutputs**: These should be populated by ExecuteActionsForUnifiedContainerAsync
3. **Check directory creation**: `ProcessLogs/TC#/` directories should be created automatically

## Future Considerations

1. **Performance**: Incremental file reading is efficient (uses `tail -c +N`)
2. **Scalability**: Works with any number of test cases and stages
3. **Extensibility**: Easy to add more output sources (e.g., database logs) using same pattern
4. **Compatibility**: Works with existing test kits without modification
