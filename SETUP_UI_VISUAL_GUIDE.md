# Setup Window UI Changes - Visual Guide

## Before (Old UI)

```
┌──────────────────────────────────────────────────────────────┐
│ Auto Grading System                                          │
│ Setup Configuration                                          │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│ Submit Folder:    [/path/to/Submit         ] [Browse...]    │
│                                                              │
│ Test Kit Folder:  [/path/to/TestKit        ] [Browse...]    │
│                                                              │
│ Save Results To:  [/path/to/Results        ] [Browse...]    │
│                                                              │
│ ┌────────────────────────────────────────────────────────┐  │
│ │ Project Configuration                                  │  │
│ │                                                        │  │
│ │  ☑ Has Client Component        ☑ Has Server Component│  │
│ │                                                        │  │
│ │    Client Project Name:           Server Project Name:│  │
│ │    [Project12      ]              [Project11        ] │  │
│ │                                                        │  │
│ └────────────────────────────────────────────────────────┘  │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ [Validation message here]            [▶ Start Grading]      │
└──────────────────────────────────────────────────────────────┘
```

**Issues with old UI:**
- Requires checkboxes for Has Client/Has Server (confusing)
- Assumes project names like "Project11" and "Project12"
- Doesn't handle cases where student submits generic names like "Q1"
- No way to specify which project is client vs server when names are generic


## After (New UI)

```
┌──────────────────────────────────────────────────────────────┐
│ Auto Grading System                                          │
│ Setup Configuration                                          │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│ Submit Folder:    [/path/to/Submit         ] [Browse...]    │
│                                                              │
│ Test Kit Folder:  [/path/to/TestKit        ] [Browse...]    │
│                                                              │
│ Save Results To:  [/path/to/Results        ] [Browse...]    │
│                                                              │
│ ┌────────────────────────────────────────────────────────┐  │
│ │ Project Configuration                                  │  │
│ │                                                        │  │
│ │ Enter project names as they appear in student         │  │
│ │ submissions (e.g., Q1, Q2, Project11, Project12).     │  │
│ │ If only one project is specified, toggles are not     │  │
│ │ needed. If two projects are specified, use toggles    │  │
│ │ to indicate which is client and which is server.      │  │
│ │                                                        │  │
│ │ Project 1: [Q1              ]                         │  │
│ │                                                        │  │
│ │ Project 2: [                ]                         │  │
│ │                                                        │  │
│ └────────────────────────────────────────────────────────┘  │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ [Validation message here]            [▶ Start Grading]      │
└──────────────────────────────────────────────────────────────┘
```

### Scenario 1: Single Project (e.g., SampleStudentAtual folder)
When only one project is entered, toggles are hidden:

```
│ ┌────────────────────────────────────────────────────────┐  │
│ │ Project Configuration                                  │  │
│ │                                                        │  │
│ │ [Helper text...]                                       │  │
│ │                                                        │  │
│ │ Project 1: [Q1              ]  [Toggles Hidden]       │  │
│ │                                                        │  │
│ │ Project 2: [                ]  [Toggles Hidden]       │  │
│ │                                                        │  │
│ └────────────────────────────────────────────────────────┘  │
```

**Result:**
- ClientProjectName = "Q1"
- ServerProjectName = "Q1"
- HasClient = true
- HasServer = true
- System looks for Q1.dll for both roles


### Scenario 2: Two Projects (e.g., Submit folder with Project11 and Project12)
When both projects are entered, toggles appear:

```
│ ┌────────────────────────────────────────────────────────┐  │
│ │ Project Configuration                                  │  │
│ │                                                        │  │
│ │ [Helper text...]                                       │  │
│ │                                                        │  │
│ │ Project 1: [Project11       ]  ⚪Client ●Server       │  │
│ │                                                        │  │
│ │ Project 2: [Project12       ]  ●Client ⚪Server       │  │
│ │                                                        │  │
│ └────────────────────────────────────────────────────────┘  │
```

**Result:**
- ClientProjectName = "Project12"
- ServerProjectName = "Project11"
- HasClient = true
- HasServer = true
- System looks for Project11.dll (server) and Project12.dll (client)


### Scenario 3: Two Projects with Generic Names
When both projects use generic names like Q1 and Q2:

```
│ ┌────────────────────────────────────────────────────────┐  │
│ │ Project Configuration                                  │  │
│ │                                                        │  │
│ │ [Helper text...]                                       │  │
│ │                                                        │  │
│ │ Project 1: [Q1              ]  ●Client ⚪Server       │  │
│ │                                                        │  │
│ │ Project 2: [Q2              ]  ⚪Client ●Server       │  │
│ │                                                        │  │
│ └────────────────────────────────────────────────────────┘  │
```

