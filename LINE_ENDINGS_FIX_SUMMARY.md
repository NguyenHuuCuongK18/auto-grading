# Fix Summary: Windows Line Endings in Shell Scripts

## Problem

Users encountered this error when trying to grade through the UI:
```
exec /scripts/unified-entrypoint.sh: no such file or directory
```

## Root Cause

The error occurs when shell scripts have **Windows line endings (CRLF - `\r\n`)** instead of **Unix line endings (LF - `\n`)**.

### Why This Happens

1. User edits shell scripts on Windows
2. Windows text editors save files with CRLF line endings
3. Docker builds the image with these files
4. Linux tries to execute the script
5. Linux reads the shebang as `#!/bin/sh\r` (with carriage return)
6. Linux looks for an interpreter at `/bin/sh\r` (doesn't exist)
7. Error: "no such file or directory" (misleading - it's about the interpreter, not the script!)

## Solution Implemented

### 1. Dockerfile Automatic Fix (Primary Solution)

**File:** `DockerImage/Dockerfile.unified`

Added `dos2unix` package and automatic conversion:

```dockerfile
# Install dos2unix along with other packages
RUN apt-get install -y supervisor netcat-openbsd procps dos2unix ...

# Convert all shell scripts from CRLF to LF
RUN dos2unix /scripts/*.sh && \
    chmod +x /scripts/*.sh
```

**Impact:** Automatic fix during Docker build - no user action required!

### 2. Git Attributes (Prevention)

**File:** `.gitattributes`

Forces LF line endings for shell scripts:

```gitattributes
# Shell scripts MUST use LF line endings
*.sh text eol=lf
```

**Impact:** Git always checks out scripts with LF endings, regardless of OS.

### 3. Comprehensive Documentation

Created three documentation files:

1. **QUICK_FIX.md** - One-page quick reference
2. **LINE_ENDINGS_FIX.md** - Detailed explanation and manual fixes
3. **DOCKER_BUILD_TROUBLESHOOTING.md** - Complete troubleshooting guide

### 4. UI Validation (Future Enhancement)

**File:** `Application/SolutionGrader.UI/Services/DockerImageValidator.cs`

Service to validate Docker images before grading starts:
- Checks if images exist
- Validates entrypoint configuration
- Provides clear error messages and fix instructions

**File:** `Application/SolutionGrader.UI/SetupWindow.xaml.cs`

Integrated validation into the setup window:
- Automatic validation on startup
- Manual re-validation button
- Prevents grading until validation passes

## Files Changed

### Core Fixes
- `DockerImage/Dockerfile.unified` - Added dos2unix installation and conversion
- `.gitattributes` - Enforces LF line endings for shell scripts

### Documentation
- `QUICK_FIX.md` - Quick reference guide
- `LINE_ENDINGS_FIX.md` - Detailed explanation
- `DOCKER_BUILD_TROUBLESHOOTING.md` - Complete troubleshooting
- `DockerImage/BUILD_INSTRUCTIONS.md` - Updated with line ending notes

### UI Enhancements
- `Application/SolutionGrader.UI/Services/DockerImageValidator.cs` - New validation service
- `Application/SolutionGrader.UI/SetupWindow.xaml` - Added Docker validation UI
- `Application/SolutionGrader.UI/SetupWindow.xaml.cs` - Integrated validation logic

## User Action Required

**Simple:**
```bash
cd DockerImage
bash build.sh
```

That's it! The `dos2unix` command in the Dockerfile will automatically fix any line ending issues.

## Verification

Test that the fix worked:

```bash
# Start a test container
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest

# Check logs - should show successful startup
docker logs test-unified

# Expected output:
# [Entrypoint] Phase 1: Creating the Named Pipe...
# [Entrypoint] Phase 2: Handing over to Supervisord...
# ... supervisord started with pid 1

# Cleanup
docker rm -f test-unified
```

## Technical Details

### Why "no such file or directory" is Misleading

The file DOES exist in the image. The error is about the **interpreter** specified in the shebang line:

1. Script content with CRLF:
   ```
   #!/bin/sh\r
   echo "Hello"\r
   ```

2. Linux interprets first line as a request to run: `/bin/sh\r`

3. File `/bin/sh\r` doesn't exist (only `/bin/sh` exists)

4. Linux reports: "no such file or directory"

### How dos2unix Fixes It

`dos2unix` is a Unix utility that converts text files from DOS/Windows format to Unix format:
- Removes all `\r` (carriage return) characters
- Leaves only `\n` (line feed) characters
- Makes scripts executable on Unix/Linux systems

### Multi-Layer Defense

1. **Layer 1 - Source Control:** `.gitattributes` ensures LF on checkout
2. **Layer 2 - Build Time:** `dos2unix` converts CRLF→LF in Dockerfile
3. **Layer 3 - Runtime Validation:** UI validates images before use

Even if developers bypass Git or work with files directly, the Dockerfile fix ensures correct line endings in the final image.

## Benefits

1. **Automatic Fix:** No manual intervention needed
2. **Cross-Platform:** Works on Windows, Mac, and Linux
3. **Foolproof:** Even if source files have CRLF, the image will be correct
4. **Well-Documented:** Multiple guides for different user needs
5. **User-Friendly:** Clear error messages and fix instructions

## Testing Performed

1. ✅ Built Docker image with dos2unix
2. ✅ Verified line endings in built image (LF only)
3. ✅ Started container successfully
4. ✅ Confirmed supervisord starts correctly
5. ✅ Tested with manual CRLF files (converted correctly)
6. ✅ UI code compiles without errors

## References

- [Docker Image Layers](https://docs.docker.com/storage/storagedriver/)
- [Git Attributes Documentation](https://git-scm.com/docs/gitattributes)
- [dos2unix Manual](https://linux.die.net/man/1/dos2unix)
- [Shebang Line Explanation](https://en.wikipedia.org/wiki/Shebang_(Unix))
- [Line Ending Issues on Stack Overflow](https://stackoverflow.com/questions/14219092/bash-script-bin-bashm-bad-interpreter-no-such-file-or-directory)

## Future Enhancements

1. Add EditorConfig file for consistent editor settings
2. Add pre-commit Git hook to check line endings
3. Enhance UI validation with real-time Docker status
4. Add automated tests for line ending conversion
5. Create CI/CD pipeline to verify images on each commit
