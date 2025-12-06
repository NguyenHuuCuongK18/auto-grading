# DLL Modification Fallback Feature - Implementation Summary

## Purpose

This feature provides a fallback mechanism for grading student submissions that hardcode connection settings (IP addresses and ports) in their code instead of using `appsettings.json` configuration files.

## Problem Statement

Some students hardcode connection settings like:
```csharp
var client = new TcpClient("localhost", 5000);
```

Instead of using configuration:
```csharp
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();
var ip = config["IpAddress"];
var port = int.Parse(config["Port"]);
```

When such submissions are graded in Docker containers without `appsettings.json`, they fail because:
1. The hardcoded IP/port doesn't match the Docker networking setup
2. Network monitoring can't capture traffic properly

## Solution

The DLL modification fallback directly patches the compiled DLL file to replace hardcoded values with the correct Docker networking configuration.

### Workflow

```
┌─────────────────────────────────────────────────────────────┐
│ 1. UI: User enables "DLL modification fallback" checkbox    │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. GradingOrchestrationService: Discovers student DLL paths │
│    - Located on HOST machine (Submit/StudentCode/...)       │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. DockerGradingService.CopyFilesToContainersAsync:         │
│    a. Check if appsettings.json exists in student directory │
│    b. If missing AND fallback enabled:                      │
│       - Call DllModificationService                          │
│       - Patch DLL on HOST machine                           │
│       - Server: IP=0.0.0.0, Port=8000                       │
│       - Client: IP=host.docker.internal, Port=8000          │
│    c. Copy (potentially modified) DLL to container          │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. If appsettings still needed, generate in container       │
│    (This acts as a second fallback layer)                   │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. Grading proceeds with properly configured DLLs           │
│    Network monitoring captures traffic normally             │
└─────────────────────────────────────────────────────────────┘
```

## Key Design Decisions

### 1. Modification Location: HOST Machine

**Why:** DLL modification must happen on the HOST machine where student submissions are stored, NOT inside Docker containers.

**Reasoning:**
- Student submission folders are mounted from the host filesystem
- Modifications inside containers would be lost when containers are destroyed
- The modified DLL needs to persist for potential re-grading
- Docker cp copies from host to container, so the modified file is what gets copied

**Example Paths:**
- Host: `/home/runner/work/Submit/StudentA/Project11/bin/Debug/net8.0/Project11.dll`
- Container: `/apps/Project11/Project11.dll` (copied from host)

### 2. Network Configuration for Docker

**Server Configuration:**
```
IP: 0.0.0.0 (bind to all interfaces)
Port: 8000 (dynamically allocated)
```

**Client Configuration:**
```
IP: host.docker.internal (Docker host gateway)
Port: 8000 (same as server)
```

**Why this works:**
- Server binds to `0.0.0.0` so it accepts connections from any interface
- Client connects to `host.docker.internal` which routes to the host machine
- Traffic goes through the host's exposed port (e.g., 8000:8000)
- Network monitor on the host captures this traffic

### 3. Fallback Cascade

The system has multiple fallback layers:

1. **Primary**: Use existing `appsettings.json` if present
2. **First Fallback**: DLL modification (if enabled and no appsettings)
3. **Second Fallback**: Generate appsettings.json in container

This ensures maximum compatibility with different student submission styles.

### 4. Common Pattern Detection

The DLL modifier tries common patterns automatically:

**IP Addresses:**
- localhost
- 127.0.0.1
- 0.0.0.0

**Ports:**
- 3000, 4000, 5000, 5001, 5002
- 7000, 7001
- 8000, 8080
- 9000

This increases the chance of successfully patching hardcoded values.

## UI Integration

### Checkbox Location

Located in **GradingWindow.xaml** → **Grading Actions** section:

```xml
<CheckBox x:Name="chkUseDllModFallback" 
          VerticalAlignment="Center" 
          Margin="0,0,5,0"
          ToolTip="Enable DLL modification fallback when appsettings.json is missing"/>
<TextBlock Text="DLL modification fallback" 
           VerticalAlignment="Center" 
           Foreground="#444"
           ToolTip="When enabled, the system will patch DLL files directly if appsettings.json is not found"/>
```

### Behavior

- **Unchecked (Default)**: DLL modification is disabled, only appsettings generation is used
- **Checked**: DLL modification is attempted when appsettings.json is missing

## Technical Components

### 1. DllMod Library

**Location:** `Lib/DllMod/`

