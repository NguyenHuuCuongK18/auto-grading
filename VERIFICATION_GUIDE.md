# Verification Guide for Grade_Content and Logging Improvements

## Quick Verification Steps

### 1. Verify Grade_Content is Read from Outer Header.xlsx

#### Setup Test
1. Create a test kit with the following structure:
   ```
   TestKit/Q1/
   ├── Header.xlsx (outer)
   │   └── Config sheet with Grade_Content = "Client"
   ├── Environment.xlsx
   ├── TC1/
   │   ├── Detail.xlsx
   │   └── Header.xlsx (per-test-case - should NOT override)
   └── TC2/
       ├── Detail.xlsx
       └── Header.xlsx (per-test-case - should NOT override)
   ```

2. In the outer Header.xlsx Config sheet, set:
   ```
   Key              | Value
   ----------------|-------
   Grade_Content   | Client
   ```

3. In any per-test-case Header.xlsx, you can try setting a different Grade_Content value (e.g., "Server"). It should be ignored.

#### Expected Behavior
- When grading starts, all test cases should use Grade_Content = "Client"
- Student's Client DLL + Golden Server DLL
- Log messages should confirm: `[TestCase] TC1: Grade_Content = 'Client'`
- No messages about "Grade_Content override from test case header"

#### Verification
Check the grading logs for lines like:
```
[TestKit] Root Header.xlsx Grade_Content: Client
[TestCase] TC1: Grade_Content = 'Client'
[TestCase] Using student CLIENT + golden SERVER
```

### 2. Verify Message Column in StudentsSolution.xlsx

#### Setup Test
1. Create a student submission that will fail (e.g., missing DLL)
2. Start grading

#### Expected Behavior
- StudentsSolution.xlsx should have these columns:
  ```
  No | StudentCode | ExamPaper | PossiblePoints | EarnedPoints | Status | StartTime | EndTime | Duration | Message | ServerIP | ...
  ```
- When a student fails, the Message column should contain error details

#### Sample Output
For a student with missing Client DLL:
```
Message: "Error: Test case 'TC1' requires student CLIENT but none was found. Grade_Content='Client'"
```

For successful grading:
```
Message: "Grading completed: 8.5/10.0"
```

### 3. Verify UI Message Column

#### Setup Test
1. Open the grading UI
2. Load students for grading
3. Start grading

#### Expected Behavior
- The UI DataGrid should show a "Message" column
- During grading, the message column updates with status:
  - "Grading completed: 8.5/10.0" for success
  - "Error: [specific error details]" for failures

#### Screenshot Locations
Take screenshots showing:
1. UI with Message column visible
2. Success message in Message column
3. Error message in Message column
4. StudentsSolution.xlsx with Message column populated

### 4. Verify Max Marks from QuestionMark Sheet

#### Setup Test
1. Create a test kit with Header.xlsx containing QuestionMark sheet:
   ```
   TestCase | Mark
   ---------|-----
   TC1      | 3.5
   TC2      | 4.0
   TC3      | 2.5
   ```

2. Total should be 10.0

#### Expected Behavior
- StudentsSolution.xlsx PossiblePoints column shows: 10.0
- UI DataGrid Max column shows: 10.0
- Student.MaxMark property is set to 10.0

#### Verification
1. Open StudentsSolution.xlsx after initialization
2. Check PossiblePoints column has correct value (10.0)
3. In UI, verify Max column displays 10.0

## Manual Testing Scenarios

### Scenario 1: Missing Student DLL

**Setup:**
- Student folder without Client DLL
- Test kit requires Client

**Expected Results:**
1. UI Message column shows: "Error: Test case 'TC1' requires student CLIENT but none was found. Grade_Content='Client'"
2. StudentsSolution.xlsx Message column contains same error
3. Status = Failed
4. Mark = 0

### Scenario 2: Grade_Content = "Server"

**Setup:**
- Outer Header.xlsx has Grade_Content = "Server"
- Student provides Server DLL only
- Test kit has golden Client in Meta/Given/Client

**Expected Results:**
1. All test cases use student's SERVER + golden CLIENT
2. Logs confirm: `[TestCase] Using student SERVER + golden CLIENT`
3. No per-test-case overrides applied

### Scenario 3: Successful Grading

**Setup:**
- Complete student submission
- Test kit with Grade_Content = "Client/Server"
- All test cases pass

**Expected Results:**
1. UI Message column shows: "Grading completed: 10.0/10.0"
2. StudentsSolution.xlsx Message column shows same
3. Status = Success
4. Mark = 10.0
5. No "Docker grading success" message

### Scenario 4: Grade_Content = "Client/Server" (Both)

