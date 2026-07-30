using System.Windows;

namespace LLMAdShield.Windows;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan LastResortShutdownWait = TimeSpan.FromSeconds(5);
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        TryFinishShutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TryFinishShutdown();
        base.OnExit(e);
    }

    private void TryFinishShutdown()
    {
        if (_mainWindow is null)
        {
            return;
        }

        try
        {
            var shutdown = _mainWindow.ShutdownAsync();
            if (!shutdown.IsCompleted)
            {
                _ = shutdown.Wait(LastResortShutdownWait);
            }

            if (shutdown.IsCompleted)
            {
                shutdown.GetAwaiter().GetResult();
            }
        }
        catch
        {
            // The audio recovery journal remains durable for the next launch.
            // Process teardown removes any remaining overlay window.
        }
    }
}
