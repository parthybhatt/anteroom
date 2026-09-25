using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Anteroom.App.Models;
using Anteroom.App.Services;
using WinForms = System.Windows.Forms;

namespace Anteroom.App.UI;

/// <summary>
/// The tab strip. It is deliberately a non-activating tool window: clicking a tab must never pull
/// focus away from the terminal the user is typing in, and it must never appear in Alt+Tab.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SessionStore _store;
    private readonly SettingsService _settings;

    private readonly ObservableCollection<SessionState> _attention = new();
    private readonly ObservableCollection<SessionState> _idle = new();

    /// <summary>null = follow the sessions; true/false = the user overrode it with the handle or the collapse arrow.</summary>
    private bool? _manualOverride;

    // Must match the XAML's starting state (Panel visible, Handle collapsed): SetCollapsed
    // no-ops when the flag already agrees, so a wrong value here wedges the panel open.
    private bool _isCollapsed;
    private bool _allowClose;
    private double _dragStartWidth;
    private (int X, int Y, int Width, int Height, bool Topmost) _lastPlacement;

    public event Action? SettingsRequested;

    /// <summary>Raised when the user asks for a held call to be remembered as always-allowed.</summary>
    public event Action<PendingPermission>? AlwaysAllowRequested;

    public OverlayWindow(SessionStore store, SettingsService settings)
    {
        InitializeComponent();

        _store = store;
        _settings = settings;

        AttentionList.ItemsSource = _attention;
        IdleList.ItemsSource = _idle;

        _store.Sessions.CollectionChanged += OnSessionsChanged;
        _store.Changed += Refresh;
        _store.AttentionRaised += OnAttentionRaised;
        _settings.Changed += _ => ApplyLayout();

        Loaded += (_, _) => { ApplyLayout(); Refresh(); };
        SizeChanged += (_, _) => Reposition();
        // SizeChanged fires before the final measure settles, so the last word on placement comes
        // from LayoutUpdated; Reposition() no-ops unless the window rect actually moved.
        LayoutUpdated += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        // NOACTIVATE keeps the caret in the terminal; TOOLWINDOW keeps us out of Alt+Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The tray owns the lifetime; the X button collapses instead of destroying the window.
        if (!_allowClose)
        {
            e.Cancel = true;
            SetCollapsed(true);
        }
        base.OnClosing(e);
    }

    public void Shutdown()
    {
        _allowClose = true;
        _store.Sessions.CollectionChanged -= OnSessionsChanged;
        _store.Changed -= Refresh;
        _store.AttentionRaised -= OnAttentionRaised;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnAttentionRaised(SessionState session)
    {
        // A new star always wins: drop any manual collapse so the question is actually seen.
        _manualOverride = null;
        Refresh();
    }

    /// <summary>Rebuilds the two lists and decides whether the panel or the handle is showing.</summary>
    public void Refresh()
    {
        Sync(_attention, _store.Sessions.Where(s => s.NeedsAttention));
        Sync(_idle, _store.Sessions.Where(s => !s.NeedsAttention));

        int waiting = _attention.Count;
        HeaderText.Text = waiting > 0
            ? $"Anteroom · {waiting} waiting"
            : "Anteroom";

        // Nothing is starred, so there is nothing for Clear all to clear.
        ClearAll.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;

        IdleLabel.Text = _idle.Count == 1 ? "1 idle session" : $"{_idle.Count} idle sessions";
        IdleToggle.Visibility = _idle.Count > 0 && waiting > 0 ? Visibility.Visible : Visibility.Collapsed;

        // With nothing waiting, the idle list is the whole point of having the panel open.
        bool showIdle = waiting == 0 || IdleToggle.IsChecked == true;
        IdleList.Visibility = showIdle && _idle.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IdleGlyph.Text = showIdle ? "▴" : "▾";

        EmptyState.Visibility = _store.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HandleText.Text = _store.Sessions.Count == 0
            ? "Anteroom"
            : $"Anteroom · {_store.Sessions.Count}";

        SetCollapsed(_manualOverride is { } manual ? !manual : waiting == 0);
    }

    private static void Sync(ObservableCollection<SessionState> target, IEnumerable<SessionState> source)
    {
        var wanted = source.ToList();

        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < wanted.Count; i++)
        {
            int current = target.IndexOf(wanted[i]);
            if (current < 0) target.Insert(i, wanted[i]);
            else if (current != i) target.Move(current, i);
        }
    }

    private void SetCollapsed(bool collapsed)
    {
        if (_isCollapsed == collapsed && IsLoaded) return;
        _isCollapsed = collapsed;

        Panel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Handle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        if (collapsed)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            Width = _settings.Current.TabWidth;
        }

        Dispatcher.BeginInvoke(new Action(Reposition), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Applies width, corner, screen and always-on-top from settings.</summary>
    public void ApplyLayout()
    {
        var settings = _settings.Current;

        Topmost = settings.AlwaysOnTop;

        if (!_isCollapsed) Width = settings.TabWidth;

        var corner = settings.TabsPosition;
        bool onLeft = corner is TabsCorner.UpperLeft or TabsCorner.LowerLeft;
        bool onTop = corner is TabsCorner.UpperLeft or TabsCorner.UpperRight;

        // The width grip belongs on the edge that faces the middle of the screen.
        GripLeft.Visibility = onLeft ? Visibility.Collapsed : Visibility.Visible;
        GripRight.Visibility = onLeft ? Visibility.Visible : Visibility.Collapsed;

        Handle.HorizontalAlignment = onLeft ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        Handle.VerticalAlignment = onTop ? System.Windows.VerticalAlignment.Top : System.Windows.VerticalAlignment.Bottom;

        var screen = TargetScreen();
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        MaxHeight = screen.WorkingArea.Height / Math.Max(dpiScale, 0.1) * 0.8;

        Reposition();
    }

    private WinForms.Screen TargetScreen()
    {
        var name = _settings.Current.ScreenDeviceName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name);
            if (match is not null) return match;
        }
        return WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
    }

    /// <summary>
    /// Positioning is done in device pixels through SetWindowPos: WPF's Left/Top live in the primary
    /// monitor's DIP space, which lands the panel in the wrong place on mixed-DPI setups.
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        if (!GetWindowRect(hwnd, out var rect)) return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        var area = TargetScreen().WorkingArea;
        const int margin = 12;
        var corner = _settings.Current.TabsPosition;

        int x = corner is TabsCorner.UpperLeft or TabsCorner.LowerLeft
            ? area.Left + margin
            : area.Right - width - margin;

        int y = corner is TabsCorner.UpperLeft or TabsCorner.UpperRight
            ? area.Top + margin
            : area.Bottom - height - margin;

        var placement = (x, y, width, height, _settings.Current.AlwaysOnTop);
        if (placement == _lastPlacement) return;
        _lastPlacement = placement;

        var insertAfter = _settings.Current.AlwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST;
        SetWindowPos(hwnd, insertAfter, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        if (_dragStartWidth <= 0) _dragStartWidth = ActualWidth;

        // Dragging the left grip widens leftwards, so the delta is inverted on right-hand corners.
        bool gripOnLeft = ReferenceEquals(sender, GripLeft);
        double delta = gripOnLeft ? -e.HorizontalChange : e.HorizontalChange;

        Width = Math.Clamp(ActualWidth + delta, 220, 720);
        Reposition();
    }

    private void OnGripDone(object sender, DragCompletedEventArgs e)
    {
        _dragStartWidth = 0;
        _settings.Current.TabWidth = Math.Round(ActualWidth);
        _settings.Save();
    }

    private static SessionState? SessionOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SessionState;

    /// <summary>
    /// The whole tab is the Open target. Buttons inside it (Allow, Deny, Dismiss) mark the click
    /// handled, so they never fall through to here.
    /// </summary>
    private void OnOpenSession(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SessionOf(sender) is not { } session) return;
        e.Handled = true;

        // Raised counts as done too: the window is alive and now flashing in the taskbar, which is
        // as far as Windows will let us take the user.
        var result = WindowFocus.Open(session);
        if (result is OpenResult.Focused or OpenResult.Raised or OpenResult.Resumed) _store.Dismiss(session);
        if (result == OpenResult.CopiedCommand)
            session.PendingText = $"That terminal is gone. Copied: {WindowFocus.ResumeCommand(session)}";
    }

    private void OnDismissSession(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } session) _store.Dismiss(session);
    }

    private void OnAllow(object sender, RoutedEventArgs e) =>
        SessionOf(sender)?.Permission?.Resolve(PermissionOutcome.Allowed, "Allowed in Anteroom");

    private void OnDeny(object sender, RoutedEventArgs e) =>
        SessionOf(sender)?.Permission?.Resolve(PermissionOutcome.Denied, "Denied in Anteroom");

    private void OnAlwaysAllow(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender)?.Permission is not { IsPending: true } permission) return;
        AlwaysAllowRequested?.Invoke(permission);
        permission.Resolve(PermissionOutcome.Allowed, "Always allowed in Anteroom");
    }

    private void OnDismissAll(object sender, RoutedEventArgs e) => _store.DismissAll();

    private void OnOpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnCollapse(object sender, RoutedEventArgs e)
    {
        _manualOverride = false;
        Refresh();
    }

    private void OnHandleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _manualOverride = true;
        Refresh();
    }

    private void OnToggleIdle(object sender, RoutedEventArgs e) => Refresh();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int newLong);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
