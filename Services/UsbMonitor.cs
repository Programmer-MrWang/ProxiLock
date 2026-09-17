using System.Management;
using System.Runtime.InteropServices;
using ProxiLock.Models;

namespace ProxiLock.Services;

/// <summary>
/// Enumerates removable storage and keeps a stable hardware identifier where
/// Windows exposes one. WMI is intentionally best-effort: a locked-down machine
/// can deny WMI access, so a volume-serial fallback still leaves the feature usable.
/// Callers receive an immutable snapshot; scanning is safe from any thread.
/// </summary>
public sealed class UsbMonitor
{
    /// <summary>
    /// How long the WMI hardware map is reused. The map is the expensive part (the
    /// coordinator asks once a second) while the drive list is cheap and is re-read on every
    /// scan, so a device that is unplugged still disappears immediately; only the moment a
    /// newly attached USB hard disk starts counting as USB-backed is delayed by this window.
    /// </summary>
    private static readonly TimeSpan HardwareCacheTtl = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Dictionary<string, UsbVolumeHardware> _hardwareCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _hardwareCacheAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Volume serial to physical-disk PNP id, learned on scans where WMI answered. It is
    /// what lets a device keep its configured identity when a later WMI query fails, or when
    /// the selection was made while WMI was unavailable.
    /// </summary>
    private readonly Dictionary<string, string> _pnpByVolumeSerial = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct UsbVolumeHardware(string PnpId, string? Model);

    public IReadOnlyList<UsbDeviceInfo> Scan()
    {
        var hardware = GetUsbVolumeHardware();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return Array.Empty<UsbDeviceInfo>();
        }

        var result = new List<UsbDeviceInfo>();
        foreach (var drive in drives)
        {
            string letter;
            bool ready;
            try
            {
                ready = drive.IsReady;
                letter = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                // A drive can disappear between enumeration and inspection.
                continue;
            }
            if (!ready) continue;

            var key = letter.ToUpperInvariant();
            var usbBacked = hardware.TryGetValue(key, out var hardwareInfo);
            // Removable media is listed even without a WMI answer; fixed disks only count
            // when WMI identified their physical bus as USB. That distinction is what makes
            // USB hard disks and SSDs work, since Windows reports them as fixed drives.
            if (!usbBacked && drive.DriveType != DriveType.Removable) continue;

            var serial = TryGetVolumeSerial(letter);
            if (usbBacked && serial is not null && !string.IsNullOrWhiteSpace(hardwareInfo.PnpId))
            {
                lock (_gate) _pnpByVolumeSerial[serial] = hardwareInfo.PnpId;
            }

            var instanceId = ResolveInstanceId(key, serial, usbBacked ? hardwareInfo.PnpId : null);
            var identifiers = new List<string> { instanceId };
            if (usbBacked && !string.IsNullOrWhiteSpace(hardwareInfo.PnpId))
                identifiers.Add(hardwareInfo.PnpId);
            if (serial is not null)
                identifiers.Add($"VOLUME:{serial}");
            identifiers.Add($"DRIVE:{key}");

            var model = usbBacked ? hardwareInfo.Model : null;
            var name = !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel
                : !string.IsNullOrWhiteSpace(model) ? model
                : "USB Drive";

            result.Add(new UsbDeviceInfo
            {
                DriveLetter = letter,
                Name = name,
                InstanceId = instanceId,
                Identifiers = identifiers.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        return result;
    }

    /// <summary>
    /// Picks the identifier to persist. WMI's PNP id is preferred because it follows the
    /// physical device across drive letters; the volume serial is next, and a learned
    /// serial-to-PNP association recovers the hardware id when WMI is momentarily unavailable.
    /// </summary>
    private string ResolveInstanceId(string driveLetterKey, string? volumeSerial, string? pnpId)
    {
        if (!string.IsNullOrWhiteSpace(pnpId)) return pnpId;

        if (volumeSerial is not null)
        {
            lock (_gate)
            {
                if (_pnpByVolumeSerial.TryGetValue(volumeSerial, out var learned) && !string.IsNullOrWhiteSpace(learned))
                    return learned;
            }
            return $"VOLUME:{volumeSerial}";
        }

        // Last-resort compatibility for unusual virtual/removable volumes.
        return $"DRIVE:{driveLetterKey}";
    }

    /// <summary>
    /// True when a device carrying the saved identifier is currently attached. Matching
    /// considers every identifier the device exposes, so a value saved under one scheme
    /// still matches when the scan can only produce another.
    /// </summary>
    public bool IsPresent(string? instanceId)
        => !string.IsNullOrWhiteSpace(instanceId)
           && Scan().Any(d => d.Identifiers.Contains(instanceId, StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, UsbVolumeHardware> GetUsbVolumeHardware()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _hardwareCacheAt < HardwareCacheTtl)
                return _hardwareCache;
        }

        var fresh = ReadUsbVolumeHardware();
        lock (_gate)
        {
            // A failed query must not erase a good map; keep the previous one so a transient
            // WMI outage does not make every USB device look unplugged.
            if (fresh.Count > 0) _hardwareCache = fresh;
            _hardwareCacheAt = DateTimeOffset.UtcNow;
            return _hardwareCache;
        }
    }

    private static Dictionary<string, UsbVolumeHardware> ReadUsbVolumeHardware()
    {
        var result = new Dictionary<string, UsbVolumeHardware>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PNPDeviceID, InterfaceType, MediaType, Model FROM Win32_DiskDrive");
            using var disks = searcher.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    var interfaceType = disk["InterfaceType"]?.ToString();
                    var mediaType = disk["MediaType"]?.ToString();
                    var pnpId = disk["PNPDeviceID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(pnpId) || !IsUsbDisk(pnpId, interfaceType, mediaType))
                        continue;

                    var model = disk["Model"]?.ToString();
                    foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                    {
                        using (partition)
                        {
                            foreach (ManagementObject logicalDisk in partition.GetRelated("Win32_LogicalDisk"))
                            {
                                using (logicalDisk)
                                {
                                    var letter = logicalDisk["DeviceID"]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(letter))
                                        result[letter.ToUpperInvariant()] = new UsbVolumeHardware(pnpId, model);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // WMI is optional; the volume serial fallback keeps scanning alive.
        }
        return result;
    }

    /// <summary>
    /// Whether a physical disk is USB storage. The PNP id is the reliable signal: removable
    /// media can report <c>Removable Media</c> or a vendor media type, and USB hard disks and
    /// SSDs are ordinary fixed disks with no media-type hint at all.
    /// </summary>
    private static bool IsUsbDisk(string pnpId, string? interfaceType, string? mediaType)
        => pnpId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
           || pnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
           || string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "Removable Media", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetVolumeSerial(string driveLetter)
    {
        try
        {
            if (GetVolumeInformation(
                    driveLetter + "\\",
                    null,
                    0,
                    out var serial,
                    out _,
                    out _,
                    null,
                    0))
                return serial.ToString("X8");
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string? rootPathName,
        string? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out int maximumComponentLength,
        out int fileSystemFlags,
        string? fileSystemNameBuffer,
        int fileSystemNameSize);
}
