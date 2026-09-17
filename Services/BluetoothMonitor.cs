using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace ProxiLock.Services;

/// <summary>Whether the configured device currently satisfies the unlock condition.</summary>
public enum BluetoothPresence { Unknown, Present, Absent }

/// <summary>
/// Why <see cref="BluetoothMonitor.Evaluate"/> reached its verdict. Callers need this to
/// explain the state honestly: "connected but the signal is too weak" and "not connected"
/// both report <see cref="BluetoothPresence.Absent"/>, but they mean different things to the
/// user, and the same verdict also implies something different while a manual unlock is held.
/// </summary>
public enum BluetoothReason
{
    /// <summary>The radio has not produced a single advertisement yet; nothing can be concluded.</summary>
    ScanIncomplete,
    /// <summary>The radio is working but the configured device has never been observed.</summary>
    NeverObserved,
    /// <summary>Advertising recently, with no threshold configured to compare against.</summary>
    Advertised,
    /// <summary>Reported connected by the connection poll.</summary>
    Connected,
    /// <summary>Paired but neither connected nor advertising for a conclusive period.</summary>
    NotConnected,
    /// <summary>Signal read at or above the configured threshold.</summary>
    SignalAboveThreshold,
    /// <summary>Signal read below the configured threshold.</summary>
    SignalBelowThreshold,
    /// <summary>Signal sits inside the hysteresis band, so the previous verdict is held.</summary>
    SignalInHysteresis,
    /// <summary>A threshold is configured but no recent measurement exists to apply it to.</summary>
    NoRecentSignal
}

/// <summary>A presence verdict together with the evidence it was based on.</summary>
public readonly record struct BluetoothStatus(BluetoothPresence Presence, BluetoothReason Reason);

/// <summary>
/// One device the user can choose from. Deliberately a plain immutable record: the
/// Bluetooth callbacks run on background threads, and XAML bindings may only be updated
/// on the UI thread, so the monitor never hands out bound objects directly.
/// </summary>
public readonly record struct BluetoothObservation(
    string Address,
    string? Name,
    short Rssi,
    bool IsPaired,
    bool IsConnected,
    bool IsRandomAddress);

/// <summary>
/// Tracks Bluetooth devices for the proximity lock using two independent signals:
/// BLE advertisements (for low-energy peripherals that broadcast) and paired-device
/// connection status (for classic Bluetooth hardware, which never sends BLE
/// advertisements and would otherwise be invisible).
/// </summary>
public sealed class BluetoothMonitor : IDisposable
{
    /// <summary>A device that advertised this recently counts as present.</summary>
    private static readonly TimeSpan PresentWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// It must be silent this long before it counts as absent. The gap between the two
    /// windows is deliberate hysteresis: with a single window, any device advertising more
    /// slowly than the evaluation interval flips between present and absent, which shows up
    /// as the screen locking and unlocking in a loop.
    ///
    /// This is generous on purpose. BLE peripherals advertise at widely varying rates, and
    /// phones throttle advertising heavily, so a short window produces a constant strobe.
    /// Waiting longer before locking is far less disruptive than locking and unlocking
    /// repeatedly.
    /// </summary>
    private static readonly TimeSpan AbsentWindow = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PairedRefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a connection reading stays valid evidence. The poll runs every three
    /// seconds, so this tolerates several missed reads before the connection stops counting.
    /// Without an expiry a device that was connected when the radio reset would look
    /// connected forever, and a device that left would keep the screen unlocked.
    /// </summary>
    private static readonly TimeSpan ConnectionStatusWindow = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan NameLookupTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long to wait for a device-handle call before giving up. These calls hit the
    /// radio, and a disabled adapter or powered-off device can make them hang.
    /// </summary>
    private static readonly TimeSpan DeviceCallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far signal must fall below the threshold before it counts as gone. Without this
    /// a reading hovering at the boundary toggles the lock every tick.
    /// </summary>
    private const int RssiHysteresisDb = 5;

    private const short RssiUnknown = -127;

    private sealed class State
    {
        public string? Name;
        public short Rssi = RssiUnknown;
        public bool IsPaired;
        public bool Connected;
        public bool RandomAddress;

