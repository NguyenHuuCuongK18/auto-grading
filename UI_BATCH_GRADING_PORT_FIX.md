# UI Batch Grading Port Allocation Fix

## Executive Summary

**Issue**: In UI batch grading mode, all students were being assigned the same port (e.g., 8000) instead of unique sequential ports, causing Docker container conflicts and grading failures.

**Root Cause**: `GradingOrchestrationService` was creating a new `PortAllocator` instance for each student, and each new allocator started from the same base port read from Environment.xlsx.

**Fix**: Removed port allocation logic from `GradingOrchestrationService` and made it use the pre-allocated ports passed in via `GradingConfiguration` from the caller (`GradingWindow`).

**Result**: Each student in batch grading now correctly receives a unique sequential port (8000, 8001, 8002, etc.).

---

## Problem Analysis

### What Was Happening (BUGGY BEHAVIOR)

When grading 3 students in parallel batch mode:

```
Student 1: Port 8000 ✓ (first to allocate)
Student 2: Port 8000 ❌ (conflict!)
Student 3: Port 8000 ❌ (conflict!)
```

### Architecture Before Fix

```
GradingWindow.StartGradingAsync
├─ Creates _sharedPortAllocator(8000)
├─ For Student 1:
│  ├─ Allocates port 8000 from _sharedPortAllocator
│  ├─ Sets studentConfig.CodeContainerHostPort = 8000
│  └─ Calls _gradingService.StartGradingAsync(student1, studentConfig)
│     ├─ GradingOrchestrationService.StartGradingAsync
│     │  └─ Creates NEW _portAllocator(8000)  ← BUG!
│     └─ GradingOrchestrationService.GradeStudentAsync
│        ├─ Allocates port from NEW allocator → 8000
│        └─ OVERWRITES studentConfig ports with 8000
│
├─ For Student 2 (parallel):
│  ├─ Allocates port 8001 from _sharedPortAllocator
│  ├─ Sets studentConfig.CodeContainerHostPort = 8001
│  └─ Calls _gradingService.StartGradingAsync(student2, studentConfig)
│     ├─ GradingOrchestrationService.StartGradingAsync
│     │  └─ Creates NEW _portAllocator(8000)  ← BUG!
│     └─ GradingOrchestrationService.GradeStudentAsync
│        ├─ Allocates port from NEW allocator → 8000  ← CONFLICT!
│        └─ OVERWRITES studentConfig ports with 8000
│
└─ For Student 3 (parallel):
   ├─ Allocates port 8002 from _sharedPortAllocator
   ├─ Sets studentConfig.CodeContainerHostPort = 8002
   └─ Calls _gradingService.StartGradingAsync(student3, studentConfig)
      ├─ GradingOrchestrationService.StartGradingAsync
      │  └─ Creates NEW _portAllocator(8000)  ← BUG!
      └─ GradingOrchestrationService.GradeStudentAsync
         ├─ Allocates port from NEW allocator → 8000  ← CONFLICT!
         └─ OVERWRITES studentConfig ports with 8000
```

### Why Creating New PortAllocator Failed

The `PortAllocator` class uses a **file-based mutex** (`/tmp/AutoGrading_NextPort.txt`) to synchronize port allocation across threads. However, when each student creates a **fresh PortAllocator instance**, they all:

1. Read the starting port from Environment.xlsx (8000)
2. Check the tracking file (empty on first run)
3. Initialize their allocator with port 8000
4. Allocate port 8000 (because they all start from the same base)

The mutex prevents simultaneous writes, but it **doesn't help** when each instance starts from the same base port before any allocation has been written to the file.

### Code Location of Bug

**File**: `Application/SolutionGrader.UI/Services/GradingOrchestrationService.cs`

**Problem Areas**:
- **Line 39**: `private PortAllocator? _portAllocator;` - Duplicate allocator per student
- **Lines 145-194**: Port allocator initialization in `StartGradingAsync` - Creates new allocator
- **Lines 421-443**: Port allocation in `GradeStudentAsync` - Allocates new port and overwrites config

---

## Solution Details

### What Changed

1. **Removed Duplicate Port Allocator**
   - Deleted `_portAllocator` field from `GradingOrchestrationService`
   - Removed initialization code that created new allocators
   - Removed disposal code for allocator

