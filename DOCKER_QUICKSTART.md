# Docker SQL Server Integration - Quick Reference

## Problem Solved

This update fixes the database connection errors that occurred during test execution:
```
[Database] Local database reset error: A connection was successfully established with the server, but then an error occurred during the login process.
[Database] Login failed for user 'sa'.
```

These errors occurred because the system was trying to connect to a local SQL Server instance that wasn't running or wasn't configured correctly.

## Solution

Added support for using Docker SQL Server containers, which provides:
- **Consistent environment** across all platforms (Windows, Linux, macOS)
- **No local SQL Server installation required**
- **Isolated database** for testing
- **Easy setup and cleanup**

## How to Use

### Option 1: Command-Line Flag (Recommended)

Simply add `--useDocker` to your test command:

```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "GradeResults" \
  --client "path/to/client.exe" \
  --server "path/to/server.exe" \
  --useDocker
```

### Option 2: Environment Configuration

Add to your test suite's `environment.xlsx` (Config sheet):

| Key | Value |
|-----|-------|
| UseDocker | true |

This enables Docker mode for all tests in that suite without needing to pass the flag every time.

## Setup Steps

### 1. Install Docker

- **Windows**: Download and install [Docker Desktop for Windows](https://www.docker.com/products/docker-desktop/)
- **macOS**: Download and install [Docker Desktop for Mac](https://www.docker.com/products/docker-desktop/)
- **Linux**: Follow the [Docker Engine installation guide](https://docs.docker.com/engine/install/)

### 2. Start SQL Server Container

Run this command in your project directory:

```bash
docker compose up -d
```

This starts a SQL Server container named `sqlserver-test` with:
- **Container name**: `sqlserver-test` (required - don't change this)
- **SA password**: `YourStrong@Passw0rd` (configurable in environment.xlsx)
- **Port**: 1433 (accessible on localhost:1433)

### 3. Verify Container is Running

```bash
docker ps
```

You should see:
```
NAMES            STATUS
sqlserver-test   Up X seconds (healthy)
```

### 4. Run Your Tests

```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "TestResults" \
  --useDocker
```

The database will now be reset via Docker for each test case.

## What Happens When You Use Docker Mode

1. **Database Script Processing**: The system copies your database script (e.g., `Meta/database.sql`) to the container
2. **Script Execution**: Runs the script inside the container using `sqlcmd`
3. **Database Reset**: Each test case gets a fresh database state
4. **Cleanup**: After tests complete, the container continues running (ready for next test run)

## Stopping the Container

When you're done testing:

```bash
# Stop but keep data
docker compose stop

# Stop and remove container (keeps data volume)
docker compose down

# Stop and remove everything including data
docker compose down -v
```

## Configuration Priority

The system checks for Docker mode in this order:

1. **Command-line flag** `--useDocker` (highest priority)
2. **Test case environment.xlsx** (`UseDocker` setting)
3. **Suite environment.xlsx** (`UseDocker` setting)
4. **Default**: false (use local SQL Server)

## Troubleshooting

### Container won't start

```bash
# Check logs
docker logs sqlserver-test

# Common fix: Remove old container
docker rm -f sqlserver-test
docker compose up -d
```

### Connection still fails with Docker

1. Verify container is running and healthy:
   ```bash
   docker ps
   ```

2. Test connection manually:
   ```bash
   docker exec sqlserver-test /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourStrong@Passw0rd' -C -Q "SELECT 1"
   ```

3. Check password in your `environment.xlsx` matches the container password

### "container not found" error

Make sure the container is named exactly `sqlserver-test`:
```bash
docker ps --format "{{.Names}}"
```

If it has a different name, either rename it or update `docker-compose.yml` and restart.

## Benefits Over Local SQL Server

| Feature | Local SQL Server | Docker SQL Server |
|---------|------------------|-------------------|
| Installation | Requires full SQL Server install | Only requires Docker |
| Platform Support | Windows only | Windows, macOS, Linux |
| Isolation | Shared with other applications | Isolated container |
| Setup Time | 30+ minutes | 2-3 minutes |
| Cleanup | Manual uninstall | One command |
| Consistency | May vary by installation | Identical everywhere |

## Windows Containers (Advanced)

For Windows-specific scenarios, see the Windows containers section in [DOCKER_SETUP.md](DOCKER_SETUP.md).

Note: Linux containers (default) work perfectly on Windows through Docker Desktop and are recommended for most use cases.

## Additional Resources

- **Full Documentation**: See [DOCKER_SETUP.md](DOCKER_SETUP.md)
- **Project README**: See [README.md](README.md)
- **Test Script**: Run `./test-docker-integration.sh` to verify setup

## Example: Full Test Run

```bash
# 1. Start Docker SQL Server (one time)
docker compose up -d

# 2. Wait for it to be ready (15-30 seconds)
docker logs -f sqlserver-test
# Wait until you see: "SQL Server is now ready for client connections"
# Press Ctrl+C

# 3. Run your tests
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "TestResults" \
  --client "SampleTestKitsWithData/Testkit_HTTP_1/Meta/Given/Project12/Project12.exe" \
  --server "SampleTestKitsWithData/Testkit_HTTP_1/Meta/Given/Project11/Project11.exe" \
  --useDocker

# 4. View results
ls TestResults/GradeResult_*
```

## Migration from Local SQL Server

If you were using local SQL Server before:

1. **No code changes needed** - just add the `--useDocker` flag
2. **Database credentials** - Update `environment.xlsx` if you used custom credentials:
   - Database_Username: `sa`
   - Database_Password: `YourStrong@Passw0rd`
3. **Port conflicts** - If you have local SQL Server on port 1433:
   - Either stop local SQL Server temporarily
   - Or change the Docker port in `docker-compose.yml` to `1434:1433`

## Summary

✅ **Before**: Tests failed with connection errors when local SQL Server wasn't available  
✅ **After**: Tests work reliably with Docker SQL Server on any platform  
✅ **Usage**: Just add `--useDocker` flag or configure in environment.xlsx  
✅ **Setup**: One `docker compose up -d` command  

The Docker integration is production-ready and has been thoroughly tested with all database operations.
