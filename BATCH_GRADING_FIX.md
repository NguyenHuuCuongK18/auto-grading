# Batch Grading and Port Override Fix

## Summary of Issues Fixed

This document explains the fixes applied to address port configuration and DLL modification issues for batch grading.

## Issue 1: DLL Not Found in Container (Fixed in commit 8a79f10)

### Problem
When DLL modification was enabled (`UseDllModificationFallback = true`), files were copied to the container with a temporary folder name, but the code tried to execute DLLs using the original folder name, causing "DLL not found" errors.

### Root Cause
```csharp
// BEFORE (BUGGY):
var tempStagingDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_Server_{studentCode}_{Guid}");
dirToCopy = tempStagingDir;
folderName = Path.GetFileName(tempStagingDir);  // e.g., "AutoGrading_Server_student_abc123"

// Files copied to: /apps/AutoGrading_Server_student_abc123/
// But STARTSERVER tried to run from: /apps/OriginalFolderName/
```

### Solution
Preserve the original folder name when copying to the container:
```csharp
// AFTER (FIXED):
var tempStagingDir = Path.Combine(Path.GetTempPath(), $"AutoGrading_Server_{studentCode}_{Guid}");
dirToCopy = tempStagingDir;
// Keep folderName as original - DO NOT change to temp name

// Files copied to: /apps/OriginalFolderName/
// STARTSERVER runs from: /apps/OriginalFolderName/ ✓
```

This ensures path consistency throughout the grading process.

## Issue 2: DLL Modification Not Enabled for Batch Grading (Fixed in commit 6abdb53)

### Problem
Students often hardcode ports in their code:
- Student A: hardcodes `4000`
- Student B: hardcodes `5000`
- Student C: hardcodes `8080`

In batch grading, each student needs a unique port:
- Student A: container on `8000`
- Student B: container on `8001`
- Student C: container on `8002`

Without DLL modification, clients can't connect because:
```
Student A DLL: tries to connect to port 4000
Student A container: running on port 8000
Result: Connection failed ❌
```

### Root Cause
The CLI's `CliGradingConfiguration` didn't have the `UseDllModificationFallback` property, so it defaulted to `false` in `DockerGradingConfig`. This meant DLL modification never ran for CLI batch grading.

### Solution
1. **Added `UseDllModificationFallback` to `CliGradingConfiguration`** with default value `true`
2. **Pass the property to `DockerGradingConfig`** in CLI service
3. **Added CLI parameter support** for explicit control

Now DLL modification is enabled by default for batch grading.

## How DLL Modification Works

### Common Hardcoded Values Replaced

The system searches for these common hardcoded values in student DLLs:

**IP Addresses:**
- `localhost`
- `127.0.0.1`
- `0.0.0.0`

**Ports:**
- `3000`, `4000`, `5000`, `8000`, `8080`, `5001`, `5002`, `7000`, `7001`, `9000`

### Replacement Strategy

**For Server DLLs:**
```csharp
// Find and replace:
localhost/127.0.0.1 → 0.0.0.0 (bind to all interfaces)
[hardcoded port] → allocated port (e.g., 8000)
```

**For Client DLLs:**
```csharp
// Find and replace:
localhost/127.0.0.1 → host.docker.internal (Docker host gateway)
[hardcoded port] → allocated port (e.g., 8000)
```

### Batch Grading Process

```
Environment.xlsx: Code_Container_Host_Port = 8000 (starting port)

┌─────────────────────────────────────────────────────────────┐
│ Student 1                                                   │
├─────────────────────────────────────────────────────────────┤
│ 1. PortAllocator: allocate port 8000                       │
│ 2. Copy student DLL to temp staging directory              │
│ 3. DLL modification: 4000 → 8000, localhost → 0.0.0.0     │
│ 4. Copy modified DLL to container: /apps/Project11/        │
│ 5. Create container: 8000:8000 (exposed port mapping)      │
│ 6. Start server: dotnet /apps/Project11/Project11.dll      │
│ 7. Server binds to 0.0.0.0:8000 ✓                          │
│ 8. Client connects to host.docker.internal:8000 ✓          │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ Student 2                                                   │
├─────────────────────────────────────────────────────────────┤
│ 1. PortAllocator: allocate port 8001                       │
│ 2. Copy student DLL to temp staging directory              │
│ 3. DLL modification: 5000 → 8001, localhost → 0.0.0.0     │
│ 4. Copy modified DLL to container: /apps/Project11/        │
│ 5. Create container: 8001:8001 (exposed port mapping)      │
│ 6. Start server: dotnet /apps/Project11/Project11.dll      │
│ 7. Server binds to 0.0.0.0:8001 ✓                          │
│ 8. Client connects to host.docker.internal:8001 ✓          │
└─────────────────────────────────────────────────────────────┘

... and so on for each student
```

## Port Configuration Flow

### 1. Read Starting Port from Environment.xlsx
```csharp
int startingPort = ReadStartingPortFromEnvironmentXlsx(testKitPath);
// Example: Environment.xlsx says Code_Container_Host_Port = 8000
// Result: startingPort = 8000
```

### 2. Create PortAllocator with Starting Port
```csharp
using var portAllocator = new PortAllocator(startingPort);
// Allocator will give: 8000, 8001, 8002, ... sequentially
```

### 3. Allocate Port for Current Student
```csharp
int allocatedPort = portAllocator.AllocatePort();
// Student 1: allocatedPort = 8000
// Student 2: allocatedPort = 8001
// Student 3: allocatedPort = 8002
```

