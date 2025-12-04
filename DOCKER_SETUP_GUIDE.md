# Docker Setup Guide for Auto-Grading System

## Overview
The auto-grading system uses Docker containers to run student code in isolated environments. This guide explains how to set up the required Docker images.

## Required Docker Images

### 1. Code Execution Image: `fptuxaes/aes-dotnet8-console:latest`

This image is used to run student code (both server and client applications).

**Option A: Build from the provided Dockerfile** (Recommended if you have the base image)
```bash
# From the repository root directory
docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
```

**Option B: Use a different base image** (If `fptuxaes/aes-dotnet8:latest` is not available)

Edit `DockerImage/Dockerfile` line 14 to use a publicly available image:

```dockerfile
# Change FROM line from:
FROM fptuxaes/aes-dotnet8:latest

# To one of these alternatives:
FROM mcr.microsoft.com/dotnet/sdk:8.0           # Official .NET 8 SDK
FROM mcr.microsoft.com/dotnet/aspnet:8.0        # Official .NET 8 Runtime
```

Then build:
```bash
docker build -t fptuxaes/aes-dotnet8-console:latest ./DockerImage
```

**Option C: Pull from a registry** (If the image is published)
```bash
docker pull fptuxaes/aes-dotnet8-console:latest
```

### 2. Database Image: `mcr.microsoft.com/mssql/server:2019-latest`

This is the official Microsoft SQL Server image.

```bash
docker pull mcr.microsoft.com/mssql/server:2019-latest
```

## Verifying Images

Check that both images are available:
```bash
docker images | grep -E "fptuxaes|mssql"
```

Expected output:
```
fptuxaes/aes-dotnet8-console    latest    abc123def456   ...
mcr.microsoft.com/mssql/server  2019-latest  def456abc123   ...
```

## Customizing Image Names

If you need to use different image names, you can configure them in your TestKit:

1. Open `TestKit/[YourQuestion]/Environment.xlsx`
2. Add or modify the `Config` sheet
3. Set the `CodeImageName` row to your custom image name

Example:
```
Key                    | Value
---------------------- | --------------------------------
CodeImageName          | my-custom-dotnet8-image:v1.0
```

## Troubleshooting

### Issue: "Docker image does not exist locally"
**Solution:** Build or pull the required image as shown above.

### Issue: "Error while try to run container ... with TTY"
**Possible causes:**
1. Docker daemon is not running
2. Image does not exist
3. Port conflict (another container using the same port)

**Solution:**
```bash
# Check Docker is running
docker info

# Check available images
docker images

# Check for port conflicts
docker ps -a
```

### Issue: Batch grading hangs when creating containers
**Possible causes:**
1. Missing Docker image (Docker tries to pull and hangs)
2. Network already exists but with wrong configuration

**Solution:**
```bash
# Ensure image exists
docker images fptuxaes/aes-dotnet8-console:latest

# Remove and recreate network if needed
docker network rm auto-grading-network
docker network create auto-grading-network
```

## Docker Network

The system creates a network called `auto-grading-network` for container communication.

To manually create or reset:
```bash
# Remove existing network
docker network rm auto-grading-network

# Create new network
docker network create auto-grading-network
```

## Cleanup

After grading, you can clean up containers and networks:

```bash
# Remove all auto-grading containers
docker ps -a | grep "ag-" | awk '{print $1}' | xargs docker rm -f

# Remove database container
docker rm -f auto-grading-sqlserver

# Remove network
docker network rm auto-grading-network

# Remove stopped containers
docker container prune
```

## Docker Compose Alternative

For easier setup, you can create a `docker-compose.yml`:

```yaml
version: '3.8'

services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2019-latest
    container_name: auto-grading-sqlserver
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=YourStrong@Passw0rd
    ports:
      - "1434:1433"
    networks:
      - auto-grading-network

networks:
  auto-grading-network:
    driver: bridge
```

Start services:
```bash
docker-compose up -d
```

## Additional Resources

- [Docker Documentation](https://docs.docker.com/)
- [.NET Docker Images](https://hub.docker.com/_/microsoft-dotnet)
- [SQL Server Docker Images](https://hub.docker.com/_/microsoft-mssql-server)

## Support

If you continue to experience issues:
1. Check Docker logs: `docker logs [container-name]`
2. Verify container status: `docker ps -a`
3. Check Docker daemon status: `docker info`
4. Review grading logs in the `Run_Log` folder
