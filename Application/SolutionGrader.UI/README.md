# SolutionGrader.UI - Auto Grading System

A WPF-based user interface for the auto-grading system that supports grading student solutions using Docker containers.

## Features

- **Student Discovery**: Automatically discovers student submissions from the Submit folder
- **Test Kit Management**: Maps test kits to papers and manages test case execution
- **Docker Integration**: Creates isolated containers for grading each student
- **Progress Tracking**: Real-time progress indicators and status updates
- **Pause/Resume**: Ability to pause and resume grading operations
- **Detailed Logging**: Per-student logging with date-stamped folders
- **Excel Reports**: Generates comprehensive Excel result files

## Folder Structure

### Submit Folder
```
Submit/
├── 1/                          # Paper No
│   ├── studentCode1/
│   │   └── 1/                  # Question No (always 1)
│   │       └── solution/
│   │           ├── Q11/        # Server project
│   │           │   └── *.dll
│   │           └── Q12/        # Client project
│   │               └── *.dll
│   └── studentCode2/
│       └── ...
└── 2/                          # Paper No 2
    └── ...
```

### Test Kit Folder
```
TestKit/
├── Q1/                         # Maps to Paper 1
│   ├── Header.xlsx
│   ├── Environment.xlsx
│   ├── Meta/
│   │   └── Given/
│   │       ├── Server/         # Reference server implementation
│   │       └── Client/         # Reference client implementation
│   ├── TC1/
│   │   ├── Detail.xlsx
│   │   └── Environment.xlsx
│   └── TC2/
│       └── ...
└── Q2/                         # Maps to Paper 2
    └── ...
```

### Output/Results Folder
```
Results/
├── StudentsSolution.xlsx       # Overall summary
└── {StudentCode}/
    ├── OverallSummary.xlsx
    └── {TestCase}/
        ├── GradeDetail.xlsx
        └── {TestCase}_Result.xlsx
```

### Logs Folder
```
Logs/
├── System_YYYYMMDD_HHMMSS.log  # System log
└── Log_{StudentCode}_{YYYYMMDD}/
    └── grading_HHMMSS.log      # Per-student log
```

## Configuration

### Project Names
Configure the client and server project names in the UI:
- **Client Project Name**: e.g., "Project12" (will search for Project12.dll)
- **Server Project Name**: e.g., "Project11" (will search for Project11.dll)

### Port Configuration
- **Internal Port**: Port used inside Docker containers
- **Host Port**: Port exposed on the host for network monitoring

## Usage

1. **Select Submit Folder**: Browse to the folder containing student submissions
2. **Select Test Kit Folder**: Browse to the folder containing test kits
3. **Configure Projects**: Check "Has Client" and/or "Has Server" and enter project names
4. **Load Students**: Click "Load Students" to discover all submissions
5. **Filter (Optional)**: Use the paper filter to focus on specific papers
6. **Start Grading**: 
   - "Start All" to grade all students
   - "Start Selected" to grade only selected students
7. **Monitor Progress**: Watch the progress in the status bar and log panel

## Grading Flow

1. Student discovery and DLL path lookup
2. Test kit validation (check if test kit exists for paper)
3. Docker container setup (server, client, database if needed)
4. File copy to containers
5. Execute test cases per test kit steps
6. Flush network monitor before each grading step
7. Compare results and calculate marks
8. Write results to Excel files
9. Cleanup containers

## Requirements

- Windows 10/11 with .NET 8.0 Runtime
- Docker Desktop running
- Network access for Docker container communication

## Building

```bash
dotnet build Application/SolutionGrader.UI/SolutionGrader.UI.csproj
```

## Running

```bash
dotnet run --project Application/SolutionGrader.UI/SolutionGrader.UI.csproj
```

Or run the built executable directly:
```bash
./Application/SolutionGrader.UI/bin/Debug/net8.0-windows/SolutionGrader.UI.exe
```
