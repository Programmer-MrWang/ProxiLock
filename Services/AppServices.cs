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

    public string? StartupWarning { get; private set; }
    private bool _needsRecoverySave;

    public void Start(MainWindow window)
    {
        AppSettings loaded;
        try
        {
            loaded = SettingsStore.Load();
            StartupWarning = SettingsStore.LoadWarning;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A damaged or inaccessible file must not silently disable the policy. Start in
            // an explicit, visible recovery state so the user can repair the file and save a
            // fresh configuration from the settings page.
            loaded = new AppSettings();
            StartupWarning = "配置文件和备份无法读取，自动锁定已停用。请检查设置并重新保存。";
        }

        if (loaded.Usb.DeviceInstanceId?.Trim().StartsWith("DRIVE:", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Older versions accepted a drive letter as an identity. Never bind its current
            // occupant automatically: it may be a different disk. Leave the original file
            // intact until the user selects a verifiable device and saves the settings.
            loaded.Usb.DeviceInstanceId = null;
            const string migrationWarning = "旧版 U 盘设置只记录了盘符，无法可靠识别原设备。请重新选择原 U 盘并保存；在此之前 U 盘自动锁定不会生效。";
            StartupWarning = string.IsNullOrWhiteSpace(StartupWarning)
                ? migrationWarning
                : StartupWarning + "\n" + migrationWarning;
        }

        _needsRecoverySave = StartupWarning is not null;
        Settings = loaded;
        // Pass the already loaded object so a failing file is not read a second time and a
        // transient replacement cannot make the UI and coordinator start with different data.
        LockCoordinator = new LockCoordinator(SettingsStore, Notifications, window.DispatcherQueue, loaded);
        LockCoordinator.ApplySettings(Settings, persist: false);
        LockCoordinator.Start();
        // Do not remove an existing startup entry just because the settings file could not
        // be read. The default recovery settings are temporary and must not overwrite another
        // system integration while the user is repairing the configuration.
        if (!_needsRecoverySave && !AutoStart.Apply(Settings.AutoStart))
            StartupWarning = "开机自启动设置未能应用，请检查系统策略。";
        if (StartupWarning is not null) Notifications.Show("ProxiLock", StartupWarning);
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
        LockCoordinator.ApplySettings(settings, forcePersist: _needsRecoverySave);
        _needsRecoverySave = false;
        StartupWarning = null;
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
