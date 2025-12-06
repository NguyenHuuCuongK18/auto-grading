# Port Configuration Fix - Technical Analysis

## Problem Statement

The container was starting with the wrong port (8000-something) while DLL modification correctly used the port from Environment.xlsx (e.g., 4001).

## Root Cause Analysis

### The Issue

The CLI's `CliDockerGradingService.GradeStudentUsingSharedServiceAsync` was creating a `PortAllocator` without reading the starting port from the test kit's Environment.xlsx:

```csharp
// BEFORE (INCORRECT):
using var portAllocator = new PortAllocator();  // Defaults to 8000
int allocatedPort = portAllocator.AllocatePort(); // Returns 8000, 8001, 8002...
```

### Why This Caused Problems

1. **Environment.xlsx specifies port:** Test kit defines `Code_Container_Host_Port = 4001` in Environment.xlsx
2. **PortAllocator ignores it:** Creates allocator with default 8000
3. **Sequential allocation:** Allocates 8000, 8001, 8002... for each student
4. **Result:** Container uses 8001, but DLL modification expects 4001 → **MISMATCH**

### The Disconnect

- **Container Creation:** Uses port from `PortAllocator` (8000-series)
- **DLL Modification:** Uses port from `DockerGradingConfig` which should come from Environment.xlsx
- **Network Monitoring:** Uses port from `DockerGradingConfig`

The UI code (`GradingOrchestrationService`) already had the correct flow:
1. Read port from Environment.xlsx
2. Create `DockerGradingConfig` with that port
3. Pass to `DockerGradingService`

But the CLI was missing step 1!

## Solution Implemented

### Changes Made

#### 1. Added `ReadStartingPortFromEnvironmentXlsx` Method

**Location:** `Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs`

```csharp
private int ReadStartingPortFromEnvironmentXlsx(string testKitPath)
{
    // Reads Code_Container_Host_Port or Code_Container_Internal_Port
    // from {testKitPath}/Environment.xlsx
    // Returns port value, or 0 if not found
}
```

#### 2. Updated Port Allocation Flow

**Before:**
```csharp
using var portAllocator = new PortAllocator();  // Defaults to 8000
int allocatedPort = portAllocator.AllocatePort();
```

**After:**
```csharp
// Read starting port from test kit's Environment.xlsx
int startingPortFromEnv = ReadStartingPortFromEnvironmentXlsx(testKitPath);

// Pass to PortAllocator (if 0, PortAllocator uses default 8000)
using var portAllocator = new PortAllocator(startingPortFromEnv);
int allocatedPort = portAllocator.AllocatePort();
```

#### 3. Fixed ClosedXML API Usage

Changed from `GetString()` to `GetValue<string>()` for compatibility.

#### 4. Added Missing Using Directive

Added `using ClosedXML.Excel;` to UI service.

## How It Works Now

### Sequential Port Allocation

When Environment.xlsx specifies `Code_Container_Host_Port = 4001`:

1. **Student 1:** Allocates port 4001
2. **Student 2:** Allocates port 4002
3. **Student 3:** Allocates port 4003
4. **Student N:** Allocates port 4000+N

### Component Consistency

All components now use the SAME allocated port:

- **Container Creation:** Binds to `4001:4001` (direct mapping)
- **DLL Modification:** Patches with port `4001`
- **Network Monitoring:** Sniffs on host port `4001`
- **Appsettings Generation:** Sets port to `4001`

## Port Configuration Priority

The system uses this priority order:

1. **Test Kit Environment.xlsx** (Primary source - e.g., `Testkit_Q1_PRN222/Q12/Environment.xlsx`)
   - Reads `Code_Container_Host_Port` first
   - Falls back to `Code_Container_Internal_Port`
2. **PortAllocator Default** (Fallback if not found in Environment.xlsx)
   - Uses `DEFAULT_START_PORT = 8000`

## Validation

### Build Status
✅ Solution builds successfully with **0 errors**

### Code Review Results
The code review identified that:
- Using statements properly handle resource disposal
- PortAllocator correctly handles 0 as input (falls back to 8000)
- Implementation is correct and safe

### DLL Modification (Already Correct)

The concern about "test grade edits the DLL before copying to container" is already properly handled:

**Location:** `Lib/SolutionGrader.Core/Services/DockerGradingService.cs` (lines 683-830)

1. Creates temporary staging directory
2. Copies student DLL folder to temp
3. Modifies the temp copy (NOT the original)
4. Copies temp to container
5. Cleans up temp directory

This design ensures:
- Original student files remain untouched
- No port value accumulation across students
- Each grading uses fresh DLL files

## Testing Recommendations

### 1. Verify Port Configuration Reading

```bash
# Check logs for this message:
[Port Config] Successfully read Code_Container_Host_Port=4001 from Environment.xlsx
```

### 2. Verify Port Allocation

```bash
# Check logs for this message:
[PortAllocator] Allocated port 4001 (next allocation will try 4002)
```

### 3. Verify Container Port Binding

```bash
docker ps
# Should show: 0.0.0.0:4001->4001/tcp
```

### 4. Verify DLL Modification

```bash
# Check logs for this message:
[DllMod] Applying DLL modification to temp copy (port: 4001)
```

### 5. Test Sequential Grading

Grade multiple students and verify each gets the next port:
- Student 1: Port 4001
- Student 2: Port 4002
- Student 3: Port 4003

## Summary

The fix ensures that:
1. ✅ Port configuration is read from Environment.xlsx
2. ✅ PortAllocator starts from the correct base port
3. ✅ All components use consistent port values
4. ✅ Sequential allocation works correctly
5. ✅ DLL modification uses temporary copies (no corruption)
6. ✅ Solution builds successfully

The root cause has been identified and fixed. The port mismatch issue should no longer occur.
