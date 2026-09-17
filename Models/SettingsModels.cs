using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ProxiLock.Models;

public enum LockMode
{
    None,
    Bluetooth,
    Usb,
    Idle
}

public sealed class AppSettings
{
    public LockMode LockMode { get; set; } = LockMode.None;
    public BluetoothSettings Bluetooth { get; set; } = new();
    public UsbSettings Usb { get; set; } = new();
    public IdleSettings Idle { get; set; } = new();
    public bool AutoStart { get; set; }
}

public sealed class BluetoothSettings
{
    public string? DeviceAddress { get; set; }
    public int? Threshold { get; set; }
}

public sealed class UsbSettings
{
    public string? DeviceInstanceId { get; set; }
}

public sealed class IdleSettings
{
    public int Minutes { get; set; } = 5;
}

public sealed class BluetoothDeviceInfo : INotifyPropertyChanged
{
    private string _name = "未知设备";
    private short _rssi = -127;
    private bool _isPaired;
    private bool _connected;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise(nameof(Name));
        }
    }

    public string Address { get; init; } = string.Empty;

    private bool _isRandomAddress;

    /// <summary>
    /// True for a rotating privacy address. Such a device cannot be tracked reliably, so
    /// the picker flags it rather than letting the user choose it and get a flapping lock.
    /// </summary>
    public bool IsRandomAddress
    {
        get => _isRandomAddress;
        set
        {
            if (_isRandomAddress == value) return;
            _isRandomAddress = value;
            Raise(nameof(IsRandomAddress));
            Raise(nameof(IsStable));
        }
    }

    /// <summary>True when this device is a reliable credential.</summary>
    public bool IsStable => IsPaired || !IsRandomAddress;

    public short Rssi
    {
        get => _rssi;
        set
        {
            if (_rssi == value) return;
            _rssi = value;
            Raise(nameof(Rssi));
            Raise(nameof(SignalText));
        }
    }

    /// <summary>Paired hardware is highlighted, since it is the reliable choice.</summary>
    public bool IsPaired
    {
        get => _isPaired;
        set
        {
            if (_isPaired == value) return;
            _isPaired = value;
            Raise(nameof(IsPaired));
            Raise(nameof(StatusText));
        }
    }

    public bool Connected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            Raise(nameof(Connected));
            // SignalText falls back to the connection state when no RSSI is reported.
            Raise(nameof(SignalText));
        }
    }

    /// <summary>
    /// Signal strength in the same dBm unit as the threshold field, so what the user reads
    /// here matches what they would type there.
    /// </summary>
    /// <remarks>
    /// Classic Bluetooth does not report RSSI at all, so a connected headset has no reading
    /// to show. A bare dash there looked like a failure, so the connection state is named
    /// instead, and a paired-but-idle device is distinguished from a connected one so the
    /// column never appears broken.
    /// </remarks>
    public string SignalText => Rssi is >= -126 and < 0
        ? $"{Rssi} dBm"
        : Connected ? "已连接"
        : IsPaired ? "未连接"
        : "—";

    /// <summary>Identity badge: pairing describes the device, not its current presence.</summary>
    public string StatusText => IsPaired ? "已配对" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public override string ToString() => $"{Name}  ·  {Address}";
}

public sealed class UsbDeviceInfo
{
    public string DriveLetter { get; init; } = string.Empty;
    public string Name { get; init; } = "USB Drive";
    public string InstanceId { get; init; } = string.Empty;
    public override string ToString() => $"{DriveLetter}  ·  {Name}";
}
