# Batch Grading Project Mapping Fix

## Problem Summary

After updating the project naming convention system to use the flexible Project1/Project2 structure, batch grading with more than 1 student failed to work correctly. Single student grading (batch size = 1) worked fine, but batch grading with 2 or more students broke.

## Root Cause

The issue was in how Project1/Project2 names were mapped to the legacy ClientProjectName/ServerProjectName properties that the grading system uses internally.

### The Problematic Flow

The `GradingConfiguration` class has an `UpdateLegacyProperties()` method that is automatically called whenever Project1Name, Project2Name, or the role flags are set. This method maps the new flexible naming to legacy properties.

**Problem**: During initialization in `SetupWindow.StartGrading_Click()`, properties were set one at a time:

```csharp
_configuration.Project1Name = "Q11";        // Triggers UpdateLegacyProperties() with incomplete data!
_configuration.Project2Name = "Q12";        // Triggers UpdateLegacyProperties() again
_configuration.Project1IsClient = false;    // Triggers UpdateLegacyProperties() again  
_configuration.Project2IsClient = true;     // Triggers UpdateLegacyProperties() again
```

Each property setter triggered `UpdateLegacyProperties()` with **incomplete** data:

1. **First call** (when Project1Name is set):
   - Project1Name = "Q11", Project2Name = "" (not set yet)
   - Condition: "only Project1 specified"
   - **Incorrectly** maps: ClientProjectName = "Q11", ServerProjectName = "Q11"

2. **Second call** (when Project2Name is set):
   - Project1Name = "Q11", Project2Name = "Q12"
   - Role flags still have default values (Project1IsClient=false, Project2IsClient=true)
   - **Correctly** maps: ClientProjectName = "Q12", ServerProjectName = "Q11"

3. **Third/Fourth calls** (when role flags are set):
   - All properties now have final values
   - **Correctly** maps based on actual roles

While the **final** state is correct, there's a period where the properties have **wrong intermediate values**. This caused issues in batch grading scenarios.

### Why It Affected Batch Grading

When `GradingWindow.LoadStudents()` is called:
1. It uses `ClientProjectName` and `ServerProjectName` to find student DLL files
2. If these properties had incorrect values (even temporarily), DLL discovery could fail
3. In parallel/batch grading scenarios, timing and thread scheduling made this more likely to cause failures

## Solution

**File**: `Application/SolutionGrader.UI/SetupWindow.xaml.cs`

Added **explicit** mapping in `StartGrading_Click()` method that runs **once** with **complete** data:

```csharp
// CRITICAL FIX: Explicitly map to legacy properties for backward compatibility
bool hasProject1 = !string.IsNullOrWhiteSpace(_configuration.Project1Name);
bool hasProject2 = !string.IsNullOrWhiteSpace(_configuration.Project2Name);

if (hasProject1 && hasProject2)
{
    // Both projects specified - map based on roles
    _configuration.ClientProjectName = _configuration.Project1IsClient 
        ? _configuration.Project1Name 
        : _configuration.Project2Name;
    _configuration.ServerProjectName = _configuration.Project1IsClient 
        ? _configuration.Project2Name 
        : _configuration.Project1Name;
}
else if (hasProject1)
{
    // Only project1 specified - it handles both roles
    _configuration.ClientProjectName = _configuration.Project1Name;
    _configuration.ServerProjectName = _configuration.Project1Name;
}
else if (hasProject2)
{
    // Only project2 specified - it handles both roles
    _configuration.ClientProjectName = _configuration.Project2Name;
    _configuration.ServerProjectName = _configuration.Project2Name;
}
```

### Key Benefits

1. **Single Correct Mapping**: Mapping happens once with complete data, not multiple times with incomplete data
2. **Explicit Logic**: Easy to understand and debug
3. **Consistent Behavior**: Works identically for single and batch grading
4. **Backward Compatible**: Legacy code using ClientProjectName/ServerProjectName continues to work

## Testing

Test the following scenarios:

### Scenario 1: Single Project (e.g., "Q1")
- **Setup**: Project 1 = "Q1", Project 2 = empty
- **Expected**: Both ClientProjectName and ServerProjectName = "Q1"
- **Test**: Grade 1 student, then grade 2+ students in parallel

### Scenario 2: Two Projects - Traditional (e.g., "Project11", "Project12")  
- **Setup**: Project 1 = "Project11" (Server), Project 2 = "Project12" (Client)
- **Expected**: ClientProjectName = "Project12", ServerProjectName = "Project11"
- **Test**: Grade 1 student, then grade 2+ students in parallel

### Scenario 3: Two Projects - Numbered (e.g., "Q11", "Q12")
- **Setup**: Project 1 = "Q11" (Server), Project 2 = "Q12" (Client)
- **Expected**: ClientProjectName = "Q12", ServerProjectName = "Q11"
- **Test**: Grade 1 student, then grade 2+ students in parallel

## Diagnostic Logging

Added logging in `GradingWindow.LoadStudents()` and `GradingWindow.GradeStudentAsync()` to show configuration state:

```
[LoadStudents] Configuration state:
  - Project1Name: 'Q11', IsClient: False
  - Project2Name: 'Q12', IsClient: True
  - ClientProjectName (legacy): 'Q12'
  - ServerProjectName (legacy): 'Q11'
  - HasClient: True, HasServer: True
```

This helps verify the mapping is correct before students are loaded and graded.

## Related Files

- `Application/SolutionGrader.UI/SetupWindow.xaml.cs` - Contains the fix
- `Application/SolutionGrader.UI/Models/GradingConfiguration.cs` - Has UpdateLegacyProperties() method
- `Application/SolutionGrader.UI/GradingWindow.xaml.cs` - Uses the mapped properties
- `Application/SolutionGrader.UI/Services/StudentDiscoveryService.cs` - Uses ClientProjectName/ServerProjectName to find DLLs

## Migration Notes

Users experiencing this issue should:

1. **Update to this branch**: Pull the latest changes
2. **Test single student first**: Verify single student grading works
3. **Test batch grading**: Set batch size to 2 or more and verify it works
4. **Check logs**: Review diagnostic logs to confirm correct project name mapping

No changes are needed to:
- Test kit structure
- Student submission format  
- Result output format

The fix is entirely in the UI initialization code and doesn't affect the core grading logic.
