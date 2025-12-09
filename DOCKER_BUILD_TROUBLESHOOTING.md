# Docker Image Build and Troubleshooting Guide

## Overview

The Auto Grading System requires two Docker images to function properly:

1. **fptuxaes/aes-dotnet8-console:latest** - Unified container for student code (client/server)
2. **fptuxaes/network-monitor:latest** - Network monitoring container for packet capture

Both images MUST be built locally before using the system.

## Quick Start - Building Images

### Using the Build Script (Recommended)

```bash
cd DockerImage
bash build.sh
```

This script builds both required images automatically.

### Manual Build

If you prefer to build images manually:

```bash
# Build unified student code container
docker build -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified DockerImage/

# Build network monitor container
docker build -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
```

### Verify Images

After building, verify the images exist:

```bash
docker images | grep fptuxaes
```

Expected output:
```
fptuxaes/aes-dotnet8-console   latest    <IMAGE_ID>    <TIME>    ~892MB
fptuxaes/network-monitor       latest    <IMAGE_ID>    <TIME>    ~83MB
```

## Common Issues and Solutions

### Issue 1: "exec /scripts/unified-entrypoint.sh: no such file or directory" ⚠️ MOST COMMON

**Symptoms:**
- Error appears when trying to grade through UI
- Container fails to start
- Docker logs show this error message
- **The file EXISTS in the image** but still shows "no such file or directory"

**Root Cause:**
This error almost always means the script has **Windows line endings (CRLF) instead of Unix line endings (LF)**.

When you edit shell scripts on Windows, they get CRLF line endings (`\r\n`). Linux expects LF only (`\n`). When Linux reads the shebang `#!/bin/sh\r`, it tries to find an interpreter named `sh\r` (with the `\r` character), which doesn't exist.

**AUTOMATIC FIX (Recommended):**

The Dockerfile now includes `dos2unix` to automatically fix line endings during build:

```bash
# Simply rebuild the image - line endings will be fixed automatically
cd DockerImage
bash build.sh
```

That's it! The `dos2unix` command in the Dockerfile will convert all scripts to LF line endings.

**MANUAL FIX (Optional):**

If you want to fix the source files before building:

1. **VS Code:** Click `CRLF` in bottom-right corner → Select `LF` → Save
2. **Notepad++:** Edit → EOL Conversion → Unix (LF)
3. **Command line:** `dos2unix DockerImage/*.sh`

**PREVENTION:**

The repository now has a `.gitattributes` file that forces LF line endings for all `.sh` files. After pulling the latest code, Git will automatically check out scripts with LF endings.

For more details, see: [LINE_ENDINGS_FIX.md](LINE_ENDINGS_FIX.md)

---

### Issue 2: Old Docker Image from Wrong Dockerfile

The system previously used `Dockerfile` (old) but now requires `Dockerfile.unified` (new).

**Solution:**
```bash
# 1. Remove the old image
docker rmi fptuxaes/aes-dotnet8-console:latest

# 2. Remove any stopped containers using the old image
docker container prune

# 3. Rebuild with correct Dockerfile
cd DockerImage
bash build.sh

# 4. Verify the new image has the correct entrypoint
docker inspect fptuxaes/aes-dotnet8-console:latest | grep -A5 Entrypoint
```

Expected output should show:
```json
"Entrypoint": [
    "/scripts/unified-entrypoint.sh"
],
```

### Issue 2: Old Docker Image from Wrong Dockerfile

**Symptoms:**
- Container fails to start
- Docker inspect shows wrong entrypoint

Docker may be using cached layers from an old build.

**Solution:**
```bash
# Build without cache
docker build --no-cache -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified DockerImage/
docker build --no-cache -t fptuxaes/network-monitor:latest -f DockerImage/NetworkMonitor.Dockerfile DockerImage/
```

### Issue 3: Docker Build Cache Issue

You may have multiple images with the same tag from different build contexts.

**Solution:**
```bash
# List all images including dangling ones
docker images -a | grep fptuxaes

# Remove ALL fptuxaes images
docker rmi $(docker images 'fptuxaes/*' -q)

# Rebuild from scratch
cd DockerImage
bash build.sh
```

### Issue 4: Multiple Images with Same Tag

### Issue 5: "Cannot connect to the Docker daemon"

**Symptoms:**
- Docker commands fail
- Error message mentions Docker daemon not running

**Solution:**
```bash
# On Linux
sudo systemctl start docker

# On Mac
# Open Docker Desktop

# On Windows
# Open Docker Desktop

# Verify Docker is running
docker ps
```

### Issue 6: Build Fails with "manifest unknown"

**Symptoms:**
- Build fails when pulling base images
- Error mentions "manifest unknown" or "not found"

**Solution:**
This usually means no internet connection or Docker Hub is unreachable.

```bash
# Check internet connection
ping -c 3 google.com

# Try pulling base image manually
docker pull mcr.microsoft.com/dotnet/sdk:8.0

# If successful, try building again
cd DockerImage
bash build.sh
```

