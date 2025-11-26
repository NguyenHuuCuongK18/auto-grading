namespace SolutionGrader.Core.Keywords;

public static class ActionKeywords
{
    // Process management actions
    public const string ClientStart = "CLIENTSTART";
    public const string ServerStart = "SERVERSTART";
    public const string ClientClose = "CLIENTCLOSE";
    public const string ServerClose = "SERVERCLOSE";
    public const string KillAll     = "KILL_ALL";
    
    // Input actions
    public const string ClientInput = "CLIENT_INPUT";
    
    // Legacy action names (for backward compatibility with old Detail.xlsx format)
    public const string Connect = "Connect";        // Old format for starting processes (mapped to Start)
    public const string Start = "Start";            // New format for starting processes
    public const string Input = "Input";            // New format for client input (alternative to CLIENT_INPUT)

    public const string RunClient   = "RUN_CLIENT";
    public const string RunServer   = "RUN_SERVER";

    public const string Wait            = "WAIT";
    public const string HttpRequest     = "HTTP_REQUEST";
    public const string AssertText      = "ASSERT_TEXT";
    public const string AssertFileExists= "ASSERT_FILE_EXISTS";

    public const string CaptureFile     = "CAPTURE_FILE";
    public const string CompareFile     = "COMPARE_FILE";
    public const string CompareText     = "COMPARE_TEXT";
    public const string CompareJson     = "COMPARE_JSON";
    public const string CompareCsv      = "COMPARE_CSV";
    public const string TcpRelay        = "TCP_RELAY";
    
    // Network flow validation actions for TCP handshake grading
    /// <summary>Compare TCP flags (SYN, SYN-ACK, ACK, PSH-ACK, FIN-ACK)</summary>
    public const string CompareNetworkFlow = "COMPARE_NETWORK_FLOW";

    public static readonly string[] All =
    [
        ClientStart, ServerStart, ClientClose, ServerClose, KillAll, ClientInput,
        Connect, Start, Input,
        RunClient, RunServer,
        Wait, HttpRequest, AssertText, AssertFileExists,
        CaptureFile, CompareFile, CompareText, CompareJson, CompareCsv, TcpRelay,
        CompareNetworkFlow
    ];
}
