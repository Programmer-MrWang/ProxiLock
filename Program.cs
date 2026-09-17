using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WinRT;

namespace ProxiLock;

internal static class Program
{
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            _instanceMutex = new Mutex(true, "ProxiLock.SingleInstance", out var createdNew);
            if (!createdNew)
                return;

            SetProcessDpiAwarenessContext(new IntPtr(-4));
            ComWrappersSupport.InitializeComWrappers();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { App.CurrentApp.ShutdownForProcessExit(); } catch { }
            };

            Application.Start(_initializationParams =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                }
                catch (Exception ex)
                {
                    LogStartupError("Application callback", ex);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            LogStartupError("Application.Start", ex);
            throw;
        }
        finally
        {
            GC.KeepAlive(_instanceMutex);
        }
    }

    private static void LogStartupError(string stage, Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProxiLock", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"{DateTimeOffset.Now:O} [{stage}] {ex}\r\n");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
