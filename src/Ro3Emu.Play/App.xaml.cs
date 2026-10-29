using System.Windows;

namespace Ro3Emu.Play;

/// <summary>One launcher window at a time: a second start brings the first one forward instead.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\Ro3Emu.Play", out bool first);
        if (!first)
        {
            ShowRunning();
            Shutdown();
            return;
        }
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>The running launcher's window, restored and brought to the front.</summary>
    private static void ShowRunning()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RO3EmuPlay"))
            if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SwRestore);
                NativeMethods.SetForegroundWindow(p.MainWindowHandle);
            }
    }
}
