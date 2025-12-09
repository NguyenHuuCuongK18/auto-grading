# Network Grading Improvements: HTTP Support & PCAP Organization

## Overview

This document describes the comprehensive improvements made to the network grading system to support:
1. **HTTP Protocol Detection and Grading** - Alongside existing TCP support
2. **Per-Test-Case PCAP Organization** - Improved file structure
3. **Enhanced Data Column Handling** - Proper copying of network data to grading sheets

## 1. HTTP Protocol Support

### What Changed

The grading system now intelligently detects and parses both TCP and HTTP traffic based on the testkit configuration and packet content.

### Configuration

In your test kit's `Header.xlsx` file, add a `Config` sheet with:

| Key | Value |
|-----|-------|
| Protocol | TCP or HTTP |

**Example for TCP test kit (Q11)**:
```
Config Sheet:
Row 2: Protocol | TCP
```

**Example for HTTP test kit**:
```
Config Sheet:
Row 2: Protocol | HTTP
```

### How It Works

#### Protocol Detection
1. **Configuration-based**: Reads `Protocol` from `Header.xlsx` Config sheet
2. **Content-based**: Automatically detects HTTP signatures in packet payload:
   - HTTP Request Methods: GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS, CONNECT, TRACE
   - HTTP Response Signature: "HTTP/"

#### Packet Parsing

**For TCP Protocol** (existing):
- Extracts: Source, Destination, Flags, State, Data, SourceRole, DestinationRole
- Data field contains TCP payload (application-layer data)

**For HTTP Protocol** (new):
- Extracts all TCP fields PLUS:
  - **URI**: Request path (e.g., `/api/students`, `/students/S001`)
  - **Method**: HTTP verb (GET, POST, PUT, DELETE)
  - **Status**: HTTP status code (e.g., "200 OK", "404 Not Found")
  - **HttpVersion**: Protocol version (e.g., "HTTP/1.1")
  - **HttpBody**: Request/response body content (JSON, text, etc.)

### Detail.xlsx Format

#### TCP Network Sheet Columns (Columns 1-10):
```
Stage | Time | Info | Source | Destination | Flags | State | Data | SourceRole | DestinationRole
```

#### HTTP Network Sheet Columns (Columns 1-15):
```
Stage | Time | Info | Source | Destination | Flags | State | URI | Method | Status | HttpVersion | HttpBody | SourceRole | DestinationRole
```

### GradeDetail.xlsx Output

#### TCP Network Sheet (18 columns):
```
Expected Columns (1-10):
  Stage, Time, Info, Source, Destination, Flags, State, Data, SourceRole, DestinationRole

Actual Columns (11-18):
  ActualFlags, ActualState, ActualSourceRole, ActualDestRole, ActualData,
  ActualSourcePort, ActualDestPort, NetworkResult
```

#### HTTP Network Sheet (26 columns):
```
Expected Columns (1-14):
  Stage, Time, Info, Source, Destination, Flags, State, URI, Method, Status, 
  HttpVersion, HttpBody, SourceRole, DestinationRole

Actual Columns (15-26):
  ActualFlags, ActualState, ActualURI, ActualMethod, ActualStatus, 
  ActualHttpVersion, ActualHttpBody, ActualSourceRole, ActualDestRole,
  ActualSourcePort, ActualDestPort, NetworkResult
```

### Grading Comparison

#### TCP Grading Criteria:
- ✅ Flags match (order-independent: "SYN, ACK" = "ACK, SYN")
- ✅ State matches
- ✅ SourceRole matches
- ✅ DestinationRole matches
- ✅ Data payload matches (if specified, case-insensitive)

#### HTTP Grading Criteria:
- ✅ All TCP criteria (Flags, State, Roles)
- ✅ URI matches (case-insensitive)
- ✅ Method matches (case-insensitive)
- ✅ Status code matches (partial match, e.g., "200" matches "200 OK")
- ✅ HttpVersion matches (case-insensitive)
- ✅ HttpBody matches (if specified, case-insensitive, whitespace-trimmed)

