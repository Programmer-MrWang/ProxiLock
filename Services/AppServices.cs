using ProxiLock.Models;

namespace ProxiLock.Services;

public sealed class AppServices
{
    public SettingsStore SettingsStore { get; } = new();
    public AutoStartService AutoStart { get; } = new();
    public NotificationService Notifications { get; } = new();
    public LockCoordinator LockCoordinator { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;

    public void Start(MainWindow window)
    {
        Settings = SettingsStore.Load();
        LockCoordinator = new LockCoordinator(SettingsStore, Notifications, window.DispatcherQueue);
        LockCoordinator.ApplySettings(Settings, persist: false);
        LockCoordinator.Start();
        AutoStart.Apply(Settings.AutoStart);
    }

    public void Apply(AppSettings settings)
    {
        Settings = settings;
        LockCoordinator.ApplySettings(settings);
        AutoStart.Apply(settings.AutoStart);
    }

    public void Stop()
    {
        LockCoordinator?.Dispose();
        Notifications.Dispose();
    }
}
