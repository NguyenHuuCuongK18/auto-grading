# Database Reset Functionality

## Overview

The `EnvironmentResetService` provides database reset functionality with support for both local Windows SQL Server environments and Docker-based SQL Server environments.

## Features

- **Local Database Reset (Default)**: Directly connects to SQL Server using ADO.NET to drop, recreate, and populate databases
- **Docker Database Reset**: Executes SQL scripts via Docker container commands
- **Automatic Connection String Building**: Uses database configuration from Excel headers
- **SQL Batch Processing**: Automatically splits and executes SQL scripts with `GO` statements

## Usage

### Command Line Interface

When using `SolutionGrader.Cli`, you can specify the database reset mode:

```bash
# Default: Local SQL Server reset (Windows)
SolutionGrader.Cli ExecuteSuite --suite <path> --out <output> --db-script <script.sql>

# Docker environment reset
SolutionGrader.Cli ExecuteSuite --suite <path> --out <output> --db-script <script.sql> --db-docker true
```

### Programmatic Usage

```csharp
var args = new ExecuteSuiteArgs
{
    SuitePath = "path/to/suite",
    ResultRoot = "path/to/results",
    DatabaseScriptPath = "path/to/reset.sql",
    UseDatabaseDockerReset = false  // false = local (default), true = docker
};

// The suite runner will automatically use the appropriate reset method
await suiteRunner.ExecuteSuiteAsync(args);
```

## Local Database Reset (Windows)

### How It Works

The database reset process automatically detects whether the SQL script manages the database lifecycle itself:

**For scripts WITH database management commands** (DROP DATABASE, CREATE DATABASE, or USE):
1. **Read Configuration**: Extracts database configuration from the suite's Header.xlsx (or uses defaults)
2. **Build Connection String**: Creates SQL Server connection string from configuration
3. **Detect Script Type**: Analyzes the SQL script to detect database management commands
4. **Execute from Master**: Runs the entire script from the `master` database context, allowing the script to drop, create, and populate the database itself

**For scripts WITHOUT database management commands**:
1. **Read Configuration**: Extracts database configuration from the suite's Header.xlsx (or uses defaults)
2. **Build Connection String**: Creates SQL Server connection string from configuration
3. **Drop Database**: If the database exists, it's set to single-user mode and dropped
4. **Create Database**: A fresh database is created
5. **Apply Script**: The SQL script is split by `GO` statements and executed in batches

This dual approach ensures compatibility with both self-contained database scripts and simple schema/data scripts.

### Default Configuration

If no database configuration is provided in the Header.xlsx, the following defaults are used:

- **Server**: `.\\SQLEXPRESS` (local SQL Express instance)
- **Database**: `Library`
- **Username**: `sa`
- **Password**: `sa`
- **Trust Server Certificate**: `True`

### Database Configuration in Header.xlsx

The database configuration can be specified in the Excel Header file under a "Config" or "Header" worksheet:

| Field | Description | Default |
|-------|-------------|---------|
| SqlServer | SQL Server instance name | SQLEXPRESS |
| Database | Database name | Library |
| Username | SQL Server username | sa |
| Password | SQL Server password | sa |
| Type | Protocol type (HTTP/TCP) | HTTP |

### SQL Script Format

The SQL script can be written in two ways:

#### Self-Contained Scripts (Recommended for provided SQL files)

Scripts that manage their own database lifecycle (e.g., scripts from instructors or sample projects):

```sql
-- Check and drop existing database
IF DB_ID(N'Library') IS NOT NULL
BEGIN
    ALTER DATABASE [Library] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [Library];
END
GO

-- Create new database
CREATE DATABASE [Library];
GO

-- Switch to the database
USE [Library];
GO

-- Create tables and insert data
CREATE TABLE Users (
    Id INT PRIMARY KEY IDENTITY(1,1),
    Name NVARCHAR(100)
);
GO

INSERT INTO Users (Name) VALUES ('Alice');
INSERT INTO Users (Name) VALUES ('Bob');
GO
```

**Advantages:**
- Works directly in SQL Server Management Studio (SSMS)
- Guaranteed to reset the database to a clean state
- No conflicts with the grader's database management
- Prevents SQL Server connection errors

#### Simple Schema Scripts (For basic scenarios)

Scripts without database management commands:

```sql
CREATE TABLE Users (
    Id INT PRIMARY KEY IDENTITY(1,1),
    Name NVARCHAR(100)
);
GO

INSERT INTO Users (Name) VALUES ('Alice');
INSERT INTO Users (Name) VALUES ('Bob');
GO
```

**Note:** For simple scripts, the grader will automatically drop/create the database before applying the script.

## Docker Database Reset

### How It Works