### 4. Create DockerGradingConfig
```csharp
var dockerConfig = new DockerGradingConfig
{
    CodeContainerInternalPort = allocatedPort,  // e.g., 8001
    CodeContainerHostPort = allocatedPort,      // e.g., 8001
    UseDllModificationFallback = true           // ENABLED for batch grading
};
```

### 5. DLL Modification Applies Allocated Port
```csharp
// Inside DockerGradingService.CopyFilesToContainersAsync:
dllModService.CheckAndPatchIfNeeded(
    tempStagingDir,
    config.ServerProjectName,
    isServer: true,
    targetPort: config.CodeContainerHostPort  // e.g., 8001
);
```

### 6. Container Created with Allocated Port
```csharp
var serverBase = new DockerBase
{
    ContainerPort = config.CodeContainerInternalPort,  // e.g., 8001
    HostPort = config.CodeContainerHostPort            // e.g., 8001
};
```

### 7. Network Monitoring on Allocated Port
```csharp
_networkMonitor.MonitorPort = config.CodeContainerHostPort;  // e.g., 8001
```

## Benefits of These Fixes

### 1. Port Consistency
All components now use the same allocated port:
- ✅ Container port binding
- ✅ DLL modification
- ✅ Network monitoring
- ✅ Client connection

### 2. Batch Grading Support
Sequential port allocation prevents conflicts:
- ✅ Student 1: port 8000
- ✅ Student 2: port 8001
- ✅ Student 3: port 8002
- ✅ Student N: port 8000+N-1

### 3. Client-Server Communication
DLL modification ensures connectivity:
- ✅ Server binds to `0.0.0.0:[allocated port]`
- ✅ Client connects to `host.docker.internal:[allocated port]`
- ✅ Network traffic flows through exposed port

### 4. Data Integrity
Original student files remain untouched:
- ✅ Modifications happen on temp copies
- ✅ Temp directories cleaned up after use
- ✅ No port accumulation across gradings

## CLI Usage

### Default Behavior (DLL Modification Enabled)
```bash
dotnet run --project Application/SolutionGrader.Cli dockergrade \
  --submit Submit \
  --testkit Testkit_Q1_PRN222
```

### Explicitly Enable DLL Modification
```bash
dotnet run --project Application/SolutionGrader.Cli dockergrade \
  --submit Submit \
  --testkit Testkit_Q1_PRN222 \
  --dll-mod=true
```

### Disable DLL Modification (Not Recommended)
```bash
dotnet run --project Application/SolutionGrader.Cli dockergrade \
  --submit Submit \
  --testkit Testkit_Q1_PRN222 \
  --no-dll-mod
```

**Note:** Disabling DLL modification may cause connection failures if students hardcoded ports.

## Verification Steps

### 1. Check Logs for Port Reading
```
[Port Config] Reading starting port from Environment.xlsx: ...
[Port Config] Successfully read Code_Container_Host_Port=8000 from Environment.xlsx
[Port Config] PortAllocator will start from port 8000 and allocate sequentially
```

### 2. Check Logs for Port Allocation
```
[PortAllocator] Allocated port 8000 (next allocation will try 8001)
[PortAllocator] Allocated port 8001 (next allocation will try 8002)
```

### 3. Check Logs for DLL Modification
```
[DllMod] Created temp staging directory for server: /tmp/AutoGrading_Server_...
[DllMod] Applying DLL modification to temp copy (port: 8000)
[DllMod] Successfully patched DLL: 2 IP replacements, 3 port replacements
```

### 4. Check Logs for Container Creation
```
[Docker] Server container created with port 8000:8000 exposed
[Docker] Copied server files from ... to container .../apps/Project11
```

### 5. Check Logs for Server Startup
```
[Docker] Starting server: dotnet /apps/Project11/Project11.dll
Server started, output: ...
```

### 6. Verify with Docker Commands
```bash
# List running containers
docker ps

# Should show:
# CONTAINER ID   IMAGE                  PORTS
# abc123...      dotnet-image          0.0.0.0:8000->8000/tcp
# def456...      dotnet-image          0.0.0.0:8001->8001/tcp
```

## Troubleshooting

### Issue: "DLL file not found" error
**Cause:** Folder name mismatch between copy and execution
**Fix:** Applied in commit 8a79f10 - original folder names now preserved

### Issue: Client can't connect to server
**Cause:** Port mismatch (hardcoded vs. allocated)
**Fix:** Applied in commit 6abdb53 - DLL modification now enabled by default

### Issue: Port already in use
**Cause:** Previous containers not cleaned up
**Solution:** 
```bash
# Clean up all containers
docker stop $(docker ps -aq) && docker rm $(docker ps -aq)

# Reset port allocation
rm /tmp/AutoGrading_NextPort.txt  # Linux/macOS
# or
del %TEMP%\AutoGrading_NextPort.txt  # Windows
```

### Issue: DLL modification not working
**Check:**
1. Is `UseDllModificationFallback` enabled? (default: true)
2. Are temp directories being created? (check logs)
3. Is the DLL actually being patched? (check for "Successfully patched" in logs)
4. Are common ports present in the student's DLL? (3000, 4000, 5000, 8000, etc.)

## Summary

These fixes ensure that:
1. ✅ DLL files are found in containers (correct folder names)
2. ✅ Ports are read from Environment.xlsx correctly
3. ✅ DLL modification patches hardcoded ports to allocated ports
4. ✅ Each student gets a unique sequential port in batch grading
5. ✅ Client-server communication works via host.docker.internal
6. ✅ Network monitoring captures traffic on correct ports
7. ✅ Original student files remain unchanged

Batch grading now works correctly with proper port allocation and DLL modification! 🎉
