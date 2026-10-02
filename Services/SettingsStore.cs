using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using ProxiLock.Models;

namespace ProxiLock.Services;

public sealed class SettingsStore
{
    // The settings file is tiny (a handful of scalar values). Refusing unexpectedly large
    // input prevents a damaged or replaced file from causing an unbounded allocation during
    // startup. The limit is deliberately generous for forward-compatible additions.
    private const int MaxConfigBytes = 64 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private bool _preserveRecoveryBackup;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>A warning when the most recent load recovered an older backup.</summary>
    public string? LoadWarning { get; private set; }

    public SettingsStore() : this(Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProxiLock",
        "config.json"))
    {
    }

    /// <summary>Uses an explicit configuration file path, including for isolated tests.</summary>
    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LoadWarning = null;
            _preserveRecoveryBackup = false;
            try
            {
                var settings = DeserializeFile(_path);
                return Normalize(settings);
            }
            catch (Exception primaryError) when (IsReadFailure(primaryError))
            {
                try
                {
                    var backup = Normalize(DeserializeFile(_path + ".bak"));
                    _preserveRecoveryBackup = true;
                    LoadWarning = $"配置文件无法读取，已从备份恢复。请检查当前锁定策略并重新保存。配置位置：{_path}";
                    return backup;
                }
                catch (Exception backupError) when (IsReadFailure(backupError))
                {
                    // File.Exists suppresses access-denied and other I/O failures. Only an
                    // actual not-found result for both files represents a fresh installation.
                    if (IsMissing(primaryError) && IsMissing(backupError))
                        return Normalize(null);

                    throw new InvalidDataException(
                        $"配置文件及备份均无法读取。配置位置：{_path}",
                        new AggregateException(primaryError, backupError));
                }
            }
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_path)!;
        var json = JsonSerializer.Serialize(normalized, _options);
        if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
            throw new InvalidDataException($"The ProxiLock settings file exceeds {MaxConfigBytes} bytes.");
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                // Write-through plus Flush(true) ensures a crash cannot leave a partially
                // written JSON document at the temporary path before the rename.
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           options: FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                AtomicReplace(temporaryPath);
                _preserveRecoveryBackup = false;
                LoadWarning = null;
            }
            finally
            {
                // If replacement failed, do not leave a stale file that a later process could
                // mistake for the current configuration.
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private AppSettings? DeserializeFile(string path)
    {
        // Read through a fixed-size buffer rather than File.ReadAllBytes. The latter allocates
        // according to the file length, so a concurrent replacement could bypass a preliminary
        // size check before the limit is enforced.
        var bytes = new byte[MaxConfigBytes + 1];
        var count = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
        }

        if (count > MaxConfigBytes)
            throw new InvalidDataException($"The ProxiLock settings file exceeds {MaxConfigBytes} bytes.");
        var offset = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return JsonSerializer.Deserialize<AppSettings>(bytes.AsSpan(offset, count - offset), _options)
            ?? throw new InvalidDataException("The ProxiLock settings file is empty.");
    }

    private static bool IsReadFailure(Exception exception)
        => exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static bool IsMissing(Exception exception)
        => exception is FileNotFoundException or DirectoryNotFoundException;

    private void AtomicReplace(string temporaryPath)
    {
        if (!File.Exists(_path))
        {
            File.Move(temporaryPath, _path);
            return;
        }

        // File.Replace is an atomic rename on NTFS and leaves a one-generation recovery copy
        // if the process or machine fails immediately after the replace. Some file systems do
        // not support replacement; Move(..., overwrite: true) is the compatible fallback.
        var backupPath = _path + ".bak";
        try
        {
            // The primary file may still be corrupt after a backup was loaded. Preserve the
            // known-good recovery copy instead of replacing it with that corrupt file.
            File.Replace(temporaryPath, _path, _preserveRecoveryBackup ? null : backupPath,
                ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (IOException) when (!OperatingSystem.IsWindows())
        {
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
            throw new InvalidDataException("The ProxiLock settings file contains an invalid lock mode.");
        return settings;
    }
}
