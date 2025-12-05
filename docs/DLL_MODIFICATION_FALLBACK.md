# DLL Modification Fallback Feature

## Overview

This feature addresses the scenario where student submissions have hardcoded IP addresses and ports in their code but don't use `appsettings.json` files. When the grader cannot find `appsettings.json` files, it **automatically** attempts to patch the compiled DLL files to replace hardcoded values with the correct grading environment settings.

## Background

The auto-grading system can generate `appsettings.json` files for configuration. However, some students may hardcode connection details directly in their code, which after compilation becomes embedded in the DLL files. This feature provides an automatic fallback when appsettings files are missing.

## Solution

This feature implements an **automatic, transparent fallback mechanism**:

1. **Non-fatal appsettings handling**: The grader logs warnings instead of failing when appsettings.json cannot be created
2. **Automatic DLL patching**: When appsettings.json is missing, the grader automatically attempts to patch compiled DLL files
3. **Docker auto-detection**: The system detects if code is running in Docker containers and applies appropriate IP addresses

**No configuration required** - this feature works automatically as part of the normal grading flow.

## How to Disable DLL Modification

If you need to disable the automatic DLL modification feature, you have two options:

### Option 1: Environment Variable (Recommended)
Set the environment variable before running the grader:

**Windows (PowerShell):**
```powershell
$env:DISABLE_DLL_MOD = "true"
dotnet run --project Application/SolutionGrader.Cli -- executesuite --suite "path" --out "path"
```

**Windows (CMD):**
```cmd
set DISABLE_DLL_MOD=true
dotnet run --project Application/SolutionGrader.Cli -- executesuite --suite "path" --out "path"
```

**Linux/Mac:**
```bash
export DISABLE_DLL_MOD=true
dotnet run --project Application/SolutionGrader.Cli -- executesuite --suite "path" --out "path"
```

### Option 2: Code Modification
Edit the service instantiation in:
- **CLI**: `Application/SolutionGrader.Cli/Program.cs` (around line 393)
- **UI**: `Application/SolutionGrader.UI/Services/LibGradingService.cs` (around line 113)

Change:
```csharp
IDllModificationService? dllMod = Environment.GetEnvironmentVariable("DISABLE_DLL_MOD") == "true" 
    ? null 
    : new DllModificationService();
```

To:
```csharp
IDllModificationService? dllMod = null;  // DLL modification disabled
```

When `dllMod` is `null`, the DLL modification feature is completely disabled and the system will only use appsettings.json files.

## How It Works

### DLL Patching Process

The DLL modification service uses **Mono.Cecil** to analyze and modify IL (Intermediate Language) code in compiled .NET assemblies. It searches for:

**IP Address Patterns:**
- `"localhost"`
- `"127.0.0.1"`
- `"http://localhost"`
- `"https://localhost"`

**Common Ports:**
- `3000`, `4000`, `5000`, `8000`, `8080`

When these patterns are found in string literals or integer constants, they are replaced with the grading environment's configured IP and port.

### Docker Container Support (Auto-detected)

The system automatically detects Docker execution and applies Docker-specific IP addresses:

**Server DLLs** (binding address):
```csharp
// Before: string ip = "localhost";
// After:  string ip = "0.0.0.0";  // Binds to all interfaces in container
```

**Client DLLs** (connection address):
```csharp
// Before: string url = "http://localhost:5000";
// After:  string url = "http://host.docker.internal:8888";  // Connects to host
```

### Docker Detection

The system automatically detects Docker execution based on:
- File paths containing `/docker/` or `/var/lib/docker`
- Presence of `/.dockerenv` file
- Environment configuration indicating Given paths (ExecutePaper flow)

No manual configuration needed!

## Credits

This feature is based on the [dll-mod](https://github.com/LostInUrMind/dll-mod.git) tool by NhatNM.
