# Line Endings Fix: Preventing "no such file or directory" Errors

## The Problem

If you see this error when trying to run containers:
```
exec /scripts/unified-entrypoint.sh: no such file or directory
```

But the file clearly exists in the Docker image, **the issue is Windows line endings (CRLF)**.

## Why This Happens

### The Root Cause

Shell scripts created or edited on Windows have line endings like this:
- **Windows:** Carriage Return + Line Feed (`\r\n` or CRLF)
- **Linux:** Line Feed only (`\n` or LF)

When Linux reads a shell script with CRLF line endings:

1. **What you wrote:**
   ```bash
   #!/bin/sh
   echo "Hello"
   ```

2. **What Linux sees:**
   ```bash
   #!/bin/sh\r
   echo "Hello"\r
   ```

3. **What happens:**
   - Linux tries to find an interpreter at `/bin/sh\r` (with the `\r` character)
   - That file doesn't exist
   - Error: `no such file or directory`

The error is about the **interpreter**, not your script!

## The Fix

We've implemented **multiple layers of protection** to prevent this issue:

### Protection Layer 1: Dockerfile (Automatic)

The `Dockerfile.unified` now includes `dos2unix` to automatically convert line endings during image build:

```dockerfile
# Install dos2unix
RUN apt-get install -y dos2unix ...

# Convert all shell scripts to LF
RUN dos2unix /scripts/*.sh && \
    chmod +x /scripts/*.sh
```

**This fix works automatically** - no action needed!

### Protection Layer 2: .gitattributes (Repository-wide)

The `.gitattributes` file ensures Git always checks out shell scripts with LF endings:

```gitattributes
# Shell scripts MUST use LF
*.sh text eol=lf
```

**This fix applies when you clone/pull** - no action needed!

### Protection Layer 3: Manual Conversion (Optional)

If you're editing scripts and want to ensure correct line endings:

#### Option A: VS Code
1. Open the script file
2. Look at bottom-right corner
3. If it says `CRLF`, click it
4. Select `LF`
5. Save the file

#### Option B: Notepad++
1. Open the script file
2. Go to **Edit** → **EOL Conversion** → **Unix (LF)**
3. Save the file

#### Option C: Visual Studio
1. Open the script file
2. **File** → **Advanced Save Options**
3. Select **Line Endings** → **Unix (LF)**
4. Save the file

#### Option D: Command Line (Linux/Mac/WSL)
```bash
# Convert a single file
dos2unix DockerImage/unified-entrypoint.sh

# Convert all shell scripts
find DockerImage -name "*.sh" -exec dos2unix {} \;
```

## Verifying Line Endings

### Method 1: Check in VS Code
Open the file and look at the bottom-right status bar:
- ✅ Should say: `LF`
- ❌ Problem if it says: `CRLF`

### Method 2: Command Line
```bash
# Linux/Mac/WSL
file DockerImage/unified-entrypoint.sh
# Should show: "POSIX shell script, ASCII text executable"
# NOT: "... with CRLF line terminators"

# View actual characters (first 50 bytes)
od -c DockerImage/unified-entrypoint.sh | head -3
# Should show: \n (line feed)
# NOT: \r\n (carriage return + line feed)
```

### Method 3: In Docker Image
```bash
# Check file inside the built image
docker run --rm --entrypoint od fptuxaes/aes-dotnet8-console:latest \
  -c /scripts/unified-entrypoint.sh | head -5

# Should show just \n, not \r\n
```

## After Fixing Line Endings

Once you've fixed the line endings (or relied on automatic fixes), rebuild the Docker image:

```bash
cd DockerImage
bash build.sh
```

The `dos2unix` command in the Dockerfile will ensure all scripts have correct line endings.

## Git Configuration for Windows Users

To prevent Git from converting line endings on Windows:

```bash
# Configure Git to never auto-convert line endings
git config --global core.autocrlf input

# For this repository only
cd /path/to/auto-grading
git config core.autocrlf input
```

Then re-clone or reset the repository:

```bash
# Reset all files to repository versions
git rm --cached -r .
git reset --hard
```

## Summary

**You should NOT need to do anything manually!** The fixes are automatic:

1. ✅ `.gitattributes` ensures Git checks out scripts with LF
2. ✅ `Dockerfile.unified` converts any CRLF to LF during build
3. ✅ Just rebuild the image: `cd DockerImage && bash build.sh`

**Only if you're developing/editing scripts:**
- Check your editor's line ending setting
- Set it to LF (Unix) for `.sh` files
- Or let the Dockerfile handle it automatically

## Testing

After rebuilding, test that the container starts:

```bash
# Start test container
docker run -d --name test-line-endings fptuxaes/aes-dotnet8-console:latest

# Check logs (should start successfully)
docker logs test-line-endings

# Should show:
# [Entrypoint] Phase 1: Creating the Named Pipe...
# [Entrypoint] Phase 2: Handing over to Supervisord...
# ... supervisord started with pid 1

# Cleanup
docker rm -f test-line-endings
```

## References

- [Why do I get "No such file or directory" when the file exists?](https://stackoverflow.com/questions/14219092/bash-script-bin-bashm-bad-interpreter-no-such-file-or-directory)
- [Git Documentation: gitattributes](https://git-scm.com/docs/gitattributes)
- [EditorConfig for consistent coding styles](https://editorconfig.org/)
