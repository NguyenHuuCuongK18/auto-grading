# Code Reorganization Plan

## Problem Statement
DockerGradingService.cs has grown to 3,747 lines with many responsibilities, duplicates, and redefinitions. The codebase needs reorganization to improve maintainability and follow separation of concerns.

## Analysis

### Current Issues
1. **DockerGradingService.cs (3,747 lines)** - God object with too many responsibilities:
   - Container management
   - PCAP parsing
   - Network monitoring
   - File operations
   - Appsettings generation/modification
   - Test case execution
   - Excel operations
   - Database operations

2. **Duplicate Code**:
   - Multiple connection string builders
   - Repeated Docker command patterns
   - Duplicate entity classes (TestKitConfig, TestCaseInfo, ExpectedNetworkFlow)

3. **Misplaced Code**:
   - Entity classes defined inside service files
   - Domain logic in Core services
   - Helper methods scattered across services

## Reorganization Strategy

### Phase 1: Extract Specialized Services (HIGH PRIORITY)

#### 1.1 Extract PcapParsingService
**From**: DockerGradingService.cs lines ~3300-3600
**To**: `Lib/SolutionGrader.Core/Services/PcapParsingService.cs`
**Responsibility**: Parse tcpdump output and extract network packet information

**Methods to move**:
- `ParseTcpdumpLine()`
- `ParseTcpdumpOutput()`
- Packet parsing state tracking

**Benefits**:
- Single responsibility: PCAP parsing only
- Reusable across different services
- Easier to test and maintain

#### 1.2 Extract ContainerManagementService
**From**: DockerGradingService.cs container setup methods
**To**: `Lib/SolutionGrader.Core/Services/ContainerManagementService.cs`
**Responsibility**: Create, start, stop, remove Docker containers

**Methods to move**:
- `SetupUnifiedContainerAsync()`
- `CleanupContainersAsync()`
- `CopyFilesToContainer()`
- Container lifecycle methods

#### 1.3 Consolidate Appsettings Services
**Current**: AppsettingsCreationService.cs + AppsettingsModificationService.cs + inline logic
**New**: `Lib/SolutionGrader.Core/Services/AppsettingsService.cs`
**Responsibility**: All appsettings operations (modify-first approach with generation fallback)

**Methods**:
- `ConfigureAppsettings()` - Main entry point
- `ModifyExistingAppsettings()` - Modification logic
- `GenerateAppsettingsAsFallback()` - Fallback when modification fails

### Phase 2: Move Entity Classes to Domain

#### 2.1 Extract Internal Entity Classes
**From**: DockerGradingService.cs (lines 3680-3737)
**To**: `Lib/Domain/Entities/Grading/`

**Classes to move**:
- `TestKitConfig` → `Lib/Domain/Entities/Grading/TestKitConfig.cs`
- `TestCaseInfo` → `Lib/Domain/Entities/Grading/TestCaseInfo.cs`
- `ExpectedNetworkFlow` → `Lib/Domain/Entities/Grading/ExpectedNetworkFlow.cs`

**Benefits**:
- Proper separation of domain entities
- Reusable across services
- Clear data structures

### Phase 3: Extract to Appropriate Projects

#### 3.1 Move Network Monitoring to NetworkMonitor Project
**Current**: NetworkMonitorService.cs, SharedNetworkMonitorService.cs in Core
**Should be**: `Lib/NetworkMonitor/Services/`

#### 3.2 Move Environment Setup to EnvironmentBuilder
**Current**: EnvironmentService.cs, EnvironmentResetService.cs in Core
**Should be**: `Lib/EnvironmentBuilder/Services/`

#### 3.3 Consolidate Helpers
**Current**: Helpers scattered across Core
**New Structure**:
- `Lib/Common/Helpers/` - Shared helpers
- Keep domain-specific helpers in their projects

### Phase 4: Remove Legacy/Unused Code

#### 4.1 Identify Dead Code
- Methods never called
- Commented-out code blocks
- Deprecated approaches (e.g., old generation logic)

#### 4.2 Remove Duplicates
- Multiple similar methods with slight variations
- Copy-pasted code blocks
- Redundant utility functions

### Phase 5: Improve DockerGradingService Structure

#### 5.1 Final Responsibility
**Core grading workflow orchestration only**:
- Coordinate between specialized services
- Execute test cases
- Manage grading lifecycle
- Report progress

#### 5.2 Target Size
- **Goal**: <800 lines (down from 3,747)
- **80% reduction** by extracting to specialized services

## Implementation Order

### Week 1: Critical Extractions
1. ✅ Extract PcapParsingService
2. ✅ Extract ContainerManagementService  
3. ✅ Consolidate AppsettingsService
4. ✅ Move entity classes to Domain

### Week 2: Project Reorganization
5. ⏳ Move NetworkMonitor services
6. ⏳ Move Environment services
7. ⏳ Consolidate helpers

### Week 3: Cleanup
8. ⏳ Remove dead/legacy code
9. ⏳ Remove duplicates
10. ⏳ Final DockerGradingService refactor

## Success Criteria

1. **DockerGradingService.cs**: <800 lines (orchestration only)
2. **No duplicate classes**: All entities in Domain
3. **Clear separation**: Each service has single responsibility
4. **Proper project structure**: Code in appropriate projects
5. **No regressions**: All grading workflows still work
6. **Build succeeds**: 0 errors, minimal warnings

## File Structure After Reorganization

```
Lib/
├── Domain/
│   ├── Entities/
│   │   └── Grading/
│   │       ├── TestKitConfig.cs (MOVED from DockerGradingService)
│   │       ├── TestCaseInfo.cs (MOVED from DockerGradingService)
│   │       └── ExpectedNetworkFlow.cs (MOVED from DockerGradingService)
│   └── Models/ (existing)
│
├── SolutionGrader.Core/
│   └── Services/
│       ├── DockerGradingService.cs (REDUCED to <800 lines)
│       ├── PcapParsingService.cs (NEW - extracted)
│       ├── ContainerManagementService.cs (NEW - extracted)
│       ├── AppsettingsService.cs (CONSOLIDATED)
│       ├── DatabaseQueryService.cs (existing)
│       ├── DataComparisonService.cs (existing)
│       ├── ExcelDetailLogService.cs (existing)
│       └── ... (other focused services)
│
├── NetworkMonitor/
│   └── Services/
│       ├── NetworkMonitorService.cs (MOVED from Core)
│       └── SharedNetworkMonitorService.cs (MOVED from Core)
│
├── EnvironmentBuilder/
│   └── Services/
│       ├── EnvironmentService.cs (MOVED from Core)
│       └── EnvironmentResetService.cs (MOVED from Core)
│
└── Common/
    └── Helpers/ (consolidated shared helpers)
```

## Preservation Strategy

### Critical: Preserve Grading Logic
- All test case execution logic preserved
- Network monitoring flow unchanged
- Excel comparison logic intact
- Database query verification unchanged

### Testing After Each Phase
1. Build solution (must succeed)
2. Run sample grading workflow
3. Verify all outputs match expected
4. Check for regressions

## Notes
- This is a **refactoring** exercise - behavior should NOT change
- Each extraction should be in its own commit
- Preserve all XML documentation
- Maintain backward compatibility during transition
