# Docker Image Build Instructions

## Quick Start

```bash
cd DockerImage
bash build.sh
```

This builds both required Docker images with automatic line ending fixes.

## Important Notes

**Line Endings Fix:** The Dockerfile includes `dos2unix` to automatically convert Windows line endings (CRLF) to Unix line endings (LF). This prevents the common error:
```
exec /scripts/unified-entrypoint.sh: no such file or directory
```

If you see this error, simply rebuild the image using the command above. The `dos2unix` conversion ensures all scripts work correctly regardless of which operating system they were edited on.

For more details about line endings, see: [../LINE_ENDINGS_FIX.md](../LINE_ENDINGS_FIX.md)

---

## Unified Container Image

**Image Name**: `fptuxaes/aes-dotnet8-console:latest`

This is the unified container image that runs both client and server processes in a single container, managed by supervisord.

### Prerequisites

- Docker installed and running
- Access to push to Docker Hub (if publishing)

### Build Command

From the repository root:

```bash
docker build -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified DockerImage/
```

### Verify Build

```bash
docker images | grep fptuxaes/aes-dotnet8-console
```

Expected output:
```
fptuxaes/aes-dotnet8-console   latest    <IMAGE_ID>    <TIME>    <SIZE>
```

### Test the Image

```bash
# Start a test container
docker run -d --name test-unified \
  -v $(pwd)/test-logs:/logs \
  fptuxaes/aes-dotnet8-console:latest

# Check if supervisord is running
docker exec test-unified supervisorctl status

# Expected output:
# client     STOPPED   Not started
# server     STOPPED   Not started

# Test starting server
docker exec test-unified /scripts/unified-control.sh StartServer 0

# Check status again
docker exec test-unified supervisorctl status

# Expected output:
# client     STOPPED   Not started
# server     RUNNING   pid <NUMBER>, uptime <TIME>

# Cleanup
docker rm -f test-unified
```

### Push to Docker Hub (Optional)

If you have access to the fptuxaes Docker Hub account:

```bash
# Login to Docker Hub
docker login

# Push the image
docker push fptuxaes/aes-dotnet8-console:latest
```

### Image Contents

```
fptuxaes/aes-dotnet8-console:latest
├─ Base: mcr.microsoft.com/dotnet/sdk:8.0
├─ /apps/
│  ├─ server/  (student server DLLs copied here)
│  └─ client/  (student client DLLs copied here)
├─ /logs/
│  ├─ server.log (server output with stage markers)
│  └─ client.log (client output with stage markers)
├─ /scripts/
│  └─ unified-control.sh (control script)
└─ /etc/supervisor/conf.d/
   └─ supervisord.conf (process manager config)
```

### Process Management

Processes are managed via supervisord:

- **Server**: `/apps/server/*.dll` (auto-detected)
- **Client**: `/apps/client/*.dll` (auto-detected)
- **Control**: `/scripts/unified-control.sh`

### Control Script Usage

```bash
# Start server
docker exec <container> /scripts/unified-control.sh StartServer <stage>

# Start client
docker exec <container> /scripts/unified-control.sh StartClient <stage>

# Stop server
docker exec <container> /scripts/unified-control.sh CloseServer <stage>

# Stop client
docker exec <container> /scripts/unified-control.sh CloseClient <stage>

# Check status
docker exec <container> /scripts/unified-control.sh Status

# Stop all
docker exec <container> /scripts/unified-control.sh StopAll
```

### Environment Variables

Pre-configured in the image:
- `DOTNET_CLI_TELEMETRY_OPTOUT=1`
- `DOTNET_RUNNING_IN_CONTAINER=true`
- `DOTNET_SYSTEM_CONSOLE_UNBUFFERED=1`

### Exposed Ports

- Port 9001: Supervisord HTTP interface (internal only)

### Volume Mounts

When running the container, mount the app folders to access logs:
```bash
docker run -d \
  --name ag-unified-student123 \
  --network auto-grading-network \
  -v /path/to/server-output:/apps/server \
  -v /path/to/client-output:/apps/client \
  fptuxaes/aes-dotnet8-console:latest
```

Note: Logs are now in `/apps/server/` and `/apps/client/`, not `/logs/`.
This allows easy export of per-stage log files.

### Logs

Logs are written per stage to the app folders:
- `/apps/server/server-stage-0.log` - Server output for stage 0
- `/apps/server/server-stage-1.log` - Server output for stage 1
- `/apps/client/client-stage-0.log` - Client output for stage 0
- `/apps/client/client-stage-1.log` - Client output for stage 1
- etc.

Each stage gets its own log file, making it easy to parse per-stage output.
No stage markers needed - file separation provides stage isolation.

### Troubleshooting

#### Supervisord not starting
```bash
docker logs <container>
```

#### Processes won't start
```bash
docker exec <container> supervisorctl tail -f server
docker exec <container> supervisorctl tail -f client
```

#### Check supervisord status
```bash
docker exec <container> supervisorctl status
```

#### View full supervisord log
```bash
docker exec <container> cat /var/log/supervisor/supervisord.log
```

### Image Size Optimization (Future)

To reduce image size:
1. Use `mcr.microsoft.com/dotnet/aspnet:8.0` as base (runtime only)
2. Remove unnecessary apt packages
3. Use multi-stage build

Current size: ~800MB (SDK)  
Optimized size: ~400MB (Runtime)

### Updating the Image

When making changes to:
- `Dockerfile.unified`
- `supervisord-unified.conf`
- `unified-control.sh`

Always rebuild and test:
```bash
# Rebuild
docker build -t fptuxaes/aes-dotnet8-console:latest -f DockerImage/Dockerfile.unified DockerImage/

# Test
docker run -d --name test-unified fptuxaes/aes-dotnet8-console:latest
docker exec test-unified /scripts/unified-control.sh Status
docker rm -f test-unified

# Push
docker push fptuxaes/aes-dotnet8-console:latest
```
