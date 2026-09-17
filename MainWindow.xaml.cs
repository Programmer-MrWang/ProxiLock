using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ProxiLock.Models;
using ProxiLock.Services;
using Windows.Graphics;
using WinRT.Interop;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace ProxiLock;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Opacity of a policy page that does not match the selected lock mode. Dimmed enough to
    /// read as "not active", still light enough to read what the page contains.
    /// </summary>
    private const double DisabledSectionOpacity = 0.45;

    private readonly AppServices _services;
    private readonly DispatcherQueueTimer _usbTimer;
    private readonly DispatcherQueueTimer _bluetoothTimer;
    private readonly DispatcherQueueTimer _applyTimer;
    private bool _initialized;
    private bool _allowClose;
    private bool _resourcesShutdown;
    private int _suppressAutoSaveDepth;

    /// <summary>
    /// True while programmatic control updates are in flight. Reference-counted because
    /// these scopes nest (e.g. loading settings triggers a list refresh), and a plain flag
    /// would be cleared by the inner scope while the outer one is still running.
    /// </summary>
    private bool _suppressAutoSave => _suppressAutoSaveDepth > 0;

    private readonly struct SuppressScope : IDisposable
    {
        private readonly MainWindow _owner;
        public SuppressScope(MainWindow owner)
        {
            _owner = owner;
            owner._suppressAutoSaveDepth++;
        }
        public void Dispose() => _owner._suppressAutoSaveDepth--;
    }
    private bool _usbRefreshRunning;
    private bool _bluetoothScanning;
    private bool _titleBarRegionFailed;
    private string _titleBarRegionSignature = string.Empty;
    private string _usbSignature = string.Empty;
    private BluetoothDeviceInfo? _selectedBluetooth;
    private UsbDeviceInfo? _selectedUsb;
    private Button? _paneToggleButton;
    private IntPtr _windowHandle;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        UpdateNavigationPaneVisuals(SettingsNavigationView.IsPaneOpen);
        SetDefaultWindowPlacement();
        AppWindow.Title = "设置 - ProxiLock";

        // Match BetterLyrics' borderless settings-window chrome while keeping
        // the standard system buttons and drag behavior.
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.TitleBar.BackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        // Deliberately leave the hover/pressed button colours unset so they keep the
        // system defaults. Forcing them transparent removed all feedback, leaving no way
        // to tell whether a click had landed on minimise/maximise/close.
        AppWindow.TitleBar.ButtonHoverBackgroundColor = null;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = null;
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // Older Windows 10 builds can lack Mica; the normal page brush remains.
        }
        // AppWindow does not automatically inherit ApplicationIcon for unpackaged WinUI apps.
        // Set the shell/title-bar icon explicitly from the published root asset.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); } catch { /* shell icon is best-effort */ }
        }
        AppWindow.Closing += AppWindow_Closing;

        _usbTimer = DispatcherQueue.CreateTimer();
        _usbTimer.Interval = TimeSpan.FromSeconds(5);
        _usbTimer.Tick += (_, _) => RefreshUsbList();

        _bluetoothTimer = DispatcherQueue.CreateTimer();
        _bluetoothTimer.Interval = TimeSpan.FromMilliseconds(600);
        _bluetoothTimer.Tick += (_, _) => RefreshBluetoothList();

        // Text fields re-validate on every keystroke; coalesce those edits into one
        // apply so a single value does not trigger a config write per character.
        _applyTimer = DispatcherQueue.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(400);
        _applyTimer.IsRepeating = false;
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            ApplyCurrentSettings();
        };

        // The extended title bar supplies a default drag region that swallows pointer
        // input along the top strip. The navigation pane toggle button sits inside that
        // strip, so declare its rectangle as an interactive passthrough region. The
        // button is template-instantiated and may not exist yet at Loaded time, so
        // retry on full layout changes until it resolves; once found, only its own
        // position changes (window resize, pane toggle) need to be re-measured.
        RootGrid.Loaded += (_, _) => AttachPaneToggleWatcher();
        SettingsNavigationView.SizeChanged += (_, _) => TryApplyTitleBarRegions();
        // Theme-derived brushes are assigned in code, so re-apply them if the system
        // flips between light and dark while the window is open.
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateStatus(_services.LockCoordinator?.IsLocked == true);
        };
    }

    private void SetDefaultWindowPlacement()
    {
        var requested = new SizeInt32(1816, 1060);
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;

            // Keep a margin so the window reads as a floating window, but never
            // request more than the display can actually show.
            var width = (int)Math.Min(requested.Width, Math.Max(720, workArea.Width - 120));
            var height = (int)Math.Min(requested.Height, Math.Max(520, workArea.Height - 120));
            var size = new SizeInt32(width, height);
            AppWindow.Resize(size);

            var x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
            var y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // Window placement is best-effort during very early shell startup.
            AppWindow.Resize(requested);
        }
    }

    public void InitializeRuntime()
    {
        if (_initialized) return;
        _initialized = true;
        _services.LockCoordinator.LockStateChanged += LockCoordinator_LockStateChanged;
        LoadSettings(_services.Settings);
        RefreshUsbList();
        UpdateStatus(_services.LockCoordinator.IsLocked);
        // Settings and controls are fully initialized before policy evaluation is armed.
        // This avoids locking from stale pre-launch idle time during startup.
        _services.LockCoordinator.Arm();
    }

    /// <summary>Allows the application to close the shell during an explicit exit.</summary>
    public void AllowClose() => _allowClose = true;

    public void ShowShell()
    {
        if (_windowHandle == IntPtr.Zero)
            _windowHandle = WindowNative.GetWindowHandle(this);
        ShowWindow(_windowHandle, SwShow);
        // Reserve the WMI-backed device scan for the time the list is actually visible.
        if (!_usbTimer.IsRunning) _usbTimer.Start();
        RefreshUsbList();
        UpdateBluetoothRefreshState();
    }

    public void HideShell()
    {
        // Nothing in the shell is observable while it is hidden; stop the periodic
        // scans instead of enumerating disks and refreshing a list nobody can see.
        _usbTimer.Stop();
        _bluetoothTimer.Stop();
        if (_windowHandle == IntPtr.Zero)
            _windowHandle = WindowNative.GetWindowHandle(this);
        ShowWindow(_windowHandle, SwHide);
    }

    /// <summary>
    /// Keeps the Bluetooth list live while it is on screen. Without this the list froze
    /// when the ten-second scan ended: signal strength stopped updating and devices that
    /// went out of range stayed listed. It refreshes only while the radio is actually
    /// collecting data, i.e. during an explicit scan or under the Bluetooth policy.
    /// </summary>
    private void UpdateBluetoothRefreshState()
    {
        var visible = _windowHandle != IntPtr.Zero && IsWindowVisible(_windowHandle);
        var radioActive = _bluetoothScanning
                          || _services.Settings?.LockMode == LockMode.Bluetooth;

        if (visible && radioActive)
        {
            if (!_bluetoothTimer.IsRunning) _bluetoothTimer.Start();
        }
        else
        {
            _bluetoothTimer.Stop();
        }
    }

    /// <summary>Stops UI timers and event subscriptions before process teardown.</summary>
    public void ShutdownResources()
    {
        if (_resourcesShutdown) return;
        _resourcesShutdown = true;
        _usbTimer.Stop();
        _bluetoothTimer.Stop();
        _applyTimer.Stop();
        if (_initialized)
            _services.LockCoordinator.LockStateChanged -= LockCoordinator_LockStateChanged;
    }

    private void LoadSettings(AppSettings settings)
    {
        using var suppress = new SuppressScope(this);
        NoneRadio.IsChecked = settings.LockMode == LockMode.None;
        BluetoothRadio.IsChecked = settings.LockMode == LockMode.Bluetooth;
        UsbRadio.IsChecked = settings.LockMode == LockMode.Usb;
        IdleRadio.IsChecked = settings.LockMode == LockMode.Idle;
        BluetoothThresholdBox.Text = settings.Bluetooth.Threshold?.ToString() ?? string.Empty;
        IdleMinutesBox.Text = settings.Idle.Minutes.ToString();
        AutoStartCheckBox.IsChecked = settings.AutoStart;
        UpdateModeUi(settings.LockMode);
        SetSectionVisibility("Mode");
        if (settings.LockMode == LockMode.Bluetooth) _ = RefreshBluetoothAsync();
    }

    private void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            SetSectionVisibility(tag);
    }

    private void SettingsNavigationView_PaneOpening(NavigationView sender, object args)
    {
        UpdateNavigationPaneVisuals(isPaneOpen: true);
    }

    private void SettingsNavigationView_PaneClosing(NavigationView sender, object args)
    {
        UpdateNavigationPaneVisuals(isPaneOpen: false);
    }

    private void UpdateNavigationPaneVisuals(bool isPaneOpen)
    {
        // The header is useful only when the pane has enough room for its logo and text.
        PaneHeader.Visibility = isPaneOpen ? Visibility.Visible : Visibility.Collapsed;

        var itemMargins = isPaneOpen
            ? new[]
            {
                new Thickness(1, 3, 5, 0),
                new Thickness(14, 0, 5, 0),
                new Thickness(14, 0, 5, 0),
                new Thickness(14, 0, 5, 0)
            }
            : new[]
            {
                new Thickness(1, 3, 1, 0),
                new Thickness(1, 0, 1, 0),
                new Thickness(1, 0, 1, 0),
                new Thickness(1, 0, 1, 0)
            };

        var items = SettingsNavigationView.MenuItems.OfType<NavigationViewItem>().ToArray();
        for (var i = 0; i < items.Length && i < itemMargins.Length; i++)
            items[i].Margin = itemMargins[i];

        var footerItems = SettingsNavigationView.FooterMenuItems.OfType<NavigationViewItem>().ToArray();
        foreach (var item in footerItems)
            item.Margin = isPaneOpen ? new Thickness(1, 0, 5, 7) : new Thickness(1, 0, 1, 7);
    }

    private void SetSectionVisibility(string tag)
    {
        if (ModeSection is null) return;

        var sections = new (string Tag, FrameworkElement Element)[]
        {
            ("Mode", ModeSection),
            ("Bluetooth", BluetoothPanel),
            ("Usb", UsbPanel),
            ("Idle", IdlePanel),
            ("General", GeneralSection)
        };

        foreach (var section in sections)
        {
            section.Element.Visibility = section.Tag == tag ? Visibility.Visible : Visibility.Collapsed;
            if (section.Tag != tag)
            {
                section.Element.Opacity = 1;
                section.Element.RenderTransform = null;
            }
        }

        // Re-apply the enabled/disabled look before the transition runs, so the page settles
        // at the right opacity. Previously the page animation forced opacity back to 1, which
        // silently undid the dimming and left a page that looked active but ignored input.
        var mode = GetSelectedMode();
        RefreshSectionEnabledStates(mode);

        var selected = sections.FirstOrDefault(section => section.Tag == tag).Element;
        if (selected is not null)
            BeginPageTransition(selected, IsSectionEnabled(tag, mode) ? 1 : DisabledSectionOpacity);

        SettingsScrollViewer.ChangeView(null, 0, null);
    }

    /// <summary>
    /// Applies the enabled look to every page. The inactive policy pages stay visible so the
    /// user can see what else exists, but are dimmed and ignore input. This is the single
    /// source of truth for that state, so navigation cannot leave a page looking active while
    /// it silently ignores input.
    /// </summary>
    private void RefreshSectionEnabledStates(LockMode mode)
    {
        ApplySectionEnabledState(BluetoothPanel, IsSectionEnabled("Bluetooth", mode));
        ApplySectionEnabledState(UsbPanel, IsSectionEnabled("Usb", mode));
        ApplySectionEnabledState(IdlePanel, IsSectionEnabled("Idle", mode));
    }

    /// <summary>
    /// Dims a page and stops it accepting input.
    /// </summary>
    /// <remarks>
    /// The opacity is set on the container because StackPanel and TextBlock have no
    /// IsEnabled, so dimming the container is the only way to grey everything on the page,
    /// including its title and a list's empty-state text. IsHitTestVisible is the input
    /// guard, kept as the existing mechanism; IsEnabled is deliberately avoided because the
    /// scan button manages its own enabled state while a scan runs, and writing it here
    /// would clear that.
    /// </remarks>
    private static void ApplySectionEnabledState(Panel element, bool enabled)
    {
        element.IsHitTestVisible = enabled;
        element.Opacity = enabled ? 1 : DisabledSectionOpacity;
    }

    /// <summary>
    /// Whether a page's controls are usable under the current mode. Only the page matching
    /// the selected policy is interactive; the rest are shown dimmed.
    /// </summary>
    private static bool IsSectionEnabled(string tag, LockMode mode) => tag switch
    {
        "Bluetooth" => mode == LockMode.Bluetooth,
        "Usb" => mode == LockMode.Usb,
        "Idle" => mode == LockMode.Idle,
        // The mode selector and the general settings apply regardless of the chosen policy.
        _ => true
    };

    /// <summary>
    /// True when the user has asked Windows to minimise animation. Movement is then
    /// replaced by a short opacity change so the state change is still legible.
    /// </summary>
    private static bool AnimationsDisabled
    {
        get
        {
            try { return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Fades and slides a page in. <paramref name="settledOpacity"/> is the opacity the page
    /// must end at, which is not always 1: an inactive policy page is dimmed to show it is
    /// disabled, and forcing 1 here silently un-dimmed it on every navigation.
    /// </summary>
    private static void BeginPageTransition(FrameworkElement element, double settledOpacity)
    {
        // Reduced motion keeps the cross-fade (which aids comprehension) but drops the
        // slide, so the page change is not a sudden teleport either way.
        var travel = AnimationsDisabled ? 0 : 10;
        var translate = new TranslateTransform { X = 0, Y = travel };
        element.RenderTransform = translate;
        element.Opacity = 0;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = settledOpacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        // An 18px / 320ms slide read as "flying in" rather than settling; a short
        // 200ms rise inside the 300ms UI budget feels like the content arriving.
        if (travel > 0)
        {
            var slide = new DoubleAnimation
            {
                From = travel,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, "Y");
            storyboard.Children.Add(slide);
        }

        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            element.Opacity = settledOpacity;
            element.RenderTransform = null;
        };
        storyboard.Begin();
    }

    private void NavigationItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimateNavigationItem(element, 2);
    }

    private void NavigationItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimateNavigationItem(element, 0);
    }

    private static void AnimateNavigationItem(FrameworkElement element, double target)
    {
        // Navigation is a tens-of-times-a-day interaction, so the hover nudge stays
        // barely perceptible (2px) and is skipped entirely when motion is reduced.
        if (AnimationsDisabled)
        {
            if (element.RenderTransform is TranslateTransform existing)
                existing.X = 0;
            return;
        }

        var translate = element.RenderTransform as TranslateTransform;
        if (translate is null)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, translate);
        Storyboard.SetTargetProperty(animation, "X");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressAutoSave) return;
        var mode = sender switch
        {
            RadioButton { Tag: "Bluetooth" } => LockMode.Bluetooth,
            RadioButton { Tag: "Usb" } => LockMode.Usb,
            RadioButton { Tag: "Idle" } => LockMode.Idle,
            _ => LockMode.None
        };
        UpdateModeUi(mode);
        if (mode == LockMode.Bluetooth) _ = RefreshBluetoothAsync();
        ApplyCurrentSettings();
    }

    private void UpdateModeUi(LockMode mode)
    {
        // Enabled/disabled appearance lives in one place so navigation animations and mode
        // changes cannot drift apart.
        RefreshSectionEnabledStates(mode);

        // A selected policy with no device is a no-op at runtime, which looks identical
        // to a working one. Surface the missing prerequisite instead of implying it works.
        var source = _services.Settings;
        var missingDevice = mode switch
        {
            LockMode.Bluetooth => string.IsNullOrWhiteSpace(_selectedBluetooth?.Address ?? source?.Bluetooth?.DeviceAddress),
            LockMode.Usb => string.IsNullOrWhiteSpace(_selectedUsb?.InstanceId ?? source?.Usb?.DeviceInstanceId),
            _ => false
        };

        BluetoothSelectionSummary.Visibility = mode == LockMode.Bluetooth ? Visibility.Visible : Visibility.Collapsed;
        if (mode == LockMode.Bluetooth)
        {
            // Keep the summary accurate even before a scan runs, so a configured device is
            // stated on startup rather than leaving the note blank. It only reports what is
            // currently bound, which is empty until the first scan.
            var configured = _selectedBluetooth?.Address ?? _services.Settings?.Bluetooth?.DeviceAddress;
            UpdateBluetoothSelectionSummary(configured);
        }

        // Switching modes changes whether the radio should be feeding the list.
        UpdateBluetoothRefreshState();

        if (mode != LockMode.None && missingDevice)
        {
            ModeHintBar.Severity = InfoBarSeverity.Warning;
            ModeHintBar.Title = "还需要选择设备";
            ModeHintBar.Message = mode == LockMode.Bluetooth
                ? "选择一个蓝牙设备后该策略才会生效。"
                : "选择一个 U 盘后该策略才会生效。";
            return;
        }

        var hint = mode == LockMode.None ? "未启用自动锁定" : mode switch
        {
            LockMode.Bluetooth => "检测不到设备或 RSSI 低于阈值时锁定",
            LockMode.Usb => "指定 U 盘拔出时锁定",
            LockMode.Idle => "空闲达到阈值时锁定",
            _ => string.Empty
        };
        ModeHintBar.Severity = InfoBarSeverity.Informational;
        ModeHintBar.Title = mode == LockMode.None ? "未启用自动锁定" : "当前策略已选择";
        ModeHintBar.Message = hint;
    }

    private async void RefreshBluetooth_Click(object sender, RoutedEventArgs e) => await RefreshBluetoothAsync();

    private async Task RefreshBluetoothAsync()
    {
        // A scan runs for ten seconds; ignore repeat clicks instead of starting
        // overlapping timers and watcher stop/start cycles.
        if (_bluetoothScanning) return;
        _bluetoothScanning = true;
        RefreshBluetoothButton.IsEnabled = false;
        // Ten seconds of a dead button reads as a hang; show that scanning is underway.
        BluetoothScanRing.IsActive = true;
        BluetoothScanRing.Visibility = Visibility.Visible;
        BluetoothScanIcon.Visibility = Visibility.Collapsed;
        try
        {
            _services.LockCoordinator.Bluetooth.Start();
            // Drive the live list through the shared helper so the timer is started only
            // when the page is actually visible.
            UpdateBluetoothRefreshState();
            RefreshBluetoothList();
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // A scan is best-effort; the list simply stays as it was.
        }
        finally
        {
            _bluetoothScanning = false;
            RefreshBluetoothButton.IsEnabled = true;
            BluetoothScanRing.IsActive = false;
            BluetoothScanRing.Visibility = Visibility.Collapsed;
            BluetoothScanIcon.Visibility = Visibility.Visible;
            // Release the radio unless the active policy keeps using it.
            if (_services.Settings?.LockMode != LockMode.Bluetooth)
                _services.LockCoordinator.Bluetooth.Stop();
            // Keep the list live only while something is still feeding it data.
            UpdateBluetoothRefreshState();
        }
    }

    private void RefreshBluetoothList()
    {
        var observations = _services.LockCoordinator.Bluetooth.Snapshot();

        using var suppress = new SuppressScope(this);
        // Rows are reconciled against the existing bound objects rather than replacing
        // the whole ItemsSource, so selection and scroll position survive each refresh.
        // The monitor returns plain observations; the bound objects are updated here on
        // the UI thread, which is where property-change notifications must originate.
        if (BluetoothList.ItemsSource is not ObservableCollection<BluetoothDeviceInfo> bound)
        {
            bound = new ObservableCollection<BluetoothDeviceInfo>();
            BluetoothList.ItemsSource = bound;
        }

        var byAddress = bound.ToDictionary(d => d.Address, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var observation in observations)
        {
            seen.Add(observation.Address);
            if (byAddress.TryGetValue(observation.Address, out var existing))
            {
                if (observation.Name is not null) existing.Name = observation.Name;
                existing.Rssi = observation.Rssi;
                existing.IsPaired = observation.IsPaired;
                existing.Connected = observation.IsConnected;
                existing.IsRandomAddress = observation.IsRandomAddress;
            }
            else
            {
                var device = new BluetoothDeviceInfo
                {
                    Address = observation.Address,
                    Rssi = observation.Rssi,
                    IsPaired = observation.IsPaired,
                    Connected = observation.IsConnected,
                    IsRandomAddress = observation.IsRandomAddress
                };
                if (observation.Name is not null) device.Name = observation.Name;
                byAddress[observation.Address] = device;
                bound.Add(device);
            }
        }

        // Drop rows that have aged out of the monitor's listing window.
        for (var i = bound.Count - 1; i >= 0; i--)
            if (!seen.Contains(bound[i].Address))
                bound.RemoveAt(i);

        BluetoothEmptyState.Visibility = bound.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _selectedBluetooth?.Address ?? _services.Settings?.Bluetooth?.DeviceAddress;
        if (!string.IsNullOrWhiteSpace(selected)
            && byAddress.TryGetValue(selected, out var matching)
            && !ReferenceEquals(BluetoothList.SelectedItem, matching))
        {
            BluetoothList.SelectedItem = matching;
            _selectedBluetooth = matching;
        }

        UpdateBluetoothSelectionSummary(selected);
    }

    /// <summary>
    /// Explains the current selection relative to the unlock condition. The list holds
    /// devices seen recently, which is a wider window than the presence test the policy
    /// uses, so this asks the monitor for the authoritative answer rather than inferring
    /// from the list.
    /// </summary>
    private void UpdateBluetoothSelectionSummary(string? configuredAddress)
    {
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            BluetoothSelectionSummary.Text = "当前未选择设备，该策略不会生效。";
            BluetoothThresholdHintBar.IsOpen = false;
            return;
        }

        var monitor = _services.LockCoordinator.Bluetooth;

        // A rotating privacy address cannot be tracked, so selecting one produces a lock
        // that repeatedly engages and releases. Say so plainly instead of letting the user
        // chase an unexplainable flicker.
        if (monitor.IsUnstableIdentity(configuredAddress))
        {
            BluetoothSelectionSummary.Text = "该设备使用随机蓝牙地址，无法持续识别，会导致反复锁定与解锁。请改选已配对的设备，或已打开蓝牙的耳机 / 手机等固定地址设备。";
            BluetoothThresholdHintBar.IsOpen = false;
            return;
        }

        var label = _selectedBluetooth is not null
                    && string.Equals(_selectedBluetooth.Address, configuredAddress, StringComparison.OrdinalIgnoreCase)
            ? $"{_selectedBluetooth.Name}（{configuredAddress}）"
            : configuredAddress;

        var threshold = _services.Settings?.Bluetooth?.Threshold;

        // A threshold on hardware that never reports RSSI cannot be measured. Warn rather
        // than let the user believe a proximity rule is being enforced.
        BluetoothThresholdHintBar.IsOpen = threshold is not null && !monitor.SupportsRssi(configuredAddress);
        if (BluetoothThresholdHintBar.IsOpen)
        {
            BluetoothThresholdHintBar.Message = monitor.IsPaired(configuredAddress)
                ? "该设备是经典蓝牙设备，Windows 不提供其信号强度，因此阈值不会生效；此时以连接状态判断是否在范围内。"
                : "尚未读到该设备的信号强度，阈值暂时无法生效；请确认设备正在广播。";
        }

        var thresholdActive = threshold is int;
        var status = _services.LockCoordinator.BluetoothStatus;
        var manualOverride = _services.LockCoordinator.ManualUnlockOverride == LockReason.Bluetooth;

        // A manual unlock is held until the device is next seen healthy. Reporting the
        // ordinary verdict in that state would claim "will lock" while the policy is
        // deliberately not re-engaging.
        if (manualOverride && status.Presence != BluetoothPresence.Present)
        {
            BluetoothSelectionSummary.Text = $"已选择：{label}，已手动解锁，设备重新被检测到之前不会重复锁定。";
            return;
        }

        switch (status.Presence)
        {
            case BluetoothPresence.Present:
                BluetoothSelectionSummary.Text = status.Reason switch
                {
                    BluetoothReason.SignalAboveThreshold => $"已选择：{label}，信号高于或等于阈值 {threshold} dBm，保持解锁。",
                    BluetoothReason.Connected => $"已选择：{label}，设备已连接，保持解锁。",
                    _ => thresholdActive && monitor.SupportsRssi(configuredAddress)
                        ? $"已选择：{label}，信号在阈值附近，按上次判定保持解锁。"
                        : $"已选择：{label}，当前在范围内，保持解锁。"
                };
                break;

            case BluetoothPresence.Absent:
                // The monitor reports why it decided this. "Connected but too weak" and
                // "not connected" both read as Absent, but they describe opposite situations
                // and the user would act on them differently.
                BluetoothSelectionSummary.Text = status.Reason switch
                {
                    BluetoothReason.SignalBelowThreshold =>
                        $"已选择：{label}，信号低于阈值 {threshold} dBm，将保持锁定。",
                    BluetoothReason.NoRecentSignal =>
                        $"已选择：{label}，已有一段时间没有读到信号，将保持锁定。",
                    BluetoothReason.NeverObserved =>
                        $"已选择：{label}，附近没有检测到该设备，将保持锁定。",
                    BluetoothReason.NotConnected when monitor.IsPaired(configuredAddress) =>
                        $"已选择：{label}，设备未连接（可能已关机或休眠），将保持锁定。",
                    _ => thresholdActive
                        ? $"已选择：{label}，信号低于阈值 {threshold} dBm，将保持锁定。"
                        : $"已选择：{label}，当前不在范围内，将保持锁定。"
                };
                break;

            default:
                BluetoothSelectionSummary.Text = status.Reason == BluetoothReason.ScanIncomplete || !monitor.HasScanned
                    // Not the same as "away": the radio may simply not have produced data yet,
                    // and saying "out of range" here would be wrong and alarming.
                    ? $"已选择：{label}，等待扫描以确认状态。"
                    : $"已选择：{label}，暂时没有读到信号，锁定状态保持不变。";
                break;
        }
    }

    private void BluetoothList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BluetoothList.SelectedItem is not BluetoothDeviceInfo selected) return;
        _selectedBluetooth = selected;
        ApplyCurrentSettings();
    }

    private void RefreshUsb_Click(object sender, RoutedEventArgs e) => RefreshUsbList();

    private async void RefreshUsbList()
    {
        // Enumerating removable disks touches DriveInfo and WMI; keep it off the UI
        // thread so the settings page never stalls behind a slow provider.
        if (_usbRefreshRunning) return;
        _usbRefreshRunning = true;
        try
        {
            var devices = await Task.Run(() => _services.LockCoordinator.Usb.Scan());
            ApplyUsbDevices(devices);
        }
        catch
        {
            // A failed scan leaves the previous list in place.
        }
        finally
        {
            _usbRefreshRunning = false;
        }
    }

    private void ApplyUsbDevices(IReadOnlyList<UsbDeviceInfo> devices)
    {
        if (_resourcesShutdown) return;
        var signature = string.Join('\u001f', devices.Select(d => $"{d.DriveLetter}|{d.Name}|{d.InstanceId}"));
        var selected = _selectedUsb?.InstanceId ?? _services.Settings?.Usb?.DeviceInstanceId;

        using var suppress = new SuppressScope(this);
        if (signature != _usbSignature)
        {
            _usbSignature = signature;
            UsbList.ItemsSource = devices;
        }
        UsbEmptyState.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selected is not null)
        {
            // Restore the selection from the list that is actually bound. A scan returns
            // brand-new instances, so searching the fresh collection can pick an object that
            // is not in the ItemsSource and the selection would silently fail to appear.
            var bound = UsbList.ItemsSource as IEnumerable<UsbDeviceInfo> ?? devices;
            var matching = bound.FirstOrDefault(d =>
                string.Equals(d.InstanceId, selected, StringComparison.OrdinalIgnoreCase)
                || d.Identifiers.Contains(selected, StringComparer.OrdinalIgnoreCase));
            if (matching is not null && !ReferenceEquals(UsbList.SelectedItem, matching))
            {
                UsbList.SelectedItem = matching;
                _selectedUsb = matching;
                UsbIdText.Text = $"实例 ID：{matching.InstanceId}";
            }
        }
    }

    private void UsbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsbList.SelectedItem is not UsbDeviceInfo selected) return;
        _selectedUsb = selected;
        UsbIdText.Text = $"实例 ID：{selected.InstanceId}";
        ApplyCurrentSettings();
    }

    private void BluetoothThresholdBox_TextChanged(object sender, TextChangedEventArgs e) => ScheduleApply();

    private void IdleMinutesBox_TextChanged(object sender, TextChangedEventArgs e) => ScheduleApply();

    private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e) => ApplyCurrentSettings();

    /// <summary>
    /// Coalesces keystroke-driven edits into a single apply. Typing "-70" would
    /// otherwise persist the config and restart the Bluetooth watcher three times.
    /// </summary>
    private void ScheduleApply()
    {
        if (!_initialized || _suppressAutoSave) return;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void ApplyCurrentSettings()
    {
        if (!_initialized || _suppressAutoSave) return;
        // A pending keystroke apply is now redundant.
        _applyTimer.Stop();

        var source = _services.Settings ?? new AppSettings();
        var mode = GetSelectedMode();

        // Keep a valid persisted value when an inactive text field is being edited.
        // The active policy still requires its field to be valid before applying.
        var threshold = source.Bluetooth.Threshold;
        if (TryParseThreshold(out var parsedThreshold))
            threshold = parsedThreshold;
        else if (mode == LockMode.Bluetooth)
            return;

        var minutes = source.Idle.Minutes;
        if (int.TryParse(IdleMinutesBox.Text, out var parsedMinutes) && parsedMinutes is >= 1 and <= 1440)
            minutes = parsedMinutes;
        else if (mode == LockMode.Idle)
        {
            ShowError("空闲时间必须是 1 到 1440 分钟。");
            return;
        }

        var settings = new AppSettings
        {
            LockMode = mode,
            Bluetooth = new BluetoothSettings
            {
                DeviceAddress = _selectedBluetooth?.Address ?? source.Bluetooth.DeviceAddress,
                Threshold = threshold
            },
            Usb = new UsbSettings
            {
                DeviceInstanceId = _selectedUsb?.InstanceId ?? source.Usb.DeviceInstanceId
            },
            Idle = new IdleSettings { Minutes = minutes },
            AutoStart = AutoStartCheckBox.IsChecked == true
        };

        try
        {
            _services.Apply(settings);
            SaveStatusText.Text = "已自动保存并应用";
            SaveStatusText.Foreground = ThemeBrush("SaveSucceededForegroundBrush");
            // Choosing a device satisfies the policy's prerequisite, so the warning
            // in the hint bar has to be re-evaluated after the settings are applied.
            UpdateModeUi(mode);
        }
        catch (Exception ex)
        {
            ShowError($"自动保存失败：{ex.Message}");
        }
    }

    private LockMode GetSelectedMode()
        => NoneRadio.IsChecked == true ? LockMode.None
            : BluetoothRadio.IsChecked == true ? LockMode.Bluetooth
            : UsbRadio.IsChecked == true ? LockMode.Usb
            : LockMode.Idle;

    private bool TryParseThreshold(out int? threshold)
    {
        threshold = null;
        if (string.IsNullOrWhiteSpace(BluetoothThresholdBox.Text)) return true;
        if (int.TryParse(BluetoothThresholdBox.Text, out var value) && value is >= -100 and <= -20)
        {
            threshold = value;
            return true;
        }

        ShowError("RSSI 阈值必须是 -100 到 -20 之间的整数。");
        return false;
    }

    private void ShowError(string message)
    {
        SaveStatusText.Text = message;
        SaveStatusText.Foreground = ThemeBrush("SaveFailedForegroundBrush");
    }

    private void LockCoordinator_LockStateChanged(object? sender, bool locked)
    {
        if (_allowClose) return;

        void ApplyState()
        {
            if (_allowClose) return;
            UpdateStatus(locked);
            // Never leave the settings surface visible while the capture layer is active.
            // The native overlays remain topmost as a second line of defense.
            if (locked) HideShell();
        }

        try
        {
            if (DispatcherQueue.HasThreadAccess)
                ApplyState();
            else
                DispatcherQueue.TryEnqueue(ApplyState);
        }
        catch { }
    }

    /// <summary>
    /// Resolves a brush from the application theme dictionaries for the window's
    /// current theme. Reading the dictionaries explicitly avoids relying on the
    /// resource indexer, which does not consistently consult theme dictionaries.
    /// </summary>
    private Brush ThemeBrush(string key)
    {
        var dictionaryKey = RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (TryGetThemeBrush(dictionaryKey, key, out var brush)) return brush;
        // Fall back to the opposite theme rather than failing to paint.
        var fallbackKey = dictionaryKey == "Light" ? "Default" : "Light";
        if (TryGetThemeBrush(fallbackKey, key, out brush)) return brush;
        return new SolidColorBrush(Colors.Transparent);
    }

    private static bool TryGetThemeBrush(string dictionaryKey, string key, out Brush brush)
    {
        brush = null!;
        if (!Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionaryKey, out var dictionary)
            || dictionary is not ResourceDictionary resources
            || !resources.TryGetValue(key, out var value)
            || value is not Brush resolved)
            return false;
        brush = resolved;
        return true;
    }

    private void UpdateStatus(bool locked)
    {
        StatusText.Text = locked ? "已锁定" : "运行中";

        // Theme-derived colours keep the pill legible in light mode; the previous
        // hardcoded literals paired dark text with a dark background there.
        StatusBadge.Background = ThemeBrush(locked ? "StatusLockedBackgroundBrush" : "StatusRunningBackgroundBrush");
        var foreground = ThemeBrush(locked ? "StatusLockedForegroundBrush" : "StatusRunningForegroundBrush");
        StatusDot.Fill = foreground;
        StatusText.Foreground = foreground;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        HideShell();
    }

    /// <summary>
    /// Resolves the template-instantiated pane toggle button after the NavigationView
    /// has applied its template, then re-measures the passthrough region only when that
    /// button's own layout moves.
    /// </summary>
    private void AttachPaneToggleWatcher()
    {
        if (_paneToggleButton is null)
            _paneToggleButton = FindDescendantByName<Button>(SettingsNavigationView, "TogglePaneButton");

        if (_paneToggleButton is null)
        {
            // The template may still be loading; one follow-up layout pass is enough.
            SettingsNavigationView.LayoutUpdated += RetryPaneToggleDiscovery;
            return;
        }

        _paneToggleButton.LayoutUpdated += (_, _) => TryApplyTitleBarRegions();
        TryApplyTitleBarRegions();
    }

    private void RetryPaneToggleDiscovery(object? sender, object e)
    {
        if (_paneToggleButton is null)
            _paneToggleButton = FindDescendantByName<Button>(SettingsNavigationView, "TogglePaneButton");
        if (_paneToggleButton is null) return;

        SettingsNavigationView.LayoutUpdated -= RetryPaneToggleDiscovery;
        _paneToggleButton.LayoutUpdated += (_, _) => TryApplyTitleBarRegions();
        TryApplyTitleBarRegions();
    }

    /// <summary>
    /// Declares the navigation pane toggle button as an interactive region so the
    /// extended title bar's default drag strip stops consuming its pointer input.
    /// </summary>
    private void TryApplyTitleBarRegions()
    {
        if (_titleBarRegionFailed || _resourcesShutdown) return;
        try
        {
            if (_paneToggleButton is null || _paneToggleButton.ActualWidth <= 0 || _paneToggleButton.ActualHeight <= 0)
                return;

            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var transform = _paneToggleButton.TransformToVisual(null);
            var bounds = transform.TransformBounds(new Windows.Foundation.Rect(
                0, 0, _paneToggleButton.ActualWidth, _paneToggleButton.ActualHeight));

            var rect = new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale));

            // LayoutUpdated fires frequently; only touch the native input source when
            // the rectangle actually changed.
            var signature = $"{rect.X},{rect.Y},{rect.Width},{rect.Height}";
            if (signature == _titleBarRegionSignature) return;

            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            source.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
            _titleBarRegionSignature = signature;
        }
        catch
        {
            // If the template part or input API is unavailable the default drag strip
            // remains in effect, which is the pre-existing behaviour. Stop retrying.
            _titleBarRegionFailed = true;
        }
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            var found = FindDescendantByName<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private const int SwHide = 0;
    private const int SwShow = 5;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
