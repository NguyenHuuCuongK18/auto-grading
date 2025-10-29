# Grading Logic Documentation

## Overview

The auto-grading system uses an **all-or-nothing** approach where points are awarded only if all test steps in a test case pass.

## How It Works

### 1. Test Suite Structure

The test suite is organized with:
- **Global Header.xlsx** - Contains test case marks in the `QuestionMark` sheet
- **Test Case Folders** (e.g., TC01, TC02) - Each contains:
  - `Header.xlsx` - Test case specific configuration
  - `Detail.xlsx` - Test steps with expected inputs/outputs

### 2. Mark Allocation

Example from TestKitGen:
```
QuestionMark Sheet in Global Header.xlsx:
  TestCase | Mark
  TC01     | 1.0
  TC02     | 2.0
```

### 3. Points Distribution

Points are distributed evenly across **comparison steps** (steps with expected output):

```
Total Mark: 1.0
Comparison Steps: 3 (steps that compare output)
Points per step: 1.0 / 3 = 0.33 points
```

### 4. All-or-Nothing Grading

**If all steps PASS:**
```
Step 1: PASS → Points Possible: 0.33, Points Awarded: 0.33
Step 2: PASS → Points Possible: 0.33, Points Awarded: 0.33  
Step 3: PASS → Points Possible: 0.34, Points Awarded: 0.34
Total: 1.0 / 1.0 ✓
```

**If any step FAILS:**
```
Step 1: PASS → Points Possible: 0.33, Points Awarded: 0.00
Step 2: FAIL → Points Possible: 0.33, Points Awarded: 0.00
Step 3: PASS → Points Possible: 0.34, Points Awarded: 0.00
Total: 0.0 / 1.0 ✗
```

## Implementation Details

### Key Components

1. **ExcelDetailLogService**
   - Tracks `_allStepsPassed` flag
   - Sets to `false` if any comparison step fails
   - Awards points in `EndCase()` only if flag is `true`

2. **ExcelSuiteLoader**
   - Reads marks from global Header.xlsx
   - Passes marks to test case definitions

3. **SuiteRunner**
   - Coordinates test execution
   - Passes marks to logging service

### Code Flow

```
1. Load Suite
   └─> ExcelSuiteLoader reads Header.xlsx
       └─> Gets marks for each test case

2. Begin Test Case
   └─> ExcelDetailLogService.BeginCase()
       └─> Sets _totalMark from test case definition
       └─> Counts comparison steps in Detail.xlsx
       └─> Calculates points per step

3. Execute Steps
   └─> For each step:
       └─> Execute action
       └─> LogStepGrade()
           └─> If step fails and is comparison: _allStepsPassed = false
           └─> Store result (points awarded = 0 initially)

4. End Test Case
   └─> ExcelDetailLogService.EndCase()
       └─> If _allStepsPassed:
           └─> Update PointsAwarded = PointsPossible for all steps
       └─> Else:
           └─> PointsAwarded stays 0 for all steps
       └─> Create FailedTestDetail.xlsx if failures exist
       └─> Save GradeDetail.xlsx
```

## Output Files

### GradeDetail.xlsx

Contains complete test results for a test case:

**Sheets:**
- `InputClients` - Input steps with results
- `OutputClients` - Client output comparisons with results
- `OutputServers` - Server output comparisons with results

**Key Columns:**
- `Stage` - Step sequence number
- `Action` - What was tested (CompareText, CompareJson, etc.)
- `Result` - PASS or FAIL
- `PointsAwarded` - Points received (0 if any test failed)
- `PointsPossible` - Maximum points for this step
- `Message` - Success/error message
- `DetailPath` - Path to diff file for failures

### FailedTestDetail.xlsx

Created only when test case has failures. Provides quick overview:

**Columns:**
- `Sheet` - Which sheet had the failure
- `Stage` - Step number
- `Result` - FAIL
- `Message` - Error description
- `DetailPath` - Link to detailed diff file

### OverallSummary.xlsx

Aggregates results across all test cases:

**Columns:**
- `TestCase` - Test case name
- `Pass/Fail` - Overall result
- `PointsAwarded` - Points received
- `PointsPossible` - Maximum points
- `TOTAL` row - Sum of all points

## Example Scenario

### Scenario: Student submission with 1 error

**Test Suite:**
- TC01: Mark = 1.0, 2 comparison steps
- TC02: Mark = 2.0, 3 comparison steps

**Execution:**

TC01: All steps pass
```
Step 1: CompareText → PASS
Step 2: CompareJson → PASS
Result: 1.0 / 1.0 points awarded ✓
```

TC02: One step fails
```
Step 1: CompareText → PASS
Step 2: CompareText → FAIL (mismatch in output)
Step 3: CompareJson → PASS
Result: 0.0 / 2.0 points awarded ✗
```

**Final Score: 1.0 / 3.0 (33.3%)**

**Files Generated:**
- `GradeResult_20231029_120000/TC01/GradeDetail.xlsx` - Shows all PASS
- `GradeResult_20231029_120000/TC02/GradeDetail.xlsx` - Shows FAIL on step 2
- `GradeResult_20231029_120000/TC02/FailedTestDetail.xlsx` - Details of step 2 failure
- `GradeResult_20231029_120000/OverallSummary.xlsx` - Shows 1.0/3.0 total

## Benefits

1. **Fair Grading** - Partial credit not given for incomplete solutions
2. **Clear Feedback** - FailedTestDetail.xlsx shows exactly what failed
3. **Flexible Marks** - Supports any mark value (doubles)
4. **Easy Review** - All results in Excel format with proper formatting
