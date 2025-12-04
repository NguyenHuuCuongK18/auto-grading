# Quick Reference: Project Naming for Question 1

## IMPORTANT: System Limitation
**This system ONLY grades Question 1. Questions 2, 3, etc. are NOT supported.**

---

## Naming Conventions for Question 1

### Scenario 1: Single Project (No Client/Server Split)
Student submits one project that handles everything.

**Common Names:**
- `Q1` → Standard single project for Question 1
- `Project1` → Alternative single project name
- `StudentCode` → Generic student code

**UI Configuration:**
```
Project 1: Q1
Project 2: [empty]
Toggles: Hidden (not needed)
```

**System Behavior:**
- Both client and server use the same DLL (Q1.dll)
- No role designation needed

---

### Scenario 2: Dual Project (Client/Server Architecture)
Student splits Question 1 into separate client and server components.

**Common Names:**

#### Option A: Numbered Split (Q11/Q12)
- `Q11` → Server component of Question 1
- `Q12` → Client component of Question 1

**UI Configuration:**
```
Project 1: Q11
Project 2: Q12
Toggle 1: Server
Toggle 2: Client
```

#### Option B: Traditional Names (Project11/Project12)
- `Project11` → Server component of Question 1
- `Project12` → Client component of Question 1

**UI Configuration:**
```
Project 1: Project11
Project 2: Project12
Toggle 1: Server
Toggle 2: Client
```

**System Behavior:**
- Server uses specified server DLL (Q11.dll or Project11.dll)
- Client uses specified client DLL (Q12.dll or Project12.dll)
- Both are components of Question 1

---

## Common Misconceptions ❌

### ❌ WRONG: Using Q2 for Question 2
```
❌ Project 1: Q1
❌ Project 2: Q2  ← This looks like Question 2!
```
**Problem:** Q2 implies Question 2, which is NOT supported.

### ✅ CORRECT: Using Q11/Q12 for Question 1 Split
```
✓ Project 1: Q11  ← Server for Question 1
✓ Project 2: Q12  ← Client for Question 1
```
**Why:** Q11 and Q12 clearly indicate components of Question 1.

---

## Folder Structure Examples

### Example 1: Single Project (SampleStudentAtual)
```
AnhDThe187386/
  1/                    ← Question 1 folder
    solution/
      Q1_anhdthe187386/ ← Published folder
        Q1.dll          ← Single DLL for both roles
```
**Configuration:** Project 1 = "Q1"

### Example 2: Dual Project Traditional (Submit)
```
cuongnhhe186494/
  1/                        ← Question 1 folder
    solution/
      Project11/            ← Server source
        Project11.dll
      Project11_published/  ← Server published
        Project11.dll
      Project12/            ← Client source
        Project12.dll
      Project12_published/  ← Client published
        Project12.dll
```
**Configuration:** Project 1 = "Project11" (Server), Project 2 = "Project12" (Client)

### Example 3: Dual Project Numbered
```
studentcode/
  1/                    ← Question 1 folder
    solution/
      Q11_published/    ← Server for Question 1
        Q11.dll
      Q12_published/    ← Client for Question 1
        Q12.dll
```
**Configuration:** Project 1 = "Q11" (Server), Project 2 = "Q12" (Client)

---

## Validation Rules

### Single Project
- ✅ At least one project name must be entered
- ✅ Project 1 or Project 2 can be used (doesn't matter which)
- ✅ Toggles are hidden
- ✅ DLL is used for both client and server

### Dual Project
- ✅ Both Project 1 and Project 2 must have values
- ✅ Toggles automatically appear
- ✅ One must be designated Client, one must be Server
- ❌ Both cannot be Client
- ❌ Both cannot be Server

---

## Common Submission Formats

### Format 1: Published Folder Name = Project Name
```
solution/
  Q1_studentcode/
    Q1.dll
```
→ Use Project 1 = "Q1"

### Format 2: Published Folder Name ≠ Project Name
```
solution/
  Q11/
    Project11.dll       ← Look for this
  Q11_studentcode/
    Project11.dll       ← Or this
```
→ Use Project 1 = "Project11" (system searches recursively)

### Format 3: Multiple Published Folders
```
solution/
  Server/
    Q11_published/
      Q11.dll
  Client/
    Q12_published/
      Q12.dll
```
→ Project 1 = "Q11" (Server), Project 2 = "Q12" (Client)

---

## TestKit Integration

The TestKit's `Header.xlsx` Grade content dictates the grading criteria, but it's for **Question 1 only**.

### TestKit Environment.xlsx
```
Code_Container_Internal_Port: 80
Code_Container_Host_Port: 8081
Client: Meta/Given/client.dll  ← Reference client (if needed)
Server: Meta/Given/server.dll  ← Reference server (if needed)
```

When student provides only one component:
- If student provides client → TestKit provides server from Meta/Given
- If student provides server → TestKit provides client from Meta/Given

---

## FAQ

**Q: Can I grade Question 2?**
A: No. This system only grades Question 1. Q2 refers to Question 2, which is not supported.

**Q: What if student submits Q1 and Q2?**
A: This implies Question 1 and Question 2. Since only Question 1 is supported, configure only Q1. If you need client/server split for Question 1, use Q11/Q12 naming instead.

**Q: What's the difference between Q1 and Q11?**
A: 
- Q1 = Single project for Question 1 (handles both roles)
- Q11 = Server component of Question 1 (split architecture)
- Q12 = Client component of Question 1 (split architecture)

**Q: Can I use custom names like "MyServer" and "MyClient"?**
A: Yes! Any naming convention works as long as it matches the DLL names in the student's submission.

**Q: Do I need to specify the .dll extension?**
A: No. Just enter the project name (e.g., "Q1" or "Project11"). The system automatically searches for the corresponding .dll file.

---

## Summary

✅ **DO:**
- Use Q1 for single project Question 1
- Use Q11/Q12 for Question 1 split (server/client)
- Use Project11/Project12 for Question 1 split (server/client)
- Match the DLL names in student submissions

❌ **DON'T:**
- Use Q2, Q3, etc. (these are different questions, not supported)
- Try to grade multiple questions (only Question 1 is supported)
- Forget to specify roles when using dual projects

🎯 **REMEMBER:**
This system only grades **Question 1**. All project names and configurations must be for Question 1 components only.
