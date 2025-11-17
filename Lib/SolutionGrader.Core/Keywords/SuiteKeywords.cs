namespace SolutionGrader.Core.Keywords;

public static class SuiteKeywords
{
    // Reference FileKeywords for standard file names
    public const string HeaderFileName = "Header.xlsx";  // Same as FileKeywords.FileName_Header
    public const string DetailFileName = "Detail.xlsx";  // Same as FileKeywords.FileName_Detail

    // Sheet names for OLD format - supporting both plural and singular variations
    // The OLD format uses InputClients/OutputClients/OutputServers or InputClient/OutputClient/OutputServer
    // The NEW format uses completely different sheets: User, Client, Server, Network (see NewFormatDetailParser)
    
    // Old format variation 1 (plural)
    public const string Sheet_InputClients   = "InputClients";
    public const string Sheet_OutputClients  = "OutputClients";
    public const string Sheet_OutputServers  = "OutputServers";
    
    // Old format variation 2 (singular)
    public const string Sheet_InputClient    = "InputClient";
    public const string Sheet_OutputClient   = "OutputClient";
    public const string Sheet_OutputServer   = "OutputServer";
    
    public const string Sheet_Header         = "Header";        // fallback header
    public const string Sheet_QuestionMark   = "QuestionMark";  // for reading test case marks

    // New format sheet names (for NewFormatDetailParser)
    public const string Sheet_User           = "User";
    public const string Sheet_Client         = "Client";
    public const string Sheet_Server         = "Server";
    public const string Sheet_Network        = "Network";

    // InputClients
    public const string Col_IC_Stage    = "Stage";
    public const string Col_IC_Input    = "Input";
    public const string Col_IC_DataType = "DataType";
    public const string Col_IC_Action   = "Action";

    // Input DataType values - supporting both old and new formats
    public const string InputDataType_System     = "System";      // New format: for system-generated actions (Start, Connect)
    public const string InputDataType_UserInput  = "UserInput";   // New format: for user input
    public const string InputDataType_Integer    = "Integer";     // Old format: specific type
    public const string InputDataType_String     = "String";      // Old format: specific type
    public const string InputDataType_Empty      = "";            // Old format: empty for Connect action

    // OutputClients
    public const string Col_OC_Stage             = "Stage";
    public const string Col_OC_Method            = "Method";
    public const string Col_OC_DataResponse      = "DataResponse";
    public const string Col_OC_StatusCode        = "StatusCode";
    public const string Col_OC_Output            = "Output";
    public const string Col_OC_DataTypeMiddleware= "DataTypeMiddleWare";
    public const string Col_OC_ByteSize          = "ByteSize";

    // OutputServers
    public const string Col_OS_Stage             = "Stage";
    public const string Col_OS_Method            = "Method";
    public const string Col_OS_DataRequest       = "DataRequest";
    public const string Col_OS_Output            = "Output";
    public const string Col_OS_DataTypeMiddleware= "DataTypeMiddleware";
    public const string Col_OS_ByteSize          = "ByteSize";

    public const string Col_Generic_QuestionId   = "QuestionId";

    // Header config (fallback) - if future variations add Config/Type, hook here.
    public const string ConfigKey_Type = "Type";
    public const string ConfigKey_Protocol = "Protocol";
}