2. **Use Pre-Allocated Ports**
   - `GradeStudentAsync` now reads port from `config.CodeContainerHostPort`
   - No new port allocation - just uses what's passed in
   - Added validation to ensure port is in valid range (1-65535)

3. **Cleaned Up Unused Code**
   - Removed `ReadStartingPortFromEnvironmentXlsx` method (now only in GradingWindow)
   - Simplified service to focus on orchestration, not resource allocation

### Architecture After Fix

```
GradingWindow.StartGradingAsync
├─ Creates ONE _sharedPortAllocator(8000)
├─ Clears port tracking file (fresh start)
├─ For Student 1:
│  ├─ Allocates port 8000 from _sharedPortAllocator ✓
│  ├─ Sets studentConfig.CodeContainerHostPort = 8000
│  └─ Calls _gradingService.StartGradingAsync(student1, studentConfig)
│     └─ GradingOrchestrationService.GradeStudentAsync
│        └─ Uses config.CodeContainerHostPort (8000) ✓
│
├─ For Student 2 (parallel):
│  ├─ Allocates port 8001 from _sharedPortAllocator ✓
│  ├─ Sets studentConfig.CodeContainerHostPort = 8001
│  └─ Calls _gradingService.StartGradingAsync(student2, studentConfig)
│     └─ GradingOrchestrationService.GradeStudentAsync
│        └─ Uses config.CodeContainerHostPort (8001) ✓
│
└─ For Student 3 (parallel):
   ├─ Allocates port 8002 from _sharedPortAllocator ✓
   ├─ Sets studentConfig.CodeContainerHostPort = 8002
   └─ Calls _gradingService.StartGradingAsync(student3, studentConfig)
      └─ GradingOrchestrationService.GradeStudentAsync
         └─ Uses config.CodeContainerHostPort (8002) ✓
```

### Key Changes in Code

**Before** (GradingOrchestrationService.cs):
```csharp
private PortAllocator? _portAllocator;

// In StartGradingAsync:
_portAllocator = new PortAllocator(startingPort);

// In GradeStudentAsync:
int portToUse = _portAllocator.AllocatePort();  // Allocates 8000, 8000, 8000...
```

**After** (GradingOrchestrationService.cs):
```csharp
// No _portAllocator field

// In GradeStudentAsync:
int portToUse = config.CodeContainerHostPort;  // Uses 8000, 8001, 8002...
```

---

## Verification

### Expected Behavior After Fix

When grading 3 students in batch mode with Environment.xlsx specifying port 8000:

```
Student 1: Port 8000 ✓ (allocated by GradingWindow)
Student 2: Port 8001 ✓ (allocated by GradingWindow)
Student 3: Port 8002 ✓ (allocated by GradingWindow)
```

### Log Messages to Verify

**Before the fix** (BUGGY):
```
[Port Config] Initialized SHARED PortAllocator with starting port 8000 for batch grading
[UI] Allocated port 8000 for student SE001 (from shared allocator)
[Port Config] [SE001] Allocated port 8000 via PortAllocator (sequential, no reuse)
[UI] Allocated port 8001 for student SE002 (from shared allocator)
[Port Config] [SE002] Allocated port 8000 via PortAllocator (sequential, no reuse) ← BUG!
[UI] Allocated port 8002 for student SE003 (from shared allocator)
[Port Config] [SE003] Allocated port 8000 via PortAllocator (sequential, no reuse) ← BUG!
```

**After the fix** (CORRECT):
```
[Port Config] Initialized SHARED PortAllocator with starting port 8000 for batch grading
[UI] Allocated port 8000 for student SE001 (from shared allocator)
[Port Config] [SE001] Using pre-allocated port 8000 for container, DLL modification, and network monitoring
[UI] Allocated port 8001 for student SE002 (from shared allocator)
[Port Config] [SE002] Using pre-allocated port 8001 for container, DLL modification, and network monitoring ✓
[UI] Allocated port 8002 for student SE003 (from shared allocator)
[Port Config] [SE003] Using pre-allocated port 8002 for container, DLL modification, and network monitoring ✓
```

### Docker Container Verification

Check running containers during batch grading:

```bash
docker ps --format "table {{.Names}}\t{{.Ports}}"
```

