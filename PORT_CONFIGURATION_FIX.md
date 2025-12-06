# Port Configuration and Environment.xlsx Early Loading - Issues and Solutions

## Issues Identified

Based on the user's feedback, there are several critical issues with port configuration and environment loading:

### 1. Port Configuration Not Being Read from environment.xlsx
**Problem**: The code defaults to port 8000 instead of reading port configuration from environment.xlsx early in the grading process.

**Current Behavior**:
- `PortAllocator` allocates ports from 8000-8099 range
- `environment.xlsx` has `MonitorPort` configuration but it's not being used as the primary port source
- Test kit configuration gets loaded during grading, but environment config should be read BEFORE grading starts

**Root Cause**:
- In `ExcelSuiteLoader.cs`, `ReadEnvironmentConfig()` is called during suite loading (line 31)
- But the port allocation happens in `LibGradingService` before suite is loaded
- The `MonitorPort` from environment.xlsx is treated as "legacy" (line 340) and GraderPort takes precedence
- However, GraderPort is never set from environment.xlsx

### 2. DLL Modification Using Wrong Port
**Problem**: DLL modification may be using default port 8000 instead of the dynamically allocated port.

**Current Behavior**:
- `DllModificationService` patches IPs and ports in DLLs
- The port used for patching should come from `config.CodeContainerHostPort`
- But there's no guarantee this port matches what's in environment.xlsx

**Root Cause**:
- Port allocation happens in `LibGradingService.GradeStudentAsync()` via `PortAllocator.AllocatePort()`
- This allocated port is set to `dockerConfig.CodeContainerHostPort`
- DLL modification happens in `DockerGradingService.CopyFilesToContainersAsync()` using this port
- But if environment.xlsx specifies a different port, there's a mismatch

### 3. No Logging of Actual Client/Server Configuration
**Problem**: Hard to debug because there's no logging showing what IP/port the client and server are actually using.

**Current Behavior**:
- Logs show container creation and port mapping
- But don't show the actual appsettings.json content or DLL-patched values

## Proposed Solutions

### Solution 1: Early Environment.xlsx Loading

**Implementation**:
1. Add a new method to load environment.xlsx BEFORE grading starts
2. Read the port configuration and use it as the initial port for allocation
3. If environment.xlsx specifies a port, use that instead of allocating from the pool

**Code Changes in `GradingOrchestrationService.cs`**:
```csharp
private async Task GradeStudentAsync(...)
{
    // ... existing code ...
    
    // BEFORE allocating port, read environment.xlsx from test kit
    var envConfig = await LoadTestKitEnvironmentConfigAsync(testKitPath);
    
    // Use port from environment.xlsx if specified, otherwise allocate from pool
    int allocatedPort;
    if (envConfig?.MonitorPort > 0)
    {
        allocatedPort = envConfig.MonitorPort;
        _logger.LogInfo($"[{student.StudentCode}] Using port from environment.xlsx: {allocatedPort}");
    }
    else
    {
        var portAllocator = new PortAllocator();
        allocatedPort = portAllocator.AllocatePort();
        _logger.LogInfo($"[{student.StudentCode}] Allocated port from pool: {allocatedPort}");
    }
    
    dockerConfig.CodeContainerHostPort = allocatedPort;
    dockerConfig.CodeContainerInternalPort = allocatedPort;
    
    // ... rest of grading ...
}
```

**New Helper Method**:
```csharp
private async Task<EnvironmentConfiguration?> LoadTestKitEnvironmentConfigAsync(string testKitPath)
{
    try
    {
        var envPath = Path.Combine(testKitPath, "environment.xlsx");
        if (!File.Exists(envPath))
        {
            envPath = Path.Combine(testKitPath, "Environment.xlsx");
            if (!File.Exists(envPath)) return null;
        }
        
        // Load using ExcelSuiteLoader logic
        using var wb = new XLWorkbook(envPath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Config", StringComparison.OrdinalIgnoreCase));
        if (ws == null) return null;
        
        var config = new EnvironmentConfiguration();
        int startRow = 2; // Skip header
        
        for (int r = startRow; r <= Math.Min(100, ws.RowCount()); r++)
        {
            var key = ws.Cell(r, 1).GetString().Trim();
            var value = ws.Cell(r, 2).GetString().Trim();
            
            if (string.IsNullOrEmpty(key)) continue;
            
            if (key.Equals("MonitorPort", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var port))
                {
                    config.MonitorPort = port;
                    _logger.LogInfo($"[Environment] Loaded MonitorPort from environment.xlsx: {port}");
                }
            }
            // Load other config as needed...
        }
        
        return config;
    }
    catch (Exception ex)
    {
        _logger.LogWarning($"Failed to load environment config: {ex.Message}");
        return null;
    }
}
```

### Solution 2: Enhanced DLL Modification Logging

**Implementation**:
Add detailed logging before and after DLL modification to show what's being patched.

