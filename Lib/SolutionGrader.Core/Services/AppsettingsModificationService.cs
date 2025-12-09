using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Helpers;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Service for modifying existing appsettings.json files for client and server applications.
/// Unlike AppsettingsCreationService which generates new files, this service:
/// 1. Reads existing appsettings.json
/// 2. Modifies only specific values (Port, IpAddress, ConnectionString)
/// 3. Preserves all other settings (logging config, custom settings, etc.)
/// 
/// This approach respects student's configuration choices while enabling grading.
/// </summary>
public sealed class AppsettingsModificationService : IAppsettingsModificationService
{
    private int _clientPort;
    private int _serverPort;
    private readonly GradingConfig _gradingConfig;

    public AppsettingsModificationService() : this(GradingConfig.Default)
    {
    }

    public AppsettingsModificationService(GradingConfig gradingConfig)
    {
        _gradingConfig = gradingConfig ?? GradingConfig.Default;
    }

    /// <summary>
    /// Modifies existing appsettings.json files for client and server.
    /// Returns true if both files were successfully modified.
    /// Returns false if either file doesn't exist (caller should use DLL mod fallback if enabled).
    /// </summary>
    public (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath)
    {
        return ModifyAppsettings(dbConfig, clientExePath, serverExePath, null, null);
    }

    public (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath, 
        EnvironmentConfiguration? envConfig)
    {
        return ModifyAppsettings(dbConfig, clientExePath, serverExePath, envConfig, null);
    }

    public (bool Success, int ClientPort, int ServerPort) ModifyAppsettings(
        DatabaseConfiguration? dbConfig, 
        string? clientExePath, 
        string? serverExePath, 
        EnvironmentConfiguration? envConfig, 
        string? protocol)
    {
        _serverPort = _gradingConfig.GraderPort;
        _clientPort = _gradingConfig.GraderPort; // Client connects to same port as server
        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Using GraderPort from config: {_serverPort}");

        var ipAddress = DetermineIpAddress(protocol ?? dbConfig?.Type ?? AppsettingKeywords.PROTOCOL_HTTP);
        bool serverSuccess = false;
        bool clientSuccess = false;

        // Modify server appsettings.json if it exists
        if (!string.IsNullOrEmpty(serverExePath) && File.Exists(serverExePath))
        {
            var serverDir = Path.GetDirectoryName(serverExePath);
            if (!string.IsNullOrEmpty(serverDir))
            {
                var serverAppsettingsPath = Path.Combine(serverDir, FileKeywords.FileName_AppSettings);
                if (File.Exists(serverAppsettingsPath))
                {
                    serverSuccess = ModifyServerAppsettings(serverAppsettingsPath, ipAddress, _serverPort, dbConfig, envConfig);
                    if (serverSuccess)
                    {
                        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Modified server appsettings: {serverAppsettingsPath}");
                    }
                    else
                    {
                        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} WARNING: Server appsettings exists but modification failed: {serverAppsettingsPath}");
                    }
                }
                else
                {
                    Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Server appsettings not found: {serverAppsettingsPath} (will use DLL mod if enabled)");
                }
            }
        }

