# GradingWindow UI Organization

## Overview

The GradingWindow UI is organized into clear sections that separate **student selection** from **grading configuration**.

## Key Concepts

### 1. Student Selection (What to Grade)
Tools to select which students you want to work with:
- **Quick Select by Index Range**: Select students from index X to index Y
- **Quick Select by Paper**: Select all students with a specific paper number
- **Manual Selection**: Use checkboxes to select individual students
- **Select All / Unselect All**: Bulk selection operations

### 2. Grading Configuration (How to Grade)
Controls how the SELECTED students are graded:
- **Number of Solutions**: How many selected students to grade simultaneously in each batch

## UI Sections

```
┌─────────────────────────────────────────────────────────────────┐
│ SECTION 1: BATCH GRADING CONFIGURATION                         │
│                                                                 │
│ Number of Solutions to Grade at a Time: [2]                    │
│ (This applies to the students you have SELECTED)               │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SECTION 2: STUDENT SELECTION                                   │
│                                                                 │
│ Quick Select by Index Range:                                   │
│   From: [0]  To: [-1]  [✓ Apply Selection]                    │
│                                                                 │
│ Quick Select by Paper: [Paper 1 ▼]                             │
│                                                                 │
│ [☑ Select All]  [☐ Unselect All]                              │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SECTION 3: GRADING ACTIONS                                     │
│                                                                 │
│ Start:   [▶ Start All]  [▶ Start Selected]                    │
│ Control: [⏸ Pause]  [▶ Resume]                                 │
│ Reset:   [↻ Reset All]  [↻ Reset Selected]                    │
└─────────────────────────────────────────────────────────────────┘
```

## Workflow Examples

### Example 1: Resume After Interruption
**Scenario**: You graded 100 students, system interrupted, need to resume from student 100

**Steps**:
1. In "Quick Select by Index Range":
   - Set From: `100`
   - Set To: `-1` (means "to the end")
   - Click `[✓ Apply Selection]`
   - Result: Students 100, 101, 102, ... are now selected
2. In "Batch Grading Configuration":
   - Set Number of Solutions: `3` (grade 3 at a time)
3. Click `[▶ Start Selected]`
   - Result: Selected students grade in batches of 3

### Example 2: Grade Specific Range with Batching
**Scenario**: Need to grade students 50-75, want to grade 5 at a time

**Steps**:
1. In "Quick Select by Index Range":
   - Set From: `50`
   - Set To: `75`
   - Click `[✓ Apply Selection]`
   - Result: Students 50-75 (26 students) are selected
2. In "Batch Grading Configuration":
   - Set Number of Solutions: `5`
3. Click `[▶ Start Selected]`
   - Result: Batches execute as:
     - Batch 1: Students 50, 51, 52, 53, 54 (5 students grading together)
     - Batch 2: Students 55, 56, 57, 58, 59 (5 students grading together)
     - ...
     - Batch 6: Student 75 (1 student grading alone)

### Example 3: Manual Selection with Batching
**Scenario**: Need to re-grade specific students that failed, want 2 at a time

**Steps**:
1. In the student list, manually check the failed students (e.g., students 5, 12, 18, 23)
2. In "Batch Grading Configuration":
   - Set Number of Solutions: `2`
3. Click `[▶ Start Selected]`
   - Result: Batches execute as:
     - Batch 1: Students 5 and 12 (2 students grading together)
     - Batch 2: Students 18 and 23 (2 students grading together)

### Example 4: Grade All Students from Paper 2
**Scenario**: Need to grade all students with Paper 2, sequential grading

**Steps**:
1. In "Quick Select by Paper":
   - Select `Paper 2` from dropdown
   - Result: All students with Paper 2 are selected
2. In "Batch Grading Configuration":
   - Leave Number of Solutions: `1` (sequential)
3. Click `[▶ Start Selected]`
   - Result: Selected students grade one at a time

## Important Notes

### Selection vs Grading
- **Selection happens FIRST**: Choose which students to work with
- **Grading happens SECOND**: Grade the selected students with specified batch size

### Index Range Selection
- **Purpose**: Quickly select a range of students without clicking checkboxes one by one
- **Use Case**: Resume after interruption, grade specific range
- **How it works**: 
  - Sets the `IsSelected` checkbox for students in the range
  - Does NOT filter or hide students
  - Does NOT affect grading directly

### Number of Solutions
- **Purpose**: Control batch size for parallel grading
- **Applies to**: Only the students that are SELECTED (checked)
- **How it works**:
  - Takes all selected students
  - Grades them in batches of specified size
  - Example: 10 selected students, batch size 3 → batches are (3, 3, 3, 1)

## Button Descriptions

### Student Selection Buttons
- **Apply Selection**: Selects students in the specified index range
- **Select All**: Checks all students in the current view
- **Unselect All**: Unchecks all students

### Grading Action Buttons
- **Start All**: Grades all students with status "Not Run" or "Paused"
- **Start Selected**: Grades only the students that are checked/selected
- **Pause**: Pauses the grading process
- **Resume**: Resumes paused grading
- **Reset All**: Resets all students' statuses to "Not Run"
- **Reset Selected**: Resets only selected students' statuses to "Not Run"

## Tips

1. **Large Batch Recovery**: If grading 200 students and interrupted at 100:
   - From: 100, To: -1, Apply Selection → selects remaining students
   
2. **Targeted Re-grading**: To re-grade specific failed students:
   - Manually check the failed students OR
   - Use index range if they're in a contiguous block
   
3. **Batch Size Selection**:
   - Use 1 for sequential (safest, slowest)
   - Use 2-3 for good performance on typical hardware
   - Use 5+ only on powerful machines with lots of RAM

4. **Paper-based Selection**: 
   - Useful when grading multiple papers
   - Select paper, start grading, repeat for next paper
