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
    private const string CONFIG_SHEET = "Config";
    private const string CONNECTION_TYPE = "Type";
    private const string SQL_EXPRESS = "SQLEXPRESS";
    private const string CONNECTION_STRINGS = "ConnectionStrings";
    private const string MY_CNN = "MyCnn";
    private const string IP_ADDRESS = "IpAddress";
    private const string PORT = "Port";
    private const string SQL_SERVER = "SqlServer";
    private const string DATABASE_NAME = "Database";
    private const string USER_ID = "Username";
    private const string PASSWORD = "Password";
    private const string TRUSTED_CONNECTION = "Trusted_Connection";
    private const string TRUST_SERVER_CERTIFICATE = "TrustServerCertificate";
    private const string APPSETTINGS = "appsettings";
}