**Code Changes in `DllModificationService.cs`**:
```csharp
public async Task<bool> TryModifyDllsAsync(...)
{
    Console.WriteLine("=== DLL MODIFICATION SUMMARY ===");
    Console.WriteLine($"Server DLL: {serverDllPath ?? "N/A"}");
    Console.WriteLine($"Client DLL: {clientDllPath ?? "N/A"}");
    Console.WriteLine($"Target Server IP: 0.0.0.0 (bind all interfaces)");
    Console.WriteLine($"Target Server Port: {targetPort}");
    Console.WriteLine($"Target Client IP: host.docker.internal");
    Console.WriteLine($"Target Client Port: {targetPort}");
    Console.WriteLine($"Common IPs to patch: localhost, 127.0.0.1, 0.0.0.0");
    Console.WriteLine($"Common Ports to patch: 3000, 4000, 5000, 5001, 7000, 8000, 8080, 9000");
    Console.WriteLine("================================");
    
    // ... existing modification code ...
    
    Console.WriteLine("=== DLL MODIFICATION RESULTS ===");
    Console.WriteLine($"Server DLL modified: {serverModified}");
    Console.WriteLine($"Client DLL modified: {clientModified}");
    Console.WriteLine("=================================");
    
    return serverModified || clientModified;
}
```

### Solution 3: Appsettings.json Content Logging

**Implementation**:
Log the actual appsettings.json content being generated.

**Code Changes in `DockerGradingService.cs`**:
```csharp
private async Task GenerateAppsettingsForBothContainersAsync(...)
{
    // ... existing code ...
    
    Console.WriteLine("=== APPSETTINGS.JSON CONFIGURATION ===");
    Console.WriteLine($"Server Container: {serverContainer}");
    Console.WriteLine($"  - Kestrel URL: http://0.0.0.0:{port}");
    Console.WriteLine($"  - ConnectionString: Server={config.DatabaseContainerName},{dbPort};...");
    Console.WriteLine($"Client Container: {clientContainer}");
    Console.WriteLine($"  - Base Address: http://host.docker.internal:{port}");
    Console.WriteLine($"  - ConnectionString: Server={config.DatabaseContainerName},{dbPort};...");
    Console.WriteLine("======================================");
    
    // ... rest of generation code ...
}
```

### Solution 4: Network Configuration Logging

**Implementation**:
Add a summary log at the start of grading showing the complete network configuration.

**Code Changes in `DockerGradingService.GradeStudentAsync()`**:
```csharp
public async Task<GradingResult> GradeStudentAsync(...)
{
    Console.WriteLine($"\n========== GRADING CONFIGURATION: {studentCode} ==========");
    Console.WriteLine($"Student Code: {studentCode}");
    Console.WriteLine($"Paper: {config.PaperNo}");
    Console.WriteLine($"Test Kit: {testKitPath}");
    Console.WriteLine($"\nNetwork Configuration:");
    Console.WriteLine($"  Docker Network: {config.DockerNetwork}");
    Console.WriteLine($"  Server Container: {serverContainer}");
    Console.WriteLine($"  Client Container: {clientContainer}");
    Console.WriteLine($"  Database Container: {config.DatabaseContainerName}");
    Console.WriteLine($"\nPort Mapping:");
    Console.WriteLine($"  Code Host Port: {config.CodeContainerHostPort}");
    Console.WriteLine($"  Code Internal Port: {config.CodeContainerInternalPort}");
    Console.WriteLine($"  Database Host Port: {config.DatabaseContainerHostPort}");
    Console.WriteLine($"  Database Internal Port: {config.DatabaseContainerInternalPort}");
    Console.WriteLine($"\nDLL Paths:");
    Console.WriteLine($"  Server DLL: {serverDllPath ?? "N/A"}");
    Console.WriteLine($"  Client DLL: {clientDllPath ?? "N/A"}");
    Console.WriteLine($"\nFeature Flags:");
    Console.WriteLine($"  DLL Modification Fallback: {config.UseDllModificationFallback}");
    Console.WriteLine($"  appsettings.json exists (Server): {File.Exists(Path.Combine(Path.GetDirectoryName(serverDllPath), "appsettings.json"))}");
    Console.WriteLine($"  appsettings.json exists (Client): {File.Exists(Path.Combine(Path.GetDirectoryName(clientDllPath), "appsettings.json"))}");
    Console.WriteLine("=========================================================\n");
    
    // ... rest of grading code ...
}
```

## Testing Recommendations

After implementing these changes:

1. **Test with environment.xlsx**: Create a test kit with environment.xlsx specifying `MonitorPort = 5555`
2. **Verify Port Usage**: Check logs to confirm port 5555 is used throughout
3. **Test DLL Modification**: Enable DLL modification checkbox and verify logs show correct ports being patched
4. **Test Network Capture**: Verify NetworkMonitor captures traffic on the specified port
5. **Test Multiple Students**: Grade 2-3 students to ensure ports don't conflict

## Migration Path

1. **Phase 1**: Add logging enhancements (Solutions 2, 3, 4) - NO BREAKING CHANGES
2. **Phase 2**: Add early environment.xlsx loading (Solution 1) - BEHAVIOR CHANGE
3. **Phase 3**: Test thoroughly with various test kits
4. **Phase 4**: Update documentation to explain port configuration precedence

## Port Configuration Precedence (After Fix)

1. **environment.xlsx MonitorPort** (highest priority) - if specified
2. **PortAllocator.AllocatePort()** (fallback) - if environment.xlsx doesn't specify port
3. **Default 8000** (last resort) - if port allocation fails

This ensures test kits can specify exact ports when needed, while still supporting dynamic allocation for parallel grading.
