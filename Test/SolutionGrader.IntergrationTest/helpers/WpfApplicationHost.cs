using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using SolutionGrader.IntergrationTest.Helpers;

namespace SolutionGrader.IntergrationTest.Helpers;

internal sealed class WpfApplicationHost : IDisposable
{
    private readonly Process _process;
    private readonly AutomationElementHandle _mainWindow;

    private WpfApplicationHost(Process process, AutomationElementHandle mainWindow)
    {
        _process = process;
        _mainWindow = mainWindow;
    }

    public int ProcessId => _process.Id;
    public AutomationElement MainWindow => _mainWindow.Element;
    public IntPtr MainWindowHandle => _mainWindow.Handle;

    public static WpfApplicationHost Launch(string executablePath, string windowTitle, TimeSpan startupTimeout)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"UI executable not found at '{executablePath}'.");
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to launch the SolutionGrader UI process.");

        var mainWindow = AutomationTestHelpers.WaitForWindow(
            process.Id,
            element => string.Equals(element.Current.Name, windowTitle, StringComparison.OrdinalIgnoreCase),
            startupTimeout)
            ?? throw new InvalidOperationException($"Failed to find main window '{windowTitle}' within the expected timeout.");

        return new WpfApplicationHost(process, new AutomationElementHandle(mainWindow));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                if (!_process.CloseMainWindow())
                {
                    _process.Kill(true);
                    return;
                }

                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(true);
                }
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _process.Dispose();
            _mainWindow.Dispose();
        }
    }

    private sealed class AutomationElementHandle : IDisposable
    {
        public AutomationElementHandle(AutomationElement element)
        {
            Element = element ?? throw new ArgumentNullException(nameof(element));
            Handle = new IntPtr(element.Current.NativeWindowHandle);
        }

        public AutomationElement Element { get; }
        public IntPtr Handle { get; }

        public void Dispose()
        {
            // AutomationElement does not implement IDisposable, but this class exists
            // to keep API symmetry and allow for future cleanup if needed.
        }
    }
}
