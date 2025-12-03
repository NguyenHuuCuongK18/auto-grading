# Parallel Grading Implementation

This document explains the parallel grading implementation for the auto-grading system.

## Overview

The system now supports grading multiple students simultaneously with the following features:
1. **Index Range Selection**: Grade students from a specific index to another (for restarting after incidents)
2. **Parallel Grading**: Grade multiple students at the same time with configurable parallelism
3. **Port Management**: Each student gets unique ports (incremented from base port)
4. **Database Isolation**: Each student has their own database instance in a shared SQL container
5. **Network Monitoring**: Each student has their own network monitor capturing traffic on their assigned port
6. **Enhanced Network Sheet**: Shows expected network (testkit) vs actual network (student) with pass/fail comparison

## Implementation Details

### 1. Configuration Properties

**UI (SolutionGrader.UI/Models/GradingConfiguration.cs)**
- `MaxParallelStudents` (default: 1): Number of students to grade simultaneously
- `StartIndex` (default: 0): Start grading from this student index (0-based)
- `EndIndex` (default: -1): End grading at this index (-1 means all)

**CLI (Application/SolutionGrader.Cli/Services/CliGradingConfiguration.cs)**
- Same properties as UI configuration

### 2. Port Management

For parallel grading, ports are incremented for each student:
- Student 0: basePort + 0
- Student 1: basePort + 1
- Student 2: basePort + 2
- etc.

**CRITICAL**: Internal and external ports MUST match for network monitoring with libpcap/npcap.
- If base port is 8000:
  - Student 0: Internal=8000, External=8000
  - Student 1: Internal=8001, External=8001
  - Student 2: Internal=8002, External=8002

### 3. Container Naming

Each student gets unique container names:
- Server: `ag-server-{studentCode}`
- Client: `ag-client-{studentCode}`
- Database: `auto-grading-sqlserver` (shared)

### 4. Database Management

**Current Implementation**:
- One SQL Server container is shared by all students
- Database container name: `auto-grading-sqlserver`

**TODO - Per-Student Database Instances**:
Each student should have their own database instance:
- Database name format: `{originalDbName}_{studentCode}`
- Example: `PE_PRN_Sum25B5_WA_cuongnhhe186494`

The connection string should be generated per student:
```csharp
var studentDbName = $"{testKitConfig.DatabaseName}_{studentCode}";
var connectionString = ConnectionStringHelper.BuildForDocker(
    config.DatabaseContainerHostPort,
    studentDbName,  // Use per-student database name
    config.DatabaseUsername,
    config.DatabasePassword);
```

Database reset should:
1. Drop the student's database if it exists
2. Create a new database for the student
3. Execute the SQL initialization file if provided

### 5. Network Monitor per Student

Each parallel student gets their own network monitor instance:
- Monitors the assigned port for that student
- Captures packets independently
- Network captures are stored per student

### 6. Parallel Execution Flow

**CLI (Application/SolutionGrader.Cli/Services/CliDockerGradingService.cs)**:
```csharp
// Sequential (MaxParallelStudents = 1)
foreach (var student in students)
{
    await GradeStudentAsync(student, config, portOffset: 0);
}

// Parallel (MaxParallelStudents > 1)
var semaphore = new SemaphoreSlim(config.MaxParallelStudents);
var tasks = students.Select(async (student, index) =>
{
    await semaphore.WaitAsync();
    try
    {
        var portOffset = index % config.MaxParallelStudents;
        await GradeStudentAsync(student, config, portOffset);
    }
    finally
    {
        semaphore.Release();
    }
});
await Task.WhenAll(tasks);
```

### 7. Enhanced Network Sheet

The network sheet now shows:
1. **Expected Network** (from testkit Detail.xlsx): Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole
2. **Actual Network** (captured from student): Same columns prefixed with "Actual_"
3. **Result Column**: PASS (green) or FAIL (pink) for each network flow

Format matches Client/Server sheets for consistency.

## CLI Usage

### Basic Commands

```bash
# List students
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- list --submit Submit --paper 1

# Grade all students sequentially
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1

# Grade 3 students in parallel
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1 --parallel 3

# Grade from index 5 to 10 (restart after incident)
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1 --start-index 5 --end-index 10

# Grade from index 5 to end
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1 --start-index 5
```

### Required Permissions

**Linux/macOS**: Network monitoring requires sudo
```bash
sudo dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade ...
```

**Windows**: Run PowerShell or Command Prompt as Administrator

## Testing Plan

### Prerequisites
1. Docker installed and running
2. SQL Server image available: `mcr.microsoft.com/mssql/server:2019-latest`
3. .NET 8 SDK installed
4. libpcap (Linux/macOS) or npcap (Windows) installed
5. Admin/sudo permissions for network monitoring

### Test Cases

#### Test 1: Sequential Grading (1 student)
```bash
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade \
  --submit Submit --testkit TestKit/Q1 --paper 1 \
  --student cuongnhhe186494 --parallel 1
```
Expected: Student graded successfully with port 8000

#### Test 2: Parallel Grading (3 students)
```bash
sudo dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade \
  --submit Submit --testkit TestKit/Q1 --paper 1 --parallel 3
```
Expected:
- 3 students graded simultaneously
- Port allocation: 8000, 8001, 8002
- Points: cuongnhhe=2, dongnvhe=5, hoangbsthe=5
- Network sheet shows expected vs actual with pass/fail

#### Test 3: Index Range (students 1-2)
```bash
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade \
  --submit Submit --testkit TestKit/Q1 --paper 1 \
  --start-index 1 --end-index 2 --parallel 2
```
Expected: Only students at index 1 and 2 are graded

#### Test 4: Network Sheet Validation
After grading, check the GradeDetail.xlsx Network sheet:
- Should show "Expected_Flags", "Actual_Flags", "Result" columns
- Expected network from testkit should be listed first
- Actual captured network should follow
- PASS/FAIL in Result column with color coding

## Known Limitations

1. **Database Instance Management**: Currently uses single database. Need to implement per-student database instances.
2. **Port Conflicts**: If base port + maxParallel > available ports, may encounter conflicts.
3. **Resource Limits**: Parallel grading requires sufficient system resources (CPU, memory, network).
4. **Network Monitoring**: Requires admin/sudo permissions and may not work in some virtualized environments.

## Future Enhancements

1. Implement per-student database instance creation and cleanup
2. Add database instance reset optimization (DROP/CREATE DATABASE instead of container restart)
3. Add resource monitoring to prevent system overload
4. Implement automatic port conflict detection and resolution
5. Add UI implementation for parallel grading (currently CLI only)
