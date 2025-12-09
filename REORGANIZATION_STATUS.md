# Code Reorganization Status

## Current State
- **DockerGradingService.cs**: 3,747 lines (needs reduction to <800 lines)
- **Total C# files in Lib**: 125 files
- **Plan documented**: CODE_REORGANIZATION_PLAN.md

## Reorganization Phases

### ✅ Phase 0: Planning (COMPLETE)
- [x] Analyze current codebase
- [x] Document reorganization strategy
- [x] Create implementation plan
- [x] User approval received

### 🚧 Phase 1: Extract Specialized Services (IN PROGRESS)

#### Step 1.1: Extract PcapParsingService ⏳
**Goal**: Move PCAP parsing logic to dedicated service

**Files to Create**:
- `Lib/SolutionGrader.Core/Services/PcapParsingService.cs`
- `Lib/SolutionGrader.Core/Abstractions/IPcapParsingService.cs`

**Code to Extract from DockerGradingService.cs**:
- `ParseTcpdumpLine()` (lines 3503-3670)
- `ParsePcapForCurrentStageAsync()` (lines 3305-3485)
- Packet parsing state: `_currentParsingPacket`, `_currentPayloadBuffer`

**Estimated Impact**: ~350 lines extracted

**Dependencies to Inject**:
- IRunContext (for adding packets)
- IProgress<string> (for logging)

#### Step 1.2: Extract ContainerManagementService ⏳
**Goal**: Move Docker container lifecycle to dedicated service

**Files to Create**:
- `Lib/SolutionGrader.Core/Services/ContainerManagementService.cs`
- `Lib/SolutionGrader.Core/Abstractions/IContainerManagementService.cs`

**Code to Extract from DockerGradingService.cs**:
- `SetupUnifiedContainerAsync()` 
- `CleanupContainersAsync()`
- `CopyFilesToContainer()`
- Container name generation logic

**Estimated Impact**: ~400 lines extracted

#### Step 1.3: Consolidate AppsettingsService ⏳
**Goal**: Single service for all appsettings operations

**Files to Modify/Create**:
- Merge: AppsettingsCreationService.cs + AppsettingsModificationService.cs + inline logic
- Create: `Lib/SolutionGrader.Core/Services/AppsettingsService.cs`
- Delete: AppsettingsCreationService.cs (legacy)

**Methods**:
- `ConfigureAppsettings()` - Main entry point (modify-first with fallback)
- `ModifyExistingAppsettings()` - From AppsettingsModificationService
- `GenerateAppsettingsAsFallback()` - From AppsettingsCreationService

**Estimated Impact**: ~200 lines consolidated

### ⏳ Phase 2: Move Entity Classes to Domain

#### Step 2.1: Extract Internal Entity Classes
**Files to Create**:
- `Lib/Domain/Entities/Grading/TestKitConfig.cs`
- `Lib/Domain/Entities/Grading/TestCaseInfo.cs`
- `Lib/Domain/Entities/Grading/ExpectedNetworkFlow.cs`

**Code to Extract from DockerGradingService.cs** (lines 3680-3737):
- `internal class TestKitConfig`
- `internal class TestCaseInfo`  
- `public class ExpectedNetworkFlow`

**Change to**: `public class` in Domain project

**Update References**: Update all using statements in services

**Estimated Impact**: ~100 lines moved, improved reusability

### ⏳ Phase 3: Project Reorganization

#### Step 3.1: Move Network Services
**Files to Move**:
- `NetworkMonitorService.cs` → `Lib/NetworkMonitor/Services/`
- `SharedNetworkMonitorService.cs` → `Lib/NetworkMonitor/Services/`
- `SharedNetworkMonitorAdapter.cs` → `Lib/NetworkMonitor/Services/`
- `SharedNetworkMonitorManager.cs` → `Lib/NetworkMonitor/Services/`

**Project References to Update**:
- SolutionGrader.Core → NetworkMonitor
- UI/CLI → NetworkMonitor

#### Step 3.2: Move Environment Services
**Files to Move**:
- `EnvironmentService.cs` → `Lib/EnvironmentBuilder/Services/`
- `EnvironmentResetService.cs` → `Lib/EnvironmentBuilder/Services/`

**Project References to Update**:
- SolutionGrader.Core → EnvironmentBuilder

### ⏳ Phase 4: Cleanup

#### Step 4.1: Remove Dead Code
**Review and Remove**:
- Commented-out code blocks
- Never-called methods
- Deprecated approaches

**Analysis Required**:
- Use IDE "Find Usages" for each method
- Identify orphaned code
- Document removal decisions

#### Step 4.2: Remove Duplicates
**Identified Duplicates**:
- Multiple connection string builders (consolidate to ConnectionStringHelper)
- Repeated Docker command patterns (extract to helper)
- Similar validation methods (consolidate)

### ⏳ Phase 5: Final Refactoring

#### Step 5.1: Slim DockerGradingService
**Final Responsibility**: Orchestration only
- Coordinate specialized services
- Execute test case workflow
- Manage grading lifecycle
- Report progress

**Target**: <800 lines

#### Step 5.2: Documentation
- Update XML docs
- Create architecture diagrams
- Document service dependencies
- Update README

## Success Metrics

### Target Metrics
- [x] Plan documented (✓ Complete)
- [ ] DockerGradingService <800 lines (Currently: 3,747)
- [ ] No duplicate entity classes
- [ ] Services in correct projects
- [ ] Build succeeds (0 errors)
- [ ] All grading workflows functional

### Progress Tracking
| Phase | Status | Progress | Lines Reduced |
|-------|--------|----------|---------------|
| Phase 0: Planning | ✅ Complete | 100% | N/A |
| Phase 1: Extract Services | 🚧 In Progress | 0% | 0 / ~950 |
| Phase 2: Move Entities | ⏳ Pending | 0% | 0 / ~100 |
| Phase 3: Project Reorg | ⏳ Pending | 0% | 0 / ~500 |
| Phase 4: Cleanup | ⏳ Pending | 0% | 0 / ~500 |
| Phase 5: Final Refactor | ⏳ Pending | 0% | 0 / ~900 |
| **TOTAL** | **5%** | **~0%** | **0 / ~2,950** |

## Next Steps

### Immediate (This PR)
1. ✅ Create reorganization plan
2. ✅ Get user approval
3. ⏳ Extract PcapParsingService (Step 1.1)
4. ⏳ Extract ContainerManagementService (Step 1.2)
5. ⏳ Consolidate AppsettingsService (Step 1.3)

### Follow-up PRs
- Phase 2-3: Entity and project reorganization
- Phase 4: Cleanup and duplicate removal
- Phase 5: Final refactoring and documentation

## Testing Strategy

### After Each Extraction
1. Build solution (must succeed)
2. Run sample grading workflow
3. Verify outputs match expected
4. Check for regressions

### Critical Test Cases
- Student with working server (AnhDThe187386)
- Student with crashed server (dungtdhe186461)
- Network packet capture
- Database query verification
- Excel output generation

## Risk Mitigation

### Preserve Grading Logic
- No behavior changes during refactoring
- All test case execution logic preserved
- Network monitoring flow unchanged
- Excel comparison intact

### Incremental Approach
- One service extraction per commit
- Build and test after each change
- Easy rollback if issues found

### Documentation
- XML docs preserved
- Comment all changes
- Update architecture docs

## Notes
- This is **refactoring** - behavior must NOT change
- Focus on code organization, not new features
- Maintain backward compatibility
- Thorough testing required at each step
