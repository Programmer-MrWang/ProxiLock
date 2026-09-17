using Microsoft.UI.Xaml;
using System.Windows.Input;

namespace ProxiLock;

public sealed partial class TrayHost : Microsoft.UI.Xaml.Controls.UserControl
{
    public ICommand OpenSettingsCommand { get; } =
        new DelegateCommand(_ => App.CurrentApp.ShowSettings());

    public TrayHost()
    {
        InitializeComponent();
        TrayIcon.LeftClickCommand = OpenSettingsCommand;
        TrayIcon.DoubleClickCommand = OpenSettingsCommand;
        // Let lock/unlock feedback be raised as a tray balloon, which works in the
        // unpackaged deployment where toast notifications are usually unavailable.
        App.Services.Notifications.AttachTrayIcon(TrayIcon);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowSettings();

    private void Exit_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ExitApplication();

    private sealed class DelegateCommand : ICommand
    {
        private readonly Action<object?> _execute;

        public DelegateCommand(Action<object?> execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}
