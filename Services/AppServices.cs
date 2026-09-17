using ProxiLock.Models;

namespace ProxiLock.Services;

/// <summary>
/// Outcome of applying a settings change. The policy itself is saved atomically, so the
/// only part that can partially fail is the optional system integration.
/// </summary>
public readonly record struct ApplyResult(bool AutoStartApplied);

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

    /// <summary>
    /// Applies and persists a settings change.
    /// </summary>
    /// <remarks>
    /// The coordinator saves the configuration before it publishes the new in-memory
    /// settings, so a failed write throws here while the running policy still matches the
    /// file and the change stays retryable. Autostart is a separate system-integration step:
    /// its failure is reported rather than hidden, but it does not invalidate the saved policy.
    /// </remarks>
    public ApplyResult Apply(AppSettings settings)
    {
        LockCoordinator.ApplySettings(settings);
        Settings = settings;
        var autoStartApplied = AutoStart.Apply(settings.AutoStart);
        return new ApplyResult(autoStartApplied);
    }

    public void Stop()
    {
        LockCoordinator?.Dispose();
        Notifications.Dispose();
    }
}