## 2. Per-Test-Case PCAP Organization

### Problem Solved

Previously, all PCAP snapshot files were stored in the student root directory, making it difficult to:
- Debug individual test cases
- Identify which network flows belong to which test case
- Clean up test artifacts

### New File Structure

```
/student/AnhDThe187386/
  network_capture.pcap          # Live capture file (cumulative, all TCs)
  OverallSummary.xlsx            # Overall grading summary
  
  TC1/
    snapshot_TC1_stage1.pcap     # Stage 1 network capture
    snapshot_TC1_stage2.pcap     # Stage 2 network capture
    GradeDetail.xlsx             # TC1 grading results
  
  TC2/
    snapshot_TC2_stage1.pcap
    snapshot_TC2_stage2.pcap
    snapshot_TC2_stage3.pcap
    GradeDetail.xlsx
  
  TC3/
    snapshot_TC3_stage1.pcap
    snapshot_TC3_stage2.pcap
    snapshot_TC3_stage3.pcap
    GradeDetail.xlsx
  
  ProcessLogs/
    client-TC1-stage-1.log
    client-TC1-stage-3.log
    server-TC1-stage-2.log
    ...
```

### How It Works

1. **During Execution**: Snapshots are created in student root with TC prefix
   - Format: `snapshot_TC3_stage2.pcap`

2. **After Test Case Completes**: Snapshots are moved to TC folder
   - Organized per test case for easier debugging
   - Keeps student root directory clean

3. **Network Monitor**: Maintains cumulative `network_capture.pcap`
   - Used for ongoing monitoring
   - Snapshots are created from this file per stage

### Benefits

✅ **Cleaner Organization**: Each TC has its own folder with relevant artifacts
✅ **Easier Debugging**: Quickly locate network captures for specific test cases
✅ **Per-TC Analysis**: Analyze network behavior for individual test cases
✅ **Reduced Clutter**: Student root only contains cumulative capture file

## 3. Data Column Handling

### Issue Fixed

The Data column (Column H/8) in `Detail.xlsx` was not being copied to `GradeDetail.xlsx` Network sheet, making it difficult to verify payload data in grading results.

### Solution

1. **Reading**: `ReadExpectedNetwork` now properly reads Data from column 8
2. **Comparison**: `CompareNetwork` compares Data field when expected
3. **Output**: `WriteTestCaseResultAsync` writes Data to both expected and actual columns
4. **Validation**: Data field comparison is case-insensitive with whitespace trimming

## 4. Smart Network Flow Isolation

### Strategy

The system uses **cumulative parsing** with **per-stage snapshots** to handle network flow isolation:

1. **Live Capture**: Single `network_capture.pcap` file grows throughout grading
2. **Per-Stage Snapshots**: Created after each stage for validation
3. **Cumulative Parsing**: `_lastParsedPacketCount` tracks packets already processed
4. **Smart Filtering**: Only new packets (after last count) are added to RunContext

### Example Flow

```
Stage 1 (Start Client):
  - Snapshot: snapshot_TC3_stage1.pcap (0 packets)
  - Parsed: 0 packets
  - _lastParsedPacketCount = 0

Stage 2 (Start Server):
  - Snapshot: snapshot_TC3_stage2.pcap (3 packets - SYN, SYN-ACK, ACK)
  - Parsed: 3 new packets
  - _lastParsedPacketCount = 3

Stage 3 (Send S001):
  - Snapshot: snapshot_TC3_stage3.pcap (10 packets total)
  - Parsed: 7 new packets (packets 4-10)
  - _lastParsedPacketCount = 10
```

### All-or-Nothing Grading

- **Principle**: All expected network flows must match for test case to pass
- **Missing Flow**: If any expected flow is not captured → FAIL
- **Extra Flows**: Captured flows not in Detail.xlsx are marked as INFO (not penalized)
- **Partial Match**: If flags/roles/data mismatch → PARTIAL (treated as FAIL)

