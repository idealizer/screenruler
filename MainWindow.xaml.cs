using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using Drawing = System.Drawing;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScreenRuler;

public partial class MainWindow : Window
{
    public const double BarThickness = 72;
    public const double MinSpan = 320;
    public const double MinArea = 180;
    public const double DefaultArea = 280;
    private const double Grip = 8;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _dockTimer;
    private readonly DispatcherTimer _statusTimer;
    private MeasureMode _mode;
    private MeasureMode _linearMode = MeasureMode.Horizontal;
    private double _opacity = 0.60;
    private bool _physical = true;
    private bool _light;
    private bool _locked;
    private bool _ready;
    private bool _hotkeyOn;
    private int _openTips;
    private bool _dockOpen;
    private string _copyText = "0 px";
    private DragKind _drag = DragKind.None;
    private NativeMethods.RECT _dragRect;
    private NativeMethods.POINT _dragCursor;
    private HwndSource? _source;
    private PixelLoupe? _loupe;
    private DispatcherTimer? _captureTimer;
    private NativeMethods.RECT _captureRect;
    private bool _capturing;
    private bool _closingSizeEdit;
    private string? _shotPath;

    public bool AllowClose { get; set; }
    public MeasureMode CurrentMode => _mode;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _mode = settings.Mode is MeasureMode.Horizontal or MeasureMode.Vertical or MeasureMode.Area
            ? settings.Mode
            : MeasureMode.Horizontal;
        _linearMode = _mode == MeasureMode.Vertical ? MeasureMode.Vertical : MeasureMode.Horizontal;
        _opacity = Math.Round(Math.Clamp(settings.Opacity, 0.30, 0.90), 2);
        _physical = settings.PhysicalPixels;
        _light = settings.LightContrast;
        _locked = settings.Locked;

        double width = settings.Width is > 40 and < 20000 ? settings.Width : 800;
        double height = settings.Height is > 40 and < 20000 ? settings.Height : BarThickness;
        if (_mode == MeasureMode.Horizontal)
            height = BarThickness;
        if (_mode == MeasureMode.Vertical)
            width = BarThickness;

        Width = width;
        Height = height;
        ApplySizeConstraints();

        if (!double.IsNaN(settings.Left) && !double.IsNaN(settings.Top))
        {
            Left = settings.Left;
            Top = settings.Top;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(24, (work.Height - Height) * 0.18);
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Persist();
        };

        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _dockTimer.Tick += (_, _) =>
        {
            _dockTimer.Stop();
            if (!IsMouseOver && _openTips == 0 && _drag == DragKind.None)
            {
                Surface.SetPointer(null);
                ShowDock(false);
                Cursor = Cursors.Arrow;
            }
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusPill.Visibility = Visibility.Collapsed;
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                _loupe?.Suspend();
            else
                _loupe?.Resume();
        };

        AddHandler(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(TipOpened));
        AddHandler(FrameworkElement.ToolTipClosingEvent, new ToolTipEventHandler(TipClosed));

