using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenRuler;

internal partial class LoupeOverlay : Window
{
    private readonly PixelLoupe _loupe;
    private int _sourceLeft;
    private int _sourceTop;
    private int _zoom = PixelLoupe.DefaultZoom;
    private int _hoverCol = -1;
    private int _hoverRow = -1;

    public LoupeOverlay(PixelLoupe loupe)
    {
        _loupe = loupe;
        InitializeComponent();
        SetMode(false);
        ApplyZoom(_zoom);
        PreviewKeyDown += Overlay_PreviewKeyDown;
    }

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _loupe.Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.None)
        {
            _loupe.Toggle();
            e.Handled = true;
        }
    }

    public void SetSource(int left, int top, int zoom)
    {
        _sourceLeft = left;
        _sourceTop = top;
        if (zoom != _zoom)
            ApplyZoom(zoom);
        UpdateHint();
        if (_loupe.CopyPixels(out byte[] pixels, out int width, out int height))
            Glass.SetPixels(pixels, width, height);
        else
            Glass.InvalidateVisual();
    }

    public void SetMode(bool zero)
    {
        BtnZero.Background = zero ? Freeze(90, 45, 212, 191) : Brushes.Transparent;
        BtnPointer.Background = zero ? Brushes.Transparent : Freeze(90, 45, 212, 191);
        Glass.MarkCorner = zero;
        UpdateHint();
        Glass.InvalidateVisual();
    }

    public bool TryViewOrigin(out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (Glass.ActualWidth < 2 || Glass.ActualHeight < 2 || PresentationSource.FromVisual(Glass) is not { CompositionTarget: { } target })
            return false;

        var origin = target.TransformToDevice.Transform(Glass.PointToScreen(new Point(0, 0)));
        var far = target.TransformToDevice.Transform(Glass.PointToScreen(new Point(Glass.ActualWidth, Glass.ActualHeight)));
        x = (int)Math.Round(origin.X);
        y = (int)Math.Round(origin.Y);
        width = Math.Max(1, (int)Math.Round(far.X - origin.X));
        height = Math.Max(1, (int)Math.Round(far.Y - origin.Y));
        return true;
    }

    private void ApplyZoom(int zoom)
    {
        _zoom = zoom;
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double side = PixelLoupe.Cells * zoom / scale;
        Glass.Width = side;
        Glass.Height = side;
        Width = Math.Max(side + 16, 248);
        Height = side + 40;
        Glass.CellDip = zoom / scale;
        Glass.InvalidateVisual();
    }

    private void UpdateHint()
    {
        if (_hoverCol < 0)
        {
            Hint.Text = _loupe.ZeroMode
                ? _loupe.Axis switch
                {
                    MeasureMode.Vertical => "Top edge",
                    MeasureMode.Area => "Top-left corner",
                    _ => "Left edge"
                }
                : "Click sets the end";
            return;
        }

        if (_loupe.ZeroMode)
        {
            Hint.Text = "Moves the corner";
            return;
        }

        int screenX = _sourceLeft + _hoverCol;
        int screenY = _sourceTop + _hoverRow;
        var origin = _loupe.OriginPixel();
        int dx = screenX - origin.X;
        int dy = screenY - origin.Y;
        Hint.Text = _loupe.Axis switch
        {
            MeasureMode.Vertical => $"{dy} from 0",
            MeasureMode.Area => $"{dx}, {dy}",
            _ => $"{dx} from 0"
        };
    }

    private void Pointer_Click(object sender, RoutedEventArgs e)
    {
        _loupe.ZeroMode = false;
        _loupe.HoldSource = false;
        SetMode(false);
    }

    private void Zero_Click(object sender, RoutedEventArgs e)
    {
        _loupe.ZeroMode = true;
        _loupe.HoldSource = false;
        SetMode(true);
    }

    private void Glass_MouseMove(object sender, MouseEventArgs e)
    {
        var cell = CellAt(e.GetPosition(Glass));
        if (cell.Col == _hoverCol && cell.Row == _hoverRow)
            return;

        _hoverCol = cell.Col;
        _hoverRow = cell.Row;
        Glass.HoverCol = _hoverCol;
        Glass.HoverRow = _hoverRow;
        _loupe.HoldSource = true;
        UpdateHint();
        Glass.InvalidateVisual();
    }

    private void Glass_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverCol = -1;
        _hoverRow = -1;
        Glass.HoverCol = -1;
        Glass.HoverRow = -1;
        _loupe.HoldSource = false;
        UpdateHint();
        Glass.InvalidateVisual();
    }

    private void Glass_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Choose(e.GetPosition(Glass), _loupe.ZeroMode);
        e.Handled = true;
    }

    private void Glass_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Choose(e.GetPosition(Glass), origin: true);
        e.Handled = true;
    }

    private void Glass_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _loupe.CycleZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Choose(Point point, bool origin)
    {
        var cell = CellAt(point);
        if (cell.Col < 0)
            return;

        _loupe.Select(_sourceLeft + cell.Col, _sourceTop + cell.Row, origin);
    }

    private (int Col, int Row) CellAt(Point point)
    {
        double cell = Glass.CellDip;
        if (cell <= 0)
            return (-1, -1);

        int col = (int)(point.X / cell);
        int row = (int)(point.Y / cell);
        if (col < 0 || row < 0 || col >= PixelLoupe.Cells || row >= PixelLoupe.Cells)
            return (-1, -1);

        return (col, row);
    }

    private static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class LoupeGlass : FrameworkElement
{
    public double CellDip { get; set; } = 8;
    public int HoverCol { get; set; } = -1;
    public int HoverRow { get; set; } = -1;
    public bool MarkCorner { get; set; }

    private System.Windows.Media.Imaging.WriteableBitmap? _bitmap;

    public LoupeGlass()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public void SetPixels(byte[] bgra, int width, int height)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4)
            return;

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_bitmap != null)
            dc.DrawImage(_bitmap, view);
        else
            dc.DrawRectangle(Brushes.Transparent, null, view);
        if (CellDip <= 0 || ActualWidth < 2)
            return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        grid.Freeze();
        for (int i = 1; i < PixelLoupe.Cells; i++)
        {
            double p = i * CellDip;
            dc.DrawLine(grid, new Point(p, 0), new Point(p, ActualHeight));
            dc.DrawLine(grid, new Point(0, p), new Point(ActualWidth, p));
        }

        int mid = PixelLoupe.Cells / 2;
        double origin = mid * CellDip;
        if (MarkCorner)
        {
            double arm = Math.Min(ActualWidth - origin, CellDip * 4);
            var halo = new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 5);
            var mark = new Pen(Brushes.White, 2);
            halo.Freeze();
            mark.Freeze();
            var across = new Point(origin + arm, origin);
            var down = new Point(origin, origin + arm);
            var corner = new Point(origin, origin);
            dc.DrawLine(halo, corner, across);
            dc.DrawLine(halo, corner, down);
            dc.DrawLine(mark, corner, across);
            dc.DrawLine(mark, corner, down);
        }

        var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 45, 212, 191)), 2);
        centerPen.Freeze();
        dc.DrawRectangle(null, centerPen, new Rect(origin + 1, origin + 1, Math.Max(1, CellDip - 2), Math.Max(1, CellDip - 2)));

        if (HoverCol >= 0 && (HoverCol != mid || HoverRow != mid))
        {
            var fill = new SolidColorBrush(Color.FromArgb(70, 251, 191, 36));
            fill.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 251, 191, 36)), 2);
            pen.Freeze();
            dc.DrawRectangle(fill, pen, new Rect(HoverCol * CellDip, HoverRow * CellDip, CellDip, CellDip));
        }
    }
}
