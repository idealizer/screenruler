using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenRuler;

internal sealed class PixelLoupe : IDisposable
{
    public const int Cells = 21;
    public const int DefaultZoom = 8;

    private static readonly int[] ZoomLevels = [4, 8, 12];

    private readonly MainWindow _owner;
    private readonly LoupeOverlay _overlay;
    private readonly DispatcherTimer _timer;
    private HwndSource? _host;
    private IntPtr _mag;
    private int _zoom = DefaultZoom;
    private bool _ready;
    private NativeMethods.RECT _source;
    private byte[]? _pixels;
    private int _pixelWidth;
    private int _pixelHeight;
    private MagImageScalingCallback? _callback;

    public PixelLoupe(MainWindow owner)
    {
        _owner = owner;
        _overlay = new LoupeOverlay(this) { Owner = owner };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => UpdateFrame();
    }

    public bool IsOpen { get; private set; }
    public bool ZeroMode { get; set; }
    public bool HoldSource { get; set; }
    public MeasureMode Axis => _owner.CurrentMode;

    public event Action<int, int, bool>? PixelSelected;

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public void Open()
    {
        if (!EnsureMagnifier())
        {
            _owner.ShowStatus("Pixel zoom could not start.");
            return;
        }

        IsOpen = true;
        ZeroMode = true;
        HoldSource = false;
        ShowWindow(_host!.Handle, SwShowNa);
        _overlay.Show();
        _overlay.SetMode(ZeroMode);
        ApplyFilter();
        _timer.Start();
        UpdateFrame();
        _owner.NotifyLoupeChanged();
    }

    public void Close()
    {
        bool wasOpen = IsOpen;
        IsOpen = false;
        HoldSource = false;
        _timer.Stop();
        if (_overlay.IsVisible)
            _overlay.Hide();
        if (_host != null)
            ShowWindow(_host.Handle, SwHide);
        if (wasOpen)
        {
            _owner.Activate();
            _owner.NotifyLoupeChanged();
        }
    }

    public void Suspend()
    {
        _timer.Stop();
        if (_overlay.IsVisible)
            _overlay.Hide();
        if (_host != null)
            ShowWindow(_host.Handle, SwHide);
    }

    public void Resume()
    {
        if (!IsOpen || _host == null)
            return;

        ShowWindow(_host.Handle, SwShowNa);
        _overlay.Show();
        _timer.Start();
        UpdateFrame();
    }

    public void CycleZoom(int direction)
    {
        int index = Array.IndexOf(ZoomLevels, _zoom);
        if (index < 0)
            index = 1;
        int next = Math.Clamp(index + direction, 0, ZoomLevels.Length - 1);
        if (ZoomLevels[next] == _zoom)
            return;

        _zoom = ZoomLevels[next];
        ApplyTransform();
        _overlay.SetSource(_source.Left, _source.Top, _zoom);
        UpdateFrame();
    }

    public NativeMethods.POINT OriginPixel()
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return default;

