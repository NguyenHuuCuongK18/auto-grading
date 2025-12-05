# DLL Modification Fallback Feature

## What's New

The auto-grading system now supports a **non-fatal appsettings fallback** with optional **DLL modification**.

### Key Changes

1. **Appsettings errors are now non-fatal**
   - Missing or inaccessible appsettings.json files no longer cause grading to fail
   - Warnings are logged instead of fatal errors
   
2. **DLL modification fallback (optional)**
   - When enabled, the system can patch compiled DLLs to replace hardcoded IPs and ports
   - Useful for student submissions that hardcode connection details
   
3. **Configurable via Environment.xlsx**
   - Add `EnableDllModificationFallback = true` to enable the feature
   - Disabled by default for backward compatibility

## Quick Start

### Enable DLL Modification in Your Test Kit

Edit your `Environment.xlsx` file:

```
Sheet: Config
+--------------------------------+-------+
| Key                            | Value |
+--------------------------------+-------+
| EnableDllModificationFallback  | true  |
+--------------------------------+-------+
```

### What Gets Modified

The system will automatically replace:
- **IPs**: `localhost`, `127.0.0.1`, `http://localhost`, `https://localhost`
- **Ports**: `3000`, `4000`, `5000`, `8000`, `8080`

With the correct grading environment values (typically port `8888`).

## Usage Scenarios

### Scenario 1: Test Kit Without Appsettings Files

If your test kit's `Meta/Given/Client` and `Meta/Given/Server` don't use appsettings.json:

1. Enable `EnableDllModificationFallback = true` in Environment.xlsx
2. Run grading normally
3. The system will patch student DLLs automatically

### Scenario 2: Mixed Student Submissions

Some students use appsettings.json, others hardcode values:

1. Enable `EnableDllModificationFallback = true`
2. Students with appsettings.json will use them (preferred)
3. Students without appsettings.json will have their DLLs patched automatically

### Scenario 3: No Modification Needed

If all submissions properly use appsettings.json:

1. Leave `EnableDllModificationFallback = false` (default)
2. System continues to work as before
3. Appsettings errors are still non-fatal (warnings only)

## Example Log Output

```
[AppsettingsCreation] Using GraderPort from config: 8888
[AppsettingsCreation] Warning: Failed to generate server appsettings.json: File not found
[TestCase] DLL modification fallback is enabled
[TestCase] Server appsettings.json not found, attempting DLL modification...
[DllMod] Searching for DLL files in: /submit/student1/server
[DllMod] Found 3 DLL file(s) to scan
[DllMod] Skipping system DLL: System.Runtime.dll
[DllMod] Attempting to patch: StudentServer.dll
[DllMod] Successfully patched StudentServer.dll: 2 IP(s), 1 port(s) replaced
[TestCase] Server DLLs modified successfully
[TestCase] [Step 1] Environment setup completed
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
