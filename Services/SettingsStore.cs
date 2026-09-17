using System.Text.Json;
using System.Text.Json.Serialization;
using ProxiLock.Models;

namespace ProxiLock.Services;

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProxiLock",
        "config.json");
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsStore()
    {
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public AppSettings Load()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path))
                    return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _options));
            }
        }
        catch { }
        return Normalize(null);
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(normalized, _options);
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        settings.Bluetooth ??= new BluetoothSettings();
        settings.Usb ??= new UsbSettings();
        settings.Idle ??= new IdleSettings();
        settings.Idle.Minutes = Math.Clamp(settings.Idle.Minutes, 1, 1440);
        if (settings.Bluetooth.Threshold is int threshold)
            settings.Bluetooth.Threshold = Math.Clamp(threshold, -100, -20);
        if (!Enum.IsDefined(settings.LockMode))
            settings.LockMode = LockMode.None;
        return settings;
    }
}
