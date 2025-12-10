# Grade_Content Architecture Diagram

## BEFORE (Incorrect Architecture)

```
┌─────────────────────────────────────────────────────────────┐
│                    TestKit/Q1/                               │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  📄 Header.xlsx (Outer)                                     │
│  ┌────────────────────────┐                                │
│  │ Config Sheet:          │                                │
│  │ Grade_Content = "Client"│                               │
│  └────────────────────────┘                                │
│                     ↓                                        │
│  ❌ COULD BE OVERRIDDEN BY:                                 │
│                     ↓                                        │
│  📁 TC1/                    📁 TC2/                         │
│  ┌──────────────────┐      ┌──────────────────┐           │
│  │ Header.xlsx      │      │ Header.xlsx      │           │
│  │ Grade_Content=   │      │ Grade_Content=   │           │
│  │ "Server" ⚠️      │      │ "Client/Server"⚠️│           │
│  └──────────────────┘      └──────────────────┘           │
│         ↓                           ↓                       │
│  ❌ INCONSISTENT CONFIGURATION ❌                           │
│                                                              │
└─────────────────────────────────────────────────────────────┘

Problem:
- Container setup happens ONCE at the beginning
- But each test case tried to use different Grade_Content
- DLLs (student vs golden) were selected at container setup time
- Per-test-case overrides caused confusion and were impossible to apply
```

## AFTER (Correct Architecture)

```
┌─────────────────────────────────────────────────────────────┐
│                    TestKit/Q1/                               │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  📄 Header.xlsx (Outer) - SINGLE SOURCE OF TRUTH            │
│  ┌────────────────────────┐                                │
│  │ Config Sheet:          │                                │
│  │ Grade_Content = "Client"│                               │
│  └────────────────────────┘                                │
│            ↓                                                 │
│     READ ONCE AT SETUP                                      │
│            ↓                                                 │
│  ┌─────────────────────────────────────────────┐           │
│  │   Container Setup (ONE TIME)                 │           │
│  │   • Load Student Client DLL                  │           │
│  │   • Load Golden Server DLL                   │           │
│  │   • Configure network/database               │           │
│  └─────────────────────────────────────────────┘           │
│            ↓                                                 │
│     APPLIED CONSISTENTLY                                     │
│            ↓                                                 │
│  📁 TC1/              📁 TC2/              📁 TC3/          │
│  Uses:                Uses:                Uses:            │
│  Student Client ✅    Student Client ✅    Student Client ✅ │
│  Golden Server ✅     Golden Server ✅     Golden Server ✅  │
│                                                              │
│  ✅ CONSISTENT ACROSS ALL TEST CASES ✅                     │
│                                                              │
└─────────────────────────────────────────────────────────────┘

Solution:
- Grade_Content read ONCE from outer Header.xlsx
- Container setup happens ONCE with selected DLLs
- All test cases use the same configuration
- No per-test-case overrides possible
- Consistent and predictable behavior
```

## Grade_Content Values and Their Effects

### Option 1: Grade_Content = "Client"
```
┌─────────────────────────────────────┐
│  Container Setup:                   │
│  • Student's Client DLL    ✅       │
│  • Golden Server DLL       ✅       │
│  • Meta/Given/Server/              │
│                                     │
│  Test Execution:                    │
│  • Student Client connects to       │
│    Golden Server                    │
│  • Student's client is graded       │
│  • Server behavior is known/fixed   │
└─────────────────────────────────────┘
```

### Option 2: Grade_Content = "Server"
```
┌─────────────────────────────────────┐
│  Container Setup:                   │
│  • Golden Client DLL       ✅       │
│  • Student's Server DLL    ✅       │
│  • Meta/Given/Client/              │
│                                     │
│  Test Execution:                    │
│  • Golden Client connects to        │
│    Student Server                   │
│  • Student's server is graded       │
│  • Client behavior is known/fixed   │
└─────────────────────────────────────┘
```

### Option 3: Grade_Content = "Client/Server"
```
┌─────────────────────────────────────┐
│  Container Setup:                   │
│  • Student's Client DLL    ✅       │
│  • Student's Server DLL    ✅       │
│  • No golden DLLs used             │
│                                     │
│  Test Execution:                    │
│  • Student Client connects to       │
│    Student Server                   │
│  • Both components graded           │
│  • Complete solution tested         │
└─────────────────────────────────────┘
```

## Data Flow for Grade_Content

