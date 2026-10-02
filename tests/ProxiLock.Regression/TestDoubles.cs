// Only UI/native side effects are replaced. Policy, monitor, persistence and keyboard
// state-machine source files are linked directly from the shipping application.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private readonly Queue<Action> _queue = new();
        public bool HasThreadAccess { get; set; } = true;
        public bool AcceptEnqueue { get; set; } = true;
        public bool TryEnqueue(Action callback)
        {
            if (!AcceptEnqueue) return false;
            _queue.Enqueue(callback);
            return true;
        }
        public void Drain()
        {
            HasThreadAccess = true;
            while (_queue.TryDequeue(out var callback)) callback();
        }
    }
}

namespace ProxiLock.Services
{
    public sealed class NotificationService : IDisposable
    {
        public List<string> Messages { get; } = new();
        public void Show(string title, string message) => Messages.Add(message);
        public void Dispose() { }
    }

    public sealed class AutoStartService
    {
        public int ApplyCalls { get; private set; }
        public bool Apply(bool enabled) { ApplyCalls++; return true; }
    }

    public sealed class LockOverlayManager : IDisposable
    {
        public bool IsVisible { get; set; }
        public bool FailReassert { get; set; }
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public void Show()
        {
            ShowCalls++;
            // Always stop before the real keyboard hook can be installed.
            throw new InvalidOperationException("Simulated native overlay failure");
        }
        public void ReassertTopmost()
        {
            if (FailReassert) throw new InvalidOperationException("Simulated display refresh failure");
        }
        public void Hide() { HideCalls++; IsVisible = false; }
        public void Dispose() => Hide();
    }
}

namespace ProxiLock
{
    public sealed class MainWindow
    {
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
    }
}
