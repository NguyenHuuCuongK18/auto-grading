namespace SolutionGrader.Core.Keywords;

public static class StageKeywords
{
    public const string Setup = "SETUP";
    public const string Input = "INPUT";
    public const string Verify = "VERIFY";
    public const string Cleanup = "CLEANUP";
    public static readonly string[] All = [ Setup, Input, Verify, Cleanup ];
}
