# Test Case Log Preservation - Complete Fix

## Problem Statement
Logs and pcap files were being overwritten between test cases because filenames didn't include test case names. Only the LAST test case's logs would survive, making it impossible to debug issues in earlier test cases.

## Root Cause

### Previous Behavior
1. Container reused across all test cases (efficient resource usage)
2. **ClearStageLogsInContainer()** deleted log files before each new test case
3. Log files named without test case: `server-stage-1.log`, `client-stage-2.log`
4. Logs exported ONCE at the end (after all test cases completed)
5. **Result**: Only the last test case's logs were preserved

### Example of Data Loss
```
Test execution sequence:
TC1 runs → creates server-stage-1.log
TC2 runs → DELETES server-stage-1.log, creates new server-stage-1.log
TC3 runs → DELETES server-stage-1.log, creates new server-stage-1.log
Export → Only TC3's logs are copied out
Result → TC1 and TC2 logs LOST FOREVER
```

## Solution Overview

Include test case name in ALL file paths throughout the grading pipeline:
- Inside Docker containers: `/apps/server/server-TC1-stage-1.log`
- Snapshot pcap files: `snapshot-TC1-stage-3.pcap`
- Export destination: `TC1/ProcessLogs/server-TC1-stage-1.log`

This ensures each test case's data is uniquely identified and preserved.

## Implementation Details

### 1. File Naming Convention

**Before** (Overwritten):
```
server-stage-1.log
server-stage-2.log
client-stage-1.log
snapshot_stage1.pcap
snapshot_stage2.pcap
```

**After** (Preserved):
```
server-TC1-stage-1.log
server-TC1-stage-2.log
client-TC1-stage-1.log
snapshot-TC1-stage-1.pcap
snapshot-TC1-stage-2.pcap

server-TC2-stage-1.log
server-TC2-stage-2.log
client-TC2-stage-1.log
snapshot-TC2-stage-1.pcap
```

### 2. Directory Structure

**Before** (Mixed/Overwritten):
```
/Run_Log/1/student/AnhDThe187386/
├── ProcessLogs/                    # All test cases mixed
│   ├── client-stage-1.log         # TC6 overwrote TC1-TC5!
│   └── server-stage-2.log         # TC6 overwrote TC1-TC5!
├── snapshot_stage1.pcap            # TC6 overwrote TC1-TC5!
├── snapshot_stage2.pcap            # TC6 overwrote TC1-TC5!
├── TC1/
│   └── GradeDetail.xlsx
├── TC2/
│   └── GradeDetail.xlsx
...
└── TC6/
    └── GradeDetail.xlsx
```

**After** (Organized per Test Case):
```
/Run_Log/1/student/AnhDThe187386/
├── TC1/
│   ├── GradeDetail.xlsx
│   ├── ProcessLogs/
│   │   ├── client-TC1-stage-1.log
│   │   ├── client-TC1-stage-3.log
│   │   └── server-TC1-stage-2.log
│   ├── snapshot-TC1-stage-1.pcap
│   ├── snapshot-TC1-stage-2.pcap
│   └── snapshot-TC1-stage-3.pcap
├── TC2/
│   ├── GradeDetail.xlsx
│   ├── ProcessLogs/
│   │   ├── client-TC2-stage-1.log
│   │   └── server-TC2-stage-2.log
│   ├── snapshot-TC2-stage-1.pcap
│   └── snapshot-TC2-stage-2.pcap
...
├── TC6/
│   ├── GradeDetail.xlsx
│   ├── ProcessLogs/
│   │   ├── client-TC6-stage-1.log
│   │   └── server-TC6-stage-3.log
│   └── snapshot-TC6-stage-1.pcap
└── network_capture.pcap  # Cumulative capture (all test cases)
```

### 3. Code Changes

#### A. DockerGradingService.cs

**Pass test case name through call chain:**
```csharp
// In GradeStudentAsync - test case loop
await ExecuteActionsForUnifiedContainerAsync(
    actions, config, unifiedContainer, testCase.Name, ct);  // Added testCase.Name

// Export logs per test case (instead of once at the end)
await ExportLogsForTestCaseAsync(unifiedContainer, tcResultPath, testCase.Name);
```