        return _owner.CurrentMode switch
        {
            MeasureMode.Vertical => new NativeMethods.POINT { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top },
            MeasureMode.Area => new NativeMethods.POINT { X = rect.Left, Y = rect.Top },
            _ => new NativeMethods.POINT { X = rect.Left, Y = rect.Top + (rect.Bottom - rect.Top) / 2 }
        };
    }

    public bool CopyPixels(out byte[] pixels, out int width, out int height)
    {
        if (_pixels == null || _pixelWidth < 1 || _pixelHeight < 1)
        {
            pixels = [];
            width = 0;
            height = 0;
            return false;
        }

        pixels = (byte[])_pixels.Clone();
        width = _pixelWidth;
        height = _pixelHeight;
        return true;
    }

    public void Select(int screenX, int screenY, bool origin)
    {
        PixelSelected?.Invoke(screenX, screenY, origin);
        HoldSource = false;
    }

    public void Dispose()
    {
        Close();
        if (_mag != IntPtr.Zero)
        {
            DestroyWindow(_mag);
            _mag = IntPtr.Zero;
        }

        _host?.Dispose();
        _host = null;
        if (_ready)
        {
            MagUninitialize();
            _ready = false;
        }

        _overlay.Close();
    }

    private bool EnsureMagnifier()
    {
        if (_mag != IntPtr.Zero)
            return true;

        if (!MagInitialize())
            return false;

        _ready = true;
        var parameters = new HwndSourceParameters("ScreenRuler.LoupeHost")
        {
            WindowStyle = unchecked((int)0x80000000) | 0x02000000,
            ExtendedWindowStyle = 0x00000008 | 0x00000080 | 0x08000000,
            PositionX = 0,
            PositionY = 0,
            Width = Cells * _zoom,
            Height = Cells * _zoom,
            UsesPerPixelOpacity = false
        };
        _host = new HwndSource(parameters);
        ShowWindow(_host.Handle, SwHide);
        _mag = CreateWindowEx(0, "Magnifier", "MagnifierWindow", 0x40000000 | 0x10000000, 0, 0, Cells * _zoom, Cells * _zoom, _host.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_mag == IntPtr.Zero)
        {
            MagUninitialize();
            _ready = false;
            return false;
        }

        ApplyTransform();
        _callback = OnMagImage;
        MagSetImageScalingCallback(_mag, _callback);
        ApplyFilter();
        return true;
    }

    private void ApplyTransform()
    {
        if (_mag == IntPtr.Zero)
            return;

        var transform = new MagTransform
        {
            M00 = _zoom,
            M11 = _zoom,
            M22 = 1
        };
        MagSetWindowTransform(_mag, ref transform);
    }

    private void ApplyFilter()
    {
        if (_mag == IntPtr.Zero || _host == null)
            return;

        var windows = new List<IntPtr> { _host.Handle, _mag };
        var ruler = new WindowInteropHelper(_owner).Handle;
        if (ruler != IntPtr.Zero)
            windows.Add(ruler);
        var overlay = new WindowInteropHelper(_overlay).Handle;
        if (overlay != IntPtr.Zero)
            windows.Add(overlay);

        MagSetWindowFilterList(_mag, 0, windows.Count, windows.ToArray());
    }

    private void UpdateFrame()
    {
        if (!IsOpen || _host == null || _mag == IntPtr.Zero)
            return;

        if (!HoldSource)
        {
            var target = ZeroMode ? OriginPixel() : CursorPixel();
            int half = Cells / 2;
            _source = new NativeMethods.RECT
            {
                Left = target.X - half,
                Top = target.Y - half,
                Right = target.X - half + Cells,
                Bottom = target.Y - half + Cells
            };
        }

        if (!_overlay.IsVisible)
            return;

        PlaceOverlay();
        if (_overlay.TryViewOrigin(out int x, out int y, out int width, out int height))
        {
            NativeMethods.SetWindowPos(_host.Handle, NativeMethods.HwndTopmost, x, y, width, height, NativeMethods.SwpNoActivate);
            NativeMethods.SetWindowPos(_mag, IntPtr.Zero, 0, 0, width, height, NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
        }

        var overlayHwnd = new WindowInteropHelper(_overlay).Handle;
        if (overlayHwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(overlayHwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);

        MagSetWindowSource(_mag, _source);
        _overlay.SetSource(_source.Left, _source.Top, _zoom);
        ApplyFilter();
    }

    private void PlaceOverlay()
    {
        if (HoldSource)
            return;

        var anchor = ZeroMode ? OriginPixel() : CursorPixel();
        double scale = OverlayScale();
        double dipW = _overlay.Width > 1 ? _overlay.Width : Cells * _zoom + 2;
        double dipH = _overlay.Height > 1 ? _overlay.Height : Cells * _zoom + 36;
        int width = (int)Math.Round(dipW * scale);
        int height = (int)Math.Round(dipH * scale);
        var work = WorkArea(anchor.X, anchor.Y);
        int x;
        int y;
        if (ZeroMode)
        {
            x = anchor.X + 24;
            y = anchor.Y - height / 2;
            if (x + width > work.Right)
                x = anchor.X - width - 24;
        }
        else
        {
            x = anchor.X + 28;
            y = anchor.Y + 28;
            if (x + width > work.Right)
                x = anchor.X - width - 28;
            if (y + height > work.Bottom)
                y = anchor.Y - height - 28;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        var hwnd = new WindowInteropHelper(_overlay).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, x, y, width, height, NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private double OverlayScale()
    {
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        return dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
    }

    private static NativeMethods.POINT CursorPixel()
    {
        NativeMethods.GetCursorPos(out var point);
        return point;
    }

    private static NativeMethods.RECT WorkArea(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(monitor, ref info))
            return info.rcWork;

        return new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private const int SwHide = 0;
    private const int SwShowNa = 8;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MagImageScalingCallback(
        IntPtr hwnd,
        IntPtr srcdata,
        MagImageHeader srcheader,
        IntPtr destdata,
        MagImageHeader destheader,
        NativeMethods.RECT unclipped,
        NativeMethods.RECT clipped,
        IntPtr dirty);

    [StructLayout(LayoutKind.Sequential)]
    private struct MagImageHeader
    {
        public uint width;
        public uint height;
        public Guid format;
        public uint stride;
        public uint offset;
        public UIntPtr cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MagTransform
    {
        public float M00, M01, M02;
        public float M10, M11, M12;
        public float M20, M21, M22;
    }

    [DllImport("Magnification.dll")]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagUninitialize();

    private bool OnMagImage(
        IntPtr hwnd,
        IntPtr srcdata,
        MagImageHeader srcheader,
        IntPtr destdata,
        MagImageHeader destheader,
        NativeMethods.RECT unclipped,
        NativeMethods.RECT clipped,
        IntPtr dirty)
    {
        var header = srcheader.width >= 2 ? srcheader : destheader;
        var data = srcheader.width >= 2 ? srcdata : destdata;
        int width = (int)header.width;
        int height = (int)header.height;
        if (data == IntPtr.Zero || width < 2 || height < 2 || width > 512 || height > 512)
            return true;

        int stride = (int)header.stride;
        if (stride < width * 4 || stride > 16384)
            stride = width * 4;

        try
        {
            var pixels = new byte[width * height * 4];
            int offset = Math.Max(0, (int)header.offset);
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(data + offset + row * stride, pixels, row * width * 4, width * 4);
                for (int col = 0; col < width; col++)
                {
                    int i = (row * width + col) * 4;
                    if (pixels[i + 3] == 0)
                        pixels[i + 3] = 255;
                }
            }

            _pixels = pixels;
            _pixelWidth = width;
            _pixelHeight = height;
        }
        catch (AccessViolationException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        return true;
    }

    [DllImport("Magnification.dll")]
    private static extern bool MagSetWindowSource(IntPtr hwnd, NativeMethods.RECT rect);

    [DllImport("Magnification.dll")]
    private static extern bool MagSetImageScalingCallback(IntPtr hwnd, MagImageScalingCallback? callback);

    [DllImport("Magnification.dll")]
    private static extern bool MagSetWindowTransform(IntPtr hwnd, ref MagTransform transform);

    [DllImport("Magnification.dll")]
    private static extern bool MagSetWindowFilterList(IntPtr hwnd, int filterMode, int count, IntPtr[] windows);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
}
