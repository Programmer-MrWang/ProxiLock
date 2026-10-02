using Microsoft.UI.Xaml;
using ProxiLock.Services;
using System.Threading;

namespace ProxiLock;

public partial class App : Application
{
    public static App CurrentApp => (App)Current;
    public static AppServices Services { get; private set; } = null!;
    public MainWindow MainWindow { get; private set; } = null!;
    public TrayWindow TrayWindow { get; private set; } = null!;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();
        Services = new AppServices();
        UnhandledException += (_, _) =>
        {
            try { ShutdownForProcessExit(); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Keep the tray icon in its own off-screen owner window, matching BetterLyrics.
        TrayWindow = new TrayWindow();
        TrayWindow.Activate();

        MainWindow = new MainWindow(Services);
        MainWindow.Activate();
        // Keep the shell alive while tray services initialize. It is hidden again
        // after runtime setup so startup XAML failures are not masked by a blank app.
        Services.Start(MainWindow);
        MainWindow.InitializeRuntime();
        if (Services.StartupWarning is null) MainWindow.HideShell();
        else MainWindow.ShowShell();
    }

    public void ShowSettings()
    {
        // A locked session must remain untouchable. The tray icon can still be
        // clicked, but bringing the settings window above the capture layer would
        // create a confusing and unsafe half-unlocked state.
        if (Services.LockCoordinator?.IsLocked == true)
            return;

        MainWindow.ShowShell();
        MainWindow.Activate();
    }

    public void ExitApplication()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        // The settings editor uses a 400 ms debounce timer.  Flush while the
        // coordinator and UI dispatcher are still alive, before disposing services.
        try { MainWindow?.FlushPendingSettings(); } catch { }
        try { MainWindow?.ShutdownResources(); } catch { }
        try { Services.Stop(); } catch { }
        try { MainWindow?.AllowClose(); } catch { }
        try { TrayWindow?.Close(); } catch { }
        try { MainWindow?.Close(); } catch { }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Cleanup path used by ProcessExit and unhandled exceptions. The last-chance
    /// editor flush only runs when the caller still owns the UI dispatcher; native
    /// overlay and hook resources are then stopped directly.
    /// </summary>
    public void ShutdownForProcessExit()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        try { MainWindow?.FlushPendingSettings(); } catch { }
        try { Services.Stop(); } catch { }
        try { MainWindow?.ShutdownResources(); } catch { }
    }
}
