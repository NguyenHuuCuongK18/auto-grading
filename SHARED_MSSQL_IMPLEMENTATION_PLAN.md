# Shared MSSQL Container Implementation Plan

## Requirements

Based on comment from @dongnuc:
1. **Shared MSSQL Container**: Single container for all students (not per-student containers)
2. **Per-Student Database Instances**: Each student gets their own database on the shared container
3. **Student-Level Reset**: Database reset only resets the student's DB, not the entire container
4. **Reference Implementation**: See main branch or https://github.com/NguyenHuuCuongK18/test-grader.git

## Current Architecture (To Be Replaced)

- Per-student database containers (costly)
- Full container restart for database reset
- Each student gets isolated MSSQL container

## New Architecture (Shared Container)

```
Grading Session Start
    ↓
Create/Start Shared MSSQL Container
    ↓
For Each Student:
    ↓
    Create Student Database (Student_{StudentCode})
    ↓
    Run Init Scripts
    ↓
    Grade Student (uses connection to Student_{StudentCode})
    ↓
    Drop Student Database (cleanup)
    ↓
Next Student
    ↓
Grading Session End
    ↓
Stop/Remove Shared MSSQL Container
```

## Implementation Components

### 1. Shared Database Container Service

**File**: `Lib/SolutionGrader.Core/Services/SharedMsSqlContainerService.cs`

**Responsibilities**:
- Start/stop shared MSSQL container
- Create per-student database instances
- Drop per-student databases
- Build connection strings for student databases
- Health checks and container status

**Key Methods**:
```csharp
Task EnsureSharedContainerRunningAsync()
Task CreateStudentDatabaseAsync(string studentCode, string? initScriptPath)
Task DropStudentDatabaseAsync(string studentCode)
Task StopSharedContainerAsync()
string GetStudentConnectionString(string studentCode)
```

### 2. Student Database Naming Convention

Format: `Student_{StudentCode}`

Examples:
- `Student_AnhDThe187386`
- `Student_dungtdhe186461`

Benefits:
- Clear ownership
- Easy to identify and cleanup
- No conflicts between students

### 3. Database Initialization Script

Per student, before grading:
```sql
-- Drop existing database if it exists
IF EXISTS (SELECT name FROM sys.databases WHERE name = 'Student_{StudentCode}')
BEGIN
    ALTER DATABASE [Student_{StudentCode}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [Student_{StudentCode}];
END
GO

-- Create fresh database
CREATE DATABASE [Student_{StudentCode}];
GO

-- Switch to student database
USE [Student_{StudentCode}];
GO

-- Run student-specific initialization script (if provided)
-- (Table creation, seed data, etc.)
```

### 4. Connection String Management

**Before (per-student container)**:
```
Server=localhost,{StudentPort};Database=Library;User Id=sa;Password=...
```

**After (shared container)**:
```
Server=localhost,1433;Database=Student_{StudentCode};User Id=sa;Password=...
```

Changes:
- Fixed port (1433) - shared container
- Dynamic database name based on student code
- All students use same server

### 5. Integration Points

**DockerGradingService Changes**:
- Remove per-student database container creation
- Use shared container service instead
- Pass student code to connection string builder
- Call CreateStudentDatabase before grading
- Call DropStudentDatabase after grading

**ConnectionStringHelper Changes**:
- Add method for student database connection strings
- Format: `BuildForStudentDatabase(int sharedPort, string studentCode, string? username, string? password)`

### 6. Container Lifecycle

**Session Start** (once per grading batch):
```csharp
_sharedDbService.EnsureSharedContainerRunningAsync()
```

**Per Student** (before grading):
```csharp
await _sharedDbService.CreateStudentDatabaseAsync(studentCode, testKitDbScriptPath);
```

**Per Student** (after grading):
```csharp
await _sharedDbService.DropStudentDatabaseAsync(studentCode);
```

**Session End** (once after all students graded):
```csharp
await _sharedDbService.StopSharedContainerAsync();
```

### 7. Resource Optimization

**Cost Savings**:
- 1 MSSQL container vs N containers (N = number of students)
- Reduced memory footprint
- Faster startup (container already running)
- Shared SQL Server process

**Performance**:
- Database creation ~1-2s (vs container startup ~10-15s)
- Instant availability (no wait for SQL Server to start)
- Parallel grading still possible (different databases on same server)

### 8. Error Handling

**Container Not Running**:
- Auto-start on first student
- Retry logic for transient failures

**Database Creation Failure**:
- Log error
- Skip student or fail gracefully
- Don't affect other students

**Database Drop Failure**:
- Log warning
- Continue (database will be dropped/recreated on next run)

### 9. Configuration

Add to `DockerGradingConfig`:
```csharp
public bool UseSharedDatabaseContainer { get; set; } = true;
public string SharedDatabaseContainerName { get; set; } = "auto-grading-mssql-shared";
public int SharedDatabasePort { get; set; } = 1433;
```

### 10. Migration Strategy

**Backward Compatibility**:
- Keep existing per-student container code
- Add `UseSharedDatabaseContainer` flag
- Default to new shared approach
- Allow fallback to old approach if needed

## Implementation Order

1. ✅ Create `SharedMsSqlContainerService` 
2. ✅ Add student database creation/drop methods
3. ✅ Update `ConnectionStringHelper` for student databases
4. ✅ Modify `DockerGradingService` to use shared container
5. ✅ Update `EnvironmentResetService` for student-level reset
6. ✅ Add configuration options
7. ✅ Test with sample students
8. ✅ Document new architecture

## Testing Strategy

1. **Single Student**: Verify database created, graded, dropped
2. **Multiple Students**: Verify no conflicts, proper isolation
3. **Parallel Grading**: Verify concurrent database access works
4. **Error Cases**: Container down, DB creation fails, etc.
5. **Resource Usage**: Monitor memory, CPU vs old approach

## Ghost Containers Question

The user asks if "ghost containers being quickly spawned and disposed" is normal.

**Answer**: 
- If using **per-student containers**: Yes, this is expected but costly
- With **shared container**: Only 1 container for entire session, no rapid spawning
- The shared approach solves the "ghost container" issue by eliminating rapid container creation/destruction
