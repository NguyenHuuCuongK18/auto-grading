# Network Sheet Logging - Already Implemented ✅

## New Requirement (Acknowledged)
"Update the network sheet in logging as the sample logging where each network flow is a separate row, and it has 'actual' + name columns that reflect the network flow that was gotten by running the grade flow of a student. The rows before are copies from the test case detail.xlsx network sheet, so it is easier to reflect the difference if needed (this also helps when debugging if any error is thrown)."

## Status: ✅ ALREADY IMPLEMENTED

The network sheet logging **already has the exact format** described in the requirement!

## Current Implementation

### Location
`Results/{PaperNo}/student/{StudentCode}/TC{n}/GradeDetail.xlsx` → Network sheet

### Column Structure

#### Expected Columns (from Detail.xlsx)
These are copied from the test case's Detail.xlsx Network sheet:
1. **Stage** - Test stage number
2. **Time** - Expected timestamp
3. **Info** - Protocol info (e.g., "TCP")
4. **Source** - Source IP:Port
5. **Destination** - Destination IP:Port
6. **Flags** - Expected TCP flags (e.g., "SYN", "SYN, ACK")
7. **State** - Expected connection state
8. **Data** - Expected data payload
9. **SourceRole** - Expected role (Client/Server)
10. **DestinationRole** - Expected role (Client/Server)

#### Actual Columns (captured during grading)
These are populated with actual captured network data:
11. **ActualFlags** ⭐ - Captured TCP flags
12. **ActualState** ⭐ - Captured connection state
13. **ActualSourceRole** ⭐ - Captured source role
14. **ActualDestRole** ⭐ - Captured destination role
15. **ActualData** ⭐ - Captured data payload

#### Result Column
16. **NetworkResult** ⭐ - PASS/FAIL comparison (with color coding)
    - 🟢 Green background = PASS (actual matches expected)
    - 🔴 Pink background = FAIL (mismatch detected)

### Example from SampleLogging

```
┌───────┬──────────┬────────────┬──────────────┬───────────────┐
│ Stage │ Flags    │ SourceRole │ ActualFlags  │ NetworkResult │
├───────┼──────────┼────────────┼──────────────┼───────────────┤
│   3   │ SYN      │ Client     │ SYN          │ PASS 🟢       │
│   3   │ SYN, ACK │ Server     │ SYN, ACK     │ PASS 🟢       │
│   3   │ ACK      │ Client     │ ACK          │ PASS 🟢       │
│   3   │ PSH, ACK │ Client     │ PSH, ACK     │ PASS 🟢       │
│   3   │ PSH, ACK │ Server     │ PSH, ACK     │ PASS 🟢       │
│   3   │ FIN, ACK │ Client     │ FIN, ACK     │ PASS 🟢       │
│   3   │ ACK      │ Server     │ ACK          │ PASS 🟢       │
└───────┴──────────┴────────────┴──────────────┴───────────────┘
```

### Benefits of This Format

1. ✅ **Side-by-side comparison**: Easy to see expected vs actual values
2. ✅ **Easy debugging**: When a test fails, you can immediately see what was expected vs what was captured
3. ✅ **Complete picture**: All network flow details in one row
4. ✅ **Color coded**: Visual indication of PASS/FAIL without reading values
5. ✅ **Full context**: Rows include test case expectations for reference

## Implementation Details

### Code Location
`Lib/SolutionGrader.Core/Services/ExcelDetailLogService.cs`

### Key Methods

#### 1. EnsureColumns (line ~1045)
Creates the Actual* and NetworkResult columns if they don't exist:
```csharp
EnsureColumns(networkWs, new[] 
{ 
    GradingKeywords.Col_ActualFlags,
    GradingKeywords.Col_ActualState,
    GradingKeywords.Col_ActualSourceRole,
    GradingKeywords.Col_ActualDestRole,
    GradingKeywords.Col_ActualData,
    GradingKeywords.Col_NetworkResult
});
```

