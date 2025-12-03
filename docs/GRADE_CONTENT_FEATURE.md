# Grade_Content Feature

## Overview

The `Grade_Content` feature provides flexible per-test-case control over which components (client/server) are graded from student submissions versus golden implementations. This solves the problem where different exam papers may require students to implement only the client, only the server, or both.

## Problem Statement

Previously, the grading system made a paper-wide decision about whether students should provide a client and/or server. This was inflexible because:
- Paper 1 might require students to code only the CLIENT
- Paper 2 might require students to code only the SERVER  
- Paper 3 might require students to code BOTH client and server

The system couldn't reliably determine what students should code for different papers.

## Solution: Grade_Content per Test Case

Each test case can now specify what should be graded using the `Grade_Content` field in its `Header.xlsx` file.

### Location

The `Grade_Content` field is defined in each test case's `Header.xlsx`, in the `Testcase_Property` sheet:

```
Test Case ID        | TC01
Execution Date      | SystemDate
Timeout(Seconds)    | 120
Domain              | http://localhost:5235
Grade_Content       | Client/Server
```

### Valid Values

The `Grade_Content` field accepts three values:

1. **`Client`** - Grade student's CLIENT implementation with GOLDEN SERVER
   - Uses: Student's client DLL + Golden server from `Meta/Given/Server`
   - Use case: Student only implements the client side

2. **`Server`** - Grade student's SERVER implementation with GOLDEN CLIENT
   - Uses: Student's server DLL + Golden client from `Meta/Given/Client`
   - Use case: Student only implements the server side

3. **`Client/Server`** - Grade BOTH student implementations (default)
   - Uses: Student's client DLL + Student's server DLL
   - No golden components used
   - Use case: Student implements both client and server

### Default Behavior

If `Grade_Content` is not specified in a test case's `Header.xlsx`, it defaults to `"Client/Server"`, maintaining backward compatibility with existing test kits.

## Implementation Details

### Student DLL Discovery

The system still discovers ALL available DLLs from student submissions:
- Server DLL: Searched when `config.HasServer = true`
- Client DLL: Searched when `config.HasClient = true`

This discovery happens ONCE per student, before any test cases run.

### Per-Test-Case DLL Selection

For EACH test case, the grading system:
1. Reads the `Grade_Content` value from that test case's `Header.xlsx`
2. Selects the appropriate DLLs:
   - `"Client"`: Use student client + golden server
   - `"Server"`: Use student server + golden client
   - `"Client/Server"`: Use student client + student server

This selection happens EVERY time a test case runs, allowing different test cases to grade different combinations.

### Golden Component Location

Golden (reference) implementations must be placed in specific folders within the test kit:

```
TestKit/
  YourTestKit/
    Meta/
      Given/
        Server/          # Golden server DLL goes here
          Project11.dll
        Client/          # Golden client DLL goes here
          Project12.dll
    TC1/
      Header.xlsx        # Contains Grade_Content field
      Detail.xlsx
    TC2/
      Header.xlsx
      Detail.xlsx
```

## Example Scenarios

### Scenario 1: Client-Only Exam

Paper requires students to implement only the client. All test cases use golden server.

**TC1/Header.xlsx:**
```
Grade_Content | Client
```

**Result:** Student's client is graded against the golden server from `Meta/Given/Server`.

### Scenario 2: Server-Only Exam

Paper requires students to implement only the server. All test cases use golden client.

**TC1/Header.xlsx:**
```
Grade_Content | Server
```

**Result:** Student's server is graded against the golden client from `Meta/Given/Client`.

### Scenario 3: Full Implementation Exam

Paper requires students to implement both client and server.

**TC1/Header.xlsx:**
```
Grade_Content | Client/Server
```

**Result:** Student's client communicates with student's server. No golden components used.

### Scenario 4: Mixed Test Cases

Paper requires full implementation, but some test cases want to isolate client or server testing.

**TC1/Header.xlsx:**
```
Grade_Content | Client        # Test client only with golden server
```

**TC2/Header.xlsx:**
```
Grade_Content | Server        # Test server only with golden client
```

**TC3/Header.xlsx:**
```
Grade_Content | Client/Server # Test full integration
```

**Result:** TC1 tests the client in isolation, TC2 tests the server in isolation, and TC3 tests the full integration.

## Logging and Diagnostics

The grading system provides detailed logging for `Grade_Content` behavior:

```
[TestKit] TC1: Grade_Content = 'Client' (from Header.xlsx)
[TestCase] TC1: Grade_Content = 'Client'
[TestCase] Using student CLIENT + golden SERVER
  Client: Project12.dll
  Server: Project11.dll (golden)
```

This makes it easy to verify which DLLs are being used for each test case.

## Migration Guide

### For Existing Test Kits

Existing test kits work without modification because `Grade_Content` defaults to `"Client/Server"`. To add flexibility:

1. Add `Grade_Content` field to each test case's `Header.xlsx` in the `Testcase_Property` sheet
2. Set the value based on what that test case should grade:
   - Use `"Client"` if you want to test the client with a golden server
   - Use `"Server"` if you want to test the server with a golden client
   - Use `"Client/Server"` if you want to test both student implementations

### Creating Golden Components

To use `"Client"` or `"Server"` modes, you must provide golden implementations:

1. Create `Meta/Given/Server/` folder in your test kit
2. Place the golden server DLL (e.g., `Project11.dll`) and all its dependencies in that folder
3. Create `Meta/Given/Client/` folder in your test kit
4. Place the golden client DLL (e.g., `Project12.dll`) and all its dependencies in that folder

## Code Changes Summary

The feature was implemented by:

1. Adding `GradeContent` property to `TestCaseInfo` class
2. Reading `Grade_Content` from `Header.xlsx` in `ReadTestCaseConfig()` method
3. Implementing DLL selection logic in `ExecuteTestCaseAsync()` based on `Grade_Content`
4. Cleaning up duplicate code:
   - Removed `CapturedPacketInfo` class (duplicate of `CapturedNetworkPacket`)
   - Removed `NetworkCaptureRecord` class (duplicate of `CapturedNetworkPacket`)

## Benefits

1. **Flexibility**: Different test cases can grade different components
2. **Isolation**: Can test client and server independently
3. **Reusability**: Same test kit can be used for different exam variations
4. **Clarity**: Explicit specification of what each test case grades
5. **Backward Compatible**: Existing test kits continue to work without modification
