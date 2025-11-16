# Docker SQL Server Setup Guide

This guide explains how to set up and use SQL Server with Docker for the auto-grading system.

## Prerequisites

- Docker Desktop installed (Windows, macOS, or Linux)
- For Windows containers: Docker Desktop must be switched to Windows container mode (Windows hosts only)

## Quick Start

### Option 1: Using Docker Compose (Recommended)

1. **Start the SQL Server container:**
   ```bash
   docker-compose up -d
   ```

2. **Verify the container is running:**
   ```bash
   docker ps
   ```
   You should see a container named `sqlserver-test` running.

3. **Wait for SQL Server to be ready:**
   ```bash
   docker logs -f sqlserver-test
   ```
   Wait until you see "SQL Server is now ready for client connections."

4. **Run your tests with Docker mode:**
   ```bash
   dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
     --suite "path/to/test/suite" \
     --out "path/to/output" \
     --useDocker
   ```

### Option 2: Manual Docker Run

If you prefer not to use Docker Compose:

```bash
docker run -d \
  --name sqlserver-test \
  -e "ACCEPT_EULA=Y" \
  -e "SA_PASSWORD=YourStrong@Passw0rd" \
  -e "MSSQL_PID=Developer" \
  -p 1433:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

## Container Configuration

### Default Settings

- **Container Name:** `sqlserver-test` (required - the application looks for this name)
- **SA Password:** `YourStrong@Passw0rd` (configurable via environment.xlsx)
- **Port:** `1433` (mapped to host port 1433)
- **Edition:** Developer (free for non-production use)

### Customizing the Password

If you want to use a different SA password:

1. Update the `SA_PASSWORD` in `docker-compose.yml`
2. Update the `Database_Password` in your test suite's `environment.xlsx`
3. Restart the container:
   ```bash
   docker-compose down
   docker-compose up -d
   ```

## Using Docker Mode

There are three ways to enable Docker mode for database operations:

### 1. Command-Line Flag (Highest Priority)

Add `--useDocker` to your command:
```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "GradeResults" \
  --useDocker
```

### 2. Environment Configuration (environment.xlsx)

Add or update the following in your test suite's `environment.xlsx` (Config sheet):

| Key | Value |
|-----|-------|
| UseDocker | true |

### 3. Test Case Specific (environment.xlsx in test case folder)

Each test case can have its own `environment.xlsx` that overrides the suite-level setting.

## Windows Containers vs Linux Containers

### Linux Containers (Default - Recommended)

The default `docker-compose.yml` uses Linux containers, which work on all platforms:
- Windows (with Docker Desktop)
- macOS
- Linux

**Advantages:**
- Works on all platforms
- Faster startup time
- Smaller image size
- Better documentation and community support

### Windows Containers (Alternative)

Windows containers require:
- Windows 10/11 Pro or Windows Server
- Docker Desktop switched to Windows container mode
- Hyper-V enabled

**To use Windows containers:**

1. Switch Docker Desktop to Windows container mode:
   - Right-click Docker Desktop system tray icon
   - Select "Switch to Windows containers..."

2. Edit `docker-compose.yml`:
   - Comment out the Linux `sqlserver-test` service
   - Uncomment the `sqlserver-test-windows` service

3. Start the container:
   ```bash
   docker-compose up -d
   ```

**Note:** Windows containers are typically larger and slower to start than Linux containers.

## Troubleshooting

### Container Won't Start

Check the logs:
```bash
docker logs sqlserver-test
```

Common issues:
- **Password too weak:** Use a strong password with uppercase, lowercase, numbers, and symbols
- **Port already in use:** Stop any local SQL Server instances or change the port mapping
- **Insufficient memory:** Ensure Docker has at least 2GB RAM allocated

### Connection Failures

1. **Verify container is running:**
   ```bash
   docker ps
   ```

2. **Test connection:**
   ```bash
   docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "SELECT @@VERSION"
   ```

3. **Check if SQL Server is ready:**
   ```bash
   docker logs sqlserver-test | grep "ready for client connections"
   ```

### Database Reset Errors

If you see database reset errors even with Docker mode enabled:

1. **Check the container name:**
   The application expects a container named `sqlserver-test`. Verify with:
   ```bash
   docker ps --format "table {{.Names}}\t{{.Status}}"
   ```

2. **Verify sqlcmd is accessible:**
   ```bash
   docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -?
   ```

3. **Check credentials:**
   Ensure the SA password in your `environment.xlsx` matches the container's SA_PASSWORD.

### Permission Issues

On Linux/macOS, you may need to adjust file permissions if the container creates files:
```bash
sudo chown -R $(whoami):$(whoami) .
```

## Stopping and Cleaning Up

### Stop the container (preserves data):
```bash
docker-compose stop
```

### Stop and remove the container (preserves data volume):
```bash
docker-compose down
```

### Remove everything including data:
```bash
docker-compose down -v
```

### Remove just the container (manual method):
```bash
docker stop sqlserver-test
docker rm sqlserver-test
```

## Performance Tips

1. **Allocate sufficient resources:** In Docker Desktop settings, allocate at least:
   - 2 CPUs
   - 2GB RAM
   - 20GB disk space

2. **Use volumes for persistence:** The docker-compose configuration uses a named volume for better performance.

3. **Keep the container running:** Starting SQL Server takes 15-30 seconds. Keep the container running between test runs.

## Security Notes

⚠️ **Important Security Considerations:**

1. **Default Password:** The default password `YourStrong@Passw0rd` is used for development only. Change it for any production or shared environment.

2. **Network Exposure:** By default, the container binds to all network interfaces (0.0.0.0:1433). For additional security, bind to localhost only:
   ```yaml
   ports:
     - "127.0.0.1:1433:1433"
   ```

3. **SA Account:** The SA account has full administrative privileges. Consider creating limited-privilege accounts for applications.

## Differences from Local SQL Server

When using Docker mode vs local SQL Server:

| Feature | Local SQL Server | Docker SQL Server |
|---------|------------------|-------------------|
| Installation | Requires local SQL Server installation | Only requires Docker |
| Startup Time | Instant (already running) | 15-30 seconds |
| Isolation | Shared with other applications | Isolated container |
| Data Persistence | Permanent | Stored in Docker volume |
| Platform Support | Windows only | All platforms (Linux containers) |
| Resource Usage | Shared with OS | Configurable limits |

## Integration with Test Suites

The auto-grading system will:

1. Check if Docker mode is enabled (via `--useDocker` flag or `environment.xlsx`)
2. If Docker mode is enabled:
   - Copy the database script to the container
   - Execute it using `sqlcmd` inside the container
   - Connect using the configured credentials
3. If Docker mode is disabled (default):
   - Connect to local SQL Server instance
   - Execute the database script directly

## Example Test Run

```bash
# Start the container
docker-compose up -d

# Wait for SQL Server to be ready
sleep 30

# Run tests with Docker mode
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "GradeResults" \
  --client "path/to/client.exe" \
  --server "path/to/server.exe" \
  --useDocker

# View results
ls -la GradeResults/GradeResult_*
```

## Additional Resources

- [SQL Server Docker Documentation](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker)
- [Docker Desktop Documentation](https://docs.docker.com/desktop/)
- [SQL Server System Requirements](https://learn.microsoft.com/en-us/sql/sql-server/install/hardware-and-software-requirements-for-installing-sql-server)
