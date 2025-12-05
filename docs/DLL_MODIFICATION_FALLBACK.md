# DLL Modification Fallback Feature

## Overview

This feature addresses the scenario where student submissions have hardcoded IP addresses and ports in their code but don't use `appsettings.json` files. When the grader cannot find or generate `appsettings.json` files, it can optionally attempt to patch the compiled DLL files to replace hardcoded values with the correct grading environment settings.

## Background

The auto-grading system previously required student submissions to use `appsettings.json` files for configuration. If these files were missing or could not be generated, the grading process would fail with a fatal error. However, some students may hardcode connection details directly in their code, which after compilation becomes embedded in the DLL files.

## Solution

This feature implements a non-fatal fallback mechanism that:

1. **Makes appsettings errors non-fatal**: The grader now logs warnings instead of failing when appsettings.json cannot be created
2. **Provides DLL modification as a fallback**: When enabled, the grader attempts to patch compiled DLL files to replace hardcoded values
3. **Is configurable**: Can be enabled/disabled per test suite via the Environment.xlsx configuration

## Configuration

### Enabling DLL Modification Fallback

Add the following to your `Environment.xlsx` file in the test kit:

**Sheet: Config**

| Key | Value |
|-----|-------|
| EnableDllModificationFallback | true |

### Default Behavior

- **Default**: `false` (disabled)
- **When disabled**: Appsettings errors are still non-fatal (logged as warnings), but no DLL modification is attempted
- **When enabled**: DLL modification is attempted when appsettings.json is missing

## How It Works

The DLL modification service uses **Mono.Cecil** to analyze and modify IL (Intermediate Language) code in compiled .NET assemblies. It searches for:

**IP Address Patterns:**
- `"localhost"`
- `"127.0.0.1"`
- `"http://localhost"`
- `"https://localhost"`

**Common Ports:**
- `3000`, `4000`, `5000`, `8000`, `8080`

When these patterns are found in string literals or integer constants, they are replaced with the grading environment's configured IP and port.

## Credits

This feature is based on the [dll-mod](https://github.com/LostInUrMind/dll-mod.git) tool by NhatNM.
