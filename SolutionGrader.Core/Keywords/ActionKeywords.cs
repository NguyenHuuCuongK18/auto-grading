namespace SolutionGrader.Core.Keywords;

public static class ActionKeywords
{
    public const string ClientStart = "CLIENTSTART";
    public const string ServerStart = "SERVERSTART";
    public const string ClientClose = "CLIENTCLOSE";
    public const string ServerClose = "SERVERCLOSE";
    public const string KillAll     = "KILL_ALL";
    public const string ClientInput = "CLIENT_INPUT";

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

    public static readonly string[] All =
    [
        ClientStart, ServerStart, ClientClose, ServerClose, KillAll, ClientInput,
        RunClient, RunServer,
        Wait, HttpRequest, AssertText, AssertFileExists,
        CaptureFile, CompareFile, CompareText, CompareJson, CompareCsv, TcpRelay
    ];
}
