namespace SolutionGrader.Core.Keywords;

/// <summary>
/// Centralized file-related constants including extensions, folder names, and file names
/// </summary>
public static class FileKeywords
{
    // File Extensions
    public const string Extension_Excel = ".xlsx";
    public const string Extension_Json = ".json";
    public const string Extension_Csv = ".csv";
    public const string Extension_Sql = ".sql";
    public const string Extension_Diff = ".diff.txt";
    public const string Extension_Log = ".log";
    public const string Extension_Jsonl = ".jsonl";

    // Standard File Names
    public const string FileName_Header = "Header.xlsx";
    public const string FileName_Detail = "Detail.xlsx";
    public const string FileName_GradeDetail = "GradeDetail.xlsx";
    public const string FileName_FailedTestDetail = "FailedTestDetail.xlsx";
    public const string FileName_OverallSummary = "OverallSummary.xlsx";
    public const string FileName_AppSettings = "appsettings.json";
    public const string FileName_ServerLog = "server.log";
    public const string FileName_ClientLog = "client.log";
    public const string FileName_Grades = "grades.csv";
    public const string FileName_DetailedLog = "detailed_log.jsonl";

    // Folder Names
    public const string Folder_Expected = "expected";
    public const string Folder_Actual = "actual";
    public const string Folder_Clients = "clients";
    public const string Folder_Servers = "servers";
    public const string Folder_ServersRequest = "servers-req";
    public const string Folder_ServersResponse = "servers-resp";
    public const string Folder_Mismatches = "mismatches";

    // File Name Patterns
    public const string Pattern_Result = "{0}_Result.xlsx";
    public const string Pattern_StageDiff = "stage_{0}.diff.txt";
    public const string Pattern_GradeResult = "GradeResult_{0}";

    // Special Values
    public const string Value_MissingPlaceholder = "-";
    public const string Value_UnknownQuestion = "Unknown";
}
