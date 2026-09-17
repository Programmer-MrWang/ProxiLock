using H.NotifyIcon;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ProxiLock.Services;

/// <summary>
/// Shows user-facing lock/unlock feedback. A tray balloon is preferred because an
/// unpackaged application usually cannot raise a toast; the toast API is a fallback
/// for environments where the tray icon is unavailable, and a system sound is the
/// last resort so a state change is never completely silent.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private TaskbarIcon? _trayIcon;

    /// <summary>Supplies the tray icon used to raise balloon notifications.</summary>
    public void AttachTrayIcon(TaskbarIcon trayIcon) => _trayIcon = trayIcon;

    public void Show(string title, string message)
    {
        if (TryShowTrayBalloon(title, message)) return;
        if (TryShowToast(title, message)) return;
        try { Console.Beep(); } catch { }
    }

    private bool TryShowTrayBalloon(string title, string message)
    {
        var trayIcon = _trayIcon;
        if (trayIcon is null) return false;
        try
        {
            trayIcon.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info, largeIcon: false, sound: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryShowToast(string title, string message)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml($"<toast><visual><binding template='ToastGeneric'><text>{Escape(title)}</text><text>{Escape(message)}</text></binding></visual></toast>");
            ToastNotificationManager.CreateToastNotifier("ProxiLock").Show(new ToastNotification(xml));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    public void Dispose() => _trayIcon = null;
}
