# GradingWindow UI Test Checklist

This document provides a comprehensive checklist for testing all UI button events and functionality in the GradingWindow.

## Prerequisites
- Docker must be installed and running
- MSSQL Server Docker image must be available
- Submit folder with student submissions
- TestKit folder with test cases
- Ensure the database password is set (environment variable AUTOGRADING_DB_PASSWORD or in Environment.xlsx)

## Setup Window Tests

### Folder Selection
- [ ] **Browse Submit Folder**: Click and verify folder browser dialog opens, select folder and verify path appears in textbox
- [ ] **Browse Test Kit Folder**: Click and verify folder browser dialog opens, select folder and verify path appears in textbox
- [ ] **Browse Save Folder**: Click and verify folder browser dialog opens, select folder and verify path appears in textbox

### Project Configuration

#### Single Project Test (e.g., SampleStudentAtual folder scenario)
- [ ] **Project 1 Only**: Enter "Q1" in Project 1 Name textbox
- [ ] **Verify Toggles Hidden**: Confirm that Client/Server toggle buttons are NOT visible when only one project is specified
- [ ] **Validation**: Click "Start Grading" and verify it proceeds without requiring role selection

#### Two Projects Test (e.g., Submit folder scenario)
- [ ] **Project 1 and 2**: Enter "Project11" in Project 1 Name and "Project12" in Project 2 Name
- [ ] **Verify Toggles Visible**: Confirm that Client/Server toggle buttons ARE visible for both projects
- [ ] **Role Validation - Same Role**: 
  - Set both Project 1 and Project 2 to "Client"
  - Click "Start Grading"
  - Verify validation error: "When two projects are specified, one must be client and one must be server."
- [ ] **Role Validation - Correct Roles**: 
  - Set Project 1 to "Server" and Project 2 to "Client"
  - Click "Start Grading"
  - Verify it proceeds to Grading Window

### Validation Tests
- [ ] **Empty Submit Folder**: Leave Submit folder empty and click "Start Grading" - verify error message
- [ ] **Empty Test Kit Folder**: Leave Test Kit folder empty and click "Start Grading" - verify error message
- [ ] **Empty Save Folder**: Leave Save folder empty and click "Start Grading" - verify error message
- [ ] **No Project Names**: Leave both project name fields empty and click "Start Grading" - verify error message

## Grading Window Tests

### Student Selection
- [ ] **Load Students**: Verify students are loaded and displayed in the DataGrid on window load
- [ ] **Paper Dropdown**: Click paper dropdown and verify papers are listed (e.g., "Paper 1", "Paper 2")
- [ ] **Select by Paper**: Select a paper from dropdown and verify all students with that paper are selected (checkboxes checked)
- [ ] **Index Range Selection**: 
  - Enter "1" in start index and "3" in end index
  - Click "Apply" button
  - Verify students 1-3 are selected
- [ ] **Select All Button**: Click "All" button and verify all visible students are selected
- [ ] **Unselect All Button**: Click "None" button and verify all students are unselected
- [ ] **Manual Checkbox**: Click individual student checkboxes and verify selection state changes

### Grading Actions

#### Start All Button
- [ ] **Click Start All**: Click "▶ Start All" button
- [ ] **Verify Execution**: 
  - Confirm grading starts for all students with status "Not Run" or "Paused"
  - Verify log messages appear in the log panel
  - Verify status changes to "InProgress" for current student
  - Verify Docker containers are created (check with `docker ps`)
  - Verify progress updates in real-time
  - Verify status bar updates (Progress, Success, Failed counts)

#### Start Selected Button
- [ ] **Select Students**: Use checkboxes to select 2-3 students
- [ ] **Click Start Selected**: Click "▶ Selected" button
- [ ] **Verify Execution**: 
  - Confirm grading starts ONLY for selected students
  - Verify non-selected students remain "Not Run"
  - Verify log messages for selected students only
  - Verify Docker containers are created
  - Verify status updates correctly

#### Pause Button
- [ ] **Start Grading**: Click "Start All" or "Start Selected" to begin grading
- [ ] **Click Pause**: While grading is in progress, click "⏸" (Pause) button
- [ ] **Verify Pause**: 
  - Confirm current student status changes to "Paused"
  - Verify grading stops
  - Verify log message indicates pause
  - Verify Resume button becomes enabled

#### Resume Button
- [ ] **After Pause**: With grading paused, click "▶" (Resume) button
- [ ] **Verify Resume**: 
  - Confirm grading resumes from paused state
  - Verify paused students are processed
  - Verify log message indicates resume

#### Batch Processing
- [ ] **Set Batch Size**: Enter "2" in the Batch textbox
- [ ] **Start All**: Click "Start All" with 5+ students
- [ ] **Verify Parallel Execution**: 
  - Confirm 2 students are graded simultaneously
  - Verify Docker containers for multiple students exist at the same time (`docker ps`)
  - Verify port allocation works correctly (different ports for each student)
  - Verify logs show parallel execution

### Reset Actions

#### Reset All Button
- [ ] **After Grading**: Complete grading for some students
- [ ] **Click Reset All**: Click "Reset All" button
- [ ] **Verify Reset**: 
  - Confirm all students return to "Not Run" status
  - Verify marks are reset to 0
  - Verify start/end times are cleared
  - Verify result files are deleted from save folder

