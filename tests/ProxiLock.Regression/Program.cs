using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using ProxiLock.Models;
using ProxiLock.Services;

// Deterministic regression runner: no application startup, radio Start(), USB scan,
// global keyboard hook, overlay windows, registry writes, or real user configuration.
const string Address = "11:22:33:44:55:66";
var tests = new (string Name, Action Run)[]
{
    ("Settings: missing files are a fresh installation", () => WithConfig((store, _) =>
    {
        Equal(LockMode.None, store.Load().LockMode);
        Equal<string?>(null, store.LoadWarning);
    })),
    ("Settings: malformed primary without backup is reported", () => WithConfig((store, path) =>
    {
        File.WriteAllText(path, "{broken");
        Throws<InvalidDataException>(() => store.Load());
    })),
    ("Settings: corrupt primary recovers backup and preserves it when saved", () => WithConfig((store, path) =>
    {
        store.Save(Policy(LockMode.Idle, minutes: 9));
        var good = File.ReadAllBytes(path);
        File.WriteAllBytes(path + ".bak", good);
        File.WriteAllText(path, "{broken");
        Equal(9, store.Load().Idle.Minutes);
        Check(store.LoadWarning is not null, "Recovery must be visible");
        store.Save(Policy(LockMode.Idle, minutes: 12));
        Check(good.SequenceEqual(File.ReadAllBytes(path + ".bak")), "Good backup was replaced by corrupt primary");
        Equal(12, store.Load().Idle.Minutes);
    })),
    ("Settings: invalid numeric enum cannot silently disable locking", () => WithConfig((store, path) =>
    {
        File.WriteAllText(path, "{\"LockMode\":999}");
        Throws<InvalidDataException>(() => store.Load());
    })),
    ("Settings: oversized input is rejected", () => WithConfig((store, path) =>
    {
        File.WriteAllText(path, new string(' ', 65537));
        Throws<InvalidDataException>(() => store.Load());
    })),
    ("Settings: oversized save preserves existing configuration", () => WithConfig((store, path) =>
    {
        store.Save(Policy(LockMode.Idle));
        var before = File.ReadAllBytes(path);
        var huge = Policy(LockMode.Usb, new string('x', 65537));
        Throws<InvalidDataException>(() => store.Save(huge));
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Oversized save damaged primary");
    })),
    ("Settings: UTF-8 BOM files remain readable", () => WithConfig((store, path) =>
    {
        File.WriteAllText(path, "{\"LockMode\":\"Idle\",\"Idle\":{\"Minutes\":13}}", new UTF8Encoding(true));
        Equal(13, store.Load().Idle.Minutes);
    })),
    ("Settings: replacement failure preserves primary and cleans temporary file", () => WithConfig((store, path) =>
    {
        store.Save(Policy(LockMode.Idle, minutes: 7));
        var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => store.Save(Policy(LockMode.Idle, minutes: 8)));
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Failed save damaged primary");
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
    })),
    ("Settings: inaccessible primary is not treated as a new installation", () => WithConfig((store, path) =>
    {
        Directory.CreateDirectory(path);
        Throws<InvalidDataException>(() => store.Load());
    })),
    ("Settings: valid save keeps the previous generation", () => WithConfig((store, path) =>
    {
        store.Save(Policy(LockMode.Idle, minutes: 4));
        store.Save(Policy(LockMode.Idle, minutes: 8));
        Equal(4, new SettingsStore(path + ".bak").Load().Idle.Minutes);
        Equal(8, store.Load().Idle.Minutes);
    })),
    ("USB: drive-letter reuse cannot validate an old hardware association", () =>
    {
        Check(!UsbMonitor.IsHardwareMatch("ABCDEF12", "11223344", false), "Cached wrong disk accepted");
        Check(!UsbMonitor.IsHardwareMatch("ABCDEF12", "11223344", true), "Fresh mismatched association accepted");
        Check(UsbMonitor.IsHardwareMatch("abcdef12", "ABCDEF12", false), "Same disk rejected");
    }),
    ("USB: unverified cached hardware cannot act as a credential", () =>
    {
        Check(!UsbMonitor.IsHardwareMatch(null, null, false), "No-serial cache accepted");
        Check(!UsbMonitor.IsHardwareMatch("ABCDEF12", null, false), "Partial cached identity accepted");
        Equal<object?>(null, CallStatic(typeof(UsbMonitor), "ResolveInstanceId", null, null));
        Equal("VOLUME:ABCDEF12", CallStatic(typeof(UsbMonitor), "ResolveInstanceId", "ABCDEF12", null));
    }),
    ("Bluetooth: connected devices still obey measured distance and hysteresis", () =>
    {
        using var monitor = new BluetoothMonitor();
        var state = AddBluetooth(monitor);
        Set(state, "Rssi", (short)-55);
        Equal(BluetoothPresence.Present, monitor.Evaluate(Address, -60).Presence);
        Set(state, "Rssi", (short)-63);
        Equal(BluetoothPresence.Present, monitor.Evaluate(Address, -60).Presence);
        Set(state, "Rssi", (short)-66);
        Equal(BluetoothPresence.Absent, monitor.Evaluate(Address, -60).Presence);
        Set(state, "Rssi", (short)-63);
        Equal(BluetoothPresence.Absent, monitor.Evaluate(Address, -60).Presence);
    }),
    ("Bluetooth: stale signal expires even while connection evidence stays fresh", () =>
    {
        using var monitor = new BluetoothMonitor();
        var state = AddBluetooth(monitor);
        Equal(BluetoothPresence.Present, monitor.Evaluate(Address, -60).Presence);
        Set(state, "RssiAt", DateTimeOffset.UtcNow.AddSeconds(-30));
        var status = monitor.Evaluate(Address, -60);
        Equal(BluetoothPresence.Absent, status.Presence);
        Equal(BluetoothReason.NoRecentSignal, status.Reason);
    }),
    ("Bluetooth: snapshots never show old RSSI as a current measurement", () =>
    {
        using var monitor = new BluetoothMonitor();
        var state = AddBluetooth(monitor);
        Set(state, "RssiAt", DateTimeOffset.UtcNow.AddSeconds(-10));
        Equal((short)-127, monitor.Snapshot().Single().Rssi);
        Check(monitor.Snapshot().Single().IsConnected, "Connection should remain separate from RSSI");
    }),
    ("Bluetooth: stop clears prior-session observations and pairing", () =>
    {
        using var monitor = new BluetoothMonitor();
        AddBluetooth(monitor);
        Set(monitor, "_hasScanned", true);
        monitor.Stop();
        Equal(0, monitor.Snapshot().Count);
        Check(!monitor.HasScanned && !monitor.IsPaired(Address), "Session evidence survived Stop");
        Equal(BluetoothPresence.Unknown, monitor.Evaluate(Address, null).Presence);
    }),
    ("Bluetooth: expired connection status cannot keep a classic device present", () =>
    {
        using var monitor = new BluetoothMonitor();
        var state = AddBluetooth(monitor);
        Set(state, "HasReportedRssi", false);
        Set(state, "AdvertisedAt", null);
        Set(state, "ConnectionStatusAt", DateTimeOffset.UtcNow.AddSeconds(-30));
        Equal(BluetoothPresence.Absent, monitor.Evaluate(Address, null).Presence);
    }),
    ("Bluetooth: pairing updates stable identity and fallback signal bindings", () =>
    {
        var row = new BluetoothDeviceInfo { IsRandomAddress = true };
        var properties = new List<string?>();
        row.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        Check(!row.IsStable, "Unpaired random identity was accepted");
        row.IsPaired = true;
        Check(row.IsStable && properties.Contains("IsStable") && properties.Contains("SignalText"), "Pairing did not refresh bindings");
    }),
    ("Keyboard: injected shortcut cannot unlock", () => WithKeyboard((keyboard, signal) =>
    {
        Key(keyboard, 0xA2, 0x10); Key(keyboard, 0xA0, 0x10); Key(keyboard, 0x4C, 0x10);
        Equal(0, Get<int>(keyboard, "_unlockPosted"));
        Check(!signal.IsSet, "Injected input unlocked");
    })),
    ("Keyboard: injected modifiers cannot combine with a physical L", () => WithKeyboard((keyboard, _) =>
    {
        Key(keyboard, 0xA2, 0x10); Key(keyboard, 0xA0, 0x10); Key(keyboard, 0x4C);
        Equal(0, Get<int>(keyboard, "_unlockPosted"));
    })),
    ("Keyboard: physical emergency shortcut still unlocks", () => WithKeyboard((keyboard, signal) =>
    {
        Key(keyboard, 0xA2); Key(keyboard, 0xA0); Key(keyboard, 0x4C);
        Check(signal.Wait(TimeSpan.FromSeconds(2)), "Physical shortcut did not unlock");
        Equal(1, Get<int>(keyboard, "_unlockPosted"));
    })),
    ("Coordinator: observations for a replaced USB cannot lock the new policy", () => WithCoordinator((coordinator, _, _, _) =>
    {
        var old = Get<AppSettings>(coordinator, "_settings");
        coordinator.ApplySettings(Policy(LockMode.Usb, "USB-B"), persist: false);
        Observe(coordinator, old, false);
        Equal(0, Overlay(coordinator).ShowCalls);
        Observe(coordinator, Get<AppSettings>(coordinator, "_settings"), false);
        Equal(1, Overlay(coordinator).ShowCalls);
    })),
    ("Coordinator: delayed queued lock is discarded after identity changes", () => WithCoordinator((coordinator, dispatcher, _, _) =>
    {
        dispatcher.HasThreadAccess = false;
        coordinator.Lock(LockReason.Usb);
        dispatcher.HasThreadAccess = true;
        coordinator.ApplySettings(Policy(LockMode.Usb, "USB-B"), persist: false);
        dispatcher.Drain();
        Equal(0, Overlay(coordinator).ShowCalls);
    })),
    ("Coordinator: rejected dispatcher enqueue can be retried", () => WithCoordinator((coordinator, dispatcher, _, _) =>
    {
        dispatcher.HasThreadAccess = false;
        dispatcher.AcceptEnqueue = false;
        coordinator.Lock(LockReason.Usb);
        Equal(0, Overlay(coordinator).ShowCalls);
        dispatcher.AcceptEnqueue = true;
        coordinator.Lock(LockReason.Usb);
        dispatcher.Drain();
        Equal(1, Overlay(coordinator).ShowCalls);
    })),
    ("Coordinator: caller mutation cannot silently replace the active device", () => WithCoordinator((coordinator, _, _, _) =>
    {
        var requested = Policy(LockMode.Usb, "USB-B");
        coordinator.ApplySettings(requested, persist: false);
        requested.Usb.DeviceInstanceId = "USB-C";
        Equal("USB-B", Get<AppSettings>(coordinator, "_settings").Usb.DeviceInstanceId);
    })),
    ("Coordinator: forced recovery save persists even unchanged defaults", () => WithCoordinator((coordinator, _, store, _) =>
    {
        coordinator.ApplySettings(Policy(LockMode.None), persist: false);
        coordinator.ApplySettings(Policy(LockMode.None), forcePersist: true);
        Equal(LockMode.None, store.Load().LockMode);
        Check(File.Exists(Get<string>(store, "_path")), "No-op recovery save was skipped");
    })),
    ("Coordinator: failed persistence leaves active settings unchanged", () => WithCoordinator((coordinator, _, store, _) =>
    {
        var path = Get<string>(store, "_path");
        store.Save(Policy(LockMode.Usb, "USB-A"));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => coordinator.ApplySettings(Policy(LockMode.Usb, "USB-B")));
        Equal("USB-A", Get<AppSettings>(coordinator, "_settings").Usb.DeviceInstanceId);
    })),
    ("Coordinator: display refresh failure releases lock even before UI dispatch", () => WithCoordinator((coordinator, dispatcher, _, notifications) =>
    {
        SimulateRefreshFailure(coordinator, dispatcher);
        Check(!coordinator.IsLocked && !Overlay(coordinator).IsVisible, "Failed display coverage remained locked");
        Check(Overlay(coordinator).HideCalls > 0, "Refresh failure did not release native resources");
        dispatcher.Drain();
        Equal(1, notifications.Messages.Count);
    })),
    ("Coordinator: delayed display failure cannot report a newer lock as unlocked", () => WithCoordinator((coordinator, dispatcher, _, notifications) =>
    {
        var unlockEvents = 0;
        coordinator.LockStateChanged += (_, locked) => { if (!locked) unlockEvents++; };
        SimulateRefreshFailure(coordinator, dispatcher);
        Set(coordinator, "_lockSession", Get<long>(coordinator, "_lockSession") + 1);
        Set(coordinator, "_reason", LockReason.Idle);
        Overlay(coordinator).IsVisible = true;
        dispatcher.Drain();
        Check(coordinator.IsLocked, "Old failure unlocked newer session");
        Equal(0, unlockEvents);
        Equal(0, notifications.Messages.Count);
    })),
    ("Coordinator: display failure notification survives a rejected UI enqueue", () => WithCoordinator((coordinator, dispatcher, _, notifications) =>
    {
        dispatcher.AcceptEnqueue = false;
        SimulateRefreshFailure(coordinator, dispatcher);
        Check(!coordinator.IsLocked, "Cleanup depended on a working UI queue");
        Equal(0, notifications.Messages.Count);
        dispatcher.AcceptEnqueue = true;
        Call(coordinator, "ReportPendingLockFailure");
        dispatcher.Drain();
        Equal(1, notifications.Messages.Count);
    })),
    ("Startup: legacy drive-letter identity prompts reselection without overwriting file", () => WithConfig((store, path) =>
    {
        store.Save(Policy(LockMode.Usb, "DRIVE:E:"));
        var original = File.ReadAllBytes(path);
        WithServices(store, services =>
        {
            Check(services.StartupWarning is not null, "Legacy identity did not warn");
            Equal<string?>(null, services.Settings.Usb.DeviceInstanceId);
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Startup overwrote original configuration");
            Equal(0, services.AutoStart.ApplyCalls);
        });
    })),
    ("Startup: damaged configuration stays visible until an explicit recovery save", () => WithConfig((store, path) =>
    {
        File.WriteAllText(path, "{broken");
        WithServices(store, services =>
        {
            Equal(LockMode.None, services.Settings.LockMode);
            Check(services.StartupWarning is not null, "Recovery was silent");
            Equal("{broken", File.ReadAllText(path));
            Equal(0, services.AutoStart.ApplyCalls);
            services.Apply(Policy(LockMode.None));
            Equal<string?>(null, services.StartupWarning);
            Equal(LockMode.None, store.Load().LockMode);
        });
    })),
    ("Coordinator: native lock failure releases resources and warns", () => WithCoordinator((coordinator, _, _, notifications) =>
    {
        coordinator.Lock(LockReason.Usb);
        Check(!coordinator.IsLocked, "Failed lock reported locked");
        Check(Overlay(coordinator).HideCalls > 0, "Failed lock was not cleaned up");
        Equal(1, notifications.Messages.Count);
        coordinator.Lock(LockReason.Usb);
        Equal(1, notifications.Messages.Count);
    }))
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}\n{ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static AppSettings Policy(LockMode mode, string? usb = null, int minutes = 5)
    => new() { LockMode = mode, Usb = new() { DeviceInstanceId = usb }, Idle = new() { Minutes = minutes } };
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static void WithConfig(Action<SettingsStore, string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "ProxiLock.Regression", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { var path = Path.Combine(directory, "config.json"); action(new SettingsStore(path), path); }
    finally { Directory.Delete(directory, recursive: true); }
}
static object? Call(object target, string name, params object?[] args)
    => Invoke(target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!, target, args);
static object? CallStatic(Type type, string name, params object?[] args)
    => Invoke(type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!, null, args);
static object? Invoke(MethodInfo method, object? target, object?[] args)
{
    try { return method.Invoke(target, args); }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
}
static void Set(object target, string name, object? value)
    => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
static T Get<T>(object target, string name)
    => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;
static object AddBluetooth(BluetoothMonitor monitor)
{
    var state = Call(monitor, "GetOrAdd", 0x112233445566UL)!;
    Set(state, "IsPaired", true); Set(state, "Connected", true);
    Set(state, "Rssi", (short)-50); Set(state, "HasReportedRssi", true);
    Set(state, "AdvertisedAt", DateTimeOffset.UtcNow);
    Set(state, "RssiAt", DateTimeOffset.UtcNow);
    Set(state, "ConnectionStatusAt", DateTimeOffset.UtcNow);
    return state;
}
static void WithKeyboard(Action<KeyboardInterceptor, ManualResetEventSlim> action)
{
    using var keyboard = new KeyboardInterceptor();
    using var signal = new ManualResetEventSlim();
    Set(keyboard, "_active", 1);
    Set(keyboard, "_unlockRequested", (Action)(() => signal.Set()));
    action(keyboard, signal);
}
static void Key(KeyboardInterceptor keyboard, int key, int flags = 0)
{
    var data = Marshal.AllocHGlobal(24);
    try
    {
        for (int offset = 0; offset < 24; offset += 4) Marshal.WriteInt32(data, offset, 0);
        Marshal.WriteInt32(data, 0, key); Marshal.WriteInt32(data, 8, flags);
        Equal(new IntPtr(1), (IntPtr)Call(keyboard, "HookCallback", 0, new IntPtr(0x100), data)!);
    }
    finally { Marshal.FreeHGlobal(data); }
}
static void WithCoordinator(Action<LockCoordinator, DispatcherQueue, SettingsStore, NotificationService> action)
    => WithConfig((store, _) =>
    {
        var dispatcher = new DispatcherQueue();
        var notifications = new NotificationService();
        using var coordinator = new LockCoordinator(store, notifications, dispatcher, Policy(LockMode.Usb, "USB-A"));
        Set(coordinator, "_startedAt", DateTimeOffset.UtcNow.AddSeconds(-10));
        action(coordinator, dispatcher, store, notifications);
    });
static LockOverlayManager Overlay(LockCoordinator coordinator) => Get<LockOverlayManager>(coordinator, "_overlays");
static void Observe(LockCoordinator coordinator, AppSettings settings, bool present)
    => Call(coordinator, "ApplyObservation", settings, new BluetoothStatus(BluetoothPresence.Unknown, BluetoothReason.ScanIncomplete), (bool?)present);
static void SimulateRefreshFailure(LockCoordinator coordinator, DispatcherQueue dispatcher)
{
    // Idle avoids physical USB enumeration or Bluetooth operations in Evaluate().
    coordinator.ApplySettings(Policy(LockMode.Idle), persist: false);
    Overlay(coordinator).IsVisible = true;
    Overlay(coordinator).FailReassert = true;
    Set(coordinator, "_reason", LockReason.Idle);
    dispatcher.HasThreadAccess = false;
    Call(coordinator, "Evaluate");
}
static void WithServices(SettingsStore store, Action<AppServices> action)
{
    var services = new AppServices();
    Set(services, "<SettingsStore>k__BackingField", store);
    try { services.Start(new ProxiLock.MainWindow()); action(services); }
    finally { services.Stop(); }
}
