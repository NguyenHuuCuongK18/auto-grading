# DLL Modification Fallback Feature

## What's New

The auto-grading system now has **automatic, transparent DLL modification** when `appsettings.json` files are missing.

### Key Changes

1. **Appsettings errors are now non-fatal**
   - Missing or inaccessible appsettings.json files no longer cause grading to fail
   - Warnings are logged instead of fatal errors
   
2. **Automatic DLL modification fallback**
   - When appsettings.json is missing, the system automatically patches compiled DLLs
   - Replaces hardcoded IPs and ports with correct grading environment values
   - **No configuration required** - works automatically
   
3. **Docker auto-detection**
   - Automatically detects if code is running in Docker containers
   - Applies appropriate IP addresses (0.0.0.0 for server, host.docker.internal for client)
   - No manual configuration needed

## Quick Start

### No Configuration Needed!

The DLL modification feature works **automatically** as part of the grading process:

1. Grader attempts to generate appsettings.json
2. If appsettings.json is missing, DLL modification kicks in automatically
3. System detects Docker vs local execution
4. Appropriate IP addresses and ports are patched

### What Gets Modified

The system automatically replaces:
- **IPs**: `localhost`, `127.0.0.1`, `http://localhost`, `https://localhost`
- **Ports**: `3000`, `4000`, `5000`, `8000`, `8080`

With the correct grading environment values:
- **Local execution**: `localhost` or `127.0.0.1` with configured port (typically `8888`)
- **Docker execution** (auto-detected):
  - Server: `0.0.0.0` (bind to all interfaces)
  - Client: `host.docker.internal` (connect to host)

## Usage Scenarios

### Scenario 1: Test Kit Without Appsettings Files

Your test kit's `Meta/Given/Client` and `Meta/Given/Server` don't use appsettings.json:

1. Run grading normally - no special configuration needed
2. The system will automatically patch student DLLs when appsettings is missing
3. Docker mode is auto-detected based on execution context

### Scenario 2: Mixed Student Submissions

Some students use appsettings.json, others hardcode values:

1. Run grading normally
2. Students with appsettings.json will use them (preferred)
3. Students without appsettings.json will have their DLLs patched automatically
4. Docker/local mode is auto-detected per execution

### Scenario 3: No Modification Needed

If all submissions properly use appsettings.json:

1. Run grading normally
2. System will use appsettings.json files
3. DLL modification is skipped (not needed)

## Example Log Output

### Local Execution (Auto-detected)
```
[AppsettingsCreation] Using GraderPort from config: 8888
[AppsettingsCreation] Warning: Failed to generate server appsettings.json: File not found
[TestCase] Server appsettings.json not found, applying DLL modification fallback...
[DllMod] Searching for DLL files in: /submit/student1/server [Local]
[DllMod] Target IP: 127.0.0.1, Target Port: 8888
[DllMod] Found 3 DLL file(s) to scan
[DllMod] Skipping system DLL: System.Runtime.dll
[DllMod] Attempting to patch: StudentServer.dll
[DllMod] Successfully patched StudentServer.dll: 2 IP(s), 1 port(s) replaced
[TestCase] Server DLLs modified successfully
[TestCase] [Step 1] Environment setup completed
```

### Docker Execution (Auto-detected)
```
[AppsettingsCreation] Using GraderPort from config: 8888
[AppsettingsCreation] Warning: Failed to generate server appsettings.json: File not found
[TestCase] Server appsettings.json not found, applying DLL modification fallback...
[TestCase] Docker mode detected - using server bind address: 0.0.0.0
[DllMod] Searching for DLL files in: /submit/student1/server [Server (Docker bind)]
[DllMod] Target IP: 0.0.0.0, Target Port: 8888
[DllMod] Successfully patched StudentServer.dll: 2 IP(s), 1 port(s) replaced
[TestCase] Server DLLs modified successfully
[TestCase] Client appsettings.json not found, applying DLL modification fallback...
[TestCase] Docker mode detected - using client address: http://host.docker.internal
[DllMod] Searching for DLL files in: /submit/student1/client [Client (Docker connect)]
[DllMod] Target IP: http://host.docker.internal, Target Port: 8888
[DllMod] Successfully patched StudentClient.dll: 1 IP(s), 1 port(s) replaced
[TestCase] Client DLLs modified successfully
```

## Safety Features

- **Automatic backups**: Original DLLs are backed up as `.dll.backup`
- **System DLLs skipped**: Only student code is modified
- **Non-breaking**: If DLL modification fails, grading continues with warnings
- **Opt-in**: Must be explicitly enabled per test kit

## Technical Details

For detailed information about how DLL modification works, see:
- [docs/DLL_MODIFICATION_FALLBACK.md](docs/DLL_MODIFICATION_FALLBACK.md)

## Credits

Based on [dll-mod](https://github.com/LostInUrMind/dll-mod.git) by NhatNM, integrated with additional safety features and configuration options.