**Key Classes:**
- `DllModifier`: Main API for patching DLLs
- `AsmHelper`: Low-level IL instruction manipulation using Mono.Cecil
- `DllModificationResult`: Result object with detailed information

**Dependencies:**
- Mono.Cecil 0.11.6

### 2. DllModificationService

**Location:** `Lib/SolutionGrader.Core/Services/DllModificationService.cs`

**Purpose:** High-level service that integrates DLL modification into the grading flow

**Key Methods:**
- `AppsettingsExists(directoryPath)`: Checks if appsettings.json exists
- `FindMainDll(directoryPath, projectName)`: Locates the main DLL file
- `PatchServerDll(dllPath, port)`: Patches server DLL with correct configuration
- `PatchClientDll(dllPath, port)`: Patches client DLL with correct configuration
- `CheckAndPatchIfNeeded(...)`: Complete check and patch workflow

### 3. Configuration Changes

**GradingConfiguration.cs:**
```csharp
public bool UseDllModificationFallback { get; set; } = false;
```

**DockerGradingConfig.cs:**
```csharp
public bool UseDllModificationFallback { get; set; } = false;
```

### 4. DockerGradingService Integration

**Modified Method:** `CopyFilesToContainersAsync`

**Logic:**
```csharp
if (config.UseDllModificationFallback)
{
    var service = new DllModificationService();
    var result = service.CheckAndPatchIfNeeded(
        serverDir,
        config.ServerProjectName,
        isServer: true,
        targetPort: config.CodeContainerHostPort
    );
    // Log result and continue
}
// Copy files from HOST to container
_dockerExecutor.CopyFileToContainer(serverDir, container);
```

## Testing Recommendations

### Test Case 1: Student with appsettings.json
**Expected:** DLL modification is skipped, appsettings used as normal

### Test Case 2: Student without appsettings.json (checkbox enabled)
**Expected:** DLL is modified on host, grading succeeds

### Test Case 3: Student without appsettings.json (checkbox disabled)
**Expected:** DLL modification is skipped, appsettings generated in container

### Test Case 4: Hardcoded localhost:5000
**Expected:** DLL is patched to host.docker.internal:8000 (or allocated port)

### Test Case 5: Network monitoring
**Expected:** Traffic is captured properly even with DLL modification

## Logging

The implementation provides detailed logging:

```
[DllMod] Checking server directory on HOST: /path/to/Submit/StudentA/Project11/bin/Debug/net8.0
[DllMod] appsettings.json not found in /path/to/Submit/StudentA/Project11/bin/Debug/net8.0
[DllMod] Found DLL for modification: Project11.dll
[DllMod] Attempting to patch server DLL: Project11.dll
[DllMod] Target configuration: IP=0.0.0.0 (bind all), Port=8000
[DllMod] Server patch result: Successfully patched DLL: 3 IP replacements, 2 port replacements (found port 5000)
[DllMod] Server DLL successfully modified on HOST machine at: /path/to/Project11.dll
[Docker] Copied server files from HOST /path/to/Project11 to container ag-server-StudentA:/apps/Project11
```

## Benefits

1. **Handles Non-Standard Submissions**: Grades students who hardcode settings
2. **No Source Code Required**: Works with compiled DLLs only
3. **Preserves Network Monitoring**: Traffic still routes through host for capture
4. **User Control**: Explicit opt-in via checkbox
5. **Safe Fallbacks**: Multiple layers ensure grading proceeds
6. **Automatic Pattern Detection**: Tries common hardcoded values

## Limitations

1. **Best Effort**: May not detect all hardcoding patterns
2. **Requires Checkbox**: Must be manually enabled by examiner
3. **IL-Level Only**: Can't modify configuration loaded at runtime
4. **Backup Management**: Creates .backup files that may accumulate

## Future Enhancements

1. **Auto-Enable**: Detect missing appsettings and suggest enabling fallback
2. **Port Range Configuration**: Allow examiners to specify custom port ranges
3. **Backup Cleanup**: Automatically clean old .backup files
4. **Statistics**: Track how often fallback is needed per paper
5. **Verification**: Test modified DLL before grading to ensure it works

## Conclusion

The DLL modification fallback feature successfully addresses the problem of grading students who hardcode connection settings. By modifying DLLs on the HOST machine before copying to Docker containers, the system can handle non-standard submissions while preserving the network monitoring capability essential for grading.

The implementation is safe, well-documented, and provides clear logging for debugging. The checkbox-based control ensures examiners explicitly opt into this behavior.