**Expected Output**:
```
NAMES                    PORTS
auto-grading-SE001      0.0.0.0:8000->8000/tcp
auto-grading-SE002      0.0.0.0:8001->8001/tcp
auto-grading-SE003      0.0.0.0:8002->8002/tcp
```

**Before Fix (BUGGY)**:
```
NAMES                    PORTS
auto-grading-SE001      0.0.0.0:8000->8000/tcp
Error: Port 8000 already in use
Error: Port 8000 already in use
```

---

## Testing Recommendations

### Test Case 1: Sequential Grading (Baseline)

**Setup**:
- Environment.xlsx: Code_Container_Host_Port = 8000
- Batch size: 1 (sequential)
- Students: 3

**Expected**:
- Each student completes successfully
- Ports used: 8000, 8001, 8002 (sequential)

**Result**: ✅ Should work (this scenario always worked)

### Test Case 2: Parallel Batch Grading (The Bug)

**Setup**:
- Environment.xlsx: Code_Container_Host_Port = 8000
- Batch size: 3 (parallel)
- Students: 3

**Before Fix**:
- ❌ All students get port 8000
- ❌ Docker conflicts
- ❌ Students 2 and 3 fail

**After Fix**:
- ✅ Students get ports 8000, 8001, 8002
- ✅ No Docker conflicts
- ✅ All students grade successfully

### Test Case 3: Large Batch

**Setup**:
- Environment.xlsx: Code_Container_Host_Port = 8000
- Batch size: 10 (parallel)
- Students: 100

**Expected**:
- Ports 8000-8099 allocated sequentially
- 10 batches of 10 students each
- Each batch runs in parallel without port conflicts
- All 100 students complete successfully

**Result**: ✅ Should work with the fix

---

## Impact Analysis

### What This Fix Affects

**Direct Impact**:
- ✅ UI batch grading mode (parallel students)
- ✅ Port allocation uniqueness
- ✅ Docker container creation

**No Impact**:
- ✅ CLI grading (uses different code path)
- ✅ Sequential grading (1 student at a time)
- ✅ Test kit configuration
- ✅ DLL modification
- ✅ Network monitoring

### Backward Compatibility

**Fully Backward Compatible**: ✅
- No changes to public APIs
- No changes to configuration files
- No changes to CLI behavior
- Works with existing test kits

### Performance Impact

**Improvement**: ✅
- Fewer file system operations (no duplicate Environment.xlsx reads)
- Fewer object allocations (no duplicate PortAllocator instances)
- Simpler call stack (removed unnecessary layer)

---

## Related Files

### Modified Files

1. **Application/SolutionGrader.UI/Services/GradingOrchestrationService.cs**
   - Removed: `_portAllocator` field
   - Removed: Port allocator initialization (lines 145-194)
   - Removed: Port allocation logic (lines 421-443)
   - Removed: `ReadStartingPortFromEnvironmentXlsx` method
   - Changed: `GradeStudentAsync` now uses `config.CodeContainerHostPort`

### Unchanged Files (For Context)

1. **Application/SolutionGrader.UI/GradingWindow.xaml.cs**
   - Still creates `_sharedPortAllocator`
   - Still allocates ports for each student
   - Still passes ports via `GradingConfiguration`

2. **Lib/SolutionGrader.Core/Services/PortAllocator.cs**
   - No changes to allocation logic
   - Still uses file-based mutex
   - Still supports sequential allocation

---

## Summary

**Problem**: Duplicate port allocation caused all students in batch grading to get the same port.

**Solution**: Single source of truth for port allocation (GradingWindow's shared allocator).

**Result**: Each student gets unique sequential port (8000, 8001, 8002...).

**Status**: ✅ Fixed, built successfully, ready for testing.

---

## Next Steps

1. **Testing** ✅
   - Test sequential grading (baseline)
   - Test parallel batch grading (the fix)
   - Test large batch (stress test)

2. **Monitoring** 📊
   - Check log messages for "pre-allocated port"
   - Verify Docker containers have unique ports
   - Confirm no port conflict errors

3. **Documentation** 📝
   - Update user documentation if needed
   - Add troubleshooting guide for port issues

---

**Fixed By**: GitHub Copilot Coding Agent  
**Status**: ✅ Complete, Build Verified, Ready for Testing
