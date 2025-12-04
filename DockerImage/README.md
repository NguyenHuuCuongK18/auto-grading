# Docker Image for Auto-Grading System

This folder contains the Dockerfile for building the code execution image used by the auto-grading system.

## Image Name
`fptuxaes/aes-dotnet8-console:latest`

## Base Image
This Dockerfile extends `fptuxaes/aes-dotnet8:latest`, which should be a custom .NET 8 image with necessary runtime dependencies.

## What This Image Provides

1. **Process Management Tools**
   - Installs `procps` package for `ps`, `pkill`, `pgrep` commands
   - Required for proper process cleanup between test cases
   - Prevents "Address already in use" errors

2. **Disabled Watch Scripts**
   - Disables auto-restart scripts from base image that interfere with grading
   - Moves watch-apps.sh, generate-nginx-config.sh, and entrypoint.sh to .disabled

3. **Container Configuration**
   - Working directory: `/apps`
   - Environment: `DOTNET_RUNNING_IN_CONTAINER=true`
   - Entrypoint: `tail -f /dev/null` (keeps container alive for exec commands)

## Building the Image

### Option 1: Use Existing Base Image
If you have access to `fptuxaes/aes-dotnet8:latest`:

```bash
# From repository root
docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
```

### Option 2: Use Official .NET Image
If you don't have the custom base image, modify the Dockerfile:

```dockerfile
# Change line 14 from:
FROM fptuxaes/aes-dotnet8:latest

# To:
FROM mcr.microsoft.com/dotnet/sdk:8.0
```

Then build:
```bash
docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
```

### Option 3: Build with Custom Name
If you need a different image name:

```bash
docker build -t my-custom-image:tag ./DockerImage
```

Then update `TestKit/[Question]/Environment.xlsx` Config sheet:
```
CodeImageName | my-custom-image:tag
```

## Verifying the Build

Check the image exists:
```bash
docker images | grep fptuxaes/aes-dotnet8-console
```

Test the image:
```bash
# Start a container
docker run -d --name test-container fptuxaes/aes-dotnet8-console:latest

# Check it's running
docker ps | grep test-container

# Verify procps is installed
docker exec test-container ps aux

# Clean up
docker rm -f test-container
```

## Image Requirements

For the auto-grading system to work correctly, the image must have:

1. **.NET Runtime/SDK**
   - .NET 8.0 or compatible version
   - Ability to run `dotnet` command

2. **Process Management**
   - `ps` command (from procps)
   - `kill` command
   - `pkill` command (optional but recommended)

3. **Shell**
   - `/bin/bash` or `/bin/sh`
   - Basic shell utilities (cat, grep, awk)

4. **Network Tools** (optional but useful for debugging)
   - curl or wget
   - netstat or ss

## Troubleshooting

### Issue: "error during connect" when building
**Cause:** Docker daemon not running
**Solution:**
```bash
# Start Docker Desktop (Windows/Mac)
# Or start Docker service (Linux)
sudo systemctl start docker
```

### Issue: "FROM image not found"
**Cause:** Base image `fptuxaes/aes-dotnet8:latest` doesn't exist
**Solution:** Change to official .NET image (see Option 2 above)

### Issue: "denied: requested access to the resource is denied"
**Cause:** Trying to push to registry without authentication
**Solution:** This is a local image, no need to push. Use it directly.

### Issue: Build fails on apt-get update
**Cause:** Base image is not Debian/Ubuntu based
**Solution:** Adjust package manager commands for your base image:
- Alpine: `apk add procps`
- RHEL/CentOS: `yum install procps-ng`

## Image Size Considerations

The image size depends on the base image:
- With .NET SDK: ~2-3 GB
- With .NET Runtime: ~500 MB - 1 GB

To reduce size:
1. Use runtime-only base image instead of SDK
2. Use Alpine-based images
3. Remove unnecessary dependencies
4. Use multi-stage builds

## Advanced Customization

You can add more tools to the image by modifying the Dockerfile:

```dockerfile
# Add networking tools
RUN apt-get update && apt-get install -y \
    curl \
    iputils-ping \
    netcat \
    && rm -rf /var/lib/apt/lists/*

# Add debugging tools
RUN apt-get update && apt-get install -y \
    strace \
    gdb \
    && rm -rf /var/lib/apt/lists/*
```

## Support

If you need help with Docker image setup:
1. Review Docker logs: `docker build --progress=plain --no-cache -t fptuxaes/aes-dotnet8-console:latest ./DockerImage`
2. Verify Docker is running: `docker info`
