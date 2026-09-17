using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace ProxiLock;

/// <summary>
/// Off-screen owner window for the tray icon. H.NotifyIcon's SecondWindow mode
/// uses this window to create a real XamlRoot for the WinUI context flyout.
/// </summary>
public sealed partial class TrayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;

    public TrayWindow()
    {
        InitializeComponent();
        AppWindow.Title = "ProxiLock Tray";
        AppWindow.Resize(new SizeInt32(1, 1));
        AppWindow.Move(new PointInt32(-10000, -10000));

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle,
                new IntPtr((style & ~WsExAppWindow) | WsExToolWindow));
        }
        catch
        {
            // The off-screen placement is sufficient if shell style APIs fail.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
