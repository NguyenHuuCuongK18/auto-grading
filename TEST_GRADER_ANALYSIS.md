# Test-Grader Repository Analysis - Database Pattern

## Repository Cloned
Successfully cloned: https://github.com/NguyenHuuCuongK18/test-grader.git

## Database Architecture Pattern Found

### Key Files Analyzed
- `/Application/EnvironmentManagerHelper/DotNetEnvironmentManagerHelper/Services/EnvironmentSetupService.cs`
- `/Application/EnvironmentManagerHelper/JavaJspEnvironmentManagerHelper/Services/EnvironmentSetupService.cs`

### Architecture Confirmed

#### 1. Shared MSSQL Container (TestKit Level)
```csharp
public override void SetupDatabaseContainer()
{
    // Creates ONE container for entire grading session
    DockerBase dockerBase = new DockerBase
    {
        ImageName = imageName,                    // MSSQL image
        ContainerName = containerName,             // Shared container name
        HostPort = hostPort,                       // Single port (e.g., 1433)
        EnvironmentVariables = new Dictionary<string, string>
        {
            { "ACCEPT_EULA", "Y" },
            { $"MSSQL_{databaseUsername.ToUpper()}_PASSWORD", databasePassword }
        }
    };
    dockerCommandExecutor.RunContainer(dockerBase, 3000);
}
```

**Key Points**:
- Container created once at testkit setup
- NOT recreated per student
- Uses environment variables from environment.xlsx

#### 2. Database Reset Per Test Case (Student Level)
```csharp
private void ResetSqlDatabase(string sqlContainerName, string sqlUsername, 
                               string sqlPassword, string databaseName)
{
    // DROP DATABASE (if exists)
    string dropDatabaseQuery = $@"
        USE master; 
        IF EXISTS(SELECT * FROM sys.databases WHERE name = '{databaseName}') 
        BEGIN 
            ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; 
            DROP DATABASE [{databaseName}]; 
        END;";
    
    string command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost " +
                     $"-U {sqlUsername} -P {sqlPassword} -Q \"{dropDatabaseQuery}\"";
    dockerCommandExecutor.ExecDockerCommand(command);

    // CREATE DATABASE from script
    command = $"{sqlContainerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost " +
              $"-U {sqlUsername} -P {sqlPassword} -i /var/opt/mssql/{databaseName}.sql";
    dockerCommandExecutor.ExecDockerCommand(command);
}
```

**Key Points**:
- Drop existing database
- Set SINGLE_USER mode with ROLLBACK IMMEDIATE (disconnects active connections)
- Drop the database
- Recreate from SQL script file
- Script file is pre-copied to `/var/opt/mssql/{databaseName}.sql`

#### 3. SQL Script Preparation
```csharp
// Copy SQL script to container (done once per question)
dockerCommandExecutor.CopyFileToContainer(
    newSqlFilePath, 
    $"{containerName}:/var/opt/mssql/{sqlFileName}"
);
```

**Key Points**:
- SQL initialization script copied to container
- Stored in `/var/opt/mssql/` directory
- Executed via `sqlcmd -i` flag

### Flow Diagram

```
Testkit Setup:
    └── SetupDatabaseContainer()
        └── Create shared MSSQL container (ONCE)

For Each Question:
    └── Copy SQL init script to container
    
For Each Test Case:
    └── ResetDatabase()
        ├── DROP DATABASE (if exists)
        ├── Disconnect active connections (SINGLE_USER mode)
        └── CREATE DATABASE from script file

Testkit Teardown:
    └── Remove shared container
```

### Configuration from environment.xlsx

The test-grader reads these keys from environment.xlsx Config sheet:

```csharp
EnvironmentConfiguration.DatabaseContainerName        // e.g., "mssql-server"
EnvironmentConfiguration.DatabaseImageName            // e.g., "mcr.microsoft.com/mssql/server:2019-latest"
EnvironmentConfiguration.DatabaseContainerInternalPort // e.g., 1433
EnvironmentConfiguration.DatabaseContainerHostPort    // e.g., 1433
EnvironmentConfiguration.DatabaseUsername             // e.g., "sa"
EnvironmentConfiguration.DatabasePassword             // e.g., "YourStrong@Passw0rd"
EnvironmentConfiguration.DatabaseName                 // e.g., "LibraryDB"
```