        /// <summary>When the last BLE advertisement arrived. Never touched by the connection poll.</summary>
        public DateTimeOffset? AdvertisedAt;

        /// <summary>
        /// When <see cref="Rssi"/> was last measured. Kept separate from
        /// <see cref="AdvertisedAt"/> and from the connection poll so a stale reading can
        /// never be presented as a fresh distance measurement.
        /// </summary>
        public DateTimeOffset? RssiAt;

        /// <summary>When <see cref="Connected"/> was last successfully read.</summary>
        public DateTimeOffset? ConnectionStatusAt;

        /// <summary>
        /// Set once an advertisement has ever carried a signal reading. Distinguishes "no
        /// reading right now" from "this hardware can never report one", which is what lets
        /// the UI explain why a threshold cannot apply.
        /// </summary>
        public bool HasReportedRssi;

        /// <summary>Last presence decision, so the hysteresis band can hold it steady.</summary>
        public bool? LastPresent;
    }

    /// <summary>
    /// A handle kept only to read <c>ConnectionStatus</c>, which is the authoritative
    /// connected/not-connected signal. Holding a handle does not itself establish a
    /// connection, so these are passive observers rather than something that keeps a
    /// peripheral awake.
    /// </summary>
    private sealed class PairedHandle
    {
        public bool IsLowEnergy;
        public BluetoothDevice? Classic;
        public BluetoothLEDevice? LowEnergy;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, State> _states = new();
    private readonly List<ulong> _order = new();
    private readonly Dictionary<ulong, string?> _resolvedNames = new();
    private readonly HashSet<ulong> _nameLookupsInFlight = new();
    private readonly Dictionary<ulong, PairedHandle> _handles = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private Timer? _pairedTimer;
    private Timer? _connectionTimer;
    /// <summary>
    /// Cancels in-flight radio work. Written under <see cref="_gate"/> and read by timer
    /// callbacks off-thread, so it is volatile; it is deliberately never disposed (see
    /// <see cref="Stop"/>).
    /// </summary>
    private volatile CancellationTokenSource? _lifetime;
    private int _pairedRefreshRunning;
    private int _connectionPollRunning;
    private int _watcherRestartRunning;
    private volatile bool _hasScanned;
    private volatile bool _active;
    private bool _disposed;

    /// <summary>When any advertisement was last received, from any device.</summary>
    private DateTimeOffset? _lastAnyAdvertisementAt;

    /// <summary>
    /// The device the policy is currently watching. It is exempt from cache eviction:
    /// dropping the entry would erase the only evidence the monitor has about the device the
    /// user chose, leaving the lock stuck in "unknown".
    /// </summary>
    private ulong? _pinnedAddress;

    /// <summary>
    /// True once any advertisement has been received or a paired device has been listed.
    /// Lets callers distinguish "nothing in range" from "nothing has run yet", which are
    /// very different conclusions to show a user.
    /// </summary>
    public bool HasScanned => _hasScanned;

    public void Start()
    {
        if (_disposed) return;
        _active = true;
        lock (_gate)
        {
            _lifetime ??= new CancellationTokenSource();
            EnsureWatcher();
        }

        // Paired devices are filtered out of the picker until they are actually connected.
        // Connection state is read from ConnectionStatus on a periodic poll, because the
        // AEP IsConnected property is not populated on every machine.
        _pairedTimer ??= new Timer(_ => RefreshPairedDevicesAsync(), null, TimeSpan.Zero, PairedRefreshInterval);
        _connectionTimer ??= new Timer(_ => PollConnectionStatusAsync(), null, TimeSpan.Zero, ConnectionPollInterval);
    }

    /// <summary>
    /// Starts the BLE watcher if it is not running. A watcher that the system already
    /// aborted (radio toggled off, adapter reset) is not reused: the object survives with a
    /// non-running status, so checking for null alone would leave the radio silent forever.
    /// </summary>
    private void EnsureWatcher()
    {
        if (_disposed) return;
        if (_watcher is not null && _watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started)
            return;

        DetachWatcher();

        try
        {
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += OnReceived;
            watcher.Stopped += OnWatcherStopped;
            watcher.Start();
            _watcher = watcher;
        }
        catch
        {
            // A missing or disabled radio simply yields no advertisements.
            _watcher = null;
        }
    }

