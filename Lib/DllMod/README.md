# DllMod - .NET Assembly Modification Library

## Overview

DllMod is a library for modifying compiled .NET assemblies (DLL files) to replace hardcoded IP addresses and port numbers. It uses [Mono.Cecil](https://github.com/jbevain/cecil) to perform IL-level instruction patching without requiring source code.

## Use Case

This library is used as a fallback mechanism in the auto-grading system when students hardcode connection settings in their code instead of using `appsettings.json` configuration files. By patching the compiled DLL directly, the grading system can ensure student code connects to the correct server and port for evaluation.

## Features

- **IP Address Replacement**: Replaces string literals containing IP addresses or "localhost" with new values
- **Port Number Replacement**: Replaces integer constants representing port numbers with new values
- **Safe Backup**: Creates a `.backup` file before modifying the original DLL
- **Common Patterns**: Built-in support for detecting common localhost variations and port numbers

## Usage

### Basic DLL Patching

```csharp
using DllMod;

// Patch a specific DLL with known values
var (ipReplacements, portReplacements) = DllModifier.PatchDll(
    dllPath: "path/to/StudentApp.dll",
    oldIp: "localhost",
    newIp: "host.docker.internal",
    oldPort: 5000,
    newPort: 8000
);

Console.WriteLine($"Replaced {ipReplacements} IP addresses and {portReplacements} ports");
```

### Smart Patching with Common Values

When you don't know what the student hardcoded:

```csharp
using DllMod;

// Try common IP addresses and ports automatically
var result = DllModifier.TryPatchWithCommonValues(
    dllPath: "path/to/StudentApp.dll",
    newIp: "host.docker.internal",
    newPort: 8000
);

if (result.Success)
{
    Console.WriteLine($"Success: {result.Message}");
    Console.WriteLine($"Found and replaced port: {result.SuccessfulPort}");
}
else
{
    Console.WriteLine($"Failed: {result.Message}");
}
```

### Using the High-Level Service

For integration with the grading system:

```csharp
using SolutionGrader.Core.Services;

var service = new DllModificationService();

// Check if appsettings.json exists, and patch DLL if it doesn't
var result = service.CheckAndPatchIfNeeded(
    directoryPath: "path/to/student/project",
    projectName: "Project11",
    isServer: true,
    targetPort: 8000
);

Console.WriteLine(result.GetSummary());
```

## How It Works

### IL Instruction Scanning

The library scans through all IL instructions in the assembly:

1. **String Literals** (`ldstr` instructions): Replaces IP addresses in connection strings, URLs, etc.
2. **Integer Constants** (`ldc.i4.*` instructions): Replaces hardcoded port numbers

### Example Transformations

**Original Code:**
```csharp
var client = new TcpClient("localhost", 5000);
string url = "http://127.0.0.1:5000/api";
int port = 5000;
```

**After Patching:**
```csharp
var client = new TcpClient("host.docker.internal", 8000);
string url = "http://host.docker.internal:8000/api";
int port = 8000;
```

## Common Patterns

### Default IP Addresses
- `localhost`
- `127.0.0.1`
- `0.0.0.0`

### Default Ports
- `3000` - Common Node.js/React dev server
- `4000` - Common GraphQL server
- `5000`, `5001` - Common ASP.NET Core ports
- `7000`, `7001` - Alternative development ports
- `8000`, `8080` - Common HTTP server ports
- `9000` - Alternative server port

## Integration with Auto-Grading

The DLL modification fallback is integrated into the grading flow:

1. **Before copying files to Docker**: Check if `appsettings.json` exists
2. **If missing and fallback enabled**: Patch the DLL on the HOST machine
3. **Copy to container**: The modified DLL is copied to the Docker container
4. **Grading proceeds**: Network monitoring captures traffic as normal

### Configuration

The feature is controlled by a checkbox in the grading UI:

- **Checkbox Label**: "DLL modification fallback"
- **Location**: GradingWindow → Grading Actions section
- **Behavior**: Only modifies DLLs when checked and `appsettings.json` is missing

### Network Flow

For Docker-based grading with network monitoring:

- **Server DLL**: Patched to bind to `0.0.0.0` (all interfaces)
- **Client DLL**: Patched to connect to `host.docker.internal` (Docker host gateway)
- **Port**: Both use the same dynamically allocated port (e.g., 8000, 8001, etc.)

This ensures network traffic routes through the host's exposed port, allowing the network monitor running on the host to capture packets.

## Limitations

1. **IL-Only Patching**: Only modifies IL instructions, not metadata or resources
2. **Hardcoded Detection**: Can only detect and replace explicit hardcoded values
3. **Best Effort**: If the student uses complex configuration or runtime generation, patching may not work
4. **Backup Required**: Original DLL is modified in place (backup created automatically)

## Technical Details

### Dependencies

- **Mono.Cecil 0.11.6**: For reading and writing .NET assemblies
- **.NET 8.0**: Target framework

### Safety Features

- Creates `.backup` file before modification
- Uses atomic file replacement (`File.Replace`)
- Validates file existence before patching
- Detailed logging of all modifications

### Error Handling

The library uses a result-based error handling approach:

```csharp
public class DllModificationResult
{
    public bool Success { get; set; }
    public int IpReplacements { get; set; }
    public int PortReplacements { get; set; }
    public List<int> AttemptedPorts { get; set; }
    public int? SuccessfulPort { get; set; }
    public string Message { get; set; }
}
```

## Authors

- **NhatNM** - Original dll-mod tool
- **GitHub Copilot** - Integration and library conversion

## License

This project is part of the auto-grading system.