### Differences from Current Implementation

**test-grader approach**:
- Uses same database name for all students
- Resets by dropping and recreating the SAME database
- SQL script defines the database structure

**Our documented approach**:
- Uses different database name per student (Student_{StudentCode})
- Each student gets isolated database
- Avoids conflicts if parallel grading

**Recommendation**: 
Our approach (Student_{StudentCode}) is BETTER for parallel grading because:
- Multiple students can be graded simultaneously
- No database name conflicts
- Easier cleanup and debugging
- Can see which database belongs to which student

However, if sequential grading is required, test-grader's approach works fine.

### Implementation for auto-grading Repository

Based on test-grader analysis, the implementation should:

1. **Shared Container Setup**:
   - Read config from environment.xlsx (Database_Container_Name, Database_Container_Host_Port, etc.)
   - Create single MSSQL container at session start
   - Use `DockerCommandExecutor.RunContainer()` with proper environment variables

2. **Per-Student Database**:
   - Either use same DB name (drop/recreate) like test-grader
   - Or use Student_{Code} naming for parallel support
   - Execute via `dockerCommandExecutor.ExecDockerCommand()` with sqlcmd

3. **SQL Script Handling**:
   - Copy init script to container: `/var/opt/mssql/{dbName}.sql`
   - Execute with: `sqlcmd -C -S localhost -U {user} -P {pwd} -i /var/opt/mssql/{dbName}.sql`

4. **Database Reset**:
   ```sql
   USE master;
   IF EXISTS(SELECT * FROM sys.databases WHERE name = '{dbName}')
   BEGIN
       ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
       DROP DATABASE [{dbName}];
   END;
   ```

5. **Container Lifecycle**:
   - Check if running: `dockerCommandExecutor.IsContainerRunning(containerName)`
   - Start if stopped: `dockerCommandExecutor.RunContainer()`
   - Stop at end: `dockerCommandExecutor.RemoveContainer(containerName)`

### Code Pattern to Follow

```csharp
// 1. Setup shared container (once per session)
public async Task EnsureSharedDatabaseContainerAsync()
{
    if (!dockerCommandExecutor.IsContainerRunning(containerName))
    {
        var dockerBase = new DockerBase
        {
            ImageName = "mcr.microsoft.com/mssql/server:2019-latest",
            ContainerName = containerName,
            HostPort = port,
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "ACCEPT_EULA", "Y" },
                { $"MSSQL_SA_PASSWORD", saPassword }
            }
        };
        dockerCommandExecutor.RunContainer(dockerBase, 3000);
    }
}

// 2. Reset database per student
private void ResetStudentDatabase(string studentCode, string initScriptPath)
{
    var dbName = $"Student_{studentCode}";  // Or just use same name
    
    // Drop existing
    var dropSql = $@"USE master; IF EXISTS(...) DROP DATABASE [{dbName}];";
    var dropCmd = $"{containerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost " +
                  $"-U sa -P {saPassword} -Q \"{dropSql}\"";
    dockerCommandExecutor.ExecDockerCommand(dropCmd);
    
    // Copy and execute init script
    dockerCommandExecutor.CopyFileToContainer(initScriptPath, $"{containerName}:/var/opt/mssql/init.sql");
    var createCmd = $"{containerName} /opt/mssql-tools18/bin/sqlcmd -C -S localhost " +
                    $"-U sa -P {saPassword} -i /var/opt/mssql/init.sql";
    dockerCommandExecutor.ExecDockerCommand(createCmd);
}
```

## Conclusion

The test-grader repository confirms our shared MSSQL container architecture is correct. The implementation uses:
- **ONE container** for all students (not per-student containers)
- **Database-level isolation** (drop/create per test case)
- **sqlcmd execution** inside the container
- **Configuration from environment.xlsx**

This eliminates the "ghost container" issue and provides significant resource savings.
