using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ProxiLock.Services;

/// <summary>
/// Owns the native, borderless input-capture windows used while ProxiLock is locked.
/// The windows are created and destroyed on one dedicated Win32 message-loop thread;
/// this makes cleanup deterministic even when a policy monitor or shutdown callback
/// originates on another thread.
/// </summary>
public sealed class LockOverlayManager : IDisposable
{
    private readonly object _stateGate = new();
    private readonly object _commandGate = new();
    private readonly ManualResetEventSlim _threadReady = new(false);
    private readonly List<IntPtr> _windows = new();

    // Tracks the monitor each window was created for. Monitor handles become
    // invalid when a display is unplugged, which is how orphaned windows are
    // detected after Windows relocates them onto a surviving monitor.
    private readonly Dictionary<IntPtr, IntPtr> _windowMonitors = new();

    private Thread? _thread;
    private uint _threadId;
    private volatile TaskCompletionSource<Exception?>? _pending;
    private long _nextToken;
    private long _pendingToken;
    private bool _disposed;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly WndProc OverlayWndProc = WindowProc;
    private static readonly MonitorEnumProc MonitorCallback = MonitorCallbackImpl;
    private static readonly object ClassGate = new();
    private static ushort _classAtom;
    private static readonly string ClassName = $"ProxiLock.LockOverlay.{Environment.ProcessId}";

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwShownoactivate = 4;

    private const uint WmClose = 0x0010;
    private const uint WmErasebkgnd = 0x0014;
    private const uint WmSetcursor = 0x0020;
    private const uint WmMouseactivate = 0x0021;
    private const uint WmNchittest = 0x0084;
    private const uint WmNcactivate = 0x0086;
    private const uint WmContextmenu = 0x007B;
    private const uint WmActivateapp = 0x001C;
    private const uint WmWindowposchanging = 0x0046;
    private const uint WmSyscommand = 0x0112;
    private const uint WmLbuttondown = 0x0201;
    private const uint WmLbuttonup = 0x0202;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint WmRbuttondown = 0x0204;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmRbuttondblclk = 0x0206;
    private const uint WmMbuttondown = 0x0207;
    private const uint WmMbuttonup = 0x0208;
    private const uint WmMbuttondblclk = 0x0209;
    private const uint WmMousewheel = 0x020A;
    private const uint WmXbuttondown = 0x020B;
    private const uint WmXbuttonup = 0x020C;
    private const uint WmXbuttondblclk = 0x020D;
    private const uint WmMousehwheel = 0x020E;
    private const uint WmNclbuttondown = 0x00A1;
    private const uint WmNclbuttonup = 0x00A2;
    private const uint WmNclbuttondblclk = 0x00A3;
    private const uint WmNcrbuttondown = 0x00A4;
    private const uint WmNcrbuttonup = 0x00A5;
    private const uint WmNcrbuttondblclk = 0x00A6;
    private const uint WmNcmbuttondown = 0x00A7;
    private const uint WmNcmbuttonup = 0x00A8;
    private const uint WmNcmbuttondblclk = 0x00A9;
    private const uint WmNcxbuttondown = 0x00AB;
    private const uint WmNcxbuttonup = 0x00AC;
    private const uint WmNcxbuttondblclk = 0x00AD;

    private const uint WmApp = 0x8000;
    private const uint CommandShow = WmApp + 1;
    private const uint CommandHide = WmApp + 2;
    private const uint CommandReassert = WmApp + 4;
    private const int MaNoactivateAndEat = 4;
    private static readonly IntPtr Htclient = new(1);

    public bool IsVisible
    {
        get { lock (_stateGate) return _windows.Count > 0; }
    }