**Setup:**
- Outer Header.xlsx has Grade_Content = "Client/Server"
- Student provides both Client and Server DLLs

**Expected Results:**
1. All test cases use student's CLIENT + student's SERVER
2. Logs confirm: `[TestCase] Using student CLIENT + student SERVER (no golden)`
3. No golden DLLs used

## Automated Verification Checklist

- [ ] Build succeeds (0 errors)
- [ ] Grade_Content is read from outer Header.xlsx
- [ ] Per-test-case Grade_Content is ignored
- [ ] Message column exists in StudentsSolution.xlsx (column 10)
- [ ] Message column shows errors for failed students
- [ ] Message column shows completion message for successful students
- [ ] UI DataGrid Message column displays correctly
- [ ] PossiblePoints column shows correct max marks from QuestionMark sheet
- [ ] UI Max column displays correct value
- [ ] "Docker grading success" message removed
- [ ] "Grading completed: X/Y" message displayed instead

## Troubleshooting

### If Grade_Content is still being read from test case headers
- Check ExcelSuiteLoader.cs line 500
- Verify gradeContent is set to suiteGradeContent
- Ensure no per-test-case override logic exists

### If Message column doesn't appear in Excel
- Check ExcelLogCoordinator.cs InitializeExcelFile method
- Verify worksheet.Cell(1, 10).Value = "Message"
- Check that all column indices are correct after adding Message column

### If error messages don't appear in Message column
- Verify GradingOrchestrationService passes student.StatusMessage
- Check ExcelLogCoordinator.UpdateStudentCompleted accepts message parameter
- Ensure message is written to row.Cell(10).Value

### If Max marks are incorrect
- Verify QuestionMark sheet exists in outer Header.xlsx
- Check TestKitDiscoveryService.GetTestKitMaxMark method
- Ensure TestCaseMarks dictionary is populated correctly
- Verify TotalMaxMark calculation in TestKitConfig

## Code Review Checklist

### ExcelSuiteLoader.cs
- [ ] Per-test-case Grade_Content reading removed
- [ ] gradeContent always set to suiteGradeContent
- [ ] Comments updated to reflect architecture

### DockerGradingService.cs
- [ ] ReadTestCaseConfig renamed to ReadTestCaseTimeout
- [ ] Grade_Content reading removed from ReadTestCaseTimeout
- [ ] TestCaseInfo.GradeContent set to tkConfig.DefaultGradeContent
- [ ] Comments explain architectural decision

### ExcelLogCoordinator.cs
- [ ] Message column added (column 10)
- [ ] Header row has "Message" label
- [ ] UpdateStudentCompleted accepts message parameter
- [ ] Message written to row.Cell(10) when provided
- [ ] All column indices updated for new column

### GradingOrchestrationService.cs
- [ ] UpdateStudentCompleted passes student.StatusMessage
- [ ] "Docker grading success" replaced with "Grading completed"
- [ ] Error messages maintain clarity

## Regression Testing

Test these existing features to ensure they still work:

1. **Batch Grading**
   - Grade multiple students simultaneously
   - Verify all complete successfully
   - Check no data loss in StudentsSolution.xlsx

2. **DLL Discovery**
   - Verify student DLLs are found correctly
   - Check both Client and Server discovery
   - Ensure golden DLL paths resolve correctly

3. **Network Monitoring**
   - Verify PCAP files are captured
   - Check network grading still works
   - Ensure protocol detection (TCP/HTTP) works

4. **Database Grading**
   - Verify database container setup
   - Check SQL script execution
   - Ensure database grading completes

5. **Excel File Generation**
   - Verify OverallSummary.xlsx created
   - Check GradeDetail.xlsx for each test case
   - Ensure all columns populated correctly

## Performance Testing

Monitor these metrics:

1. **Excel Writing Performance**
   - Time to initialize StudentsSolution.xlsx
   - Time to update single student completion
   - Batch update delay (should be ~2 seconds)

2. **Grading Performance**
   - Average time per student
   - Total batch grading time
   - No degradation from baseline

## Success Criteria

All of the following must be true:

✅ Build succeeds with 0 errors
✅ Grade_Content read from outer Header.xlsx consistently
✅ No per-test-case Grade_Content overrides
✅ Message column exists and populated correctly
✅ Error messages visible in UI and Excel
✅ Max marks display correctly
✅ No regression in existing functionality
✅ Performance remains acceptable

## Sign-off

- [ ] Code changes reviewed
- [ ] Manual testing completed
- [ ] Regression testing passed
- [ ] Documentation updated
- [ ] Ready for merge

---

**Note:** This verification guide should be used in conjunction with the main documentation (GRADE_CONTENT_AND_LOGGING_IMPROVEMENTS.md) to ensure all changes are working correctly.
