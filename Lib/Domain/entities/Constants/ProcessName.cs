using System;
using System.IO;

namespace Domain.Entities.Constants
{
    public class ProcessName
    {
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\.."));

        //public static readonly string TestCaseExecution = Path.Combine(ProjectRoot, @"Application\TestCaseExecution\bin\Debug") + @"\TestCaseExecution.exe";
        //public static readonly string TestKitReader = Path.Combine(ProjectRoot, @"Application\TestKitReader\bin\Debug") + @"\TestKitReader.exe";
        //public static readonly string SolutionSummary = Path.Combine(ProjectRoot, @"Application\SolutionSummary\bin\Debug") + @"\SolutionSummary.exe";
        //public static readonly string GradingSolution = Path.Combine(ProjectRoot, @"Application\GradingSolution\bin\Debug") + @"\GradingSolution.exe";
        public static readonly string EnvironmentManager = Path.Combine(ProjectRoot, @"EnvironmentManager\bin\Debug\net8.0") + @"\EnvironmentManager.exe";

        //public static readonly string ChromeDriverPortablePath = Path.Combine(ProjectRoot, @"GoogleChromePortable64\App\Chrome-bin") + @"\chrome.exe";
        //public static readonly string FirefoxDriverPortablePath = Path.Combine(ProjectRoot, @"FirefoxPortable\App\Firefox") + @"\firefox.exe";
        //public static readonly string GeckoDriverPortablePath = Path.Combine(ProjectRoot, @"FirefoxPortable") + @"\geckodriver.exe";

        //public static readonly string DotNetEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\DotNetEnvironmentManagerHelper\bin\Debug") + @"\DotNetEnvironmentManagerHelper.dll";
        public static readonly string DotNetEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"EnvironmentManagerHelper\DotNetEnvironmentManagerHelper\bin\Debug\net8.0") + @"\DotNetEnvironmentManagerHelper.dll";
        //public static readonly string JavaJspEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\JavaJspEnvironmentManagerHelper\bin\Debug") + @"\JavaJspEnvironmentManagerHelper.dll";
        //public static readonly string JavaSpringEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\JavaSpringEnvironmentManagerHelper\bin\Debug") + @"\JavaSpringEnvironmentManagerHelper.dll";
        //public static readonly string NodeJsEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\NodeJsEnvironmentManagerHelper\bin\Debug") + @"\NodeJsEnvironmentManagerHelper.dll";
        //public static readonly string PythonDjangoEnvironmentManagerHelperPath = Path.Combine(ProjectRoot, @"Application\EnvironmentManagerHelper\PythonDjangoEnvironmentManagerHelper\bin\Debug") + @"\PythonDjangoEnvironmentManagerHelper.dll";

    }
}