    public void Show()
    {
        lock (_commandGate)
        {
            ThrowIfDisposed();
            EnsureThread();
            SendCommandAndWait(CommandShow, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Reconciles the capture layer with the current display configuration: repairs
    /// changed bounds, creates windows for monitors that appeared, and removes
    /// windows for monitors that disappeared. Existing coverage is never torn down
    /// first, so input is not briefly unblocked while the layer is refreshed.
    /// </summary>
    public void ReassertTopmost() => PostCommand(CommandReassert);

    public void Hide()
    {
        lock (_commandGate)
        {
            if (_thread is not { IsAlive: true })
            {
                lock (_stateGate)
                {
                    _windows.Clear();
                    _windowMonitors.Clear();
                }
                return;
            }

            SendCommandAndWait(CommandHide, TimeSpan.FromSeconds(3));
        }
    }

    public void Dispose()
    {
        lock (_commandGate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_thread is { IsAlive: true })
            {
                TrySendCommandAndWait(CommandHide, TimeSpan.FromSeconds(2));
                PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(TimeSpan.FromSeconds(3));
            }

            lock (_stateGate)
            {
                _windows.Clear();
                _windowMonitors.Clear();
            }
            _threadReady.Dispose();
        }
    }

    private void EnsureThread()
    {
        lock (_stateGate)
        {
            if (_thread is { IsAlive: true }) return;
            _threadReady.Reset();
            _thread = new Thread(OverlayThreadMain)
            {
                IsBackground = true,
                Name = "ProxiLock Overlay"
            };
            _thread.Start();
        }

        if (!_threadReady.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("Lock overlay thread did not start.");
    }

    private void OverlayThreadMain()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoremove);
        _threadReady.Set();

        try
        {
            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;

                if (message.Message is CommandShow or CommandHide or CommandReassert)
                {
                    // A non-zero wParam carries the token of a synchronous command
                    // whose caller is waiting on the matching completion source.
                    var pending = message.WParam != IntPtr.Zero ? _pending : null;
                    var token = message.WParam.ToInt64();
                    try
                    {
                        switch (message.Message)
                        {
                            case CommandShow: SyncWindowsCore(); break;
                            case CommandHide: DestroyWindowsCore(); break;
                            // A reassert can be posted just before an unlock; without this
                            // guard it would run after teardown and recreate the capture
                            // layer over an unlocked screen.
                            case CommandReassert: if (IsVisible) SyncWindowsCore(); break;
                        }
                        if (pending is not null && Interlocked.Read(ref _pendingToken) == token)
                            pending.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        if (pending is not null && Interlocked.Read(ref _pendingToken) == token)
                            pending.TrySetResult(ex);
                    }
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            try { DestroyWindowsCore(); } catch { }
            lock (_stateGate)
            {
                _threadId = 0;
                _thread = null;
            }
        }
    }

    private void SendCommandAndWait(uint command, TimeSpan timeout)
    {
        // Each synchronous command gets its own token and completion source, so a
        // command that timed out can never be mistaken for the next command's reply.
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = Interlocked.Increment(ref _nextToken);
        Interlocked.Exchange(ref _pendingToken, token);
        _pending = completion;
        try
        {
            if (!PostThreadMessage(_threadId, command, new IntPtr(token), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to send lock overlay command.");
            if (!completion.Task.Wait(timeout))
                throw new TimeoutException("Lock overlay command timed out.");
        }
        finally
        {
            if (Interlocked.Read(ref _pendingToken) == token)
                Interlocked.Exchange(ref _pendingToken, 0);
            _pending = null;
        }

        var error = completion.Task.Result;
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void TrySendCommandAndWait(uint command, TimeSpan timeout)
    {
        try { SendCommandAndWait(command, timeout); } catch { }
    }

    private void PostCommand(uint command)
    {
        if (_disposed) return;
        uint threadId;
        lock (_stateGate) threadId = _threadId;
        if (threadId != 0) PostThreadMessage(threadId, command, IntPtr.Zero, IntPtr.Zero);
    }

    private void SyncWindowsCore()
    {
        EnsureWindowClass();
        // Each entry is keyed by monitor handle; IntPtr.Zero is the synthetic
        // virtual-screen rectangle used only when enumeration yields nothing.
        var monitors = EnumerateMonitors();
        var live = new HashSet<IntPtr>(monitors.Select(m => m.Handle));

        IntPtr[] existing;
        lock (_stateGate) existing = _windows.ToArray();

        // Repair coverage that is already in place: drop windows whose monitor is
        // gone and resize the rest. Nothing is destroyed first, so the screen never
        // becomes interactive while the layer is being refreshed.
        foreach (var hwnd in existing)
        {
            if (!IsWindow(hwnd)) { RemoveWindow(hwnd); continue; }

            IntPtr recorded;
            lock (_stateGate) _windowMonitors.TryGetValue(hwnd, out recorded);
            if (!live.Contains(recorded))
            {
                RemoveWindow(hwnd);
                continue;
            }

            MoveWindowToRect(hwnd, GetMonitorRectFromHandle(recorded));
        }

        // Add coverage for monitors that are not represented yet.
        var covered = new HashSet<IntPtr>();
        lock (_stateGate)
        {
            foreach (var hwnd in _windows)
                if (_windowMonitors.TryGetValue(hwnd, out var recorded))
                    covered.Add(recorded);
        }

        foreach (var monitor in monitors)
        {
            if (covered.Contains(monitor.Handle)) continue;
            CreateWindowForMonitor(monitor.Handle, monitor.Rect);
        }
    }

    private void CreateWindowForMonitor(IntPtr monitorHandle, RECT monitor)
    {
        var width = Math.Max(1, monitor.Right - monitor.Left);
        var height = Math.Max(1, monitor.Bottom - monitor.Top);
        var hwnd = CreateWindowEx(
            WsExTopmost | WsExToolWindow | WsExLayered | WsExNoActivate,
            ClassName, "ProxiLock", WsPopup,
            monitor.Left, monitor.Top, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create lock overlay window.");

        // Explicitly strip every caption/system-menu/resizing style. WS_POPUP
        // already has no title bar, but this also repairs any shell-injected style.
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
        style |= WsPopup;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        SetLayeredWindowAttributes(hwnd, 0, 1, LwaAlpha);
        SetWindowPos(hwnd, HwndTopmost, monitor.Left, monitor.Top, width, height, SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder | SwpFrameChanged);
        ShowWindow(hwnd, SwShownoactivate);

        lock (_stateGate)
        {
            _windows.Add(hwnd);
            _windowMonitors[hwnd] = monitorHandle;
        }
    }

    private void RemoveWindow(IntPtr hwnd)
    {
        try { if (IsWindow(hwnd)) DestroyWindow(hwnd); } catch { }
        lock (_stateGate)
        {
            _windows.Remove(hwnd);
            _windowMonitors.Remove(hwnd);
        }
    }

    private void DestroyWindowsCore()
    {
        IntPtr[] windows;
        lock (_stateGate) windows = _windows.ToArray();
        foreach (var hwnd in windows)
        {
            try { if (IsWindow(hwnd)) DestroyWindow(hwnd); } catch { }
        }
        lock (_stateGate)
        {
            _windows.Clear();
            _windowMonitors.Clear();
        }
    }

    private static void MoveWindowToRect(IntPtr hwnd, RECT rect)
    {
        if (!IsWindow(hwnd)) return;
        SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
    }

    private static RECT GetMonitorRectFromHandle(IntPtr monitor)
    {
        var info = new MONITORINFO { CbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.RcMonitor;
        return VirtualScreenRect();
    }

    private static RECT GetMonitorRect(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return GetMonitorRectFromHandle(monitor);
    }

    private static RECT VirtualScreenRect()
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        return new RECT
        {
            Left = left,
            Top = top,
            Right = left + Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)),
            Bottom = top + Math.Max(1, GetSystemMetrics(SmCyVirtualScreen))
        };
    }

    private static void EnsureWindowClass()
    {
        if (_classAtom != 0) return;
        lock (ClassGate)
        {
            if (_classAtom != 0) return;
            var wc = new WndClassEx
            {
                CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(OverlayWndProc),
                HInstance = GetModuleHandle(null),
                HCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
                LpszClassName = ClassName
            };
            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register lock overlay window class.");
        }
    }

    private static List<(IntPtr Handle, RECT Rect)> EnumerateMonitors()
    {
        List<(IntPtr Handle, RECT Rect)> monitors;
        lock (MonitorResultsGate)
        {
            MonitorResults.Clear();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);
            monitors = new List<(IntPtr, RECT)>(MonitorResults);
        }
        if (monitors.Count == 0)
            monitors.Add((IntPtr.Zero, VirtualScreenRect()));
        return monitors;
    }

    private static readonly object MonitorResultsGate = new();
    private static readonly List<(IntPtr Handle, RECT Rect)> MonitorResults = new();
    private static bool MonitorCallbackImpl(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
    {
        lock (MonitorResultsGate) MonitorResults.Add((hMonitor, rect));
        return true;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmMouseactivate: return new IntPtr(MaNoactivateAndEat);
            case WmNchittest: return Htclient;
            case WmSetcursor: SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32512)); return new IntPtr(1);
            case WmWindowposchanging:
                if (lParam != IntPtr.Zero)
                {
                    try
                    {
                        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                        var rect = GetMonitorRect(hwnd);
                        position.X = rect.Left;
                        position.Y = rect.Top;
                        position.Cx = rect.Right - rect.Left;
                        position.Cy = rect.Bottom - rect.Top;
                        position.Flags &= ~(SwpNoMove | SwpNoSize);
                        Marshal.StructureToPtr(position, lParam, false);
                    }
                    catch { }
                }
                return IntPtr.Zero;
            case WmContextmenu:
            case WmSyscommand:
            case WmClose:
            case WmNcactivate:
            case WmActivateapp:
                return IntPtr.Zero;
            case WmErasebkgnd:
                return new IntPtr(1);
            case WmLbuttondown: case WmLbuttonup: case WmLbuttondblclk:
            case WmRbuttondown: case WmRbuttonup: case WmRbuttondblclk:
            case WmMbuttondown: case WmMbuttonup: case WmMbuttondblclk:
            case WmMousewheel: case WmMousehwheel:
            case WmXbuttondown: case WmXbuttonup: case WmXbuttondblclk:
            case WmNclbuttondown: case WmNclbuttonup: case WmNclbuttondblclk:
            case WmNcrbuttondown: case WmNcrbuttonup: case WmNcrbuttondblclk:
            case WmNcmbuttondown: case WmNcmbuttonup: case WmNcmbuttondblclk:
            case WmNcxbuttondown: case WmNcxbuttonup: case WmNcxbuttondblclk:
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LockOverlayManager));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize; public uint Style; public IntPtr LpfnWndProc; public int CbClsExtra; public int CbWndExtra;
        public IntPtr HInstance; public IntPtr HIcon; public IntPtr HCursor; public IntPtr HbrBackground;
        public string? LpszMenuName; public string LpszClassName; public IntPtr HIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint CbSize; public RECT RcMonitor; public RECT RcWork; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr Hwnd; public IntPtr HwndInsertAfter; public int X, Y, Cx, Cy; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int PtX; public int PtY; public uint Private; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GwlStyle = -16;
    private const uint PmNoremove = 0x0000;
    private const uint MonitorDefaultToNearest = 2;
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
