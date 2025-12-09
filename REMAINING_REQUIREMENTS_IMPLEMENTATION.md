# Remaining Requirements Implementation Plan

## Overview
This document outlines the implementation plan for the remaining requirements mentioned in the original problem statement.

## Requirements Status

### ✅ Completed
1. **Network Data Logging** - Fixed actual payload extraction
2. **Expected Data Preservation** - Network sheet template values preserved
3. **RST+ACK Packet Capture** - Server crash detection working
4. **TCP Flags Comparison** - Regex-based normalization implemented

### 🚧 In Progress

#### 1. Ghost Containers Investigation
**Issue**: Containers are being spawned and disposed quickly during grading

**Investigation Needed**:
- Check container lifecycle management in DockerGradingService
- Verify if containers are being cleaned up prematurely
- Review SharedNetworkMonitorService container management
- Check if unified container architecture is being used consistently

**Files to Review**:
- `Lib/SolutionGrader.Core/Services/DockerGradingService.cs`
- `Lib/SolutionGrader.Core/Services/SharedNetworkMonitorService.cs`
- `Application/SolutionGrader.UI/Services/GradingOrchestrationService.cs`

#### 2. Shared MSSQL Container Architecture
**Issue**: Need shared MSSQL container with per-student DB instances instead of per-student containers

**Reference Repositories**:
- https://github.com/NguyenHuuCuongK18/auto-grading.git (main branch)
- https://github.com/NguyenHuuCuongK18/test-grader.git

**Implementation Steps**:
1. Create single MSSQL container that persists across all students
2. Create per-student database instances within the shared container
3. Implement database initialization scripts
4. Update connection string management
5. Ensure proper isolation between student databases

**Key Components**:
- Container startup: Single MSSQL container at grading session start
- DB creation: Per-student database created before each student's grading
- DB reset: Only reset specific student's database, not entire container
- Connection strings: Update to point to student-specific database

#### 3. AppSettings Modification (Not Generation)
**Issue**: Currently generating new appsettings.json files; should modify existing ones

**Current Behavior**:
- `AppsettingsCreationService` generates new appsettings.json files
- Replaces student's existing configuration

**Desired Behavior**:
- Read existing appsettings.json from student's project
- Modify only specific values: Port, IpAddress, ConnectionString (MyCnn)
- Preserve all other settings (logging levels, custom config, etc.)

**Implementation**:
```csharp
// OLD: Generate new file
public void CreateAppsettings(string targetPath, string ipAddress, int port, string connectionString)
{
    var json = new JObject {
        ["IpAddress"] = ipAddress,
        ["Port"] = port,
        ["ConnectionStrings"] = new JObject {
            ["MyCnn"] = connectionString
        }
    };
    File.WriteAllText(Path.Combine(targetPath, "appsettings.json"), json.ToString());
}

// NEW: Modify existing file
public void ModifyAppsettings(string targetPath, string ipAddress, int port, string connectionString)
{
    var appsettingsPath = Path.Combine(targetPath, "appsettings.json");
    
    JObject json;
    if (File.Exists(appsettingsPath))
    {
        // Read existing appsettings
        json = JObject.Parse(File.ReadAllText(appsettingsPath));
    }
    else
    {
        // Fallback to DLL mod if no appsettings exists
        return null; // Signal to use DLL mod
    }
    
    // Modify only specific values
    if (json["IpAddress"] != null) json["IpAddress"] = ipAddress;
    if (json["Port"] != null) json["Port"] = port;
    if (json["ConnectionStrings"]?["MyCnn"] != null)
        json["ConnectionStrings"]["MyCnn"] = connectionString;
    
    File.WriteAllText(appsettingsPath, json.ToString());
}
```

**Files to Modify**:
- `Lib/SolutionGrader.Core/Services/AppsettingsCreationService.cs` → rename to `AppsettingsModificationService.cs`
- `Lib/SolutionGrader.Core/Services/DockerGradingService.cs` - update to use modification service

#### 4. DLL Mod as UI-Controlled Fallback
**Issue**: DLL modification should only be used when appsettings.json doesn't exist, and should be UI-controlled

**Current Behavior**:
- `UseDllModificationFallback` is a boolean flag
- Applied automatically in some cases