    private void DetachWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnWatcherStopped;
        try { watcher.Stop(); } catch { }
    }

    /// <summary>
    /// Recreates the watcher after the radio stops it, which happens when Bluetooth is
    /// turned off and on again or the adapter resets. Without this the policy would keep
    /// evaluating against a scan that silently ended.
    /// </summary>
    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (ReferenceEquals(_watcher, sender)) _watcher = null;
            lifetime = _lifetime;
        }

        if (!_active || _disposed || lifetime is null) return;
        if (Interlocked.Exchange(ref _watcherRestartRunning, 1) != 0) return;

        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Give the radio a moment to settle before rebuilding the watcher.
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                // Stop may have run during the delay; rebuilding then would resurrect a
                // watcher the caller had already released.
                if (!_active || _disposed || token.IsCancellationRequested) return;
                lock (_gate) EnsureWatcher();
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _watcherRestartRunning, 0);
            }
        }, token);
    }

    public void Stop()
    {
        _active = false;
        lock (_gate)
        {
            DetachWatcher();

            // The token source is cancelled but intentionally not disposed: callbacks and
            // polling work that already captured it may still read Token, and reading it
            // after disposal throws. Nothing here uses CancelAfter, so there is no timer to
            // free. A fresh source is created the next time Start runs.
            var lifetime = _lifetime;
            _lifetime = null;
            try { lifetime?.Cancel(); } catch { }
        }

        var paired = _pairedTimer;
        _pairedTimer = null;
        paired?.Dispose();
        var connection = _connectionTimer;
        _connectionTimer = null;
        connection?.Dispose();
    }

    /// <summary>
    /// Devices offered to the user: anything the user could reasonably pick.
    /// </summary>
    /// <remarks>
    /// Paired devices are listed even when they are not currently connected, because
    /// hardware the user has deliberately paired is something they expect to find in the
    /// list; hiding it the moment it powers down would make it look lost. Its state is
    /// reported instead (see <see cref="BluetoothObservation.IsConnected"/>), so the UI can
    /// say "not connected" rather than implying it is out of range.
    /// </remarks>
    public IReadOnlyList<BluetoothObservation> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var result = new List<BluetoothObservation>(_order.Count);
            foreach (var address in _order)
            {
                if (!_states.TryGetValue(address, out var state)) continue;

                var advertised = state.AdvertisedAt is { } seen && now - seen <= AbsentWindow;
                if (!advertised && !state.IsPaired) continue;

                result.Add(new BluetoothObservation(
                    FormatAddress(address),
                    state.Name,
                    state.Rssi,
                    state.IsPaired,
                    state.IsPaired && ConnectionFresh(state, now),
                    state.RandomAddress));
            }

            // Paired hardware first: it is the choice a user can rely on, and connected
            // devices ahead of idle ones so the usable entries are at the top.
            return result
                .OrderByDescending(observation => observation.IsPaired)
                .ThenByDescending(observation => observation.IsConnected)
                .ThenByDescending(observation => observation.Rssi)
                .ThenBy(observation => observation.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(observation => observation.Address, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Decides whether the configured device satisfies the unlock condition, applying
    /// hysteresis so a borderline device cannot flap the lock.
    /// </summary>
    /// <remarks>
    /// A configured threshold is the authority whenever a real signal reading exists, and
    /// that includes a connected device: connection proves the device is here, but it says
    /// nothing about distance, so it must never override an explicit proximity requirement.
    /// A reading is only usable while it is fresh; once the device stops advertising, a
    /// threshold can no longer be evaluated and the previous verdict is held until the
    /// silence becomes conclusive. When no reading is ever available (classic Bluetooth
    /// hardware reports no RSSI at all) presence falls back to the connection/advertisement
    /// evidence, and the UI reports that limitation rather than silently dropping the check.
    /// </remarks>
    public BluetoothStatus Evaluate(string? address, int? threshold)
    {
        if (!TryParseAddress(address, out var value))
            return new BluetoothStatus(BluetoothPresence.Unknown, BluetoothReason.ScanIncomplete);

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            // Watch the configured device so eviction cannot discard it.
            _pinnedAddress = value;

            if (!_states.TryGetValue(value, out var state))
            {
                // Never observed. Once the radio is demonstrably working (advertisements
                // from other devices are arriving) the configured device is genuinely away.
                // Before the first advertisement there is nothing to conclude, which keeps
                // startup from locking on an empty cache.
                var radioLive = _lastAnyAdvertisementAt is { } any && now - any <= AbsentWindow;
                return radioLive
                    ? new BluetoothStatus(BluetoothPresence.Absent, BluetoothReason.NeverObserved)
                    : new BluetoothStatus(BluetoothPresence.Unknown, BluetoothReason.ScanIncomplete);
            }

            var advertised = state.AdvertisedAt is { } seen && now - seen <= PresentWindow;
            var connected = state.IsPaired && ConnectionFresh(state, now);
            var measurementMinutes = state.RssiAt is { } measured && now - measured <= PresentWindow && state.Rssi != RssiUnknown;
            var silenceConclusive = LastEvidence(state) is not { } evidence || now - evidence >= AbsentWindow;

            // Threshold mode. Only a fresh measurement can decide it: a signed-out reading or
            // a connection alone cannot stand in for distance.
            if (threshold is int limit && state.HasReportedRssi)
            {
                if (!measurementMinutes)
                {
                    if (silenceConclusive)
                    {
                        state.LastPresent = false;
                        return new BluetoothStatus(BluetoothPresence.Absent, BluetoothReason.NoRecentSignal);
                    }
                    return Held(state, BluetoothReason.NoRecentSignal);
                }

                if (state.Rssi >= limit)
                {
                    state.LastPresent = true;
                    return new BluetoothStatus(BluetoothPresence.Present, BluetoothReason.SignalAboveThreshold);
                }

                if (state.Rssi <= limit - RssiHysteresisDb)
                {
                    state.LastPresent = false;
                    return new BluetoothStatus(BluetoothPresence.Absent, BluetoothReason.SignalBelowThreshold);
                }

                // Inside the hysteresis band: hold the previous verdict.
                return Held(state, BluetoothReason.SignalInHysteresis);
            }

            // No usable signal reading. A live connection is direct evidence that the device
            // is here, which is the best available answer for hardware that reports no RSSI.
            if (connected || advertised)
            {
                state.LastPresent = true;
                return new BluetoothStatus(BluetoothPresence.Present, connected ? BluetoothReason.Connected : BluetoothReason.Advertised);
            }

            // Not currently evidenced. Only call it absent once the silence is conclusive,
            // otherwise keep the last answer so a slow advertiser does not flap the lock.
            if (silenceConclusive)
            {
                state.LastPresent = false;
                return new BluetoothStatus(BluetoothPresence.Absent, BluetoothReason.NotConnected);
            }

            return Held(state, BluetoothReason.NoRecentSignal);
        }

        // Between the two windows there is no new evidence, so the previous verdict stands.
        static BluetoothStatus Held(State state, BluetoothReason reason) => state.LastPresent switch
        {
            true => new BluetoothStatus(BluetoothPresence.Present, reason),
            false => new BluetoothStatus(BluetoothPresence.Absent, reason),
            _ => new BluetoothStatus(BluetoothPresence.Unknown, reason)
        };
    }

    /// <summary>
    /// True when <c>Connected</c> was read recently enough to be treated as current. The
    /// timestamp is only advanced by a successful poll, so a radio that stops answering
    /// ages out instead of pinning the device as present.
    /// </summary>
    private static bool ConnectionFresh(State state, DateTimeOffset now)
        => state.Connected
           && state.ConnectionStatusAt is { } read
           && now - read <= ConnectionStatusWindow;

    /// <summary>The most recent moment the device gave any sign of being present.</summary>
    private static DateTimeOffset? LastEvidence(State state)
    {
        var advertised = state.AdvertisedAt;
        var connectedAt = state.Connected ? state.ConnectionStatusAt : null;
        if (advertised is null) return connectedAt;
        if (connectedAt is null) return advertised;
        return advertised > connectedAt ? advertised : connectedAt;
    }

    /// <summary>
    /// True when the device has ever reported a signal reading, i.e. it sends BLE
    /// advertisements. Classic Bluetooth hardware (audio headsets, keyboards) reports no
    /// RSSI through any Windows API, so a signal-strength threshold cannot apply to it and
    /// the UI has to say so instead of appearing to enforce a rule it cannot measure.
    /// </summary>
    public bool SupportsRssi(string? address)
    {
        if (!TryParseAddress(address, out var value)) return false;
        lock (_gate) return _states.TryGetValue(value, out var state) && state.HasReportedRssi;
    }

    /// <summary>True when the address belongs to a device paired with this machine.</summary>
    public bool IsPaired(string? address)
    {
        if (!TryParseAddress(address, out var value)) return false;
        lock (_gate) return _states.TryGetValue(value, out var state) && state.IsPaired;
    }

    /// <summary>
    /// True when the address is a rotating privacy address, or was never seen advertising
    /// and is not a paired device. Such an address cannot be matched reliably, which is the
    /// usual reason a selection produces constant lock/unlock flapping.
    /// </summary>
    public bool IsUnstableIdentity(string? address)
    {
        if (!TryParseAddress(address, out var value)) return false;
        lock (_gate)
        {
            if (!_states.TryGetValue(value, out var state)) return false;
            if (state.IsPaired) return false;
            return state.RandomAddress;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertisedName = args.Advertisement.LocalName;
        var address = args.BluetoothAddress;
        bool needsNameLookup;

        lock (_gate)
        {
            var state = GetOrAdd(address);
            _hasScanned = true;

            // Only ever upgrade the name. Overwriting it with the (usually empty) name from
            // the current packet is what made every device read as "未知设备".
            if (!string.IsNullOrWhiteSpace(advertisedName))
                state.Name = advertisedName;
            else if (_resolvedNames.TryGetValue(address, out var resolved) && !string.IsNullOrWhiteSpace(resolved))
                state.Name = resolved;

            var now = DateTimeOffset.UtcNow;
            state.Rssi = args.RawSignalStrengthInDBm;
            state.AdvertisedAt = now;
            state.RssiAt = now;
            _lastAnyAdvertisementAt = now;
            // BLE reports -127 for "unknown", so only a real reading proves this hardware
            // can be measured against a threshold at all.
            if (state.Rssi != RssiUnknown) state.HasReportedRssi = true;
            // A random (privacy) address means the device rotates its identity, so it can
            // never be matched reliably over time; the picker warns about selecting one.
            state.RandomAddress = args.BluetoothAddressType == BluetoothAddressType.Random;

            Evict();

            needsNameLookup = string.IsNullOrWhiteSpace(state.Name)
                              && !_resolvedNames.ContainsKey(address)
                              && _nameLookupsInFlight.Add(address);
        }

        if (needsNameLookup)
            _ = ResolveNameAsync(address);
    }

    private State GetOrAdd(ulong address)
    {
        if (!_states.TryGetValue(address, out var state))
        {
            state = new State();
            _states[address] = state;
            _order.Add(address);
        }
        return state;
    }

    /// <summary>
    /// Enumerates paired devices so they can be offered in the picker. Classic Bluetooth
    /// hardware (headphones, speakers, mice) never emits BLE advertisements, so without
    /// this it would be impossible for a user to select it at all.
    /// </summary>
    /// <remarks>
    /// Connection state is deliberately not taken from here. The AEP IsConnected property
    /// used for this enumeration has been observed to report false even for a connected
    /// device, so presence is read from ConnectionStatus instead (see
    /// <see cref="PollConnectionStatusAsync"/>).
    /// </remarks>
    private async void RefreshPairedDevicesAsync()
    {
        // The timer can fire again while a slow enumeration is still running.
        if (Interlocked.Exchange(ref _pairedRefreshRunning, 1) != 0) return;
        try
        {
            var lowEnergyFound = await FindPairedAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).ConfigureAwait(false);
            var classicFound = await FindPairedAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)).ConfigureAwait(false);

            lock (_gate)
            {
                if (lowEnergyFound.Count > 0 || classicFound.Count > 0)
                    _hasScanned = true;

                Apply(lowEnergyFound, isLowEnergy: true);
                Apply(classicFound, isLowEnergy: false);
            }
        }
        catch
        {
            // Enumeration can fail transiently (radio reset, adapter removed).
        }
        finally
        {
            Interlocked.Exchange(ref _pairedRefreshRunning, 0);
        }

        void Apply(List<(ulong Address, string? Name)> found, bool isLowEnergy)
        {
            foreach (var (address, name) in found)
            {
                var state = GetOrAdd(address);
                // Pairing proves identity, not presence: the device may be switched off or
                // out of range. It is kept in the list so the user can recognise it, but its
                // presence still has to be established by a connection or an advertisement.
                state.IsPaired = true;
                if (string.IsNullOrWhiteSpace(state.Name) && !string.IsNullOrWhiteSpace(name))
                    state.Name = name;

                if (!_handles.ContainsKey(address))
                    _handles[address] = new PairedHandle { IsLowEnergy = isLowEnergy };
            }
        }
    }

    /// <summary>
    /// Reads <c>ConnectionStatus</c> for each paired device. This is the authoritative
    /// connected/not-connected signal and works for classic Bluetooth hardware that never
    /// advertises; a live connection also proves the device is physically present.
    /// </summary>
    private async void PollConnectionStatusAsync()
    {
        if (Interlocked.Exchange(ref _connectionPollRunning, 1) != 0) return;
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            List<(ulong Address, PairedHandle Handle)> handles;
            lock (_gate) handles = _handles.Select(pair => (pair.Key, pair.Value)).ToList();

            foreach (var (address, handle) in handles)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (handle.IsLowEnergy)
                    {
                        handle.LowEnergy ??= await CreateLowEnergyAsync(address, token).ConfigureAwait(false);
                        if (handle.LowEnergy is null) continue;
                        Record(address, handle.LowEnergy.ConnectionStatus == BluetoothConnectionStatus.Connected, handle.LowEnergy.Name);
                    }
                    else
                    {
                        handle.Classic ??= await CreateClassicAsync(address, token).ConfigureAwait(false);
                        if (handle.Classic is null) continue;
                        Record(address, handle.Classic.ConnectionStatus == BluetoothConnectionStatus.Connected, handle.Classic.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // A handle can become invalid when the radio resets or the device is
                    // removed. Drop it so the next paired refresh recreates it; the
                    // connection reading then ages out instead of pinning the device present.
                    DropHandle(address);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Polling is best-effort; the previous readings age out on their own.
        }
        finally
        {
            Interlocked.Exchange(ref _connectionPollRunning, 0);
        }
    }

    private void DropHandle(ulong address)
    {
        PairedHandle? handle;
        lock (_gate)
        {
            if (!_handles.TryGetValue(address, out handle)) return;
            _handles.Remove(address);
            // A device that is no longer readable is still paired, so its state (and name)
            // stays; only the connection reading is invalidated.
            if (_states.TryGetValue(address, out var state))
                state.Connected = false;
        }

        try { handle.Classic?.Dispose(); } catch { }
        try { handle.LowEnergy?.Dispose(); } catch { }
    }

    private static async Task<BluetoothDevice?> CreateClassicAsync(ulong address, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(DeviceCallTimeout);
            return await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the operation also rejects the pending projection, so an object
            // that arrives late is released instead of leaking outside the caller.
            return null;
        }
    }

    private static async Task<BluetoothLEDevice?> CreateLowEnergyAsync(ulong address, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(DeviceCallTimeout);
            return await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void Record(ulong address, bool connected, string? name)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(address, out var state)) return;
            state.Connected = connected;
            // Only a successful read stamps this, which is what lets a stale "connected"
            // expire when the radio stops answering.
            state.ConnectionStatusAt = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(state.Name) && !string.IsNullOrWhiteSpace(name))
                state.Name = name;
        }
    }

    private static async Task<List<(ulong Address, string? Name)>> FindPairedAsync(string selector)
    {
        var result = new List<(ulong, string?)>();
        try
        {
            var properties = new[] { "System.Devices.Aep.DeviceAddress" };
            var found = await DeviceInformation
                .FindAllAsync(selector, properties, DeviceInformationKind.AssociationEndpoint)
                .AsTask().ConfigureAwait(false);

            foreach (var information in found)
            {
                var text = information.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var value)
                    ? value?.ToString()
                    : null;

                // That property is frequently absent for paired Bluetooth endpoints, so the
                // identity has to come from the device Id instead.
                if (string.IsNullOrWhiteSpace(text))
                    text = ExtractAddressFromDeviceId(information.Id);

                if (!TryParseAddress(text, out var address) || address == 0) continue;

                // Only the identity is taken here; connection state comes from
                // ConnectionStatus, because the AEP flag is not reliable on every machine.
                result.Add((address, information.Name));
            }
        }
        catch
        {
            // A locked-down or radio-less machine simply yields no paired devices.
        }
        return result;
    }


    /// <summary>
    /// Asks the system for a device's name. Many peripherals expose their name only through
    /// the device object rather than in the advertisement, which is what left the picker
    /// full of identical "未知设备" rows.
    /// </summary>
    private async Task ResolveNameAsync(ulong address)
    {
        string? name = null;
        BluetoothLEDevice? device = null;
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(NameLookupTimeout);
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is not null && !string.IsNullOrWhiteSpace(device.Name))
                name = device.Name;
        }
        catch (OperationCanceledException)
        {
            // Best-effort: an unnamed device still works as an unlock credential.
        }
        catch
        {
            // Best-effort: an unnamed device still works as an unlock credential.
        }
        finally
        {
            lock (_gate)
            {
                // Record the attempt either way so an unnamed device is not re-queried on
                // every subsequent advertisement.
                _resolvedNames[address] = name;
                _nameLookupsInFlight.Remove(address);
                if (!string.IsNullOrWhiteSpace(name) && _states.TryGetValue(address, out var state))
                    state.Name = name;
            }

            try { device?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Keeps tracked devices bounded by dropping the least recently seen. The device the
    /// policy is watching is never dropped, and paired devices are kept so the picker does
    /// not lose hardware the user has deliberately set up.
    /// </summary>
    private void Evict()
    {
        while (_order.Count > 150)
        {
            var oldest = _order.FirstOrDefault(address =>
                address != _pinnedAddress
                && !(_states.TryGetValue(address, out var state) && state.IsPaired));
            if (oldest == 0) break;
            _order.Remove(oldest);
            _states.Remove(oldest);
            _resolvedNames.Remove(oldest);
        }
    }

    public static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Pulls the remote device address out of a paired Bluetooth device Id, which has the
    /// form <c>Bluetooth#Bluetooth&lt;local adapter&gt;-&lt;remote device&gt;</c> with
    /// colon-separated hex addresses. The remote device is the part after the final '-',
    /// and taking the whole suffix would otherwise include the local adapter address and
    /// parse into the wrong device.
    /// </summary>
    private static string? ExtractAddressFromDeviceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var dash = id.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= id.Length) return null;
        var suffix = id[(dash + 1)..];
        // Only accept a plain hex address so a differently shaped Id cannot be misread.
        return suffix.All(c => Uri.IsHexDigit(c) || c == ':') ? suffix : null;
    }

    private static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Replace(":", string.Empty).Replace("-", string.Empty);
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        Stop();

        List<PairedHandle> handles;
        lock (_gate) handles = _handles.Values.ToList();

        foreach (var handle in handles)
        {
            try { handle.Classic?.Dispose(); } catch { }
            try { handle.LowEnergy?.Dispose(); } catch { }
        }

        lock (_gate)
        {
            _handles.Clear();
            _states.Clear();
            _order.Clear();
            _resolvedNames.Clear();
            _nameLookupsInFlight.Clear();
        }
    }
}