**ExecuteActionsForUnifiedContainerAsync signature:**
```csharp
// BEFORE
private async Task<...> ExecuteActionsForUnifiedContainerAsync(
    List<...> actions, DockerGradingConfig config, string unifiedContainer, CancellationToken ct)

// AFTER  
private async Task<...> ExecuteActionsForUnifiedContainerAsync(
    List<...> actions, DockerGradingConfig config, string unifiedContainer, 
    string testCaseName, CancellationToken ct)  // Added testCaseName
```

**Start server/client with test case name:**
```csharp
// BEFORE
var startServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartServer {stage}";

// AFTER
var startServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartServer {stage} {testCaseName}";
```

**Read logs with test case name:**
```csharp
// BEFORE
var serverLogPath = $"/apps/server/server-stage-{stage}.log";

// AFTER
var serverLogPath = $"/apps/server/server-{testCaseName}-stage-{stage}.log";
```

**Snapshot pcap paths:**
```csharp
// BEFORE
var snapshotPath = Path.Combine(..., $"snapshot_stage{currentStage}.pcap");

// AFTER
var snapshotPath = Path.Combine(..., $"snapshot-{testCaseName}-stage{currentStage}.pcap");
```

**New export method per test case:**
```csharp
private async Task ExportLogsForTestCaseAsync(
    string unifiedContainer, string testCaseResultPath, string testCaseName)
{
    // Export server logs matching pattern: server-{testCaseName}-stage-*.log
    // Export client logs matching pattern: client-{testCaseName}-stage-*.log
    // Copy snapshot pcap files matching: snapshot-{testCaseName}-stage*.pcap
}
```

**Disabled log clearing:**
```csharp
private void ClearStageLogsInContainer(string unifiedContainer)
{
    // NO-OP: Files now include test case name, so no need to clear
    // This preserves all test case logs instead of overwriting them
}
```

#### B. unified-control.sh

**Accept test case name parameter:**
```bash
# BEFORE
ACTION=$1
STAGE=${2:-0}

# AFTER
ACTION=$1
STAGE=${2:-0}
TESTCASE=${3:-"default"}  # New parameter
```

**Write test case to temp file:**
```bash
# StartServer
echo "$STAGE" > /tmp/server_stage
echo "$TESTCASE" > /tmp/server_testcase  # NEW

# StartClient
echo "$STAGE" > /tmp/client_stage
echo "$TESTCASE" > /tmp/client_testcase  # NEW
```

**Update SendInput parameter position:**
```bash
# BEFORE
INPUT="${3:-}"  # Input was 3rd parameter

# AFTER
INPUT="${4:-}"  # Input is now 4th parameter (after TESTCASE)
```

#### C. server-wrapper.sh & client-wrapper.sh

**Read test case name:**
```bash
# Read TESTCASE from file
TESTCASE="default"
if [ -f /tmp/server_testcase ]; then
    TESTCASE=$(cat /tmp/server_testcase)
fi

# Export for dotnet process
export TESTCASE
```

**Use in log path:**
```bash
# BEFORE
exec dotnet "$DLL" >> "/apps/server/server-stage-${STAGE}.log" 2>&1

# AFTER
exec dotnet "$DLL" >> "/apps/server/server-${TESTCASE}-stage-${STAGE}.log" 2>&1
```

### 4. Data Flow

