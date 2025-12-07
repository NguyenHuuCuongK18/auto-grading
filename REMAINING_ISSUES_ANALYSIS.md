# Remaining Issues Analysis

## Executive Summary

Analysis of `Hand_edited_log/` results reveals TWO critical bugs that remain unfixed:

1. **Packet Cross-Contamination**: dungtdhe186461's "Hello World!" server (exits immediately) shows 22 captured network flows including full TCP communications - packets are leaking from other students
2. **Non-Deterministic Results**: First run: students get 4-5 points, Second run: more pass with 5 points - indicates state persistence between runs

**Note**: The scoring logic bug (TC6 showing PASS despite FAILs) was fixed in commit c6395b2, but requires verification with actual packet capture (libpcap).

## Detailed Analysis of dungtdhe186461 Results

### Student Code
```csharp
// batchtest/1/dungtdhe186461/1/solution/Program.cs
Console.WriteLine("Hello, World!");
// Program exits immediately - no network listening, no TCP server
```

### Expected Behavior
- Server starts, prints "Hello, World!", exits immediately (< 1 second)
- Network capture: 0 packets (or at most SYN from client → RST from OS)
- All test cases: FAIL (server not responding to requests)
- Total points: 0/5

### Actual Behavior (from Hand_edited_log)
- Network capture: **22 packets** including:
  - Full TCP handshakes (SYN, SYN-ACK, ACK)
  - Data transfer (PSH, ACK with payload "Sabc")
  - Clean shutdowns (FIN, ACK sequences)
- Test cases: ALL PASS (TC1-TC6)
- Total points: **5/5**

### Packet Analysis

**TC6 Network Sheet** (22 captured flows):
- Rows 2-6: Complete TCP conversation (SYN → SYN-ACK → ACK → PSH-ACK → ACK)
- Rows 7, 12, 18, 23: **FAIL** - "(MISSING - not captured)"
- Rows 8-11: Clean connection shutdown (FIN-ACK sequence)
- Rows 13-23: Second TCP conversation (stage 6)

**Critical Finding**: The captured packets show ports 4005, 51766 communicating successfully. This is IMPOSSIBLE if dungtdhe186461's server exits immediately!

**Hypothesis**: Port 4005 was allocated to dungtdhe186461, but packets from another student's server (also on port 4005, or port mapping issue) leaked into dungtdhe186461's packet buffer.

## Root Cause Analysis

### Bug #1: Packet Cross-Contamination

**Previous Fixes Applied** (commits b9a7b90, 185f070, 074e5e5):
- Added `ClearStudentCaptures()` to clear both buffer AND RunContext
- Added ownership validation before storing packets
- Enhanced registration/unregistration logging
- Added CLI monitor cleanup

**Why These Didn't Fully Fix It**:
The fixes prevent packets from staying BETWEEN grading sessions, but may not prevent cross-contamination DURING parallel grading when multiple students' servers are running simultaneously.

**Suspected Issues**:
1. **Port Reuse**: If student A finishes and unregisters port 4005, then student B gets port 4005, packets from B might leak to A's RunContext if timing is wrong
2. **SharedNetworkMonitor Timing**: Packets captured before proper registration or after unregistration
3. **Process Exit Detection**: No mechanism to stop capturing packets after student's server process exits

### Bug #2: Non-Deterministic Results

**User Report**:
> "when i ran grading for the first time some student get 5 or 4 point but when i reset all and run grading again, a lot of them is passing with 5 point"

**Suspected Causes**:
1. **Docker Container State**: Containers not fully removed between runs
2. **Port Allocation**: Ports remaining in TIME_WAIT state affecting next run
3. **File Locking**: Excel files or temp files locked from previous run
4. **Packet Buffer Persistence**: RunContext or SharedNetworkMonitor state leaking
5. **Database State**: SQL Server databases not fully dropped

### Bug #3: Scoring Logic (FIXED but needs verification)

**Commit c6395b2** fixed `CompareNetwork()` to use `GetAllCapturedNetworkPackets()` instead of `GetCapturedNetworkPackets("", stage)`.

**The Fix**: 
```csharp
// BEFORE (WRONG)
var capturedPackets = _runContext.GetCapturedNetworkPackets("", exp.Stage.ToString());
// Key "-{stage}" might not match how packets were stored

// AFTER (CORRECT)
var allCapturedPackets = _runContext.GetAllCapturedNetworkPackets();
var capturedPackets = allCapturedPackets.Where(p => p.Stage == exp.Stage).ToList();
// Gets ALL packets then filters by stage - matches Excel writer
```

