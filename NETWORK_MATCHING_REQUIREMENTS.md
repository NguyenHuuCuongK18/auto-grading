# Network Flow Matching Requirements - Clarification Needed

## Current Situation

**Student**: AnhDThe187386  
**Baseline Score**: 1.50/5.00 (with PCAP truncation fix only)

### Test Case Results
| TC | Score | Status | Issue |
|----|-------|--------|-------|
| TC1 | 0.50/0.50 | PASS ✅ | Working correctly |
| TC2 | 1.00/1.00 | PASS ✅ | Working correctly |
| TC3 | 0.00/1.00 | FAIL ❌ | **Should PASS** (student has correct flows per manual check) |
| TC4 | 0.00/0.50 | FAIL ❌ | **Correctly FAILS** (student closes conn first) |
| TC5 | 0.00/1.00 | FAIL ❌ | **Correctly FAILS** (student closes conn first) |
| TC6 | 0.00/1.00 | FAIL ❌ | **Correctly FAILS** (student closes conn first) |

## TC3 Analysis

### Expected Flows (7 total)
```
1. SYN from Client to Server
2. SYN-ACK from Server to Client  
3. ACK from Client to Server
4. PSH-ACK from Client to Server with data='S123'
5. ACK from Server to Client
6. PSH-ACK from Server to Client with data='{"StudentId":"S123"...}'
7. ACK from Client to Server
```

### Student's Captured Flows (Manual Verification)
```
Time: 01:48:28
1. Client(57275)→Server(4000): SYN
2. Server(4000)→Client(57275): SYN-ACK
3. Client(57275)→Server(4000): ACK
4. Client(57275)→Server(4000): PSH-ACK "S123"
5. Server(4000)→Client(57275): ACK
6. Server(4000)→Client(57275): PSH-ACK "{"StudentId":"S123","Name":null...}"
7. Client(57275)→Server(4000): ACK
8. Server(4000)→Client(57275): FIN-ACK
9. Client(57275)→Server(4000): ACK
10. Client(57275)→Server(4000): FIN-ACK
11. Server(4000)→Client(57275): ACK
```

**Analysis**: Student has flows 1-7 matching expected! ✅

### Current Comparison Failure
```
Comparison Results:
✓ PASS - Flow 1: SYN Client→Server
✓ PASS - Flow 2: SYN-ACK Server→Client
✓ PASS - Flow 3: ACK Client→Server
✓ PASS - Flow 4: PSH-ACK Client→Server data='S123'
✗ FAIL - Flow 5: expected Server→Client but got Client→Server
✗ FAIL - Flow 6: expected Server→Client with JSON but got Client→Server with 'S123'
✓ PASS - Flow 7: ACK Client→Server

Result: 5/7 PASS → FAIL
```

### Why Comparison Fails

**Current Logic** (flags-only matching):
```csharp
var matchingPacket = capturedPackets.FirstOrDefault(p =>
    FlagsMatch(exp.Flags, p.Flags));
```

**Problem**: When looking for flow 5 (ACK Server→Client):
1. Searches for ANY packet with ACK flags
2. Finds Flow 3 (ACK Client→Server) ← WRONG DIRECTION!
3. Compares roles → FAIL

**Problem**: When looking for flow 6 (PSH-ACK Server→Client with JSON):
1. Searches for ANY packet with PSH-ACK flags
2. Finds Flow 4 (PSH-ACK Client→Server with "S123") ← WRONG DIRECTION!
3. Compares data → FAIL

## TC4-6 Analysis

**Issue**: Student's server initiates FIN (closes connection first)  
**Expected**: Server should only close AFTER client closes  
**Result**: Should FAIL ❌

**Question**: How does the comparison detect this if we only check expected flows?
- If we only validate expected flows exist, we won't catch "extra wrong flows"
- Need to validate SEQUENCE and ORDER

## Possible Matching Strategies

### Strategy 1: Flags-Only (Current - BROKEN)
```csharp
var match = capturedPackets.FirstOrDefault(p => 
    FlagsMatch(exp.Flags, p.Flags));
```
- ❌ Matches wrong direction (TC3 fails when it should pass)
- ❌ Ignores order completely

### Strategy 2: Flags + Roles (Tried Earlier)
```csharp
var match = capturedPackets.FirstOrDefault(p =>
    FlagsMatch(exp.Flags, p.Flags) &&
    RolesMatch(exp, p));
```
- ✅ Fixes TC3 (matches correct direction)
- ❌ Still ignores order (might pass TC4-6 when they should fail?)

### Strategy 3: Sequential with Tracking (Tried Earlier)
```csharp
// Find FIRST unmatched packet that matches
for (each packet) {
    if (not matched && flags+roles match) {
        use this;
        mark matched;
        break;
    }
}
```
- ✅ Enforces order
- ✅ 1-to-1 mapping
- ❓ Does this catch TC4-6 failures?

### Strategy 4: Positional (Strict Index Matching)
```csharp
// Expected flow at index i MUST match captured flow at index i
if (i < capturedPackets.Count) {
    match = capturedPackets[i];
    if (!FlagsMatch(exp.Flags, match.Flags) ||
        !RolesMatch(exp, match)) {
        FAIL
    }
}
```
- ✅ Strictest order enforcement
- ❌ Might be TOO strict (extra handshake packets ok?)

## Questions Needing Clarification

### Q1: Should we match by exact position or sequence?
- **Option A**: Flow[0] in expected MUST be Flow[0] in captured (positional)
- **Option B**: Flow[0] in expected MUST be the FIRST matching unmatched flow (sequential)
- **Option C**: Flow[0] in expected can be ANY matching flow (current - broken)

### Q2: Should we fail on unexpected flows?
- **Example**: TC3 has extra FIN-ACK flows (8-11) that aren't in expected
- **Current**: Ignores extra flows (only validates expected exist)
- **Question**: Should extra flows at wrong time cause failure?

### Q3: For TC4-6, why do they fail?
- **User says**: "Student server closes connection first"
- **Question**: Does Detail.xlsx explicitly expect "Client closes first"?
- **Or**: Does it just not expect "Server closes first" and we catch the wrong sequence?

### Q4: How to detect "Server closes first" error?
- **Option A**: Expected flows include connection closing order explicitly
- **Option B**: System detects Server FIN before Client FIN as violation
- **Option C**: All-or-nothing comparison catches mismatched flows

## Hypothesis

Based on the evidence, I believe the correct solution is:

**Strategy 2 (Flags + Roles) OR Strategy 3 (Sequential)**

Because:
1. TC3 needs role matching to avoid matching wrong direction
2. TC4-6 likely have explicit expected flows that show correct close order
3. When student closes wrong, the expected "Client FIN first" flow won't match "Server FIN first"
4. All-or-nothing grading causes FAIL

**But I need confirmation** on:
- Are TC4-6 Detail.xlsx flows explicitly checking close order?
- Or is there additional validation logic I'm missing?

## Recommendation

Please clarify:
1. Should matching be sequential (first unmatched) or positional (exact index)?
2. For TC4-6, are the expected flows explicitly checking "Client closes first"?
3. Should we implement additional validation for "unexpected flows at wrong time"?

Once clarified, I can implement the correct matching strategy.
