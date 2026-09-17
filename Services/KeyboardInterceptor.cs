using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProxiLock.Services;

/// <summary>
/// Installs a WH_KEYBOARD_LL hook on a dedicated thread. While active every
/// keyboard message is swallowed except that Ctrl+Shift+L requests an unlock.
/// </summary>
public sealed class KeyboardInterceptor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint PmNoremove = 0x0000;

    private const int VkL = 0x4C;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;

    private readonly object _gate = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly ManualResetEventSlim _threadReady = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Action? _unlockRequested;
    private int _active;
    private volatile bool _installFailed;

    // Accessed only by the hook thread.
    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _lDown;
    private int _unlockPosted;

    public KeyboardInterceptor() => _proc = HookCallback;

    public bool IsInstalled => Volatile.Read(ref _hook) != IntPtr.Zero;

    public void Start(Action unlockRequested)
    {
        ArgumentNullException.ThrowIfNull(unlockRequested);
        lock (_gate)
        {
            if (Volatile.Read(ref _active) != 0) return;
            Volatile.Write(ref _active, 1);
            _installFailed = false;
            _unlockRequested = unlockRequested;
            _threadReady.Reset();
            _leftCtrlDown = _rightCtrlDown = _leftShiftDown = _rightShiftDown = _lDown = false;
            Interlocked.Exchange(ref _unlockPosted, 0);
            _thread = new Thread(HookThread)
            {
                IsBackground = true,
                Name = "ProxiLock.KeyboardHook"
            };
            _thread.Start();
        }

        // Wait until the worker has created its message queue and attempted hook
        // installation. This prevents Stop() racing PostThreadMessage before a queue exists.
        if (!_threadReady.Wait(TimeSpan.FromSeconds(2)) || _installFailed)
        {
            Stop();
        }
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        // A thread message queue is lazily created by User32. PeekMessage forces
        // creation before the owner thread can call PostThreadMessage during Stop.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoremove);

        try
        {
            // Passing the current executable module is required for a process-wide
            // low-level hook and also works for single-file deployments.
            var module = GetModuleHandle(null);
            var hook = SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
            Volatile.Write(ref _hook, hook);
            if (hook == IntPtr.Zero)
                _installFailed = true;
        }
        catch
        {
            _installFailed = true;
        }
        finally
        {
            _threadReady.Set();
        }

        if (_installFailed)
        {
            Volatile.Write(ref _active, 0);
            return;
        }

        try
        {
            while (Volatile.Read(ref _active) != 0 && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch { }
        finally
        {
            var hook = Volatile.Read(ref _hook);
            if (hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(hook); } catch { }
                Volatile.Write(ref _hook, IntPtr.Zero);
            }
            Volatile.Write(ref _active, 0);
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        IntPtr hook;
        lock (_gate)
        {
            Volatile.Write(ref _active, 0);
            _unlockRequested = null;
            thread = _thread;
            threadId = _threadId;
            hook = Volatile.Read(ref _hook);
        }

        // Unhook immediately so no further input is swallowed while the worker exits.
        if (hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(hook); } catch { }
            Volatile.Write(ref _hook, IntPtr.Zero);
        }
        if (threadId != 0)
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (thread is { IsAlive: true } && !ReferenceEquals(Thread.CurrentThread, thread))
            thread.Join(2000);

        lock (_gate)
        {
            _thread = null;
            _threadId = 0;
            _leftCtrlDown = _rightCtrlDown = _leftShiftDown = _rightShiftDown = _lDown = false;
            Interlocked.Exchange(ref _unlockPosted, 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Volatile.Read(ref _active) != 0)
        {
            var message = unchecked((int)wParam.ToInt64());
            var vkCode = Marshal.ReadInt32(lParam);
            var isDown = message is WmKeydown or WmSyskeydown;
            var isUp = message is WmKeyup or WmSyskeyup;

            if (isDown)
            {
                switch (vkCode)
                {
                    case VkLControl:
                    case VkControl: _leftCtrlDown = true; break;
                    case VkRControl: _rightCtrlDown = true; break;
                    case VkLShift:
                    case VkShift: _leftShiftDown = true; break;
                    case VkRShift: _rightShiftDown = true; break;
                    case VkL: _lDown = true; break;
                }

                if (_lDown && (_leftCtrlDown || _rightCtrlDown) && (_leftShiftDown || _rightShiftDown) && Interlocked.Exchange(ref _unlockPosted, 1) == 0)
                {
                    PostUnlockRequest();
                }
            }
            else if (isUp)
            {
                switch (vkCode)
                {
                    case VkLControl:
                    case VkControl: _leftCtrlDown = false; break;
                    case VkRControl: _rightCtrlDown = false; break;
                    case VkLShift:
                    case VkShift: _leftShiftDown = false; break;
                    case VkRShift: _rightShiftDown = false; break;
                    case VkL:
                        _lDown = false;
                        Interlocked.Exchange(ref _unlockPosted, 0);
                        break;
                }
            }

            // Every keyboard event is blocked while locked, including the modifiers.
            if (isDown || isUp)
                return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void PostUnlockRequest()
    {
        var callback = _unlockRequested;
        if (callback is not null)
        {
            ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), callback);
        }
    }

    public void Dispose()
    {
        Stop();
        _threadReady.Dispose();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam; public IntPtr LParam; public uint Time; public int PtX; public int PtY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint minFilter, uint maxFilter, uint removeMsg);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint minFilter, uint maxFilter);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
}