```
┌─────────────────────────────────────────────────────────────┐
│ DockerGradingService.cs                                      │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ For each test case (TC1, TC2, TC3, ..., TC6):          │ │
│ │                                                         │ │
│ │  1. ExecuteActionsForUnifiedContainerAsync(TC1)        │ │
│ │     ↓                                                   │ │
│ │  2. unified-control.sh StartServer stage testcase      │ │
│ │     ├─> Write stage to /tmp/server_stage              │ │
│ │     └─> Write TC1 to /tmp/server_testcase             │ │
│ │                                                         │ │
│ │  3. server-wrapper.sh                                  │ │
│ │     ├─> Read stage and TC1 from /tmp files            │ │
│ │     └─> Log to server-TC1-stage-N.log                 │ │
│ │                                                         │ │
│ │  4. ParsePcapForCurrentStageAsync(stage, port, TC1)    │ │
│ │     └─> Create snapshot-TC1-stage-N.pcap              │ │
│ │                                                         │ │
│ │  5. ExportLogsForTestCaseAsync(TC1)                    │ │
│ │     ├─> Copy server-TC1-stage-*.log to TC1/ProcessLogs│ │
│ │     ├─> Copy client-TC1-stage-*.log to TC1/ProcessLogs│ │
│ │     └─> Copy snapshot-TC1-stage*.pcap to TC1/         │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

## Benefits

### 1. Data Preservation
✅ All test case logs preserved - no data loss
✅ Can debug TC1 even if TC6 failed
✅ Historical record of all test case executions

### 2. Organization
✅ Logs organized by test case in separate directories
✅ Easy to find logs for specific test case
✅ Clean separation of test case data

### 3. Debugging
✅ Can compare logs across test cases
✅ Can see how student code behaves in different scenarios
✅ Pcap files available for each test case

### 4. No Conflicts
✅ Files don't overwrite each other
✅ Test cases don't interfere with each other
✅ No need to clear logs between test cases

## Testing & Verification

### 1. Run Grading with Multiple Test Cases
```bash
# Run grading for student with 6 test cases
dotnet run --project Application/SolutionGrader.CLI grade ...
```

### 2. Verify Directory Structure
```bash
cd Run_Log/1/student/StudentCode/
ls -la */ProcessLogs/
# Should see files for ALL test cases, not just the last one
```

### 3. Check File Count
```bash
# Count server logs - should be 1 per stage per test case
find Run_Log/1/student/StudentCode/ -name "server-TC*-stage-*.log" | wc -l

# Count client logs
find Run_Log/1/student/StudentCode/ -name "client-TC*-stage-*.log" | wc -l

# Count snapshots
find Run_Log/1/student/StudentCode/ -name "snapshot-TC*-stage*.pcap" | wc -l
```

### 4. Verify File Content
```bash
# TC1 server log should contain TC1-specific output
cat Run_Log/1/student/StudentCode/TC1/ProcessLogs/server-TC1-stage-1.log

# TC2 server log should contain TC2-specific output (not TC1!)
cat Run_Log/1/student/StudentCode/TC2/ProcessLogs/server-TC2-stage-1.log
```

## Backward Compatibility

### Docker Image Update Required
The changes require updated Docker scripts (unified-control.sh, server-wrapper.sh, client-wrapper.sh).

**Action Required:**
1. Build new Docker image with updated scripts
2. Push to `fptuxaes/aes-dotnet8-console:latest`
3. Or use new tag and update test kit Environment.xlsx

### Graceful Degradation
If test case name is not provided (backward compatibility):
- Scripts default to `TESTCASE="default"`
- Files created as: `server-default-stage-1.log`
- System still works, just without per-test-case separation

## Migration Notes

### For Existing Grading Runs
Old grading results with the previous structure remain unchanged. The new structure only applies to new grading runs.

### For Test Kit Creators
No changes required to test kits - the test case name is automatically extracted from the directory structure (TC1, TC2, etc.).

## Troubleshooting

### Logs Still Missing
1. Check Docker image version - ensure using latest with updated scripts
2. Verify unified-control.sh accepts 3 parameters: `ACTION STAGE TESTCASE`
3. Check /tmp files in container: `docker exec <container> cat /tmp/server_testcase`

### Files Have "default" Instead of Test Case Name
- Test case name parameter not being passed correctly
- Check DockerGradingService.cs passes `testCase.Name` to ExecuteActionsForUnifiedContainerAsync

### Logs in Wrong Directory
- Check ExportLogsForTestCaseAsync is using correct test case result path
- Verify test case name matches directory name (TC1, TC2, etc.)

## Future Enhancements

### Possible Improvements
1. Compress old test case logs to save space
2. Add test case summary file in each directory
3. Generate combined report across all test cases
4. Archive logs to cloud storage for long-term retention

## Conclusion
This fix ensures complete preservation of all test case logs and pcap files by including the test case name in file paths throughout the grading pipeline. Each test case's data is now isolated in its own directory, making debugging and analysis much easier.
