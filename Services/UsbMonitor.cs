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
    /// The volume serial is deliberately kept with the cached WMI entry. A drive letter is
    /// not an identity: Windows can assign the same letter to a different USB disk as soon as
    /// the original one is removed. Cached entries are only trusted when their serial still
    /// matches the volume currently mounted at that letter.
    /// </summary>
    private readonly record struct UsbVolumeHardware(string PnpId, string? Model, string? VolumeSerial);

    private readonly record struct HardwareSnapshot(
        IReadOnlyDictionary<string, UsbVolumeHardware> Entries,
        bool IsFresh);

    public IReadOnlyList<UsbDeviceInfo> Scan()
    {
        var hardware = GetUsbVolumeHardware();
        var retriedHardware = false;
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
            try
            {
                if (!drive.IsReady) continue;

                var letter = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
                var key = letter.ToUpperInvariant();
                var serial = TryGetVolumeSerial(letter);

                // A PNP-only volume cannot validate a cached drive-letter association. Read
                // WMI again before deciding rather than alternately accepting it on refresh
                // ticks and treating it as absent for the rest of the cache lifetime. One
                // additional query covers every remaining drive in this scan.
                if (!hardware.IsFresh && !retriedHardware
                    && hardware.Entries.TryGetValue(key, out var cachedHardware)
                    && (serial is null || cachedHardware.VolumeSerial is null))
                {
                    retriedHardware = true;
                    hardware = GetUsbVolumeHardware(forceRefresh: true);
                }

                // A freshly read WMI map is safe even when the volume serial API is
                // unavailable; a cached map is only safe with an exact serial match.
                var usbBacked = hardware.Entries.TryGetValue(key, out var hardwareInfo)
                    && IsHardwareMatch(serial, hardwareInfo.VolumeSerial, hardware.IsFresh);

                var driveType = drive.DriveType;
                // Removable media is listed even without a WMI answer; fixed disks only count
                // when WMI identified their physical bus as USB. That distinction is what makes
                // USB hard disks and SSDs work, since Windows reports them as fixed drives.
                if (!usbBacked && driveType != DriveType.Removable) continue;

                // A credential must have an identity that survives drive-letter reuse. If both
                // WMI and the volume serial API are unavailable, keep the volume visible for
                // diagnostics but leave its identity empty so it cannot be saved as a lock
                // credential. Never manufacture DRIVE:E:, which any replacement disk could
                // satisfy.
                var instanceId = ResolveInstanceId(serial, usbBacked ? hardwareInfo.PnpId : null) ?? string.Empty;
                var identifiers = new List<string>();
                if (!string.IsNullOrWhiteSpace(instanceId))
                    identifiers.Add(instanceId);
                if (usbBacked && !string.IsNullOrWhiteSpace(hardwareInfo.PnpId))
                    identifiers.Add(hardwareInfo.PnpId);
                if (serial is not null)
                    identifiers.Add($"VOLUME:{serial}");

                var model = usbBacked ? hardwareInfo.Model : null;
                var volumeLabel = drive.VolumeLabel;
                var name = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel
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
            catch
            {
                // A drive can disappear between enumeration and inspection.
                continue;
            }
        }

        return result;
    }

    /// <summary>
    /// A cached drive-letter association is valid only with matching volume serials.
    /// When either serial is unavailable, a fresh WMI association is required.
    /// </summary>
    internal static bool IsHardwareMatch(string? currentSerial, string? recordedSerial, bool fresh)
        => currentSerial is not null && recordedSerial is not null
            ? string.Equals(currentSerial, recordedSerial, StringComparison.OrdinalIgnoreCase)
            : fresh;

    // Prefer physical identity; a volume serial is the fallback. Never use a drive letter.
    private static string? ResolveInstanceId(string? volumeSerial, string? pnpId)
    {
        if (!string.IsNullOrWhiteSpace(pnpId)) return pnpId;
        return volumeSerial is not null ? $"VOLUME:{volumeSerial}" : null;
    }

    /// <summary>
    /// True when a device carrying the saved identifier is currently attached. Matching
    /// considers each verified identifier exposed by the current scan.
    /// </summary>
    public bool IsPresent(string? instanceId)
        => !string.IsNullOrWhiteSpace(instanceId)
           && Scan().Any(d => d.Identifiers.Contains(instanceId, StringComparer.OrdinalIgnoreCase));

    private HardwareSnapshot GetUsbVolumeHardware(bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (!forceRefresh && DateTimeOffset.UtcNow - _hardwareCacheAt < HardwareCacheTtl)
                return new HardwareSnapshot(_hardwareCache, IsFresh: false);
        }

        var read = ReadUsbVolumeHardware();
        lock (_gate)
        {
            // A failed query may keep the old map for continuity, but Scan validates each
            // cached entry against its volume serial before trusting it. A successful empty
            // query must clear the old map so an unplugged disk is never retained.
            if (read.Succeeded) _hardwareCache = read.Entries;
            _hardwareCacheAt = DateTimeOffset.UtcNow;
            return new HardwareSnapshot(_hardwareCache, read.Succeeded);
        }
    }

    private readonly record struct HardwareReadResult(
        Dictionary<string, UsbVolumeHardware> Entries,
        bool Succeeded);

    private static HardwareReadResult ReadUsbVolumeHardware()
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
                                    {
                                        var volumeSerial = logicalDisk["VolumeSerialNumber"]?.ToString();
                                        if (string.IsNullOrWhiteSpace(volumeSerial)) volumeSerial = null;
                                        result[letter.ToUpperInvariant()] = new UsbVolumeHardware(pnpId, model, volumeSerial);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // WMI is optional. The caller may use a previously cached entry only when its
            // volume serial still matches; a different disk in the same drive letter is then
            // rejected instead of being mistaken for the configured credential.
            return new HardwareReadResult(result, Succeeded: false);
        }
        return new HardwareReadResult(result, Succeeded: true);
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
