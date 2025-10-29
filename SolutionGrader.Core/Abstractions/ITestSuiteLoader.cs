namespace SolutionGrader.Core.Abstractions;

using SolutionGrader.Core.Domain.Models;

public interface ITestSuiteLoader
{
    SuiteDefinition Load(string suitePathOrHeaderXlsx);
}
