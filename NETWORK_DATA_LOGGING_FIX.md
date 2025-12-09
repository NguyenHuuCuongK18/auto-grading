# Network Data Logging Fix Documentation

## Problem Summary

The auto-grading system was experiencing several issues with network packet capture and Excel logging:

1. **Actual payload data not logged**: Excel showed "4 bytes" instead of actual data like "S123"
2. **Expected Data column lost**: Template's expected Data values (e.g., "S123") disappeared in graded output
3. **RST+ACK packets not captured**: Server crashes/rejections showed as "(MISSING)" instead of RST-ACK flags
4. **Byte count instead of text**: PCAP parser only extracted packet length, not readable payload

## Root Causes

### Issue 1: tcpdump Without ASCII Flag
**Location**: `DockerGradingService.cs:3266`

**Before**:
```csharp
var dockerCmd = $"docker run --rm ... tcpdump ... -r /pcap/{snapshotFile} -nn -tttt tcp";
```

**Problem**: Without the `-A` flag, tcpdump only outputs packet headers and metadata. The payload data is not included in human-readable form.

**Fix**:
```csharp
var dockerCmd = $"docker run --rm ... tcpdump ... -r /pcap/{snapshotFile} -nn -tttt -A tcp";
```

The `-A` flag makes tcpdump print packet payload in ASCII format:
```
2024-12-09 09:37:31.571729 IP 127.0.0.1.33238 > 127.0.0.1.4000: Flags [P.], seq 1:5, ack 1, win 512, length 4
	0x0000:  4500 0038 ...   E..8...
	0x0010:  ... S001       (actual readable data)
```

### Issue 2: Parser Only Extracted Byte Count
**Location**: `DockerGradingService.cs:3339-3528`

**Before**:
```csharp
var lengthMatch = Regex.Match(line, @"length (\d+)");
string data = lengthMatch.Success && int.Parse(lengthMatch.Groups[1].Value) > 0 
    ? $"{lengthMatch.Groups[1].Value} bytes"  // ❌ Only byte count
    : "";
```

**Problem**: Parser regex extracted "length 4" from packet header but didn't parse the actual payload data.

**Fix**: Complete rewrite to handle multi-line tcpdump output:
```csharp
// State tracking for multi-line payloads
private CapturedNetworkPacket? _currentParsingPacket = null;
private StringBuilder _currentPayloadBuffer = new StringBuilder();

private CapturedNetworkPacket? ParseTcpdumpLine(string line, ...)
{
    // Check if this is a payload line (hex dump)
    if (line.TrimStart().StartsWith("0x") || ...)
    {
        // Extract ASCII from hex dump
        var asciiPart = parts[parts.Length - 1].Trim();
        var readable = new string(asciiPart.Where(c => c >= 32 && c < 127).ToArray());
        _currentPayloadBuffer.Append(readable);
        return null; // Still collecting
    }
    
    // Finalize previous packet with collected payload
    if (_currentParsingPacket != null)
    {
        var payload = _currentPayloadBuffer.ToString().Trim();
        if (!string.IsNullOrEmpty(payload))
            _currentParsingPacket.Data = payload; // ✅ Actual text
    }
    
    // Parse new packet header...
}
```

### Issue 3: RST+ACK Not Recognized
**Location**: `DockerGradingService.cs:3470-3474`

**Before**:
```csharp
else if (line.Contains("Flags [R]"))
{
    flags = "RST";
    state = "RESET";
}
// ❌ Missing [R.] check
```

**Problem**: tcpdump uses `[R]` for RST only and `[R.]` for RST+ACK. The parser only checked for `[R]`, missing RST+ACK packets.

**Fix**:
```csharp
else if (line.Contains("Flags [R.]"))
{
    flags = "RST-ACK";  // ✅ Server rejecting connection
    state = "RESET";
}
else if (line.Contains("Flags [R]"))
{
    flags = "RST";
    state = "RESET";
}
```

### Issue 4: Expected Data Column Lost
**Location**: `ExcelDetailLogService.cs:1203-1229`

**Problem**: When ClosedXML loads the template Detail.xlsx and adds new columns, the expected Data column values were being lost. The template has:
```
Row 5: Data = "S123"
Row 7: Data = "{"StudentId":"S123",...}"
```

But the graded output showed:
```
Row 5: Data = ""
Row 7: Data = ""
```

**Root Cause**: ClosedXML may treat empty/populated cells inconsistently when loading, modifying, and saving workbooks.

**Fix**: Added explicit preservation method:
```csharp
private void PreserveNetworkExpectedData(IXLWorksheet ws, Dictionary<string, int> hdr)
{
    if (!hdr.TryGetValue(NetworkKeywords.Col_Data, out var dataCol)) return;
    
    var rng = ws.RangeUsed();
    if (rng == null) return;
    
    // Read and re-write Data values to force ClosedXML to preserve them
    foreach (var row in rng.RowsUsed().Skip(1))
    {
        var dataCell = ws.Cell(row.RowNumber(), dataCol);
        var dataValue = dataCell.GetString();
        
        // Re-assign to ensure preservation
        if (!string.IsNullOrEmpty(dataValue))
        {
            dataCell.Value = dataValue; // ✅ Explicitly preserve
        }
    }
}
```