**Status**: Fix is theoretically correct, but `Hand_edited_log/` was generated in environment without libpcap (no packets captured), so the fix couldn't be verified to work.

## Testing Procedures

### Prerequisites
```bash
# Install libpcap (required for network capture)
sudo apt-get update
sudo apt-get install -y libpcap-dev
```

### Test 1: Verify Packet Isolation (Critical)

```bash
# Clean everything
rm -rf Hand_edited_log/ GradingResults/ batchtest/Results/
sudo docker stop $(sudo docker ps -aq) 2>/dev/null
sudo docker rm $(sudo docker ps -aq) 2>/dev/null

# Run grading with network capture
cd Application/SolutionGrader.Cli
sudo dotnet run -c Release --framework net8.0 -- dockergrade \
  --submit ../../batchtest \
  --testkit ../../Testkit_Q1_PRN222 \
  --parallel 5
```

**Expected Results**:
1. dungtdhe186461 should have **0 packets captured** (not 22)
2. Console should show: `[SharedNetworkMonitor] [{dungtdhe186461}|Port:XXXX] Stored 0 packets`
3. No cross-contamination warnings in console
4. Each student should only see packets from their own port

**Verification**:
```bash
cd ../../Hand_edited_log/1/student/dungtdhe186461/TC6
# Open GradeDetail.xlsx, Network sheet
# Should have 0 or very few rows (not 22)
```

### Test 2: Verify Scoring Logic (Critical)

**Expected Results**:
1. TC3 should show **FAIL** in OverallSummary (has 1 FAIL network flow)
2. TC4 should show **FAIL** in OverallSummary (has 2 FAIL network flows)
3. TC5 should show **FAIL** in OverallSummary (has 1 FAIL network flow)
4. TC6 should show **FAIL** in OverallSummary (has 4 FAIL network flows)
5. Total points: **2.5/5** (only TC1 0.5 + TC2 1.0 pass)

**Verification**:
```bash
cd Hand_edited_log/1/student/dungtdhe186461
# Open OverallSummary.xlsx
# Check each TC result and total points
```

### Test 3: Verify Deterministic Results (Critical)

```bash
# Run grading AGAIN without cleaning
cd Application/SolutionGrader.Cli
sudo dotnet run -c Release --framework net8.0 -- dockergrade \
  --submit ../../batchtest \
  --testkit ../../Testkit_Q1_PRN222 \
  --parallel 5
```

**Expected Results**:
1. All students get **IDENTICAL scores** as first run
2. dungtdhe186461 still gets same points (should be 2.5/5)
3. No students flip from FAIL to PASS or vice versa

**Verification**:
```bash
# Compare two result files
diff -r Hand_edited_log.run1/ Hand_edited_log.run2/
# Should show NO differences in scores (only timestamps may differ)
```

## Success Criteria

- ✅ dungtdhe186461 captures 0 packets (not 22)
- ✅ Test cases with FAIL network flows correctly FAIL (not PASS)
- ✅ dungtdhe186461 gets ≤2.5 points (not 5)
- ✅ Second grading run produces identical results
- ✅ No cross-contamination warnings in console
- ✅ All students' scores are deterministic

## If Tests Fail

If packet cross-contamination persists:
1. Check console for SharedNetworkMonitor registration messages
2. Verify each student got unique port assignment
3. Check if packets show correct attribution in console logs
4. Investigate process lifecycle - when does server exit vs when does capture stop

If scoring is still wrong:
1. Add debug logging to CompareNetwork() to see what packets it receives
2. Verify GetAllCapturedNetworkPackets() returns expected packets
3. Check if networkComparisons list is populated correctly
4. Verify failCount calculation is correct

If results are non-deterministic:
1. Check if Docker containers fully stopped between runs: `docker ps -a`
2. Check if ports released: `sudo netstat -tulpn | grep 400[0-5]`
3. Check if files locked: `lsof | grep "GradingMessages\|OverallSummary"`
4. Add more aggressive cleanup between runs

## Current Status

**Environment Limitation**: CI/CD environment doesn't have libpcap installed, so network capture doesn't work. Cannot verify fixes without user testing in proper environment with libpcap.

**Recommendation**: User must run tests above in environment with libpcap to verify all fixes work correctly.