## 5. Testing Your Test Kit

### TCP Test Kit Example (Q11)

**Header.xlsx Config Sheet**:
```
Protocol | TCP
```

**Detail.xlsx Network Sheet**:
```
Stage | Time | Info | Source | Destination | Flags | State | Data | SourceRole | DestRole
1     |      | TCP  |        |             | SYN   | ...   |      | Client     | Server
2     |      | TCP  |        |             | SYN-ACK| ...  |      | Server     | Client
3     |      | TCP  |        |             | PSH-ACK| ...  | S001 | Client     | Server
```

### HTTP Test Kit Example

**Header.xlsx Config Sheet**:
```
Protocol | HTTP
```

**Detail.xlsx Network Sheet**:
```
Stage | ... | Flags | State | URI           | Method | Status | HttpVersion | HttpBody | SourceRole | DestRole
1     | ... | PSH-ACK| EST  | /api/students | GET    |        | HTTP/1.1    |          | Client     | Server
2     | ... | PSH-ACK| EST  |               |        | 200 OK | HTTP/1.1    | {...}    | Server     | Client
3     | ... | PSH-ACK| EST  | /students/S001| POST   |        | HTTP/1.1    | {...}    | Client     | Server
```

## 6. Implementation Details

### SharpPcap Parser Enhancements

**File**: `Lib/SolutionGrader.Core/Services/SharpPcapParsingService.cs`

Key methods:
- `ParsePcapFile()`: Main parsing entry point, accepts protocol parameter
- `IsHttpPacket()`: Detects HTTP signatures in payload
- `ParseHttpFields()`: Extracts HTTP-specific fields from payload
- `CleanPayloadData()`: Removes non-printable characters while preserving structure

### Model Extensions

**File**: `Lib/Domain/Models/CapturedNetworkPacket.cs`

New HTTP properties:
```csharp
public string? URI { get; set; }
public string? Method { get; set; }
public string? Status { get; set; }
public string? HttpVersion { get; set; }
public string? HttpBody { get; set; }
public bool IsHttpRequest { get; set; }
```

### Grading Service Updates

**File**: `Lib/SolutionGrader.Core/Services/DockerGradingService.cs`

Key changes:
- `_currentTestKitProtocol`: Tracks protocol type
- `ReadExpectedNetwork()`: Reads HTTP fields from columns 11-15
- `CompareNetwork()`: Compares HTTP-specific fields
- `SetNetworkSheetHeaders()`: Dynamic headers based on protocol
- `MoveSnapshotsToTCFolder()`: Organizes PCAP files per TC

## 7. Troubleshooting

### HTTP Not Being Detected

**Check**:
1. `Header.xlsx` has Config sheet with Protocol=HTTP
2. Packet payload actually contains HTTP headers
3. Check GradingLogs for protocol detection messages

### Data Column Still Empty

**Check**:
1. `Detail.xlsx` Network sheet column 8 has data
2. TCP protocol is being used (HTTP uses HttpBody instead)
3. Check if Data field is marked as "None" (ignored)

### Snapshots Not in TC Folders

**Check**:
1. Test case completed successfully
2. Check for errors in GradingLogs about file moving
3. Verify permissions on TC result directories

## 8. Backward Compatibility

✅ **Fully backward compatible** with existing test kits:
- TCP-only test kits work without any changes
- Missing Protocol field defaults to "TCP"
- Existing Detail.xlsx format still supported
- HTTP fields in Detail.xlsx are optional

## 9. Future Enhancements

Potential improvements:
- [ ] HTTPS support (encrypted traffic)
- [ ] WebSocket protocol support
- [ ] Custom protocol definitions
- [ ] Regex-based payload matching
- [ ] Per-field comparison modes (exact, contains, regex)

## 10. Support

For issues or questions:
1. Check GradingLogs for detailed error messages
2. Verify test kit format matches this documentation
3. Test with provided Q11/Q12 test kits
4. Review PCAP files in TC folders for debugging