### Step 1: Loading Configuration
```
┌────────────────────────────────────────────────────┐
│ ExcelSuiteLoader.ReadGradeContentFromHeader()     │
│                                                    │
│ Input:  TestKit/Q1/Header.xlsx                   │
│ Reads:  Config sheet, Grade_Content key          │
│ Output: "Client" (example)                        │
└───────────────────────┬────────────────────────────┘
                        ↓
┌────────────────────────────────────────────────────┐
│ ExcelSuiteLoader.BuildCasesFromDirectory()        │
│                                                    │
│ • Create TestCaseDefinition for each TC           │
│ • Set GradeContent = suiteGradeContent (from outer)│
│ • NO per-test-case overrides                      │
└───────────────────────┬────────────────────────────┘
                        ↓
┌────────────────────────────────────────────────────┐
│ DockerGradingService.ReadTestKitConfig()          │
│                                                    │
│ • Store DefaultGradeContent = "Client"            │
│ • Create TestCaseInfo for each TC                 │
│ • Set GradeContent = DefaultGradeContent          │
└───────────────────────┬────────────────────────────┘
                        ↓
┌────────────────────────────────────────────────────┐
│ DockerGradingService.ExecuteDockerGradingAsync()  │
│                                                    │
│ • For each TestCase:                              │
│   - Read testCase.GradeContent                    │
│   - Select appropriate DLLs                       │
│   - Execute test with selected DLLs               │
└────────────────────────────────────────────────────┘
```

### Step 2: DLL Selection Logic
```
Grade_Content = "Client"
        ↓
┌─────────────────────────────────┐
│ IF Grade_Content == "Client":   │
│   actualClientDll = studentDll  │
│   actualServerDll = goldenDll   │
└─────────────────────────────────┘
        ↓
┌─────────────────────────────────┐
│ Validate required DLLs exist:   │
│ • Student Client DLL ✓          │
│ • Golden Server DLL ✓           │
└─────────────────────────────────┘
        ↓
┌─────────────────────────────────┐
│ Copy DLLs to container:         │
│ • /apps/client/ ← student       │
│ • /apps/server/ ← golden        │
└─────────────────────────────────┘
        ↓
Execute Test Case
```

## Message Column Data Flow

### Before (No Message Column)
```
Exception in grading
        ↓
Student.StatusMessage = "Error: ..."
        ↓
UI DataGrid shows message ✅
        ↓
❌ Excel file has NO message column
❌ Hard to find errors after grading
```

### After (With Message Column)
```
Exception in grading
        ↓
Student.StatusMessage = "Error: ..."
        ↓
┌──────────────────────────────────────┐
│ GradingOrchestrationService finally: │
│ _excelCoordinator.UpdateStudentCompleted(│
│   ...,                                │
│   student.StatusMessage  ← Pass here!│
│ )                                     │
└──────────────┬───────────────────────┘
               ↓
┌──────────────────────────────────────┐
│ ExcelLogCoordinator:                 │
│ row.Cell(10).Value = message         │
│ (Message column)                     │
└──────────────┬───────────────────────┘
               ↓
✅ UI DataGrid shows message
✅ Excel file has message in column 10
✅ Easy to find errors in StudentsSolution.xlsx
```

## Excel File Structure Comparison

### Before (No Message Column)
```
| No | StudentCode | Paper | Max | Mark | Status  | Start | End | Duration |
|----|-------------|-------|-----|------|---------|-------|-----|----------|
| 1  | student1    | 1     | 10  | 8.5  | Success | 10:00 | 10:01| 45s     |
| 2  | student2    | 1     | 10  | 0    | Failed  | 10:02 | 10:02| 15s     |
                                           ↑
                           ❌ Where's the error message?
```

### After (With Message Column)
```
| No | StudentCode | Paper | Max | Mark | Status  | Start | End | Duration | Message                    |
|----|-------------|-------|-----|------|---------|-------|-----|----------|----------------------------|
| 1  | student1    | 1     | 10  | 8.5  | Success | 10:00 | 10:01| 45s     | Grading completed: 8.5/10.0|
| 2  | student2    | 1     | 10  | 0    | Failed  | 10:02 | 10:02| 15s     | Error: CLIENT DLL not found|
                                                                             ↑
                                                                   ✅ Error clearly visible!
```

## Key Architectural Principles

### 1. Single Source of Truth
```
Outer Header.xlsx
        ↓
   SINGLE READ
        ↓
All Test Cases
```
- Grade_Content is read ONCE from outer Header.xlsx
- No ambiguity, no overrides
- Consistent behavior

### 2. One-Time Container Setup
```
Container Setup (ONCE)
        ↓
   Select DLLs
        ↓
Execute All Test Cases
```
- Container is created ONCE with selected DLLs
- Cannot change DLLs mid-execution
- Per-test-case overrides are impossible

### 3. Explicit Error Reporting
```
Exception Occurs
        ↓
StatusMessage Set
        ↓
UI Shows Message ✅
Excel Shows Message ✅
```
- All errors captured in StatusMessage
- Visible in both UI and Excel
- Easy troubleshooting

## Summary

| Aspect              | Before                      | After                       |
|---------------------|-----------------------------|-----------------------------|
| Grade_Content Source| Per-test-case (inconsistent)| Outer Header.xlsx (consistent)|
| Container Setup     | Multiple? (confusing)       | Once (clear)                |
| DLL Selection       | Per test case (wrong)       | At setup (correct)          |
| Error Visibility    | UI only                     | UI + Excel                  |
| Message Column      | ❌ Not present              | ✅ Present (column 10)      |
| Troubleshooting     | ❌ Difficult                | ✅ Easy                     |
| Architecture        | ❌ Broken                   | ✅ Aligned                  |

The changes restore architectural consistency and improve error visibility, making the system more reliable and easier to use.
