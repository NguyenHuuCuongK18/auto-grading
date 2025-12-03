# Parallel Grading Implementation

This document explains the batch-based parallel grading implementation for the auto-grading system.

## Overview

The system now supports grading multiple students simultaneously in batches with the following features:
1. **Student Range Selection**: Select students by index range (Start Index to End Index) - similar to selecting by paper
2. **Batch Grading**: Grade multiple students at the same time with configurable batch size ("Number of Solutions")
3. **Port Management**: Each student in a batch gets unique ports (incremented from base port)
4. **Database Isolation**: Each student has their own database instance in a shared SQL container
5. **Network Monitoring**: Each student has their own network monitor capturing traffic on their assigned port
6. **Enhanced Network Sheet**: Shows expected network (testkit) vs actual network (student) with pass/fail comparison

## Configuration in GradingWindow

### UI Configuration Location

**IMPORTANT**: All batch grading and student selection configuration is done in the **GradingWindow**, NOT in SetupWindow.

SetupWindow is only for:
- Selecting Submit folder
- Selecting TestKit folder
- Selecting Save Results folder
- Configuring Client/Server project names

### GradingWindow Configuration Sections

The GradingWindow has three clearly organized sections:

#### 1. Batch Grading Configuration
- **Number of Solutions to Grade at a Time** (default: 1): How many students are graded simultaneously in each batch
- **Start Index** (default: 0): Start grading from this student index (0-based) - allows selecting student range like selecting by paper
- **End Index** (default: -1): End grading at this index (-1 means all) - combined with Start Index for range selection

#### 2. Student Selection
- **Quick Select by Paper**: Dropdown to quickly select all students with a specific paper number
- **Select All / Unselect All**: Buttons to bulk select/deselect students

#### 3. Grading Actions
- **Start**: Start All / Start Selected buttons
- **Control**: Pause / Resume buttons
- **Reset**: Reset All / Reset Selected buttons

## How Batch Grading Works

### Example 1: Sequential Grading
- Number of Solutions: 1
- Start Index: 0
- End Index: -1

Result: Students are graded one at a time (original behavior)

### Example 2: Batch Grading with 5 Students
- Number of Solutions: 2
- Start Index: 0
- End Index: -1

Execution:
- **Batch 1**: Students 0 and 1 grade simultaneously
- **Batch 2**: Students 2 and 3 grade simultaneously
- **Batch 3**: Student 4 grades alone (only 1 student left)

### Example 3: Range Selection with Batch Grading
- Number of Solutions: 3
- Start Index: 5
- End Index: 10

Execution:
- Selects students at indices 5, 6, 7, 8, 9, 10 (6 students total)
- **Batch 1**: Students 5, 6, and 7 grade simultaneously
- **Batch 2**: Students 8, 9, and 10 grade simultaneously

## Implementation Details

### Port Management

For batch grading, ports are incremented for each student in a batch:
- Student 0: basePort + 0
- Student 1: basePort + 1
- Student 2: basePort + 2
- etc.

**CRITICAL**: Internal and external ports MUST match for network monitoring with libpcap/npcap.
- If base port is 8000 and batch size is 3:
  - Student 0: Internal=8000, External=8000
  - Student 1: Internal=8001, External=8001
  - Student 2: Internal=8002, External=8002
  - Student 3 (next batch): Internal=8000, External=8000 (ports reused after batch completes)

### Container Naming

Each student gets unique container names:
- Server: `ag-server-{studentCode}`
- Client: `ag-client-{studentCode}`
- Database: `auto-grading-sqlserver` (shared)

### Database Management

**Current Implementation**:
- One SQL Server container is shared by all students
- Database container name: `auto-grading-sqlserver`

**Per-Student Database Instances**:
Each student gets their own database instance:
- Database name format: `{originalDbName}_{studentCode}`
- Example: `PE_PRN_Sum25B5_WA_cuongnhhe186494`

The connection string is generated per student with their unique database name.

### Network Monitor per Student

Each student in a batch gets their own network monitor instance:
- Monitors the assigned port for that student
- Captures packets independently
- Network captures are stored per student

### Batch Execution Flow

**Sequential (Number of Solutions = 1)**:
```csharp
foreach (var student in students)
{
    await GradeStudentAsync(student, config, portOffset: 0);
}
```

**Batch (Number of Solutions > 1)**:
```csharp
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

The semaphore limits how many students can be graded simultaneously, creating the batch behavior.

## CLI Usage

### Basic Commands

```bash
# List students
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- list --submit Submit --paper 1

# Grade all students sequentially
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1

# Grade 3 students in parallel (batch size = 3)
dotnet run --project Application/SolutionGrader.Cli --framework net8.0 -- dockergrade --submit Submit --testkit TestKit/Q1 --paper 1 --parallel 3

# Grade from index 5 to 10 (student range selection)
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

## UI Design Principles

The GradingWindow UI follows these principles:
1. **Clear Visual Hierarchy**: 3 distinct sections with headers
2. **Logical Grouping**: Related controls grouped together
3. **Visual Separators**: Lines between button groups
4. **Comprehensive Tooltips**: Multi-line tooltips with examples
5. **Consistent Styling**: White sections on light gray background
6. **Professional Appearance**: Bootstrap-inspired color scheme

## Testing Plan

### Prerequisites
1. Docker installed and running
2. SQL Server image available: `mcr.microsoft.com/mssql/server:2019-latest`
3. .NET 8 SDK installed
4. libpcap (Linux/macOS) or npcap (Windows) installed
5. Admin/sudo permissions for network monitoring

### Test Cases

#### Test 1: Sequential Grading (1 student at a time)
- Number of Solutions: 1
- Start Index: 0, End Index: -1
- Expected: Students graded one at a time with port 8000

#### Test 2: Batch Grading (3 students per batch)
- Number of Solutions: 3
- Start Index: 0, End Index: -1
- Expected: Students graded in batches of 3 with ports 8000, 8001, 8002

#### Test 3: Range Selection (students 5-10)
- Number of Solutions: 2
- Start Index: 5, End Index: 10
- Expected: Only students at indices 5-10 are graded in batches of 2

#### Test 4: UI Clarity
- Verify section headers are clear
- Verify tooltips are informative
- Verify button grouping makes sense
- Verify visual consistency

## Known Limitations

1. **Database Instance Management**: Currently uses single database. Per-student database instances implementation in progress.
2. **Port Conflicts**: If base port + batchSize > available ports, may encounter conflicts.
3. **Resource Limits**: Batch grading requires sufficient system resources (CPU, memory, network).
4. **Network Monitoring**: Requires admin/sudo permissions and may not work in some virtualized environments.

## Future Enhancements

1. Implement per-student database instance creation and cleanup
2. Add database instance reset optimization (DROP/CREATE DATABASE instead of container restart)
3. Add resource monitoring to prevent system overload
4. Implement automatic port conflict detection and resolution
5. Add batch progress indicators in UI
