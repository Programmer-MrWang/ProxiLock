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
    public IReadOnlyList<UsbDeviceInfo> Scan()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Removable)
            .ToList();
        var hardwareIds = TryReadHardwareIds();
        return drives.Select(d =>
        {
            var letter = d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
            var key = letter.ToUpperInvariant();
            var id = hardwareIds.TryGetValue(key, out var hardwareId)
                ? hardwareId
                : GetVolumeFallbackId(letter);
            return new UsbDeviceInfo
            {
                DriveLetter = letter,
                Name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "USB Drive" : d.VolumeLabel,
                InstanceId = id
            };
        }).ToList();
    }

    public bool IsPresent(string? instanceId)
        => !string.IsNullOrWhiteSpace(instanceId)
           && Scan().Any(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> TryReadHardwareIds()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    if (string.IsNullOrWhiteSpace(pnpId)
                        || (!string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(mediaType, "Removable Media", StringComparison.OrdinalIgnoreCase)))
                        continue;

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
                                        result[letter.ToUpperInvariant()] = pnpId;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // WMI is optional; the volume serial fallback below keeps scanning alive.
        }
        return result;
    }

    private static string GetVolumeFallbackId(string driveLetter)
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
                return $"VOLUME:{serial:X8}";
        }
        catch { }

        // Last-resort compatibility for unusual virtual/removable volumes.
        return $"DRIVE:{driveLetter.ToUpperInvariant()}";
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
