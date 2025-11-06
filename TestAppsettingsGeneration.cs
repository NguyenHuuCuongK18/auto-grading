using SolutionGrader.Core.Services;
using SolutionGrader.Core.Domain.Models;

// Demo: Test the AppsettingsCreationService infrastructure

Console.WriteLine("=== Testing AppsettingsCreationService Infrastructure ===\n");

// Create a mock database configuration matching Header.xlsx structure
var dbConfig = new DatabaseConfiguration
{
    Type = "HTTP",
    SqlServer = "SQLExpress",
    Database = "Library",
    Username = "sa",
    Password = "sa"
};

Console.WriteLine("Database Configuration:");
Console.WriteLine($"  Type: {dbConfig.Type}");
Console.WriteLine($"  SQL Server: {dbConfig.SqlServer}");
Console.WriteLine($"  Database: {dbConfig.Database}");
Console.WriteLine($"  Username: {dbConfig.Username}");
Console.WriteLine($"  Password: {dbConfig.Password}");
Console.WriteLine();

// Create the service
var appsettingsService = new AppsettingsCreationService();

// Generate appsettings (without actual files for now)
Console.WriteLine("Generating port allocations...");
var (proxyPort, serverPort) = appsettingsService.GenerateAppsettings(dbConfig, null, null);

Console.WriteLine($"\nAllocated Ports:");
Console.WriteLine($"  Proxy Port: {proxyPort}");
Console.WriteLine($"  Server Port: {serverPort}");

// Test connection string generation
Console.WriteLine("\nExpected Server appsettings.json structure:");
Console.WriteLine("{");
Console.WriteLine("  \"ConnectionStrings\": {");
Console.WriteLine($"    \"MyCnn\": \"server=localhost;database=Library;uid=sa;pwd=sa;TrustServerCertificate=true\"");
Console.WriteLine("  },");
Console.WriteLine($"  \"IPAddress\": \"http://localhost\",");
Console.WriteLine($"  \"Port\": \"{serverPort}\"");
Console.WriteLine("}");

Console.WriteLine("\nExpected Client appsettings.json structure:");
Console.WriteLine("{");
Console.WriteLine($"  \"IPAddress\": \"http://localhost\",");
Console.WriteLine($"  \"Port\": \"{proxyPort}\"");
Console.WriteLine("}");

// Test TCP mode
Console.WriteLine("\n\n=== Testing TCP Configuration ===\n");
var tcpConfig = new DatabaseConfiguration
{
    Type = "TCP",
    SqlServer = "SQLExpress",
    Database = "Library",
    Username = "sa",
    Password = "sa"
};

var tcpService = new AppsettingsCreationService();
var (tcpProxyPort, tcpServerPort) = tcpService.GenerateAppsettings(tcpConfig, null, null);

Console.WriteLine($"TCP Mode - IP Address should be: 127.0.0.1");
Console.WriteLine($"Allocated Ports:");
Console.WriteLine($"  Proxy Port: {tcpProxyPort}");
Console.WriteLine($"  Server Port: {tcpServerPort}");

Console.WriteLine("\n=== Test Complete ===");