#### 2. PopulateNetworkActualColumns (line ~1180)
Populates the Actual* columns with captured data:
```csharp
private void PopulateNetworkActualColumns(IXLWorksheet ws, Dictionary<string, int> hdr)
{
    // Get captured packets from RunContext
    var capturedPackets = _run.GetCapturedNetworkPackets(questionCode, stageStr);
    
    // For each expected row (from Detail.xlsx)
    foreach (var row in rng.RowsUsed().Skip(1))
    {
        // Populate actual data
        ws.Cell(row.RowNumber(), actualFlagsCol).Value = actualPacket.Flags;
        ws.Cell(row.RowNumber(), actualStateCol).Value = actualPacket.State;
        ws.Cell(row.RowNumber(), actualSrcRoleCol).Value = actualPacket.SourceRole;
        ws.Cell(row.RowNumber(), actualDstRoleCol).Value = actualPacket.DestinationRole;
        
        // Compare and set result
        bool matched = CompareExpectedVsActual(...);
        ws.Cell(row.RowNumber(), resultCol).Value = matched ? "PASS" : "FAIL";
        
        // Apply color coding
        if (matched)
            ws.Cell(row.RowNumber(), resultCol).Style.Fill.BackgroundColor = XLColor.LightGreen;
        else
            ws.Cell(row.RowNumber(), resultCol).Style.Fill.BackgroundColor = XLColor.LightPink;
    }
}
```

### How It Works

1. **Test kit defines expected flows**: Detail.xlsx Network sheet has expected packets
2. **Grading copies expected values**: Rows are copied to GradeDetail.xlsx
3. **Network monitor captures actual flows**: NetworkMonitorService captures packets on student's port
4. **Data is stored**: RunContext.AddCapturedNetworkPacket stores captured data
5. **Actual columns populated**: PopulateNetworkActualColumns fills in Actual* columns
6. **Comparison performed**: Expected vs Actual values compared
7. **Result displayed**: NetworkResult column shows PASS/FAIL with colors

## Testing Verification

### Quick Test
1. Run grading for a student with network monitoring
2. Open: `Results/{PaperNo}/student/{StudentCode}/TC1/GradeDetail.xlsx`
3. Go to **Network** sheet
4. Verify:
   - ✅ Columns 1-10: Expected values (from test kit)
   - ✅ Columns 11-15: Actual* values (captured)
   - ✅ Column 16: NetworkResult (PASS/FAIL)
   - ✅ Colors: Green for matches, pink for mismatches

### Sample Verification
Already verified in `SampleLogging/1/student/CuongNHE186494/TC1/GradeDetail.xlsx`:
```python
# Run this to see the network sheet format:
import openpyxl
wb = openpyxl.load_workbook('SampleLogging/1/student/CuongNHE186494/TC1/GradeDetail.xlsx')
ws = wb['Network']
headers = [cell.value for cell in ws[1]]
print(headers)
# Output: ['Stage', 'Time', 'Info', 'Source', 'Destination', 
#          'Flags', 'State', 'Data', 'SourceRole', 'DestinationRole',
#          'ActualFlags', 'ActualState', 'ActualSourceRole', 
#          'ActualDestRole', 'ActualData', 'NetworkResult']
```

## Conclusion

✅ **No code changes needed!** The network sheet logging already implements the exact format described in the new requirement.

The system has been doing this correctly since the NetworkMonitorService was implemented. The recent fixes to parallel grading (one monitor per student port) ensure this data is captured correctly for all students.

## Related Files
- `Lib/SolutionGrader.Core/Services/ExcelDetailLogService.cs` - Network sheet logging
- `Lib/SolutionGrader.Core/Services/NetworkMonitorService.cs` - Network capture
- `Lib/SolutionGrader.Core/Services/RunContext.cs` - Data storage
- `Lib/SolutionGrader.Core/Keywords/GradingKeywords.cs` - Column name constants
- `SampleLogging/1/student/CuongNHE186494/TC1/GradeDetail.xlsx` - Example output

## Next Steps for User
1. ✅ No changes needed for network sheet format
2. ✅ Test parallel grading with network monitoring (Issue #1 fix)
3. ✅ Verify network data is captured correctly
4. ✅ Check GradeDetail.xlsx Network sheet has Actual* columns populated
5. ✅ Merge to main when testing complete
