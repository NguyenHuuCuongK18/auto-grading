# Appsettings.json Generation Service

## Overview

This document describes the infrastructure for dynamically generating `appsettings.json` files for client and server applications based on configuration in `Header.xlsx`.

## Setup

### Header.xlsx Configuration

The `Header.xlsx` file should contain a worksheet named "Config" with the following key-value pairs:

| Key | Value | Description |
|-----|-------|-------------|
| Type | HTTP or TCP | Protocol type - determines IP address format |
| Sql Server | SQLExpress | SQL Server instance name |
| Database | Library | Database name |
| Username | sa | Database username |
| Password | sa | Database password |

Example `Header.xlsx` structure:
```
Config worksheet:
Row 1: Key              | Value
Row 2: Type             | HTTP
Row 3: Sql Server       | SQLExpress  
Row 4: Database         | Library
Row 5: Username         | sa
Row 6: Password         | sa
```

### Port Allocation

The service automatically:
1. Determines IP address based on Type:
   - **HTTP** → `http://localhost`
   - **TCP** → `127.0.0.1`
2. Allocates random available ports from the ephemeral range (49152-65535)
3. Validates that ports are not already in use
4. Retries with different ports if conflicts are detected

## Generated Files

### Server appsettings.json
```json
{
  "ConnectionStrings": {
    "MyCnn": "server=.\\SQLEXPRESS;database=Library;uid=sa;pwd=sa;TrustServerCertificate=True;"
  },
  "IPAddress": "http://localhost",
  "Port": "5001"
}
```

### Client appsettings.json
```json
{
  "IPAddress": "http://localhost",
  "Port": "5000"
}
```

Note: The client appsettings.json does NOT include connection strings.

## Usage

### Basic Usage

```csharp
using SolutionGrader.Core.Services;
using SolutionGrader.Core.Domain.Models;

// Load database configuration from Header.xlsx
var loader = new ExcelSuiteLoader();
var suite = loader.Load("./TestKitDemo/Header.xlsx");
var dbConfig = suite.DatabaseConfig;

// Create the appsettings generation service
var appsettingsService = new AppsettingsCreationService();

// Generate appsettings.json files
// Ports are allocated automatically and files are written to exe directories
var (proxyPort, serverPort) = appsettingsService.GenerateAppsettings(
    dbConfig,
    clientExePath: "./path/to/client.exe",
    serverExePath: "./path/to/server.exe"
);

Console.WriteLine($"Proxy Port: {proxyPort}");
Console.WriteLine($"Server Port: {serverPort}");
```

### Using with Middleware

The middleware proxy service can be configured with the dynamically allocated ports:

```csharp
using SolutionGrader.Core.Services;

// After generating appsettings
var (proxyPort, serverPort) = appsettingsService.GenerateAppsettings(...);

// Configure middleware with allocated ports
IMiddlewareService middleware = new MiddlewareProxyService(runContext);
middleware.ConfigurePorts(proxyPort, serverPort);

// Or use the ConfigurableMiddlewareProxyService directly
IMiddlewareService middleware = new ConfigurableMiddlewareProxyService(
    runContext, 
    proxyPort, 
    serverPort
);

// Start the middleware
await middleware.StartAsync(useHttp: true);
```

## Integration Points

### 1. ExcelSuiteLoader
- **Location**: `Lib/SolutionGrader.Core/Services/ExcelSuiteLoader.cs`
- **Enhancement**: Now reads database configuration from the "Config" worksheet in Header.xlsx
- **Returns**: `SuiteDefinition` with populated `DatabaseConfig` property

### 2. AppsettingsCreationService
- **Location**: `Lib/SolutionGrader.Core/Services/AppsettingsCreationService.cs`
- **Interface**: `IAppsettingsCreationService`
- **Features**:
  - Random port allocation with conflict detection
  - IP address determination based on protocol type
  - Connection string generation
  - File writing to client/server directories

### 3. Middleware Services
Both middleware implementations now support dynamic port configuration:
- `MiddlewareProxyService` - Default implementation with configurable ports
- `ConfigurableMiddlewareProxyService` - Alternative implementation accepting ports at construction

## Connection String Format

The generated connection string follows this format:
```
server={SqlServer};database={Database};uid={Username};pwd={Password};TrustServerCertificate=True;
```

Where:
- `{SqlServer}` defaults to `.\\SQLEXPRESS` if not specified
- SQL Server instance names without `.\` prefix are automatically formatted
- `TrustServerCertificate=True` is always included for development environments

## Testing

A test script is provided at `TestAppsettingsGeneration.cs` that demonstrates:
- Database configuration loading
- Port allocation
- IP address determination for both HTTP and TCP modes
- Expected JSON structures

Run the test:
```bash
dotnet script TestAppsettingsGeneration.cs
```

## Future Integration

While the infrastructure is ready, integration into the main workflow is pending. To integrate:

1. In `SuiteRunner.ExecuteSuiteAsync`:
   - Call `AppsettingsCreationService.GenerateAppsettings()` after loading the suite
   - Pass allocated ports to middleware configuration
   - Use generated appsettings.json files instead of templates

2. Update `Program.cs` to make appsettings generation optional:
   - Keep existing `--client-appsettings` and `--server-appsettings` flags for backward compatibility
   - Generate appsettings dynamically when templates are not provided

## Notes

- Port allocation uses ephemeral port range (49152-65535) to avoid conflicts with system services
- The service performs up to 100 attempts to find available ports
- If all attempts fail, the OS assigns a port automatically
- Generated appsettings.json files are written directly to the executable directories
- Files are overwritten if they already exist