**Result:**
- ClientProjectName = "Q1"
- ServerProjectName = "Q2"
- HasClient = true
- HasServer = true
- System looks for Q1.dll (client) and Q2.dll (server)


## Validation Examples

### Valid Configuration - Single Project
```
Submit Folder:    ✓ /home/user/Submit
Test Kit Folder:  ✓ /home/user/TestKit
Save Results To:  ✓ /home/user/Results
Project 1:        ✓ Q1
Project 2:        [empty]

[                                        ] [▶ Start Grading]
                                             ↓
                                         Proceeds ✓
```

### Valid Configuration - Two Projects with Roles
```
Submit Folder:    ✓ /home/user/Submit
Test Kit Folder:  ✓ /home/user/TestKit
Save Results To:  ✓ /home/user/Results
Project 1:        ✓ Project11 → Server
Project 2:        ✓ Project12 → Client

[                                        ] [▶ Start Grading]
                                             ↓
                                         Proceeds ✓
```

### Invalid Configuration - No Projects
```
Submit Folder:    ✓ /home/user/Submit
Test Kit Folder:  ✓ /home/user/TestKit
Save Results To:  ✓ /home/user/Results
Project 1:        ✗ [empty]
Project 2:        ✗ [empty]

[Please enter at least one project name ] [▶ Start Grading]
                                             ↓
                                         Blocked ✗
```

### Invalid Configuration - Same Roles
```
Submit Folder:    ✓ /home/user/Submit
Test Kit Folder:  ✓ /home/user/TestKit
Save Results To:  ✓ /home/user/Results
Project 1:        ✓ Project11 → Client
Project 2:        ✓ Project12 → Client (same!)

[When two projects are specified, one   ] [▶ Start Grading]
[must be client and one must be server. ]     ↓
                                         Blocked ✗
```

### Invalid Configuration - Missing Test Kit
```
Submit Folder:    ✓ /home/user/Submit
Test Kit Folder:  ✗ [does not exist]
Save Results To:  ✓ /home/user/Results
Project 1:        ✓ Q1

[Test Kit folder does not exist.        ] [▶ Start Grading]
                                             ↓
                                         Blocked ✗
```


## Benefits of New UI

1. **Flexibility**: Handles any project naming convention
   - Generic names: Q1, Q2, Q3
   - Specific names: Project11, Project12
   - Custom names: MyServer, MyClient

2. **Clarity**: Role toggles only appear when needed
   - Single project: No confusion, no toggles needed
   - Two projects: Clear client/server designation required

3. **Validation**: Prevents invalid configurations
   - Must have at least one project
   - Two projects must have different roles
   - Clear error messages guide the user

4. **Backward Compatibility**: Works with existing code
   - Legacy ClientProjectName/ServerProjectName still work
   - Automatic property synchronization
   - No breaking changes to existing functionality

5. **Real-world Support**: Handles actual student submissions
   - Supports SampleStudentAtual folder structure (Q1_studentcode)
   - Supports Submit folder structure (Project11_studentcode, Project12_studentcode)
   - Supports mixed scenarios (student provides one, testkit provides other)


## Code Mapping

### Single Project Example
```csharp
// User Input
txtProject1Name.Text = "Q1";
txtProject2Name.Text = "";

// Result after StartGrading_Click
configuration.Project1Name = "Q1";
configuration.Project2Name = "";
configuration.Project1IsClient = false; // doesn't matter
configuration.Project2IsClient = true;  // doesn't matter
configuration.HasClient = true;
configuration.HasServer = true;

// Automatic mapping via UpdateLegacyProperties()
configuration.ClientProjectName = "Q1";
configuration.ServerProjectName = "Q1";
```

### Two Projects Example
```csharp
// User Input
txtProject1Name.Text = "Project11";
txtProject2Name.Text = "Project12";
rbProject1Server.IsChecked = true;  // Project1 is server
rbProject2Client.IsChecked = true;  // Project2 is client

// Result after StartGrading_Click
configuration.Project1Name = "Project11";
configuration.Project2Name = "Project12";
configuration.Project1IsClient = false; // false = server
configuration.Project2IsClient = true;  // true = client
configuration.HasClient = true;
configuration.HasServer = true;

// Automatic mapping via UpdateLegacyProperties()
configuration.ClientProjectName = "Project12";
configuration.ServerProjectName = "Project11";
```


## Summary

The new UI provides a flexible, intuitive way to configure project mapping that adapts to different student submission formats while maintaining backward compatibility with existing code. The automatic show/hide of role toggles makes the interface cleaner and less confusing for users.