This method is called in `AddStdoutColumnsToSheets`:
```csharp
if (_wb.Worksheets.TryGetWorksheet(SuiteKeywords.Sheet_Network, out networkWs))
{
    EnsureColumns(networkWs, new[] { /* Actual* columns */ });
    var hdr = GetHeaderIndex(networkWs);
    
    PreserveNetworkExpectedData(networkWs, hdr); // ✅ Preserve before populating
    PopulateNetworkActualColumns(networkWs, hdr);
}
```

## Architecture: Expected vs Actual Columns

All sheets follow a consistent pattern of showing expected values from the template alongside actual captured values:

### Network Sheet
| Expected Columns | Actual Columns |
|-----------------|----------------|
| `Data` | `ActualData` |
| `Flags` | `ActualFlags` |
| `State` | `ActualState` |
| `SourceRole` | `ActualSourceRole` |
| `DestinationRole` | `ActualDestRole` |
| (not applicable) | `ActualSourcePort` |
| (not applicable) | `ActualDestPort` |

**Purpose**: Compare expected network traffic (from test kit) with actual captured packets

### Client Sheet
| Expected Columns | Actual Columns |
|-----------------|----------------|
| `Console` | `ActualOutput` |
| (varies) | `ClientStdout` |

**Purpose**: Compare expected console output with actual client stdout

### Server Sheet  
| Expected Columns | Actual Columns |
|-----------------|----------------|
| `Console` | `ActualOutput` |
| (varies) | `ServerStdout` |

**Purpose**: Compare expected console output with actual server stdout

## Testing Verification

### Test Case 1: Working Server (AnhDThe187386)
**Expected Behavior**:
- Row 5 (PSH-ACK): Expected Data = "S001", Actual Data = "S001" ✅
- Row 7 (PSH-ACK): Expected Data = JSON, Actual Data = JSON ✅

**Before Fix**:
```
Row 5: Data = "", ActualData = "4 bytes"
```

**After Fix**:
```
Row 5: Data = "S001", ActualData = "S001"
```

### Test Case 2: Crashed Server (dungtdhe186461)
**Expected Behavior**:
- Row 2 (SYN): Flags = "SYN"
- Row 3 (RST-ACK): Flags = "RST-ACK" (server immediately rejects)

**Before Fix**:
```
Row 2: ActualFlags = "(MISSING - not captured)"
Row 3: ActualFlags = "(MISSING - not captured)"
```

**After Fix**:
```
Row 2: ActualFlags = "SYN"
Row 3: ActualFlags = "RST-ACK"
```

## Files Modified

1. **DockerGradingService.cs**
   - Added `-A` flag to tcpdump command
   - Rewrote `ParseTcpdumpLine` for multi-line payload parsing
   - Added RST-ACK flag handling
   - Added state tracking fields for payload accumulation

2. **ExcelDetailLogService.cs**
   - Added `PreserveNetworkExpectedData` method
   - Added ActualSourcePort and ActualDestPort columns
   - Enhanced Network sheet handling

3. **GradingKeywords.cs**
   - Added `Col_ActualSourcePort` constant
   - Added `Col_ActualDestPort` constant

## Performance Considerations

### Multi-line Parsing Overhead
The new parser processes payload lines in addition to header lines, roughly doubling the lines processed per packet. However:
- Payload lines are short (typically 1-3 lines per packet)
- Parsing is still O(n) where n = total output lines
- tcpdump output is already buffered in memory
- Impact is negligible compared to Docker container overhead

### Memory Usage
The `_currentPayloadBuffer` StringBuilder accumulates payload across lines but is cleared after each packet, keeping memory usage minimal.

## Edge Cases Handled

1. **Empty payload**: Packets with `length 0` don't enter payload accumulation
2. **Non-printable characters**: ASCII extraction filters to printable range (32-126)
3. **Partial hex dumps**: Parser handles incomplete payloads gracefully
4. **Multiple simultaneous parsing**: Each DockerGradingService instance has its own state
5. **Last packet in file**: Finalization code handles remaining buffered packet

## Known Limitations

1. **Binary payload**: Only ASCII-printable characters are extracted. Binary data appears as gaps in the output.
2. **Large payloads**: Payloads > `ACTUAL_DATA_COLUMN_MAX_CHARS` are truncated with "..."
3. **tcpdump availability**: Requires fptuxaes/network-monitor Docker image with tcpdump installed

## Future Improvements

1. **Base64 encoding**: For binary payloads, consider base64 encoding the raw bytes
2. **Hex view**: Add separate column showing hex representation of payload
3. **Packet reassembly**: For fragmented TCP streams, reassemble complete messages
4. **Protocol parsing**: Add specialized parsers for HTTP, WebSocket, etc.
