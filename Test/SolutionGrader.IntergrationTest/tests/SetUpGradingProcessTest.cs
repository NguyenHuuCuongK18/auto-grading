using System;
using System.Threading;
using NUnit.Framework;
using SolutionGrader.IntergrationTest.Helpers;

namespace SolutionGrader.IntergrationTest.tests;

[Apartment(ApartmentState.STA)]
internal class SetUpGradingProcessTest
{
    private const string WindowTitle = "Auto Grading System - Setup";

    [Test]
    public void BrowseTestKitFolder_ShouldDisplaySelectedPathInTextBox()
    {
        RunFieldInjectionScenario("txtTestKitFolder");
    }

    [Test]
    public void EnterSubmitFolder_ShouldDisplayInjectedPath()
    {
        RunFieldInjectionScenario("txtSubmitFolder");
    }

    [Test]
    public void EnterSaveFolder_ShouldDisplayInjectedPath()
    {
        RunFieldInjectionScenario("txtSaveFolder");
    }

    private static void RunFieldInjectionScenario(string automationId)
    {
        var uiExecutable = TestEnvironmentPaths.GetUiExecutablePath();
        var sampleFolder = TestEnvironmentPaths.GetSampleTestKitFolder();

        using var host = WpfApplicationHost.Launch(uiExecutable, WindowTitle, TimeSpan.FromSeconds(40));

        var targetTextBox = AutomationTestHelpers.FindElementByAutomationId(host.MainWindow, automationId)
                             ?? throw new AssertionException($"Unable to find the textbox with automation id '{automationId}'.");

        AutomationTestHelpers.SetValue(targetTextBox, sampleFolder);

        AutomationTestHelpers.WaitUntil(
            () => string.Equals(AutomationTestHelpers.GetValue(targetTextBox), sampleFolder, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            $"The textbox '{automationId}' did not update with the injected path in time.");

        Assert.That(AutomationTestHelpers.GetValue(targetTextBox), Is.EqualTo(sampleFolder));
    }
}
