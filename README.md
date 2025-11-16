# Auto-Grading System

An automated grading system for client-server applications with support for HTTP and TCP protocols, database operations, and middleware proxy testing.

## Features

- **Protocol Support**: HTTP and TCP client-server testing
- **Database Integration**: SQL Server with both local and Docker support
- **Middleware Proxy**: Network traffic capture and verification
- **Flexible Configuration**: Excel-based test suite configuration
- **Detailed Logging**: Comprehensive test results and Excel reports
- **Cross-Platform**: Works on Windows, Linux, and macOS (with Docker)

## Quick Start

### Prerequisites

- .NET 8.0 SDK or later
- Docker (for database operations - recommended)
- SQL Server 2022 (optional, if not using Docker)

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/NguyenHuuCuongK18/auto-grading.git
   cd auto-grading
   ```

2. Build the solution:
   ```bash
   dotnet build SolutionGrader.sln
   ```

3. Start SQL Server with Docker (recommended):
   ```bash
   docker compose up -d
   ```

### Running Tests

Basic usage:
```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "GradeResults" \
  --useDocker
```

With custom executables:
```bash
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "GradeResults" \
  --client "path/to/client.exe" \
  --server "path/to/server.exe" \
  --useDocker
```

## Database Configuration

### Using Docker (Recommended)

Docker provides a consistent SQL Server environment across all platforms:

1. **Start the container:**
   ```bash
   docker compose up -d
   ```

2. **Use the `--useDocker` flag when running tests:**
   ```bash
   dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
     --suite "path/to/test/suite" \
     --out "path/to/output" \
     --useDocker
   ```

3. **Or configure in environment.xlsx:**
   Add to the Config sheet:
   ```
   Key         | Value
   ------------|------
   UseDocker   | true
   ```

For detailed Docker setup instructions, see [DOCKER_SETUP.md](DOCKER_SETUP.md).

### Using Local SQL Server

If you have SQL Server installed locally:

1. Ensure SQL Server is running
2. Update database credentials in your test suite's `Header.xlsx` (Config sheet)
3. Run tests without the `--useDocker` flag

## Test Suite Structure

A typical test suite has the following structure:

```
TestSuite/
├── Header.xlsx           # Suite configuration (protocol, database)
├── environment.xlsx      # Environment settings (ports, paths)
├── Meta/
│   ├── database.sql     # Database schema and seed data
│   └── Given/           # Reference implementations
│       ├── Server/
│       └── Client/
└── TC01_TestCase/       # Individual test cases
    ├── Detail.xlsx      # Test steps
    ├── header.xlsx      # Test case properties
    └── environment.xlsx # Test case specific settings
```

### Configuration Files

#### Header.xlsx (Suite Level)
- **Config Sheet**: Protocol type, database connection settings
- **QuestionMark Sheet**: Test case marks/scores

#### environment.xlsx (Suite or Test Case Level)
- **Config Sheet**: 
  - `Code_Container_Internal_Port`: Middleware/proxy port
  - `Code_Container_Host_Port`: Server port
  - `Default_Database_File_Path`: Path to database script
  - `UseDocker`: Enable Docker mode (true/false)
  - Database credentials (username, password, database name)

#### Detail.xlsx (Test Case Level)
Defines test steps including:
- Client and server startup
- Input/output operations
- Network traffic validation
- Database state verification

## Command-Line Options

### Required Arguments
- `--suite`: Path to test suite folder or Header.xlsx file
- `--out`: Output directory for grading results

### Optional Arguments
- `--client`: Path to client executable (overrides environment.xlsx)
- `--server`: Path to server executable (overrides environment.xlsx)
- `--useDocker`: Use Docker SQL Server container for database operations

### Configuration Priority

The system uses the following priority for configuration:

1. **Command-line arguments** (highest priority)
2. **Test case environment.xlsx** (test case specific)
3. **Suite environment.xlsx** (suite level defaults)
4. **Header.xlsx** (legacy database configuration)
5. **System defaults** (lowest priority)

## Testing

### Run Integration Tests

Verify Docker integration:
```bash
./test-docker-integration.sh
```

### Build and Test

```bash
# Build the solution
dotnet build SolutionGrader.sln

# Run with sample test suite
dotnet run --project Application/SolutionGrader.Cli -- ExecuteSuite \
  --suite "SampleTestKitsWithData/Testkit_HTTP_1" \
  --out "TestResults" \
  --useDocker
```

## Architecture

### Components

- **SolutionGrader.Core**: Core grading logic, test execution, and evaluation
- **SolutionGrader.Cli**: Command-line interface for running test suites
- **ProcessLauncher**: Process management for client/server applications

### Key Services

- **SuiteRunner**: Orchestrates test suite execution
- **Executor**: Executes individual test steps
- **EnvironmentResetService**: Manages database reset operations
- **MiddlewareProxyService**: Captures and analyzes network traffic
- **DataComparisonService**: Validates test outputs
- **ExcelDetailLogService**: Generates detailed Excel reports

## Protocols

### HTTP Protocol
- RESTful API testing
- HTTP method verification (GET, POST, PUT, DELETE)
- Request/response payload validation
- Status code checking

### TCP Protocol
- Raw TCP socket communication
- Binary data transfer
- Custom protocol testing

## Output

Test results are saved to timestamped directories:
```
GradeResults/
└── GradeResult_YYYYMMDD_HHMMSS/
    ├── TC01_TestCase/
    │   ├── report.xlsx      # Detailed test results
    │   ├── client_console.txt
    │   ├── server_console.txt
    │   └── network_traffic.txt
    └── TC02_TestCase/
        └── ...
```

## Troubleshooting

### Database Connection Issues

If you see database connection errors:

1. **Using Docker**: Ensure container is running
   ```bash
   docker ps | grep sqlserver-test
   ```

2. **Using Local SQL Server**: Verify SQL Server is running and accessible
   ```bash
   # On Windows
   Get-Service MSSQLSERVER
   
   # Test connection
   sqlcmd -S localhost -U sa -P YourPassword -Q "SELECT 1"
   ```

3. **Check credentials**: Verify database credentials in environment.xlsx match your setup

### Client/Server Not Starting

1. Check executable permissions
2. Verify paths in environment.xlsx
3. Review console output in test results directory

### Network Traffic Not Captured

1. Ensure middleware proxy is starting (check test logs)
2. Verify port configuration in environment.xlsx
3. Check that client connects to proxy port, not server port directly

For more troubleshooting tips, see [DOCKER_SETUP.md](DOCKER_SETUP.md).

## Contributing

Contributions are welcome! Please ensure:

1. Code builds without errors
2. Tests pass
3. Documentation is updated
4. Commit messages are descriptive

## License

[Specify your license here]

## Support

For issues and questions:
- Create an issue on GitHub
- Check existing documentation
- Review test suite examples in `SampleTestKitsWithData/`

## Acknowledgments

Built with:
- .NET 8.0
- ClosedXML (Excel handling)
- Microsoft.Data.SqlClient
- Docker SQL Server
