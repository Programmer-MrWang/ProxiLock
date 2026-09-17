using Microsoft.Win32;

namespace ProxiLock.Services;

public sealed class AutoStartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProxiLock";

    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key is null) return;

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable)) return;
                var command = $"\"{executable}\"";
                // Avoid a needless registry write on every settings change.
                if (string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                    return;
                key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Autostart is optional; a locked-down registry must not stop the
            // tray application from starting or being used.
        }
    }
}
