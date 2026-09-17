using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using ProxiLock.Models;

namespace ProxiLock.Services;

public enum LockReason { None, Bluetooth, Usb, Idle }

/// <summary>
/// Evaluates the selected proximity/idle policy and owns the lock lifecycle.
/// Evaluation runs off the UI thread; all window and hook operations are marshalled
/// back to the dispatcher. A short arming phase prevents stale system idle time or
/// partially loaded settings from locking the first screen shown to the user.
/// </summary>
public sealed class LockCoordinator : IDisposable
{
    /// <summary>
    /// How long the unlock device must remain away before the screen is locked. Combined
    /// with the monitor's presence windows this guarantees the lock cannot oscillate, even
    /// if the device's advertising is erratic.
    /// </summary>
    private static readonly TimeSpan AbsenceConfirmation = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _store;
    private readonly NotificationService _notifications;
    private readonly LockOverlayManager _overlays = new();
    private readonly KeyboardInterceptor _keyboard = new();
    private readonly BluetoothMonitor _bluetooth = new();
    private readonly UsbMonitor _usb = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly object _settingsGate = new();
    private CancellationTokenSource? _cts;
    private AppSettings _settings;
    private volatile LockReason _reason;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _idleBaselineAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _bluetoothAbsentSince;
    private uint _lastInputTick;
    private volatile LockReason _manualUnlockOverride;
    private BluetoothStatus _bluetoothStatus = new(BluetoothPresence.Unknown, BluetoothReason.ScanIncomplete);
    private volatile bool _armed;
    private volatile bool _disposed;

    public bool IsLocked => _overlays.IsVisible;
    public BluetoothMonitor Bluetooth => _bluetooth;
    public UsbMonitor Usb => _usb;

    /// <summary>
    /// The most recent Bluetooth verdict and the evidence behind it. The settings page
    /// reports this rather than inferring a reason from the presence value alone, so
    /// "connected but the signal is too weak" is never described as "not connected".
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_settingsGate"/> rather than marked volatile: a struct cannot be
    /// volatile, and this is written by the monitor thread and read by the UI thread.
    /// </remarks>
    public BluetoothStatus BluetoothStatus
    {
        get { lock (_settingsGate) return _bluetoothStatus; }
    }

    /// <summary>
    /// The condition the user manually unlocked from, if the device has not since been seen
    /// healthy. The UI uses this to explain why the lock is not re-engaging.
    /// </summary>
    public LockReason ManualUnlockOverride => _manualUnlockOverride;

    public event EventHandler<bool>? LockStateChanged;

    public LockCoordinator(SettingsStore store, NotificationService notifications, DispatcherQueue dispatcher)
    {
        _store = store;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _settings = Normalize(store.Load());
        ResetIdleBaseline();
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _startedAt = DateTimeOffset.UtcNow;
        _armed = false;
        ResetIdleBaseline();
        _ = MonitorLoop(_cts.Token);
        // Do not depend solely on a view lifecycle callback to arm policy
        // evaluation. A delayed fallback keeps startup safe even when the tray
        // host opens the settings window lazily or a future UI omits Arm().
        _ = ArmAfterStartupAsync(_cts.Token);
    }

    /// <summary>Enables policy evaluation after the settings window has loaded.</summary>
    public void Arm()
    {
        _startedAt = DateTimeOffset.UtcNow;
        ResetIdleBaseline();
        _armed = true;
    }

