using Microsoft.Win32;

namespace ProxiLock.Services;

public sealed class AutoStartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProxiLock";

    /// <summary>
    /// Enables or disables launch at sign-in.
    /// </summary>
    /// <returns>
    /// True when the registry now reflects the requested state. The caller reports a false
    /// result to the user: silently swallowing the failure left people believing autostart
    /// was configured on machines where policy blocks the registry write.
    /// </returns>
    public bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key is null) return false;

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable)) return false;
                var command = $"\"{executable}\"";
                // Avoid a needless registry write on every settings change.
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            // Autostart is optional; a locked-down registry must not stop the
            // tray application from starting or being used.
            return false;
        }
    }
}