#### Reset Selected Button
- [ ] **After Grading**: Complete grading for some students
- [ ] **Select Students**: Check 2-3 students
- [ ] **Click Reset Selected**: Click "Reset Selected" button
- [ ] **Verify Reset**: 
  - Confirm ONLY selected students return to "Not Run" status
  - Verify non-selected students retain their status
  - Verify result files for selected students are deleted

### Navigation

#### Back to Setup Button
- [ ] **Click Back to Setup**: Click "← Back to Setup" button
- [ ] **Verify Navigation**: 
  - If grading is NOT in progress, confirm it navigates back to Setup Window
- [ ] **Confirm Dialog During Grading**: 
  - Start grading
  - Click "← Back to Setup"
  - Verify confirmation dialog appears asking to cancel grading
  - Click "No" and verify grading continues
  - Click "Yes" and verify it goes back to Setup Window

### Window Closing
- [ ] **Close During Grading**: Start grading and click the window close (X) button
- [ ] **Verify Confirmation**: Confirm dialog appears asking to cancel
- [ ] **Verify Cancellation**: Click "Yes" and verify window closes and grading stops
- [ ] **Verify Continue**: Click "No" and verify grading continues

### Real-time Updates

#### Status Bar
- [ ] **Progress Counter**: Verify "Progress: X/Y (Z%)" updates in real-time as students are graded
- [ ] **Success Counter**: Verify "Success: X" increments when students pass
- [ ] **Failed Counter**: Verify "Failed: X" increments when students fail
- [ ] **Not Run Counter**: Verify "Not Run: X" decrements as students are graded
- [ ] **Elapsed Time**: Verify "Elapsed: Xs" or "Xm Ys" updates every second during grading
- [ ] **Current Student**: Verify "Current: StudentCode" shows the student currently being graded

#### DataGrid Updates
- [ ] **Status Column**: Verify status changes (Not Run → InProgress → Success/Failed) in real-time
- [ ] **Mark Column**: Verify mark updates after grading completes
- [ ] **Progress Column**: Verify progress percentage updates during grading
- [ ] **Start Time Column**: Verify start time is set when grading begins
- [ ] **End Time Column**: Verify end time is set when grading completes
- [ ] **Duration Column**: Verify duration is calculated correctly
- [ ] **Message Column**: Verify status messages appear (errors, warnings, etc.)

#### Log Panel
- [ ] **Log Messages**: Verify log messages appear in real-time with timestamps
- [ ] **Log Levels**: Verify different log levels (INFO, ERROR, WARNING) are displayed
- [ ] **Log Scrolling**: Verify log auto-scrolls to show latest messages
- [ ] **Log Formatting**: Verify log is formatted with timestamp, level, and message

### Button State Management
- [ ] **During Grading**: 
  - Verify Start All and Start Selected buttons are DISABLED
  - Verify Pause button is ENABLED
  - Verify Resume button is DISABLED
  - Verify Reset All and Reset Selected buttons are DISABLED
- [ ] **While Paused**: 
  - Verify Start All and Start Selected buttons are ENABLED
  - Verify Pause button is DISABLED
  - Verify Resume button is ENABLED
  - Verify Reset All and Reset Selected buttons are DISABLED
- [ ] **When Idle**: 
  - Verify Start All and Start Selected buttons are ENABLED
  - Verify Pause and Resume buttons are DISABLED
  - Verify Reset All and Reset Selected buttons are ENABLED

## Integration Tests

### Docker Container Management
- [ ] **Container Creation**: Verify Docker containers are created for each student (code container + database container)
- [ ] **Container Naming**: Verify containers have unique names (e.g., auto-grading-dotnet-console-app-student123)
- [ ] **Port Allocation**: Verify each student gets a unique port (no conflicts)
- [ ] **Container Cleanup**: Verify containers are removed after grading completes
- [ ] **Network Creation**: Verify Docker network is created (auto-grading-network)

### File System Operations
- [ ] **Result Files**: Verify result Excel files are created in the save folder
- [ ] **Folder Structure**: Verify folder structure follows the pattern: SaveFolder/PaperNo/student/StudentCode/
- [ ] **File Content**: Open a few result Excel files and verify they contain grading data
- [ ] **Zip Extraction**: If student submits .zip files, verify they are extracted to solution folder

### Error Handling
- [ ] **Missing Test Kit**: Remove a test kit and verify appropriate error message
- [ ] **Missing Student DLL**: Remove a student DLL and verify appropriate error message
- [ ] **Docker Not Running**: Stop Docker and try grading - verify appropriate error message
- [ ] **Network Errors**: Disconnect network during grading and verify graceful handling

## Performance Tests
- [ ] **10 Students Sequential**: Grade 10 students sequentially (batch=1) and verify completion
- [ ] **10 Students Parallel**: Grade 10 students in parallel (batch=3) and verify completion
- [ ] **Memory Usage**: Monitor memory during grading and verify no memory leaks
- [ ] **Port Exhaustion**: Grade 20+ students to verify port allocation doesn't exhaust available ports

## Test Results Summary

### Pass/Fail Summary
- Total Tests: ___
- Passed: ___
- Failed: ___
- Blocked: ___

### Issues Found
1. _____________________________________________
2. _____________________________________________
3. _____________________________________________

### Notes
_____________________________________________________
_____________________________________________________
_____________________________________________________
