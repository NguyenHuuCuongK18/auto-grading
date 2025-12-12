using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

namespace SolutionGrader.IntergrationTest.Helpers;

internal static class AutomationTestHelpers
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    public static AutomationElement? WaitForWindow(int processId, Func<AutomationElement, bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));

            foreach (AutomationElement window in windows)
            {
                if (predicate(window))
                {
                    return window;
                }
            }

            Thread.Sleep(DefaultPollInterval);
        }

        return null;
    }

    public static AutomationElement? WaitForDialogWindow(int processId, IntPtr ownerHandle, TimeSpan timeout)
    {
        return WaitForWindow(processId, element =>
        {
            if (element.Current.ControlType != ControlType.Window)
            {
                return false;
            }

            var handle = new IntPtr(element.Current.NativeWindowHandle);
            if (handle == IntPtr.Zero || handle == ownerHandle)
            {
                return false;
            }

            return true;
        }, timeout);
    }

    public static AutomationElement? FindElementByAutomationId(AutomationElement root, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    public static AutomationElement? FindSiblingButton(AutomationElement referenceElement, string buttonName)
    {
        var parent = TreeWalker.ControlViewWalker.GetParent(referenceElement);
        if (parent == null)
        {
            return null;
        }

        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, buttonName));

        return parent.FindFirst(TreeScope.Children, condition);
    }

    public static AutomationElement? FindFirstEdit(AutomationElement root)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    public static AutomationElement? FindDialogConfirmationButton(AutomationElement dialog)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, "OK"),
                new PropertyCondition(AutomationElement.NameProperty, "Select Folder"),
                new PropertyCondition(AutomationElement.NameProperty, "Open")));

        var button = dialog.FindFirst(TreeScope.Descendants, condition);
        if (button != null)
        {
            return button;
        }

        var buttonsFallback = dialog.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        return buttonsFallback.Count > 0 ? buttonsFallback[0] : null;
    }

    public static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObj) && patternObj is InvokePattern invokePattern)
        {
            invokePattern.Invoke();
            return;
        }

        throw new InvalidOperationException("The specified element does not support InvokePattern.");
    }

    public static void SetValue(AutomationElement element, string value)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) && patternObj is ValuePattern valuePattern)
        {
            valuePattern.SetValue(value);
            return;
        }

        throw new InvalidOperationException("The specified element does not support ValuePattern.");
    }

    public static string? GetValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) && patternObj is ValuePattern valuePattern)
        {
            return valuePattern.Current.Value;
        }

        return null;
    }

    public static void EnterPathInFolderDialog(AutomationElement dialog, string folderPath)
    {
        var edit = FindFirstEdit(dialog) ?? throw new InvalidOperationException("Unable to find folder input control inside the dialog.");
        SetValue(edit, folderPath);
    }

    public static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(DefaultPollInterval);
        }

        throw new TimeoutException(failureMessage);
    }
}