        ApplyTheme();
        UpdateModeChrome();
        RefreshSurface();
    }

    public void UseIcon(System.Drawing.Icon icon)
    {
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        PullBoundsFromHwnd();
        EnsureOnScreen();
        Topmost = true;
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(hwnd);
        Focus();
    }

    public void ShowCenteredOnCursor()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetCursorPos(out var point);
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (hwnd != IntPtr.Zero
            && NativeMethods.GetMonitorInfo(monitor, ref info)
            && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            var work = info.rcWork;
            int x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
            int y = work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 2);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTopmost,
                x,
                y,
                width,
                height,
                NativeMethods.SwpShowWindow);
        }

        Topmost = true;
        Activate();
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(hwnd);
        Focus();
        PullBoundsFromHwnd();
        UpdateReadout();
        ScheduleSave();
    }

    public void SetMode(MeasureMode mode)
    {
        if (mode == _mode || _drag != DragKind.None)
            return;

        double width = Width;
        double height = Height;
        double nextWidth = width;
        double nextHeight = height;

        switch (mode)
        {
            case MeasureMode.Horizontal:
                nextWidth = Math.Max(MinSpan, _mode == MeasureMode.Vertical ? height : width);
                nextHeight = BarThickness;
                _linearMode = MeasureMode.Horizontal;
                break;
            case MeasureMode.Vertical:
                nextHeight = Math.Max(MinSpan, _mode == MeasureMode.Horizontal ? width : height);
                nextWidth = BarThickness;
                _linearMode = MeasureMode.Vertical;
                break;
            case MeasureMode.Area:
                if (_mode == MeasureMode.Horizontal)
                    nextHeight = Math.Max(DefaultArea, nextHeight);
                else if (_mode == MeasureMode.Vertical)
                    nextWidth = Math.Max(DefaultArea, nextWidth);
                nextWidth = Math.Max(MinArea, nextWidth);
                nextHeight = Math.Max(MinArea, nextHeight);
                break;
        }

        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        _mode = mode;
        Width = nextWidth;
        Height = nextHeight;
        ApplySizeConstraints();
        RefreshSurface();
        UpdateModeChrome();
        UpdateReadout();
        UpdateHoles();
        ScheduleSave();
    }

    public void Persist()
    {
        if (TryGetDipBounds(out double left, out double top, out double width, out double height))
        {
            _settings.Left = left;
            _settings.Top = top;
            _settings.Width = width;
            _settings.Height = height;
        }
        else
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
        }

        _settings.Mode = _mode;
        _settings.Opacity = _opacity;
        _settings.PhysicalPixels = _physical;
        _settings.LightContrast = _light;
        _settings.Locked = _locked;
        _settings.Save();
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _hotkeyOn = NativeMethods.RegisterHotKey(
            hwnd,
            NativeMethods.HotkeyId,
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat,
            NativeMethods.VkR);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureOnScreen();
        UpdateReadout();
        UpdateHoles();
        _ready = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Persist();
            Hide();
            return;
        }

        _loupe?.Dispose();
        _loupe = null;
        Persist();
        UnregisterHotkey();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
    }

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (_mode == MeasureMode.Horizontal)
        {
            MaxHeight = double.PositiveInfinity;
            Height = BarThickness;
        }
        else if (_mode == MeasureMode.Vertical)
        {
            MaxWidth = double.PositiveInfinity;
            Width = BarThickness;
        }

        ApplySizeConstraints();
        RefreshSurface();
        UpdateReadout();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateReadout();
        UpdateHoles();
        if (_ready)
            ScheduleSave();
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        if (_ready && _drag == DragKind.None)
            ScheduleSave();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SizeBox.IsKeyboardFocused)
            return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.F:
                    Flip();
                    e.Handled = true;
                    return;
                case Key.C:
                    CopyReadout();
                    e.Handled = true;
                    return;
                case Key.D1:
                case Key.NumPad1:
                    SetMode(MeasureMode.Horizontal);
                    e.Handled = true;
                    return;
                case Key.D2:
                case Key.NumPad2:
                    SetMode(MeasureMode.Vertical);
                    e.Handled = true;
                    return;
                case Key.D3:
                case Key.NumPad3:
                    SetMode(MeasureMode.Area);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Escape && mods == ModifierKeys.None)
        {
            if (SizePopup.IsOpen)
            {
                EndSizeEdit(apply: false);
                e.Handled = true;
                return;
            }

            if (ShotPopup.IsOpen)
            {
                ShotPopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            if (_loupe?.IsOpen == true)
            {
                _loupe.Close();
                e.Handled = true;
                return;
            }

            Persist();
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && mods == ModifierKeys.None)
        {
            EnsureLoupe().Toggle();
            e.Handled = true;
            return;
        }

        if ((mods & ~ModifierKeys.Shift) != ModifierKeys.None)
            return;

        int step = (mods & ModifierKeys.Shift) == ModifierKeys.Shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left:
                Nudge(-step, 0);
                e.Handled = true;
                break;
            case Key.Right:
                Nudge(step, 0);
                e.Handled = true;
                break;
            case Key.Up:
                Nudge(0, -step);
                e.Handled = true;
                break;
            case Key.Down:
                Nudge(0, step);
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SizeBox.IsKeyboardFocused)
            return;

        SetOpacity(_opacity + (e.Delta > 0 ? 0.05 : -0.05));
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag != DragKind.None)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                ContinueDrag();
            else
                EndDrag();
        }

        var local = e.GetPosition(this);
        Surface.SetPointer(e.GetPosition(Surface));
        UpdateCursor(local);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || IsInteractive(e.OriginalSource as DependencyObject))
            return;

        var edge = HitEdge(e.GetPosition(this));
        StartDrag(edge == DragKind.None ? DragKind.Move : edge);
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None)
            return;

        EndDrag();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _dockTimer.Stop();
        ShowDock(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag != DragKind.None)
            return;

        _dockTimer.Start();
    }

    private void Flip_Click(object sender, RoutedEventArgs e) => Flip();

    private void Area_Click(object sender, RoutedEventArgs e) =>
        SetMode(_mode == MeasureMode.Area ? _linearMode : MeasureMode.Area);

    private void Unit_Click(object sender, RoutedEventArgs e)
    {
        _physical = !_physical;
        RefreshSurface();
        UpdateModeChrome();
        UpdateReadout();
        ScheduleSave();
        int percent = ScalePercent();
        if (percent == 100)
            ShowStatus("This display is at 100%, so pixels and DIP are the same number.");
        else if (_physical)
            ShowStatus($"Physical pixels. This display is at {percent}%.");
        else
            ShowStatus($"DIPs. This display is at {percent}%.");
    }

    private void Zoom_Click(object sender, RoutedEventArgs e) => EnsureLoupe().Toggle();

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != MeasureMode.Area)
        {
            ShowStatus("Switch to area (Ctrl+3), then snapshot the box.");
            return;
        }

        if (_capturing)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out _captureRect))
            return;

        _capturing = true;
        ShotPopup.IsOpen = false;
        Hide();
        _captureTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _captureTimer.Tick -= CaptureTick;
        _captureTimer.Tick += CaptureTick;
        _captureTimer.Start();
    }

    public void ShowStatus(string message)
    {
        StatusText.Text = message;
        if (_light)
        {
            StatusPill.Background = Freeze(246, 255, 252, 248);
            StatusPill.BorderBrush = Freeze(120, 15, 118, 110);
            StatusText.Foreground = Freeze(255, 15, 23, 42);
        }
        else
        {
            StatusPill.Background = Freeze(242, 15, 23, 42);
            StatusPill.BorderBrush = Freeze(90, 45, 212, 191);
            StatusText.Foreground = Freeze(255, 248, 250, 252);
        }

        StatusText.TextWrapping = _mode == MeasureMode.Horizontal ? TextWrapping.NoWrap : TextWrapping.Wrap;
        if (_mode == MeasureMode.Area)
        {
            StatusPill.VerticalAlignment = VerticalAlignment.Bottom;
            StatusPill.Margin = new Thickness(8, 0, 8, 28);
            StatusPill.MaxWidth = Math.Max(120, ActualWidth - 24);
        }
        else
        {
            StatusPill.VerticalAlignment = VerticalAlignment.Top;
            StatusPill.Margin = new Thickness(8, 6, 0, 0);
            StatusPill.MaxWidth = _mode == MeasureMode.Horizontal ? 220 : Math.Max(56, ActualWidth - 12);
        }
        StatusPill.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    public void NotifyLoupeChanged() => UpdateModeChrome();

    public void SnapToPixel(int screenX, int screenY, bool origin)
    {
        if (_locked)
        {
            ShowStatus("Unlock to move the edges.");
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return;

        int left = rect.Left;
        int top = rect.Top;
        int right = rect.Right;
        int bottom = rect.Bottom;
        if (_mode != MeasureMode.Vertical)
            SetSpan(ref left, ref right, screenX, origin, 1);
        if (_mode != MeasureMode.Horizontal)
            SetSpan(ref top, ref bottom, screenY, origin, 1);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top),
            NativeMethods.SwpNoActivate);
        PullBoundsFromHwnd();
        UpdateReadout();
        ScheduleSave();
        ShowStatus(origin ? "Zero is on that pixel." : "End is on that pixel.");
    }

    private PixelLoupe EnsureLoupe()
    {
        if (_loupe != null)
            return _loupe;

        _loupe = new PixelLoupe(this);
        _loupe.PixelSelected += (x, y, origin) => SnapToPixel(x, y, origin);
        return _loupe;
    }

    private void CaptureTick(object? sender, EventArgs e)
    {
        _captureTimer?.Stop();
        string? saved = null;
        try
        {
            int width = _captureRect.Right - _captureRect.Left;
            int height = _captureRect.Bottom - _captureRect.Top;
            if (width < 1 || height < 1)
                throw new InvalidOperationException();

            using var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(_captureRect.Left, _captureRect.Top, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenRuler");
            Directory.CreateDirectory(directory);
            saved = Path.Combine(directory, $"area-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            bitmap.Save(saved, Drawing.Imaging.ImageFormat.Png);

            var handle = bitmap.GetHbitmap();
            System.Windows.Media.Imaging.BitmapSource source;
            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                try
                {
                    Clipboard.SetImage(source);
                }
                catch (COMException)
                {
                    // The picture is still saved and shown on the card.
                }
            }
            finally
            {
                NativeMethods.DeleteObject(handle);
            }

            if (!IsVisible)
                Show();
            ShowSnapshot(saved, source);
        }
        catch
        {
            ShowStatus(saved == null ? "Couldn't capture that area." : "Snapshot saved. Open it from the card.");
        }
        finally
        {
            _capturing = false;
            if (!IsVisible)
                Show();
            if (ShotPopup.IsOpen)
                Dispatcher.BeginInvoke(() => RaisePopup(ShotPopup), DispatcherPriority.Loaded);
        }
    }

    private int ScalePercent()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)Math.Round((dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX) * 100);
    }

    private int FloorSpan(int minimum, bool horizontal)
    {
        if (!TryWindowSize(out int width, out int height))
            return minimum;

        int current = horizontal ? width : height;
        return current < minimum ? current : minimum;
    }

    private static void SetSpan(ref int start, ref int end, int pixel, bool origin, int min)
    {
        if (origin)
        {
            start = pixel;
            if (end - start < min)
                end = start + min;
        }
        else
        {
            end = pixel + 1;
            if (end - start < min)
                start = end - min;
        }
    }

    private void Contrast_Click(object sender, RoutedEventArgs e)
    {
        _light = !_light;
        ApplyTheme();
        RefreshSurface();
        UpdateModeChrome();
        ScheduleSave();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _locked = !_locked;
        if (_locked && _drag != DragKind.None)
            EndDrag();
        UpdateModeChrome();
        ScheduleSave();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        Persist();
        Hide();
    }

    private void TipOpened(object sender, RoutedEventArgs e) => _openTips++;

    private void TipClosed(object sender, RoutedEventArgs e)
    {
        _openTips = Math.Max(0, _openTips - 1);
        if (_openTips == 0 && !IsMouseOver)
            _dockTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WmNcHitTest:
                handled = true;
                return (IntPtr)HitTestCode(lParam);
            case NativeMethods.WmNcLButtonDown:
                var kind = KindFromHit(wParam.ToInt32());
                if (kind != DragKind.None && !_locked)
                {
                    StartDrag(kind);
                    NativeMethods.SetCapture(hwnd);
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case NativeMethods.WmMouseMove:
                if (_drag != DragKind.None)
                    ContinueDrag();
                break;
            case NativeMethods.WmLButtonUp:
                if (_drag != DragKind.None)
                {
                    EndDrag();
                    handled = true;
                }
                break;
            case NativeMethods.WmCaptureChanged:
                if (_drag != DragKind.None)
                    EndDrag();
                break;
            case NativeMethods.WmNcLButtonDblClk:
                handled = true;
                return IntPtr.Zero;
            case NativeMethods.WmSysCommand:
                if ((wParam.ToInt32() & 0xFFF0) == NativeMethods.ScMaximize)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case NativeMethods.WmGetMinMaxInfo:
                ApplyMinMax(lParam);
                handled = true;
                return IntPtr.Zero;
            case NativeMethods.WmHotkey:
                if (wParam.ToInt32() == NativeMethods.HotkeyId)
                {
                    ShowCenteredOnCursor();
                    handled = true;
                }
                break;
        }

        return IntPtr.Zero;
    }

    private int HitTestCode(IntPtr lParam)
    {
        if (_locked || !TryLocalFromScreen(lParam, out var local))
            return NativeMethods.HtClient;
        if (IsOverDock(local))
            return NativeMethods.HtClient;

        return HitEdge(local) switch
        {
            DragKind.Left => NativeMethods.HtLeft,
            DragKind.Right => NativeMethods.HtRight,
            DragKind.Top => NativeMethods.HtTop,
            DragKind.Bottom => NativeMethods.HtBottom,
            DragKind.TopLeft => NativeMethods.HtTopLeft,
            DragKind.TopRight => NativeMethods.HtTopRight,
            DragKind.BottomLeft => NativeMethods.HtBottomLeft,
            DragKind.BottomRight => NativeMethods.HtBottomRight,
            _ => NativeMethods.HtClient
        };
    }

    private void ApplyMinMax(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        var dpi = VisualTreeHelper.GetDpi(this);
        int virtualW = Math.Max(800, NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen));
        int virtualH = Math.Max(600, NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

        if (_locked && TryWindowSize(out int currentW, out int currentH))
        {
            info.ptMinTrackSize = new NativeMethods.POINT { X = currentW, Y = currentH };
            info.ptMaxTrackSize = new NativeMethods.POINT { X = currentW, Y = currentH };
        }
        else if (_mode == MeasureMode.Horizontal)
        {
            int thickness = DipToPx(BarThickness, dpi.DpiScaleY);
            int minX = FloorSpan(DipToPx(MinSpan, dpi.DpiScaleX), horizontal: true);
            info.ptMinTrackSize = new NativeMethods.POINT { X = minX, Y = thickness };
            info.ptMaxTrackSize = new NativeMethods.POINT { X = virtualW, Y = thickness };
        }
        else if (_mode == MeasureMode.Vertical)
        {
            int thickness = DipToPx(BarThickness, dpi.DpiScaleX);
            int minY = FloorSpan(DipToPx(MinSpan, dpi.DpiScaleY), horizontal: false);
            info.ptMinTrackSize = new NativeMethods.POINT { X = thickness, Y = minY };
            info.ptMaxTrackSize = new NativeMethods.POINT { X = thickness, Y = virtualH };
        }
        else
        {
            info.ptMinTrackSize = new NativeMethods.POINT
            {
                X = FloorSpan(DipToPx(MinArea, dpi.DpiScaleX), horizontal: true),
                Y = FloorSpan(DipToPx(MinArea, dpi.DpiScaleY), horizontal: false)
            };
            info.ptMaxTrackSize = new NativeMethods.POINT { X = virtualW, Y = virtualH };
        }

        Marshal.StructureToPtr(info, lParam, false);
    }

    private void StartDrag(DragKind kind)
    {
        if (kind == DragKind.None || _locked)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out _dragRect))
            return;

        NativeMethods.GetCursorPos(out _dragCursor);
        _drag = kind;
        Cursor = CursorFor(kind);
    }

    private void ContinueDrag()
    {
        if (_drag == DragKind.None)
            return;

        NativeMethods.GetCursorPos(out var cursor);
        int dx = cursor.X - _dragCursor.X;
        int dy = cursor.Y - _dragCursor.Y;
        var rect = _dragRect;

        switch (_drag)
        {
            case DragKind.Move:
                rect.Left += dx;
                rect.Right += dx;
                rect.Top += dy;
                rect.Bottom += dy;
                break;
            case DragKind.Left:
                rect.Left += dx;
                break;
            case DragKind.Right:
                rect.Right += dx;
                break;
            case DragKind.Top:
                rect.Top += dy;
                break;
            case DragKind.Bottom:
                rect.Bottom += dy;
                break;
            case DragKind.TopLeft:
                rect.Left += dx;
                rect.Top += dy;
                break;
            case DragKind.TopRight:
                rect.Right += dx;
                rect.Top += dy;
                break;
            case DragKind.BottomLeft:
                rect.Left += dx;
                rect.Bottom += dy;
                break;
            case DragKind.BottomRight:
                rect.Right += dx;
                rect.Bottom += dy;
                break;
        }

        Constrain(ref rect);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top),
            NativeMethods.SwpNoActivate);
        UpdateReadout();
    }

    private void EndDrag()
    {
        if (_drag == DragKind.None)
            return;

        _drag = DragKind.None;
        NativeMethods.ReleaseCapture();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        UpdateReadout();
        ScheduleSave();
    }

    private void Constrain(ref NativeMethods.RECT rect)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int minX = DipToPx(_mode == MeasureMode.Area ? MinArea : MinSpan, dpi.DpiScaleX);
        int minY = DipToPx(_mode == MeasureMode.Area ? MinArea : MinSpan, dpi.DpiScaleY);
        int startW = Math.Max(1, _dragRect.Right - _dragRect.Left);
        int startH = Math.Max(1, _dragRect.Bottom - _dragRect.Top);
        if (startW < minX)
            minX = startW;
        if (startH < minY)
            minY = startH;
        int maxW = Math.Max(minX, NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen));
        int maxH = Math.Max(minY, NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

        if (_mode == MeasureMode.Horizontal)
        {
            rect.Bottom = rect.Top + DipToPx(BarThickness, dpi.DpiScaleY);
            ClampEdge(ref rect, horizontal: true, minX, maxW);
        }
        else if (_mode == MeasureMode.Vertical)
        {
            rect.Right = rect.Left + DipToPx(BarThickness, dpi.DpiScaleX);
            ClampEdge(ref rect, horizontal: false, minY, maxH);
        }
        else
        {
            ClampEdge(ref rect, horizontal: true, minX, maxW);
            ClampEdge(ref rect, horizontal: false, minY, maxH);
        }
    }

    private void ClampEdge(ref NativeMethods.RECT rect, bool horizontal, int min, int max)
    {
        if (horizontal)
        {
            int width = rect.Right - rect.Left;
            if (width < min)
            {
                if (_drag is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft)
                    rect.Left = rect.Right - min;
                else
                    rect.Right = rect.Left + min;
            }
            else if (width > max)
            {
                if (_drag is DragKind.Left or DragKind.TopLeft or DragKind.BottomLeft)
                    rect.Left = rect.Right - max;
                else
                    rect.Right = rect.Left + max;
            }
        }
        else
        {
            int height = rect.Bottom - rect.Top;
            if (height < min)
            {
                if (_drag is DragKind.Top or DragKind.TopLeft or DragKind.TopRight)
                    rect.Top = rect.Bottom - min;
                else
                    rect.Bottom = rect.Top + min;
            }
            else if (height > max)
            {
                if (_drag is DragKind.Top or DragKind.TopLeft or DragKind.TopRight)
                    rect.Top = rect.Bottom - max;
                else
                    rect.Bottom = rect.Top + max;
            }
        }
    }

    private void Nudge(int dx, int dy)
    {
        if (_locked || (dx == 0 && dy == 0))
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int stepX = _physical ? 1 : Math.Max(1, (int)Math.Round(dpi.DpiScaleX));
        int stepY = _physical ? 1 : Math.Max(1, (int)Math.Round(dpi.DpiScaleY));
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            rect.Left + dx * stepX,
            rect.Top + dy * stepY,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            NativeMethods.SwpNoActivate);
        PullBoundsFromHwnd();
        ScheduleSave();
    }

    private void Flip()
    {
        SetMode(_linearMode == MeasureMode.Vertical ? MeasureMode.Horizontal : MeasureMode.Vertical);
    }

    private void SetOpacity(double opacity)
    {
        opacity = Math.Round(Math.Clamp(opacity, 0.30, 0.90), 2);
        if (Math.Abs(opacity - _opacity) < 0.001)
            return;

        _opacity = opacity;
        RefreshSurface();
        ScheduleSave();
    }

    private void CopyReadout()
    {
        if (string.IsNullOrWhiteSpace(_copyText))
            return;

        try
        {
            Clipboard.SetText(_copyText);
        }
        catch (COMException)
        {
            // Another app holds the clipboard.
        }
    }

    private void UpdateReadout()
    {
        ReadPixels(out int width, out int height);
        string unit = _physical ? "px" : "dip";
        _copyText = _mode switch
        {
            MeasureMode.Area => $"{width:N0} × {height:N0} {unit}",
            MeasureMode.Vertical => $"{height:N0} {unit}",
            _ => $"{width:N0} {unit}"
        };
        ReadoutText.Text = _copyText;
        Title = $"Screen Ruler — {_copyText}";
        FitReadout();
    }

    private void Readout_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 2)
            BeginSizeEdit();
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EndSizeEdit(apply: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndSizeEdit(apply: false);
        }
    }

    private void SizeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(CloseSizePopupIfIdle, DispatcherPriority.Input);
    }

    private void SizePopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        EndSizeEdit(apply: false);
        e.Handled = true;
    }

    private void ShotPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        ShotPopup.IsOpen = false;
        e.Handled = true;
    }

    private void CloseSizePopupIfIdle()
    {
        if (_closingSizeEdit || !SizePopup.IsOpen || SizePopup.IsKeyboardFocusWithin)
            return;

        EndSizeEdit(apply: false);
    }

    private void BeginSizeEdit()
    {
        if (_locked)
        {
            ShowStatus("Unlock to move the edges.");
            return;
        }

        if (SizePopup.IsOpen)
            return;

        ReadPixels(out int width, out int height);
        SizeBox.Text = $"{width} × {height}";
        SizePopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => RaisePopup(SizePopup), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() =>
        {
            SizeBox.Focus();
            SizeBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void EndSizeEdit(bool apply)
    {
        if (_closingSizeEdit || !SizePopup.IsOpen)
            return;

        _closingSizeEdit = true;
        string text = SizeBox.Text;
        SizePopup.IsOpen = false;
        Focus();
        _closingSizeEdit = false;

        if (!apply)
        {
            UpdateReadout();
            return;
        }

        if (!TryParseSize(text, out int width, out int? height))
        {
            ShowStatus("Type width × height, for example 800 × 600.");
            UpdateReadout();
            return;
        }

        ApplyTypedSize(width, height);
    }

    private void SizeSet_Click(object sender, RoutedEventArgs e) => EndSizeEdit(apply: true);

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var parts = tag.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
            return;

        EndSizeEdit(apply: false);
        ApplyTypedSize(width, height);
    }

    private void ShowSnapshot(string path, System.Windows.Media.Imaging.BitmapSource image)
    {
        _shotPath = path;
        ShotPreview.Source = image;
        ShotFile.Text = Path.GetFileName(path);
        ShotPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => RaisePopup(ShotPopup), DispatcherPriority.Loaded);
    }

    private void ShotPopup_Opened(object sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => RaisePopup(ShotPopup), DispatcherPriority.Loaded);

    private void SizePopup_Opened(object sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => RaisePopup(SizePopup), DispatcherPriority.Loaded);

    private CustomPopupPlacement[] PlaceShotCard(Size popupSize, Size targetSize, Point offset)
    {
        const double gap = 12;
        double width = popupSize.Width > 1 ? popupSize.Width : 260;
        double height = popupSize.Height > 1 ? popupSize.Height : 220;
        var work = MonitorWorkDip();
        var spots = new[]
        {
            new Point(0, targetSize.Height + gap),
            new Point(0, -height - gap),
            new Point(targetSize.Width + gap, 0),
            new Point(-width - gap, 0)
        };

        foreach (var spot in spots)
        {
            double x = Left + spot.X;
            double y = Top + spot.Y;
            if (x >= work.Left - 1 && y >= work.Top - 1 && x + width <= work.Right + 1 && y + height <= work.Bottom + 1)
                return new[] { new CustomPopupPlacement(spot, PopupPrimaryAxis.None) };
        }

        var below = spots[0];
        double fx = Math.Clamp(Left + below.X, work.Left, Math.Max(work.Left, work.Right - width)) - Left;
        double fy = Math.Clamp(Top + below.Y, work.Top, Math.Max(work.Top, work.Bottom - height)) - Top;
        return new[] { new CustomPopupPlacement(new Point(fx, fy), PopupPrimaryAxis.None) };
    }

    private Rect MonitorWorkDip()
    {
        var fallback = SystemParameters.WorkArea;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return fallback;

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT
            {
                X = rect.Left + Math.Max(0, rect.Right - rect.Left) / 2,
                Y = rect.Top + Math.Max(0, rect.Bottom - rect.Top) / 2
            },
            NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return fallback;

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Rect(
            info.rcWork.Left / sx,
            info.rcWork.Top / sy,
            Math.Max(1, info.rcWork.Right - info.rcWork.Left) / sx,
            Math.Max(1, info.rcWork.Bottom - info.rcWork.Top) / sy);
    }

    private static void RaisePopup(Popup popup)
    {
        if (!popup.IsOpen || popup.Child == null)
            return;
        if (PresentationSource.FromVisual(popup.Child) is not HwndSource source || source.Handle == IntPtr.Zero)
            return;

        NativeMethods.SetWindowPos(
            source.Handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private void ShotOpen_Click(object sender, RoutedEventArgs e) => OpenShot(folder: false);

    private void ShotFolder_Click(object sender, RoutedEventArgs e) => OpenShot(folder: true);

    private void ShotClose_Click(object sender, RoutedEventArgs e) => ShotPopup.IsOpen = false;

    private void OpenShot(bool folder)
    {
        if (string.IsNullOrWhiteSpace(_shotPath) || !File.Exists(_shotPath))
        {
            ShowStatus("That snapshot is no longer there.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder ? "explorer.exe" : _shotPath,
                Arguments = folder ? "/select,\"" + _shotPath + "\"" : "",
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowStatus("Couldn't open the snapshot.");
        }
    }

    private void ApplyTypedSize(int widthUnits, int? heightUnits)
    {
        if (_locked)
        {
            ShowStatus("Unlock to move the edges.");
            UpdateReadout();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            UpdateReadout();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        int widthPx = ToDevicePx(widthUnits, dpi.DpiScaleX);
        int thicknessX = DipToPx(BarThickness, dpi.DpiScaleX);
        int thicknessY = DipToPx(BarThickness, dpi.DpiScaleY);
        int newW;
        int newH;
        MeasureMode mode = _mode;

        if (heightUnits is int heightValue)
        {
            int heightPx = ToDevicePx(heightValue, dpi.DpiScaleY);
            if (_mode == MeasureMode.Horizontal && Math.Abs(heightPx - thicknessY) <= 1)
            {
                mode = MeasureMode.Horizontal;
                newW = widthPx;
                newH = thicknessY;
            }
            else if (_mode == MeasureMode.Vertical && Math.Abs(widthPx - thicknessX) <= 1)
            {
                mode = MeasureMode.Vertical;
                newW = thicknessX;
                newH = heightPx;
            }
            else
            {
                mode = MeasureMode.Area;
                newW = widthPx;
                newH = heightPx;
            }
        }
        else if (_mode == MeasureMode.Vertical)
        {
            newW = thicknessX;
            newH = widthPx;
        }
        else if (_mode == MeasureMode.Horizontal)
        {
            newW = widthPx;
            newH = thicknessY;
        }
        else
        {
            newW = widthPx;
            newH = Math.Max(1, rect.Bottom - rect.Top);
        }

        if (mode is MeasureMode.Horizontal or MeasureMode.Vertical)
            _linearMode = mode;
        _mode = mode;

        MinWidth = 1;
        MinHeight = 1;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            rect.Left,
            rect.Top,
            Math.Max(1, newW),
            Math.Max(1, newH),
            NativeMethods.SwpNoActivate);
        PullBoundsFromHwnd();
        RefreshSurface();
        UpdateModeChrome();
        UpdateReadout();
        ScheduleSave();
    }

    private int ToDevicePx(int units, double scale)
    {
        if (_physical)
            return Math.Clamp(units, 1, 20000);

        return DipToPx(units, scale);
    }

    private static bool TryParseSize(string text, out int width, out int? height)
    {
        width = 0;
        height = null;
        var match = Regex.Match(
            text.Trim(),
            @"^(?<w>\d[\d.,]*)(?:(?:\s*(?:[x×*]|by)\s*|\s+)(?<h>\d[\d.,]*))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !TryParseCount(match.Groups["w"].Value, out width))
            return false;

        if (match.Groups["h"].Success && match.Groups["h"].Value.Length > 0)
        {
            if (!TryParseCount(match.Groups["h"].Value, out int parsed))
                return false;
            height = parsed;
        }

        return true;
    }

    private static bool TryParseCount(string token, out int value)
    {
        value = 0;
        token = token.Trim();
        if (Regex.IsMatch(token, @"^\d{1,3}([.,]\d{3})+$"))
            token = token.Replace(".", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        else if (!Regex.IsMatch(token, @"^\d+$"))
            return false;

        return int.TryParse(token, out value) && value is >= 1 and <= 20000;
    }

    private void ReadPixels(out int width, out int height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        double dipW;
        double dipH;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            dipW = (rect.Right - rect.Left) / scaleX;
            dipH = (rect.Bottom - rect.Top) / scaleY;
            if (_physical)
            {
                width = Math.Max(0, rect.Right - rect.Left);
                height = Math.Max(0, rect.Bottom - rect.Top);
                return;
            }
        }
        else
        {
            dipW = ActualWidth;
            dipH = ActualHeight;
        }

        if (_physical)
        {
            width = (int)Math.Round(dipW * scaleX, MidpointRounding.AwayFromZero);
            height = (int)Math.Round(dipH * scaleY, MidpointRounding.AwayFromZero);
            return;
        }

        width = (int)Math.Round(dipW, MidpointRounding.AwayFromZero);
        height = (int)Math.Round(dipH, MidpointRounding.AwayFromZero);
    }

    private void FitReadout()
    {
        double limit = _mode == MeasureMode.Vertical ? ActualHeight - 28 : ActualWidth - 28;
        if (limit < 48)
            limit = 48;

        ReadoutText.FontSize = 15;
        ReadoutText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        while (ReadoutText.DesiredSize.Width > limit && ReadoutText.FontSize > 9)
        {
            ReadoutText.FontSize -= 0.5;
            ReadoutText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
    }

    private void ApplyTheme()
    {
        if (_light)
        {
            ReadoutPill.Background = Freeze(246, 255, 252, 248);
            ReadoutPill.BorderBrush = Freeze(120, 15, 118, 110);
            ReadoutText.Foreground = Freeze(255, 15, 23, 42);
        }
        else
        {
            ReadoutPill.Background = Freeze(242, 15, 23, 42);
            ReadoutPill.BorderBrush = Freeze(90, 45, 212, 191);
            ReadoutText.Foreground = Freeze(255, 248, 250, 252);
        }
    }

    private void UpdateModeChrome()
    {
        BtnArea.Background = _mode == MeasureMode.Area
            ? Freeze(80, 45, 212, 191)
            : Brushes.Transparent;
        BtnLock.Content = _locked ? "\uE72E" : "\uE785";
        BtnLock.ToolTip = _locked ? "Unlock position" : "Lock position";
        int percent = ScalePercent();
        BtnUnit.ToolTip = _physical
            ? $"Physical pixels. This display is {percent}%. Click for DIPs."
            : $"Logical DIPs. This display is {percent}%. Click for pixels.";
        BtnZoom.Background = _loupe?.IsOpen == true
            ? Freeze(80, 45, 212, 191)
            : Brushes.Transparent;
        SyncShotButton();
        BtnContrast.ToolTip = _light
            ? "Dark ticks for bright screens. Click to invert."
            : "Light ticks for dark screens. Click for a bright background.";
        ReadoutRotate.Angle = _mode == MeasureMode.Vertical ? -90 : 0;

        int count = Math.Max(1, DockGrid.Children.Count);
        if (_mode == MeasureMode.Vertical)
        {
            DockGrid.Columns = 2;
            DockGrid.Rows = (count + 1) / 2;
            Dock.HorizontalAlignment = HorizontalAlignment.Center;
            Dock.VerticalAlignment = VerticalAlignment.Bottom;
            Dock.Margin = new Thickness(4, 4, 4, 12);
        }
        else if (_mode == MeasureMode.Area)
        {
            DockGrid.Rows = 1;
            DockGrid.Columns = count;
            Dock.HorizontalAlignment = HorizontalAlignment.Right;
            Dock.VerticalAlignment = VerticalAlignment.Top;
            Dock.Margin = new Thickness(10);
        }
        else
        {
            DockGrid.Rows = 1;
            DockGrid.Columns = count;
            Dock.HorizontalAlignment = HorizontalAlignment.Right;
            Dock.VerticalAlignment = VerticalAlignment.Center;
            Dock.Margin = new Thickness(8, 0, 12, 0);
        }
    }

    private void SyncShotButton()
    {
        if (_mode == MeasureMode.Area)
        {
            if (BtnShot.Parent is not UniformGrid)
                DockGrid.Children.Insert(Math.Min(3, DockGrid.Children.Count), BtnShot);
        }
        else if (BtnShot.Parent is UniformGrid)
        {
            DockGrid.Children.Remove(BtnShot);
        }
    }

    private void ShowDock(bool show)
    {
        _dockOpen = show;
        Dock.IsHitTestVisible = show;
        UpdateHoles();
        var animation = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Dock.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateHoles()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Surface.SetHoles(ElementHole(ReadoutPill), _dockOpen ? ElementHole(Dock) : Rect.Empty);
        }, DispatcherPriority.Loaded);
    }

    private Rect ElementHole(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return Rect.Empty;

        try
        {
            var origin = element.TranslatePoint(new Point(-8, -6), Surface);
            return new Rect(origin, new Size(element.ActualWidth + 16, element.ActualHeight + 12));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private void RefreshSurface() => Surface.SetTheme(_light, _physical, _opacity, _mode);

    private void ApplySizeConstraints()
    {
        double spanW = Width < MinSpan ? Math.Max(1, Width) : MinSpan;
        double spanH = Height < MinSpan ? Math.Max(1, Height) : MinSpan;
        double areaW = Width < MinArea ? Math.Max(1, Width) : MinArea;
        double areaH = Height < MinArea ? Math.Max(1, Height) : MinArea;
        switch (_mode)
        {
            case MeasureMode.Horizontal:
                MinWidth = spanW;
                MaxWidth = 16000;
                MinHeight = BarThickness;
                MaxHeight = BarThickness;
                break;
            case MeasureMode.Vertical:
                MinWidth = BarThickness;
                MaxWidth = BarThickness;
                MinHeight = spanH;
                MaxHeight = 16000;
                break;
            default:
                MinWidth = areaW;
                MinHeight = areaH;
                MaxWidth = 16000;
                MaxHeight = 16000;
                break;
        }
    }

    private void EnsureOnScreen()
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;
        var window = new Rect(Left, Top, Width, Height);
        var screen = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        var visible = Rect.Intersect(window, screen);
        if (visible.IsEmpty || visible.Width < 48 || visible.Height < 24)
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(24, (work.Height - Height) * 0.18);
        }
    }

    private bool TryGetDipBounds(out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;
        if (PresentationSource.FromVisual(this) is not { CompositionTarget: { } target })
            return false;

        var origin = target.TransformFromDevice.Transform(new Point(rect.Left, rect.Top));
        var size = target.TransformFromDevice.Transform(new Point(rect.Right - rect.Left, rect.Bottom - rect.Top));
        if (size.X < 1 || size.Y < 1)
            return false;

        left = origin.X;
        top = origin.Y;
        width = size.X;
        height = size.Y;
        return true;
    }

    private void PullBoundsFromHwnd()
    {
        if (!TryGetDipBounds(out double left, out double top, out double width, out double height))
            return;

        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        MinWidth = 1;
        MinHeight = 1;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        ApplySizeConstraints();
    }

    private void ScheduleSave()
    {
        if (!_ready)
            return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UnregisterHotkey()
    {
        if (!_hotkeyOn)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(hwnd, NativeMethods.HotkeyId);
        _hotkeyOn = false;
    }

    private DragKind HitEdge(Point local)
    {
        if (_locked)
            return DragKind.None;

        bool left = local.X <= Grip;
        bool right = local.X >= ActualWidth - Grip;
        bool top = local.Y <= Grip;
        bool bottom = local.Y >= ActualHeight - Grip;

        if (_mode == MeasureMode.Horizontal)
        {
            if (left) return DragKind.Left;
            if (right) return DragKind.Right;
            return DragKind.None;
        }

        if (_mode == MeasureMode.Vertical)
        {
            if (top) return DragKind.Top;
            if (bottom) return DragKind.Bottom;
            return DragKind.None;
        }

        if (top && left) return DragKind.TopLeft;
        if (top && right) return DragKind.TopRight;
        if (bottom && left) return DragKind.BottomLeft;
        if (bottom && right) return DragKind.BottomRight;
        if (left) return DragKind.Left;
        if (right) return DragKind.Right;
        if (top) return DragKind.Top;
        if (bottom) return DragKind.Bottom;
        return DragKind.None;
    }

    private void UpdateCursor(Point local)
    {
        if (_locked)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        var kind = _drag != DragKind.None ? _drag : HitEdge(local);
        Cursor = kind == DragKind.None ? Cursors.SizeAll : CursorFor(kind);
    }

    private static Cursor CursorFor(DragKind kind) => kind switch
    {
        DragKind.Left or DragKind.Right => Cursors.SizeWE,
        DragKind.Top or DragKind.Bottom => Cursors.SizeNS,
        DragKind.TopLeft or DragKind.BottomRight => Cursors.SizeNWSE,
        DragKind.TopRight or DragKind.BottomLeft => Cursors.SizeNESW,
        DragKind.Move => Cursors.SizeAll,
        _ => Cursors.Arrow
    };

    private bool IsOverDock(Point local)
    {
        if (!Dock.IsHitTestVisible || Dock.Opacity < 0.2 || Dock.ActualWidth <= 0)
            return false;

        try
        {
            var point = TranslatePoint(local, Dock);
            return VisualTreeHelper.HitTest(Dock, point) != null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryLocalFromScreen(IntPtr lParam, out Point local)
    {
        Unpack(lParam, out int x, out int y);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            local = default;
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        local = new Point((x - rect.Left) / dpi.DpiScaleX, (y - rect.Top) / dpi.DpiScaleY);
        return true;
    }

    private static void Unpack(IntPtr lParam, out int x, out int y)
    {
        int packed = unchecked((int)lParam.ToInt64());
        x = (short)(packed & 0xFFFF);
        y = (short)((packed >> 16) & 0xFFFF);
    }

    private static DragKind KindFromHit(int hit) => hit switch
    {
        NativeMethods.HtLeft => DragKind.Left,
        NativeMethods.HtRight => DragKind.Right,
        NativeMethods.HtTop => DragKind.Top,
        NativeMethods.HtBottom => DragKind.Bottom,
        NativeMethods.HtTopLeft => DragKind.TopLeft,
        NativeMethods.HtTopRight => DragKind.TopRight,
        NativeMethods.HtBottomLeft => DragKind.BottomLeft,
        NativeMethods.HtBottomRight => DragKind.BottomRight,
        _ => DragKind.None
    };

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button or TextBox)
                return true;
            if (source is FrameworkElement { Name: "ReadoutPill" })
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private bool TryWindowSize(out int width, out int height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            width = Math.Max(1, rect.Right - rect.Left);
            height = Math.Max(1, rect.Bottom - rect.Top);
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    private static int DipToPx(double dip, double scale) =>
        Math.Max(1, (int)Math.Round(dip * (scale <= 0 ? 1 : scale), MidpointRounding.AwayFromZero));

    private static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private enum DragKind
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}
