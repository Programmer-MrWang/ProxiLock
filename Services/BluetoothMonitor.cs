using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace ProxiLock.Services;

/// <summary>Whether the configured device currently satisfies the unlock condition.</summary>
public enum BluetoothPresence { Unknown, Present, Absent }

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
        public DateTimeOffset? LastSeen;
        public bool IsPaired;
        public bool Connected;
        public bool RandomAddress;

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
    private int _pairedRefreshRunning;
    private int _connectionPollRunning;
    private volatile bool _hasScanned;
    private bool _disposed;

    /// <summary>
    /// True once any advertisement has been received or a paired device has been listed.
    /// Lets callers distinguish "nothing in range" from "nothing has run yet", which are
    /// very different conclusions to show a user.
    /// </summary>
    public bool HasScanned => _hasScanned;

    public void Start()
    {
        if (_disposed) return;
        if (_watcher is null)
        {
            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                _watcher.Received += OnReceived;
                _watcher.Start();
            }
            catch
            {
                _watcher = null;
            }
        }

        // Paired devices are filtered out of the picker until they are actually connected.
        // Connection state is read from ConnectionStatus on a periodic poll, because the
        // AEP IsConnected property is not populated on every machine.
        _pairedTimer ??= new Timer(_ => RefreshPairedDevicesAsync(), null, TimeSpan.Zero, PairedRefreshInterval);
        _connectionTimer ??= new Timer(_ => PollConnectionStatusAsync(), null, TimeSpan.Zero, ConnectionPollInterval);
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.Received -= OnReceived;
            try { _watcher.Stop(); } catch { }
            _watcher = null;
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

                var advertised = state.LastSeen is { } seen && now - seen <= AbsentWindow;
                if (!advertised && !state.IsPaired) continue;

                result.Add(new BluetoothObservation(
                    FormatAddress(address),
                    state.Name,
                    state.Rssi,
                    state.IsPaired,
                    state.IsPaired && state.Connected,
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
    /// When no signal reading is available (classic Bluetooth hardware reports no RSSI at
    /// all) the threshold simply cannot be evaluated, and presence falls back to the
    /// connection/advertisement evidence. The UI reports that limitation rather than
    /// silently dropping the check.
    /// </remarks>
    public BluetoothPresence Evaluate(string? address, int? threshold)
    {
        if (!TryParseAddress(address, out var value)) return BluetoothPresence.Unknown;

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_states.TryGetValue(value, out var state))
                return BluetoothPresence.Unknown;

            var seenRecently = state.LastSeen is { } seen && now - seen <= PresentWindow;
            // Silence long enough to be conclusive. Between the two windows the previous
            // decision is retained, which is what removes the flicker.
            var silentConclusively = state.LastSeen is null || now - state.LastSeen.Value >= AbsentWindow;
            var connected = state.IsPaired && state.Connected;

            // Threshold mode. Connection is deliberately not consulted first here, because a
            // connected device whose signal has faded should still lock.
            if (threshold is int limit && state.Rssi != RssiUnknown && seenRecently)
            {
                if (state.Rssi >= limit)
                {
                    state.LastPresent = true;
                    return BluetoothPresence.Present;
                }

                if (state.Rssi <= limit - RssiHysteresisDb)
                {
                    state.LastPresent = false;
                    return BluetoothPresence.Absent;
                }

                // Inside the hysteresis band: hold the previous verdict.
                return state.LastPresent == true ? BluetoothPresence.Present : BluetoothPresence.Absent;
            }

            // No usable signal reading. A live connection is direct evidence that the device
            // is here, which is the best available answer for hardware that reports no RSSI.
            if (connected || seenRecently)
            {
                state.LastPresent = true;
                return BluetoothPresence.Present;
            }

            // Not currently evidenced. Only call it absent once the silence is conclusive,
            // otherwise keep the last answer so a slow advertiser does not flap the lock.
            if (silentConclusively)
            {
                state.LastPresent = false;
                return BluetoothPresence.Absent;
            }

            return state.LastPresent == true ? BluetoothPresence.Present : BluetoothPresence.Unknown;
        }
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

            state.Rssi = args.RawSignalStrengthInDBm;
            state.LastSeen = DateTimeOffset.UtcNow;
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
            List<(ulong Address, PairedHandle Handle)> handles;
            lock (_gate) handles = _handles.Select(pair => (pair.Key, pair.Value)).ToList();

            foreach (var (address, handle) in handles)
            {
                try
                {
                    if (handle.IsLowEnergy)
                    {
                        handle.LowEnergy ??= await CreateLowEnergyAsync(address).ConfigureAwait(false);
                        if (handle.LowEnergy is null) continue;
                        Record(address, handle.LowEnergy.ConnectionStatus == BluetoothConnectionStatus.Connected, handle.LowEnergy.Name);
                    }
                    else
                    {
                        handle.Classic ??= await CreateClassicAsync(address).ConfigureAwait(false);
                        if (handle.Classic is null) continue;
                        Record(address, handle.Classic.ConnectionStatus == BluetoothConnectionStatus.Connected, handle.Classic.Name);
                    }
                }
                catch
                {
                    // A handle can become invalid when the radio resets or the device is
                    // removed; drop it so the next paired refresh recreates it.
                    lock (_gate) _handles.Remove(address);
                }
            }
        }
        catch
        {
            // Polling is best-effort; the previous readings stay in place.
        }
        finally
        {
            Interlocked.Exchange(ref _connectionPollRunning, 0);
        }
    }

    private static async Task<BluetoothDevice?> CreateClassicAsync(ulong address)
    {
        var task = BluetoothDevice.FromBluetoothAddressAsync(address).AsTask();
        return await Task.WhenAny(task, Task.Delay(DeviceCallTimeout)).ConfigureAwait(false) == task
            ? await task.ConfigureAwait(false)
            : null;
    }

    private static async Task<BluetoothLEDevice?> CreateLowEnergyAsync(ulong address)
    {
        var task = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
        return await Task.WhenAny(task, Task.Delay(DeviceCallTimeout)).ConfigureAwait(false) == task
            ? await task.ConfigureAwait(false)
            : null;
    }

    private void Record(ulong address, bool connected, string? name)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(address, out var state)) return;
            state.Connected = connected;
            // A live connection proves presence, which is what lets headphones and other
            // non-advertising hardware serve as an unlock credential.
            if (connected) state.LastSeen = DateTimeOffset.UtcNow;
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
        try
        {
            var lookup = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
            if (await Task.WhenAny(lookup, Task.Delay(NameLookupTimeout)).ConfigureAwait(false) == lookup)
            {
                using var device = await lookup.ConfigureAwait(false);
                if (device is not null && !string.IsNullOrWhiteSpace(device.Name))
                    name = device.Name;
            }
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
        }
    }

    /// <summary>Keeps tracked devices bounded by dropping the least recently seen.</summary>
    private void Evict()
    {
        while (_order.Count > 150)
        {
            var oldest = _order.FirstOrDefault(address =>
                !(_states.TryGetValue(address, out var state) && state.IsPaired));
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
        }
    }
}