**Desired Behavior**:
- DLL mod is ONLY used when appsettings.json does NOT exist
- User can enable/disable via UI checkbox
- When disabled and no appsettings exists, show error instead of auto-applying DLL mod

**Implementation**:
```csharp
// In GradingConfiguration
public bool EnableDllModFallback { get; set; } = true;

// In DockerGradingService
private void PrepareStudentCode(...)
{
    var appsettingsPath = Path.Combine(studentCodePath, "appsettings.json");
    
    if (File.Exists(appsettingsPath))
    {
        // Modify existing appsettings
        _appsettingsModService.ModifyAppsettings(studentCodePath, ip, port, connStr);
    }
    else
    {
        // No appsettings.json exists
        if (config.EnableDllModFallback)
        {
            // Use DLL modification
            _dllModService.ModifyDlls(studentCodePath, ip, port);
        }
        else
        {
            // Error: No appsettings and DLL mod disabled
            throw new Exception("No appsettings.json found and DLL modification is disabled");
        }
    }
}
```

**UI Changes**:
- Add checkbox to GradingWindow.xaml: "Enable DLL Modification Fallback"
- Bind to configuration property
- Show tooltip explaining when it's used

**Files to Modify**:
- `Application/SolutionGrader.UI/Models/GradingConfiguration.cs` - add property
- `Application/SolutionGrader.UI/GradingWindow.xaml` - add UI control
- `Lib/SolutionGrader.Core/Services/DockerGradingService.cs` - implement logic

#### 5. Database Reset (Student Instance Only)
**Issue**: Database reset should only affect the student's database, not the entire MSSQL container

**Current Behavior**:
- Unknown - need to investigate current DB reset logic

**Desired Behavior**:
- Keep MSSQL container running throughout entire grading session
- Before each student's grading:
  1. Drop student's database (if exists)
  2. Create fresh student database
  3. Run initialization scripts
- After all grading complete: Stop MSSQL container

**Implementation**:
```sql
-- Reset script per student
IF EXISTS (SELECT name FROM sys.databases WHERE name = 'Student_{StudentCode}')
BEGIN
    DROP DATABASE [Student_{StudentCode}];
END
GO

CREATE DATABASE [Student_{StudentCode}];
GO

USE [Student_{StudentCode}];
GO

-- Run initialization scripts
-- (Create tables, seed data, etc.)
```

**Files to Create/Modify**:
- `Lib/SolutionGrader.Core/Services/MsSqlDatabaseService.cs` - new service
- `Lib/SolutionGrader.Core/Services/DockerGradingService.cs` - integrate DB service
- SQL scripts directory for initialization

## Implementation Priority

1. **AppSettings Modification** (High Priority)
   - Most impactful for students
   - Preserves their configuration
   - Relatively straightforward to implement

2. **DLL Mod as Fallback** (High Priority)
   - Closely related to AppSettings modification
   - Important for user control

3. **Shared MSSQL Container** (Medium Priority)
   - Requires understanding reference repositories
   - More complex architectural change
   - Significant resource savings

4. **Database Reset** (Medium Priority)
   - Depends on MSSQL container implementation
   - Important for test isolation

5. **Ghost Containers Investigation** (Low Priority)
   - May be a non-issue (containers supposed to be temporary)
   - Need more information to determine if it's a problem

## Next Steps

1. Review reference repositories for MSSQL architecture
2. Implement AppSettings modification service
3. Update UI to add DLL mod fallback control
4. Test with sample student projects
5. Document new behavior

## Testing Strategy

### AppSettings Modification
- Test with student project that HAS appsettings.json
- Test with student project WITHOUT appsettings.json (should use DLL mod if enabled)
- Verify original settings are preserved (logging config, custom settings)
- Verify modified values (Port, IpAddress, ConnectionString)

### DLL Mod Fallback
- Test with DLL mod enabled + no appsettings → should use DLL mod
- Test with DLL mod disabled + no appsettings → should show error
- Test with DLL mod disabled + has appsettings → should use appsettings

### MSSQL Container
- Verify single container created for entire grading session
- Verify per-student databases created
- Verify database reset between students
- Verify proper cleanup after grading session

## Notes

- All changes should be backward compatible where possible
- Add comprehensive logging for debugging
- Update documentation for each change
- Consider migration path for existing deployments
