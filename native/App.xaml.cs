using System.Windows;
using System.Threading;

namespace WraithView;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LegacyImport.Run();

        if (IsTriffViewRunning())
        {
            System.Windows.MessageBox.Show(
                "TriffView is running. Quit TriffView before starting WraithView so the two apps don't compete for previews and hotkeys.",
                "WraithView",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "WraithView.Standalone.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "WraithView is already running.",
                "WraithView",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown();
            return;
        }

        var window = new MainWindow(e.Args);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        base.OnExit(e);
    }

    internal static bool IsTriffViewRunning()
    {
        return System.Diagnostics.Process.GetProcessesByName("TriffView")
            .Concat(System.Diagnostics.Process.GetProcessesByName("TriffHud"))
            .Any(process =>
            {
                try
                {
                    return process.Id != Environment.ProcessId;
                }
                catch
                {
                    return false;
                }
            });
    }
}
