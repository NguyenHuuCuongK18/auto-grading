namespace SolutionGrader.Core.Keywords;

public static class SuiteKeywords
{
    public const string HeaderFileName = "Header.xlsx";
    public const string DetailFileName = "Detail.xlsx";

    public const string Sheet_InputClients   = "InputClients";
    public const string Sheet_OutputClients  = "OutputClients";
    public const string Sheet_OutputServers  = "OutputServers";
    public const string Sheet_Header         = "Header";        // fallback header

    // InputClients
    public const string Col_IC_Stage    = "Stage";
    public const string Col_IC_Input    = "Input";
    public const string Col_IC_DataType = "DataType";
    public const string Col_IC_Action   = "Action";

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
}