    private async Task ArmAfterStartupAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (!_disposed && !_armed)
                Arm();
        }
        catch (OperationCanceledException) { }
    }

    public void ApplySettings(AppSettings settings, bool persist = true)
    {
        var normalized = Normalize(settings);
        LockMode previousMode;
        AppSettings previous;
        lock (_settingsGate)
        {
            previous = _settings;
            previousMode = _settings.LockMode;
            _settings = normalized;
        }

        // Settings are re-applied on every keystroke in the text fields. Skipping
        // identical values avoids rewriting the config and restarting the BLE
        // watcher (a stop/start cycle) for input that changed nothing.
        var changed = !AreEquivalent(previous, normalized);
        if (changed)
        {
            if (persist)
                _store.Save(normalized);
            _manualUnlockOverride = LockReason.None;
            // A newly selected device must start its confirmation window fresh rather than
            // inheriting the previous device's absence.
            _bluetoothAbsentSince = null;
        }

        // Reconcile the watcher with the desired mode. Start()/Stop() are idempotent,
        // so a no-op apply does not restart an already running scan.
        if (normalized.LockMode == LockMode.Bluetooth)
            _bluetooth.Start();
        else
            _bluetooth.Stop();

        // Switching to None is an explicit unlock operation. Other mode changes
        // are evaluated on the next monitor tick once the new policy is armed.
        if (normalized.LockMode == LockMode.None)
            Unlock();
        else if (previousMode != normalized.LockMode && IsLocked)
            Unlock();
        ResetIdleBaseline();
    }

    public void Lock(LockReason reason)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Lock(reason));
            return;
        }
        if (IsLocked || _manualUnlockOverride == reason) return;

        _reason = reason;
        try
        {
            _overlays.Show();
            _keyboard.Start(() => _dispatcher.TryEnqueue(KeyboardUnlock));
            // If the hook could not be installed, do not leave a user behind an
            // input-capture surface with no guaranteed escape path.
            if (!_keyboard.IsInstalled)
            {
                _overlays.Hide();
                _reason = LockReason.None;
                return;
            }
        }
        catch
        {
            _keyboard.Stop();
            _overlays.Hide();
            _reason = LockReason.None;
            return;
        }

        _notifications.Show("ProxiLock", "已锁定");
        LockStateChanged?.Invoke(this, true);
    }

    public void Unlock() => UnlockCore(preserveManualOverride: false);

    private void KeyboardUnlock()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(KeyboardUnlock);
            return;
        }

        // A manual shortcut unlock wins over the currently missing proximity
        // condition. The monitor clears this override as soon as the device is
        // observed healthy again, preventing an immediate lock/unlock loop.
        //
        // Idle deliberately does not participate: unlocking restarts the idle clock,
        // so the condition cannot still hold on the next tick and an override would
        // only suppress every later idle lock.
        if (_reason is LockReason.Bluetooth or LockReason.Usb)
        {
            _manualUnlockOverride = _reason;
        }
        UnlockCore(preserveManualOverride: true);
    }

    private void UnlockCore(bool preserveManualOverride)
    {
        if (_disposed && !IsLocked) return;
        if (!_dispatcher.HasThreadAccess)
        {
            // Re-enter through this same overload so the caller's intent is kept.
            _dispatcher.TryEnqueue(() => UnlockCore(preserveManualOverride));
            return;
        }
        if (!IsLocked)
        {
            ResetIdleBaseline();
            return;
        }

        _keyboard.Stop();
        _overlays.Hide();
        _idleBaselineAt = DateTimeOffset.UtcNow;
        if (!preserveManualOverride)
            _manualUnlockOverride = LockReason.None;
        _reason = LockReason.None;
        _notifications.Show("ProxiLock", "已解锁");
        LockStateChanged?.Invoke(this, false);
    }

    private async Task MonitorLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_armed && (DateTimeOffset.UtcNow - _startedAt).TotalSeconds >= 2)
                        Evaluate();
                }
                catch
                {
                    // Device APIs can fail transiently (Bluetooth radio reset,
                    // removable media disappearing). Keep the monitor alive and
                    // retry on the next tick.
                }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Evaluate()
    {
        if (IsLocked)
        {
            // The overlay manager owns its own Win32 message-loop thread. Ask it to
            // reconcile every tick so another topmost window cannot expose a gap and
            // monitor hot-plug is picked up. Existing coverage is repaired in place,
            // so input stays captured across a display change.
            _overlays.ReassertTopmost();
        }

        AppSettings settings;
        lock (_settingsGate) settings = _settings;

        // Evaluate the configured device on every tick, not only while the Bluetooth policy
        // is active. The settings page reports this verdict, and it must describe the device
        // the user is looking at rather than a stale value from whenever the policy last ran.
        // Evaluating once here also keeps the hysteresis state advancing consistently.
        var bluetoothStatus = string.IsNullOrWhiteSpace(settings.Bluetooth.DeviceAddress)
            ? new BluetoothStatus(BluetoothPresence.Unknown, BluetoothReason.ScanIncomplete)
            : _bluetooth.Evaluate(settings.Bluetooth.DeviceAddress, settings.Bluetooth.Threshold);
        lock (_settingsGate) _bluetoothStatus = bluetoothStatus;

        switch (settings.LockMode)
        {
            case LockMode.Bluetooth:
                EvaluateBluetooth(settings, bluetoothStatus);
                break;
            case LockMode.Usb:
                if (string.IsNullOrWhiteSpace(settings.Usb.DeviceInstanceId)) break;
                var usbPresent = _usb.IsPresent(settings.Usb.DeviceInstanceId);
                if (!usbPresent) Lock(LockReason.Usb);
                else
                {
                    if (_manualUnlockOverride == LockReason.Usb)
                    {
                        _manualUnlockOverride = LockReason.None;
                    }
                    if (IsLocked && _reason == LockReason.Usb) Unlock();
                }
                break;
            case LockMode.Idle:
                EvaluateIdle(settings);
                break;
            default:
                if (IsLocked) Unlock();
                break;
        }
    }

    private void EvaluateBluetooth(AppSettings settings, BluetoothStatus status)
    {
        // A Bluetooth policy without a selected device is incomplete. Treat it
        // as a no-op instead of locking a fresh install into an unresolvable state.
        if (string.IsNullOrWhiteSpace(settings.Bluetooth.DeviceAddress))
            return;

        switch (status.Presence)
        {
            case BluetoothPresence.Present:
                // The device is back, so a previous manual unlock no longer applies.
                _bluetoothAbsentSince = null;
                if (_manualUnlockOverride == LockReason.Bluetooth)
                    _manualUnlockOverride = LockReason.None;
                if (IsLocked && _reason == LockReason.Bluetooth) Unlock();
                break;

            case BluetoothPresence.Absent:
                // Before acting, require the device to stay away for a full confirmation
                // period. The monitor already smooths single samples; this is the last line
                // of defence, because an input-capturing lock that toggles is unusable.
                _bluetoothAbsentSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - _bluetoothAbsentSince.Value >= AbsenceConfirmation)
                    Lock(LockReason.Bluetooth);
                break;

            default:
                // Unknown: keep whatever state we are in rather than guessing. The absence
                // timer is intentionally left running so a brief gap does not reset it.
                break;
        }
    }

    private void EvaluateIdle(AppSettings settings)
    {
        if (IsLocked) return; // the idle clock is intentionally frozen while locked.
        var idleSeconds = GetIdleSeconds(out var inputTick);
        if (inputTick != 0 && inputTick != _lastInputTick)
        {
            _lastInputTick = inputTick;
            _idleBaselineAt = DateTimeOffset.UtcNow;
            idleSeconds = 0;
        }

        // Bound system idle by our own baseline. This prevents an old pre-launch
        // idle duration from immediately locking after the app is first opened.
        var sinceBaseline = Math.Max(0, (DateTimeOffset.UtcNow - _idleBaselineAt).TotalSeconds);
        var effectiveIdle = Math.Min(idleSeconds, sinceBaseline);
        if (effectiveIdle >= Math.Clamp(settings.Idle.Minutes, 1, 1440) * 60)
            Lock(LockReason.Idle);
    }

    private void ResetIdleBaseline()
    {
        _idleBaselineAt = DateTimeOffset.UtcNow;
        _ = GetIdleSeconds(out var inputTick);
        _lastInputTick = inputTick;
    }

    private static double GetIdleSeconds(out uint inputTick)
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            inputTick = 0;
            return 0;
        }
        inputTick = info.dwTime;
        // LASTINPUTINFO stores the low 32 bits of GetTickCount. Unsigned
        // subtraction preserves the correct interval across the 49-day wrap.
        var elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsedMs / 1000d;
    }

    private static bool AreEquivalent(AppSettings a, AppSettings b)
        => a.LockMode == b.LockMode
           && a.AutoStart == b.AutoStart
           && a.Idle.Minutes == b.Idle.Minutes
           && a.Bluetooth.Threshold == b.Bluetooth.Threshold
           && string.Equals(a.Bluetooth.DeviceAddress ?? string.Empty, b.Bluetooth.DeviceAddress ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(a.Usb.DeviceInstanceId ?? string.Empty, b.Usb.DeviceInstanceId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static AppSettings Normalize(AppSettings? source)
    {
        var settings = source ?? new AppSettings();
        settings.Bluetooth ??= new BluetoothSettings();
        settings.Usb ??= new UsbSettings();
        settings.Idle ??= new IdleSettings();
        settings.Idle.Minutes = Math.Clamp(settings.Idle.Minutes, 1, 1440);
        if (settings.Bluetooth.Threshold is int threshold)
            settings.Bluetooth.Threshold = Math.Clamp(threshold, -100, -20);
        if (!Enum.IsDefined(settings.LockMode)) settings.LockMode = LockMode.None;
        return settings;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts = null;
        // Both native resources have deterministic, thread-safe teardown. Do this
        // directly instead of enqueueing work that may never run during ProcessExit.
        try { _keyboard.Stop(); } catch { }
        try { _overlays.Hide(); } catch { }
        _reason = LockReason.None;
        _manualUnlockOverride = LockReason.None;
        try { _bluetooth.Stop(); } catch { }
        try { _bluetooth.Dispose(); } catch { }
        try { _keyboard.Dispose(); } catch { }
        try { _overlays.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