        // Modify client appsettings.json if it exists
        if (!string.IsNullOrEmpty(clientExePath) && File.Exists(clientExePath))
        {
            var clientDir = Path.GetDirectoryName(clientExePath);
            if (!string.IsNullOrEmpty(clientDir))
            {
                var clientAppsettingsPath = Path.Combine(clientDir, FileKeywords.FileName_AppSettings);
                if (File.Exists(clientAppsettingsPath))
                {
                    clientSuccess = ModifyClientAppsettings(clientAppsettingsPath, ipAddress, _clientPort);
                    if (clientSuccess)
                    {
                        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Modified client appsettings: {clientAppsettingsPath}");
                    }
                    else
                    {
                        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} WARNING: Client appsettings exists but modification failed: {clientAppsettingsPath}");
                    }
                }
                else
                {
                    Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Client appsettings not found: {clientAppsettingsPath} (will use DLL mod if enabled)");
                }
            }
        }

        var overallSuccess = (serverSuccess || string.IsNullOrEmpty(serverExePath)) && 
                             (clientSuccess || string.IsNullOrEmpty(clientExePath));

        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Modification result - Server: {serverSuccess}, Client: {clientSuccess}, Overall: {overallSuccess}");
        Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Allocated ports - Client: {_clientPort}, Server: {_serverPort}");

        return (overallSuccess, _clientPort, _serverPort);
    }

    public (int ClientPort, int ServerPort) GetPorts()
    {
        return (_clientPort, _serverPort);
    }

    private static string DetermineIpAddress(string type)
    {
        return type.Equals(AppsettingKeywords.PROTOCOL_TCP, StringComparison.OrdinalIgnoreCase) 
            ? AppsettingKeywords.TCP_LOCALHOST 
            : AppsettingKeywords.HTTP_LOCALHOST;
    }

    /// <summary>
    /// Modifies server appsettings.json, preserving all existing settings while updating:
    /// - ConnectionStrings.MyCnn (if exists)
    /// - IpAddress (if exists)
    /// - Port (if exists)
    /// </summary>
    private static bool ModifyServerAppsettings(string path, string ipAddress, int port, DatabaseConfiguration? dbConfig, EnvironmentConfiguration? envConfig)
    {
        try
        {
            var jsonText = File.ReadAllText(path);
            var jsonNode = JsonNode.Parse(jsonText);
            
            if (jsonNode == null || jsonNode is not JsonObject jsonObj)
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} ERROR: Invalid JSON in {path}");
                return false;
            }

            var modified = false;

            // Update ConnectionStrings.MyCnn if it exists
            if (jsonObj["ConnectionStrings"] is JsonObject connStrings)
            {
                if (connStrings["MyCnn"] != null)
                {
                    var connectionString = ConnectionStringHelper.Build(dbConfig, envConfig);
                    connStrings["MyCnn"] = connectionString;
                    modified = true;
                    Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Updated ConnectionStrings.MyCnn");
                }
            }

            // Update IpAddress if it exists
            if (jsonObj["IpAddress"] != null)
            {
                jsonObj["IpAddress"] = ipAddress;
                modified = true;
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Updated IpAddress to {ipAddress}");
            }

            // Update Port if it exists (handle both string and number formats)
            if (jsonObj["Port"] != null)
            {
                // Check if original value was a string or number
                var originalPort = jsonObj["Port"];
                if (originalPort?.GetValueKind() == JsonValueKind.String)
                {
                    jsonObj["Port"] = port.ToString();
                }
                else
                {
                    jsonObj["Port"] = port;
                }
                modified = true;
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Updated Port to {port}");
            }

            if (modified)
            {
                // Write back with indentation to preserve readability
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, jsonNode.ToJsonString(options));
            }
            else
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} WARNING: No matching properties found to modify in server appsettings");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} ERROR modifying server appsettings: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Modifies client appsettings.json, preserving all existing settings while updating:
    /// - IpAddress (if exists)
    /// - Port (if exists)
    /// </summary>
    private static bool ModifyClientAppsettings(string path, string ipAddress, int port)
    {
        try
        {
            var jsonText = File.ReadAllText(path);
            var jsonNode = JsonNode.Parse(jsonText);
            
            if (jsonNode == null || jsonNode is not JsonObject jsonObj)
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} ERROR: Invalid JSON in {path}");
                return false;
            }

            var modified = false;

            // Update IpAddress if it exists
            if (jsonObj["IpAddress"] != null)
            {
                jsonObj["IpAddress"] = ipAddress;
                modified = true;
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Updated IpAddress to {ipAddress}");
            }

            // Update Port if it exists (handle both string and number formats)
            if (jsonObj["Port"] != null)
            {
                var originalPort = jsonObj["Port"];
                if (originalPort?.GetValueKind() == JsonValueKind.String)
                {
                    jsonObj["Port"] = port.ToString();
                }
                else
                {
                    jsonObj["Port"] = port;
                }
                modified = true;
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} Updated Port to {port}");
            }

            if (modified)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, jsonNode.ToJsonString(options));
            }
            else
            {
                Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} WARNING: No matching properties found to modify in client appsettings");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{AppsettingKeywords.LOG_PREFIX_APPSETTINGS_MODIFICATION} ERROR modifying client appsettings: {ex.Message}");
            return false;
        }
    }
}