### Issue 7: "Permission denied" on build.sh

**Symptoms:**
- Cannot execute build.sh
- Error: "Permission denied"

**Solution:**
```bash
# Make the script executable
chmod +x DockerImage/build.sh

# Run it
cd DockerImage
bash build.sh
```

## Advanced Troubleshooting

### Test Container Startup Manually

Test if the unified container starts correctly:

```bash
# Start a test container
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest

# Check if it's running
docker ps | grep test-unified

# Check logs (should show supervisord starting)
docker logs test-unified

# Check if entrypoint script exists inside container
docker exec test-unified ls -la /scripts/

# Cleanup
docker rm -f test-unified
```

### Inspect Image Layers

Check what's actually in the image:

```bash
# View image history
docker history fptuxaes/aes-dotnet8-console:latest

# Inspect full image configuration
docker inspect fptuxaes/aes-dotnet8-console:latest

# Check specific file in image
docker run --rm --entrypoint ls fptuxaes/aes-dotnet8-console:latest -la /scripts/
```

### Check File Permissions in Image

```bash
docker run --rm --entrypoint sh fptuxaes/aes-dotnet8-console:latest -c "ls -la /scripts/ && cat /scripts/unified-entrypoint.sh"
```

### Verify Line Endings in Built Image

```bash
docker run --rm --entrypoint od fptuxaes/aes-dotnet8-console:latest -c /scripts/unified-entrypoint.sh | head -10
```

Should show `\n` (LF) not `\r\n` (CRLF).

## Clean Rebuild Procedure

If all else fails, perform a complete clean rebuild:

```bash
# 1. Stop all containers
docker stop $(docker ps -aq)

# 2. Remove all containers
docker container prune -f

# 3. Remove all fptuxaes images
docker rmi -f $(docker images 'fptuxaes/*' -q)

# 4. Clear Docker build cache
docker builder prune -af

# 5. Rebuild images
cd DockerImage
bash build.sh

# 6. Verify images
docker images | grep fptuxaes

# 7. Test startup
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest
docker logs test-unified
docker rm -f test-unified
```

## Understanding the Docker Images

### Unified Container (fptuxaes/aes-dotnet8-console:latest)

**Purpose:** Runs student code (both client and server) in a single container

**Key Features:**
- Base: `mcr.microsoft.com/dotnet/sdk:8.0`
- Supervisord process manager
- Separate /apps/server and /apps/client directories
- Per-stage logging
- Named pipe for client input

**Structure:**
```
/apps/
  server/     - Server DLLs and logs
  client/     - Client DLLs and logs
/scripts/
  unified-entrypoint.sh    - Container entrypoint (CRITICAL)
  unified-control.sh       - Process control
  server-wrapper.sh        - Server startup wrapper
  client-wrapper.sh        - Client startup wrapper
/etc/supervisor/conf.d/
  supervisord.conf         - Supervisord configuration
```

### Network Monitor (fptuxaes/network-monitor:latest)

**Purpose:** Captures network traffic for validation

**Key Features:**
- Base: `debian:bullseye-slim`
- tcpdump for packet capture
- Lightweight (83MB)

**Usage:**
Attached to student container using `--net=container:{student-container}` to capture localhost traffic.

## UI Validation

The Auto Grading System UI includes built-in Docker image validation:

1. When you open the Setup Window, it automatically checks your Docker images
2. Green checkmark (✅) = Images are correct
3. Red X (❌) = Images are missing or incorrect

**If validation fails:**
1. Read the error message carefully
2. Follow the instructions to rebuild images
3. Click "Check Docker Images" button to re-validate
4. Start Grading button is disabled until validation passes

## Getting Help

If you continue to experience issues after following this guide:

1. Collect diagnostic information:
   ```bash
   docker version > docker-info.txt
   docker images >> docker-info.txt
   docker ps -a >> docker-info.txt
   docker inspect fptuxaes/aes-dotnet8-console:latest >> docker-info.txt
   ```

2. Check the Docker logs from a test container:
   ```bash
   docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest
   sleep 3
   docker logs test-unified > container-logs.txt
   docker rm -f test-unified
   ```

3. Open an issue on GitHub with:
   - The error message you're seeing
   - Your operating system (Windows/Mac/Linux)
   - Docker version
   - The diagnostic files created above

## Reference

- **Dockerfile.unified** - Main student code container definition
- **NetworkMonitor.Dockerfile** - Network monitor container definition
- **build.sh** - Automated build script
- **BUILD_INSTRUCTIONS.md** - Detailed build instructions

## Version History

- **Current (Unified Container Architecture):**
  - Uses Dockerfile.unified
  - Single container per student
  - Supervisord process management
  - Entrypoint: /scripts/unified-entrypoint.sh

- **Legacy (Deprecated):**
  - Used separate Dockerfile
  - Different entrypoint
  - No longer supported
