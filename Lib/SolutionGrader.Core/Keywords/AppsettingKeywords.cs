using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SolutionGrader.Core.Keywords;

/// <summary>
/// Keywords and constants used for building appsettings.
/// Centralizes all grading-related string constants for maintainability.
/// </summary>
public static class AppsettingKeywords
{
    public const string HEADER = FileKeywords.FileName_Header;
    public const string CONFIG_SHEET = "Config";
    public const string CONNECTION_TYPE = "Type";
    public const string SQL_EXPRESS = "SQLEXPRESS";
    public const string CONNECTION_STRINGS = "ConnectionStrings";
    public const string MY_CNN = "MyCnn";
    public const string IP_ADDRESS = "IpAddress";
    public const string PORT = "Port";
    public const string SQL_SERVER = "SqlServer";
    public const string DATABASE_NAME = "Database";
    public const string USER_ID = "Username";
    public const string PASSWORD = "Password";
    public const string TRUSTED_CONNECTION = "Trusted_Connection";
    public const string TRUST_SERVER_CERTIFICATE = "TrustServerCertificate";
    public const string APPSETTINGS = "appsettings";
    
    // Connection string format: lowercase keywords for consistency with SqlClient
    public const string CONN_STR_SERVER = "server";
    public const string CONN_STR_DATABASE = "database";
    public const string CONN_STR_UID = "uid";
    public const string CONN_STR_PWD = "pwd";
    public const string CONN_STR_TRUST_CERT = "TrustServerCertificate";
    
    // Server instance keywords
    public const string SERVER_LOCAL_PREFIX = ".\\";
    public const string SERVER_LOCAL_KEYWORD = "(local)";
    public const string SERVER_LOCALHOST = "localhost";
    
    // Database defaults
    public const string DEFAULT_SQL_SERVER_INSTANCE = ".\\SQLEXPRESS";
    public const string DEFAULT_SQL_SERVER_DOCKER = "localhost,1433";
    public const string DEFAULT_DATABASE_NAME = "Library";
    public const string DEFAULT_USERNAME = "sa";
    public const string DEFAULT_PASSWORD = "sa";
    public const string MASTER_DATABASE = "master";
    
    // Default connection string template
    // Format: server={server};database={database};uid={username};pwd={password};TrustServerCertificate=true
    public const string DEFAULT_CONNECTION_STRING_TEMPLATE = "server={0};database={1};uid={2};pwd={3};TrustServerCertificate=true";
    
    // Protocol values
    public const string PROTOCOL_HTTP = "HTTP";
    public const string PROTOCOL_TCP = "TCP";
    public const string HTTP_LOCALHOST = "http://localhost";
    public const string TCP_LOCALHOST = "127.0.0.1";
    
    // Docker constants
    public const string DOCKER_COMMAND = "docker";
    public const string DOCKER_CONTAINER_NAME = "sqlserver-test";
    public const string DOCKER_SQLCMD_PATH = "/opt/mssql-tools18/bin/sqlcmd";
    public const string DOCKER_TMP_SCRIPT_PATH = "/tmp/db_reset.sql";
    public const string DOCKER_SA_PASSWORD = "YourStrong@Passw0rd";
    
    // SQL error levels
    public const string SQL_ERROR_LEVEL_16 = "Level 16";
    
    // Database field names
    public const string DB_FIELD_INITIAL_CATALOG = "Initial Catalog";
    
    // Log prefixes
    public const string LOG_PREFIX_APPSETTINGS = "[Appsettings]";
    public const string LOG_PREFIX_DATABASE = "[Database]";
    public const string LOG_PREFIX_APPSETTINGS_CREATION = "[AppsettingsCreation]";
    
    // Messages
    public const string MSG_GENERATING_FROM_HEADER = "Generating appsettings.json from Header.xlsx configuration...";
    public const string MSG_CONFIGURED_MIDDLEWARE = "Configured middleware - Proxy: {0}, Server: {1}";
    public const string MSG_RESETTING_DATABASE = "Resetting database from script...";
    public const string MSG_DATABASE_RESET_SUCCESS = "Database reset completed successfully";
    public const string MSG_DATABASE_RESET_FAILED = "Warning: Could not execute database reset script";
    public const string MSG_DATABASE_RESET_ERROR = "Warning: Database reset failed: {0}";
    public const string MSG_LOCAL_DB_RESET_ERROR = "Local database reset error: {0}";
    public const string MSG_DOCKER_DB_RESET_ERROR = "Docker database reset error: {0}";
    public const string MSG_NO_INITIAL_CATALOG = "Warning: Connection string does not specify a database name (Initial Catalog).";
    public const string MSG_SQL_WARNINGS_NONFATAL = "SQL execution had warnings (non-fatal)";
    public const string MSG_GENERATED_SERVER_APPSETTINGS = "Generated server appsettings.json at: {0}";
    public const string MSG_GENERATED_CLIENT_APPSETTINGS = "Generated client appsettings.json at: {0}";
    public const string MSG_ALLOCATED_PORTS = "Allocated ports - Proxy: {0}, Server: {1}";
    public const string MSG_SCRIPT_SELF_MANAGING = "Script contains database management commands, executing from master context...";
    public const string MSG_MANUAL_DB_MANAGEMENT = "Using manual database drop/create/apply...";
    public const string MSG_APPSETTINGS_REPLACE_FAILED = "Failed to replace appsettings.";
}
