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
            _singleInstance.Dispose(); // not ours to release
            _singleInstance = null;
            ShowRunning();
            Shutdown();
            return;
        }
        Updater.CleanUp(Ro3Emu.Setup.SetupHome.AppDir);
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>Lets the next launcher start (an update starts it before this one has exited). On the UI thread.</summary>
    public void ReleaseSingleInstance()
    {
        if (_singleInstance == null) return;
        _singleInstance.ReleaseMutex();
        _singleInstance.Dispose();
        _singleInstance = null;
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