1. **Copy Script**: Copies the SQL script into the Docker container
2. **Execute Script**: Runs `sqlcmd` inside the container to execute the script
3. **Handle Errors**: Gracefully handles connection and execution errors

### Requirements

- Docker must be installed and running
- A SQL Server container named `sqlserver-test` must be running
- Container must have `sqlcmd` available at `/opt/mssql-tools18/bin/sqlcmd`
- Default SA password: `YourStrong@Passw0rd`

### Docker Container Setup

Example Docker command to run SQL Server:

```bash
docker run -d \
  --name sqlserver-test \
  -e 'ACCEPT_EULA=Y' \
  -e 'SA_PASSWORD=YourStrong@Passw0rd' \
  -p 1433:1433 \
  mcr.microsoft.com/mssql/server:2022-latest
```

## Error Handling

The database reset functionality is designed to be resilient:

- If the database script file is not found, the reset is skipped (no error)
- If the reset fails, a warning is logged but test execution continues
- Docker connection failures are handled gracefully
- SQL execution errors are caught and logged

## Implementation Details

### Key Classes

- **IEnvironmentResetService**: Interface defining the reset contract
- **EnvironmentResetService**: Implementation with both local and Docker support
- **ExecuteSuiteArgs**: Contains `UseDatabaseDockerReset` flag to control mode
- **DatabaseConfiguration**: Model for database connection settings

### Methods

#### `RunDatabaseResetAsync`
Main entry point for database reset. Determines whether to use local or Docker method based on `useDocker` parameter.

#### `ExecuteSqlViaLocalConnectionAsync`
Performs local database reset using direct SQL Server connection:
- Drops existing database
- Creates new database
- Applies SQL script in batches

#### `ExecuteSqlViaDockerAsync`
Performs Docker-based database reset:
- Copies SQL file to container
- Executes via sqlcmd

## Dependencies

- **Microsoft.Data.SqlClient**: Required for local database reset functionality
- **System.Data**: For ADO.NET data types
- **System.Text.RegularExpressions**: For parsing `GO` statements in SQL scripts

## Best Practices

1. **Testing**: Always test your database reset script manually before using in automation
2. **Permissions**: Ensure the SQL Server user has appropriate permissions to drop/create databases
3. **Timeouts**: Database operations can be slow; ensure adequate timeout settings
4. **Docker**: Keep Docker containers running during test execution
5. **Security**: 
   - Use strong passwords and avoid hardcoding credentials in scripts
   - Configure database credentials properly in Header.xlsx for production use
   - The default 'sa'/'sa' credentials are for testing only
   - For Docker mode, consider using environment variables for the SA password instead of command-line arguments
   - Connection strings are built using SqlConnectionStringBuilder to prevent injection vulnerabilities

## Troubleshooting

### Local Reset Issues

**Connection Failures**:
- Verify SQL Server is running
- Check SQL Server authentication is enabled (not just Windows Auth)
- Ensure firewall allows SQL Server connections
- Verify username/password are correct

**Permission Errors**:
- User must have CREATE DATABASE and DROP DATABASE permissions
- Usually requires `sysadmin` or `dbcreator` role

**"Cannot drop database because it is currently in use" or "Database already exists" errors**:
- **Solution**: This issue has been fixed! The grader now automatically detects if your SQL script contains database management commands (DROP DATABASE, CREATE DATABASE, or USE)
- If your script manages the database itself, the grader executes it from the `master` database context to avoid conflicts
- For existing scripts: Ensure your SQL script includes DROP DATABASE at the beginning (as shown in the "Self-Contained Scripts" example above)
- The sample script `PE PRN222 sp25.sql` already follows this pattern and will work correctly

**SQL Server enters weird error state after reset attempt**:
- This was caused by the grader trying to drop/create the database while also running a script that drops/creates the same database
- **Fixed**: The grader now detects self-managing scripts and runs them appropriately
- If you encounter this with an old installation, restart SQL Server service to clear the state

### Docker Reset Issues

**Container Not Found**:
- Ensure container is running: `docker ps | grep sqlserver-test`
- Start container if needed: `docker start sqlserver-test`

**sqlcmd Not Found**:
- Verify SQL Server version includes command-line tools
- Check the sqlcmd path in the container

## Migration Guide

If you have existing code using the old Docker-only approach:

**Before**:
```csharp
await _env.RunDatabaseResetAsync(dbScriptPath, ct);
```

**After** (with explicit control):
```csharp
// For local Windows reset (new default)
await _env.RunDatabaseResetAsync(dbScriptPath, dbConfig, false, ct);

// For Docker reset (old behavior)
await _env.RunDatabaseResetAsync(dbScriptPath, dbConfig, true, ct);
```

The `SuiteRunner` automatically handles this based on the `ExecuteSuiteArgs.UseDatabaseDockerReset` flag.
