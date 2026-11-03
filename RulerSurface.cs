using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScreenRuler;

public sealed class RulerSurface : FrameworkElement
{
    private static readonly Typeface TickFace = new(
        new FontFamily("Segoe UI Variable, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Medium,
        FontStretches.Normal);

    private static readonly Typeface TagFace = new(
        new FontFamily("Segoe UI Variable, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private MeasureMode _mode = MeasureMode.Horizontal;
    private bool _light;
    private bool _physical = true;
    private double _opacity = 0.60;
    private Point? _pointer;
    private Rect _holeA = Rect.Empty;
    private Rect _holeB = Rect.Empty;
    private DrawingGroup? _cached;
    private int _cacheKey;

    public RulerSurface()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    public void SetTheme(bool light, bool physical, double opacity, MeasureMode mode)
    {
        _light = light;
        _physical = physical;
        _opacity = opacity;
        _mode = mode;
        InvalidateVisual();
    }

    public void SetPointer(Point? pointer)
    {
        if (_pointer is { } old && pointer is { } next)
        {
            if (Math.Abs(old.X - next.X) < 0.35 && Math.Abs(old.Y - next.Y) < 0.35)
                return;
        }
        else if (_pointer is null && pointer is null)
        {
            return;
        }

        _pointer = pointer;
        InvalidateVisual();
    }

    public void SetHoles(Rect readout, Rect dock)
    {
        readout = Round(readout);
        dock = Round(dock);
        if (readout == _holeA && dock == _holeB)
            return;

        _holeA = readout;
        _holeB = dock;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 2 || ActualHeight < 2)
            return;

        var key = CacheKey();
        if (_cached == null || key != _cacheKey)
        {
            var drawing = new DrawingGroup();
            using (var ticks = drawing.Open())
                DrawChrome(ticks);

            drawing.Freeze();
            _cached = drawing;
            _cacheKey = key;
        }

        dc.DrawDrawing(_cached);
        DrawHairline(dc);
    }

    private int CacheKey()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var hash = new HashCode();
        hash.Add(_mode);
        hash.Add(_light);
        hash.Add(_physical);
        hash.Add(Math.Round(ActualWidth, 2));
        hash.Add(Math.Round(ActualHeight, 2));
        hash.Add(Math.Round(_opacity, 2));
        hash.Add(Math.Round(dpi.DpiScaleX, 3));
        hash.Add(Math.Round(dpi.DpiScaleY, 3));
        hash.Add(_holeA);
        hash.Add(_holeB);
        return hash.ToHashCode();
    }

    private void DrawChrome(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        double px = 1.0 / scaleX;
        var palette = Palette.Create(_light, _opacity, _mode);

        dc.DrawRectangle(palette.Fill, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var sheen = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(_light ? (byte)48 : (byte)56, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.42)
            }
        };
        sheen.Freeze();
        dc.DrawRectangle(sheen, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double unitsX = _physical ? scaleX : 1;
        double unitsY = _physical ? scaleY : 1;
        bool labelX = _mode != MeasureMode.Vertical;
        bool labelY = _mode != MeasureMode.Horizontal;

        if (_mode != MeasureMode.Vertical)
        {
            DrawEdgeTicks(dc, horizontal: true, fromNear: true, ActualWidth, unitsX, scaleX, palette, dpi.PixelsPerDip, labelX);
            DrawEdgeTicks(dc, horizontal: true, fromNear: false, ActualWidth, unitsX, scaleX, palette, dpi.PixelsPerDip, false);
        }

        if (_mode != MeasureMode.Horizontal)
        {
            DrawEdgeTicks(dc, horizontal: false, fromNear: true, ActualHeight, unitsY, scaleY, palette, dpi.PixelsPerDip, labelY);
            DrawEdgeTicks(dc, horizontal: false, fromNear: false, ActualHeight, unitsY, scaleY, palette, dpi.PixelsPerDip, false);
        }

        DrawOrigin(dc, palette, px);

        double border = _mode == MeasureMode.Area ? px * 2 : px;
        var pen = MakePen(palette.Edge, border);
        dc.DrawRectangle(null, pen, new Rect(border / 2, border / 2, Math.Max(0, ActualWidth - border), Math.Max(0, ActualHeight - border)));
    }

    private void DrawOrigin(DrawingContext dc, Palette palette, double px)
    {
        var pen = MakePen(palette.Major, px * 2);
        if (_mode != MeasureMode.Vertical)
            dc.DrawLine(pen, new Point(px, 0), new Point(px, ActualHeight));
        if (_mode != MeasureMode.Horizontal)
            dc.DrawLine(pen, new Point(0, px), new Point(ActualWidth, px));
    }

    private void DrawEdgeTicks(
        DrawingContext dc,
        bool horizontal,
        bool fromNear,
        double lengthDip,
        double unitsPerDip,
        double scale,
        Palette palette,
        double pixelsPerDip,
        bool labels)
    {
        int span = Math.Max(0, (int)Math.Round(lengthDip * unitsPerDip, MidpointRounding.AwayFromZero));
        double thickness = horizontal ? ActualHeight : ActualWidth;
        var minor = MakePen(palette.Minor, 1.0 / scale);
        var mid = MakePen(palette.Mid, 1.0 / scale);
        var major = MakePen(palette.Major, Math.Max(1.0 / scale, 1.15 / scale));

        for (int unit = 0; unit <= span; unit += 10)
        {
            double along = Math.Clamp(Snap(unit / unitsPerDip, scale), 0, lengthDip);
            bool isMajor = unit % 100 == 0;
            bool isMid = !isMajor && unit % 50 == 0;
            double length = isMajor ? 18 : isMid ? 12 : 7;
            var pen = isMajor ? major : isMid ? mid : minor;

            if (horizontal)
            {
                double y1 = fromNear ? 0 : thickness;
                double y2 = fromNear ? length : thickness - length;
                dc.DrawLine(pen, new Point(along, y1), new Point(along, y2));
                if (labels && isMajor)
                    DrawTickLabel(dc, unit, along, length + 1, horizontal: true, palette, pixelsPerDip);
            }
            else
            {
                double x1 = fromNear ? 0 : thickness;
                double x2 = fromNear ? length : thickness - length;
                dc.DrawLine(pen, new Point(x1, along), new Point(x2, along));
                if (labels && isMajor)
                    DrawTickLabel(dc, unit, along, length + 2, horizontal: false, palette, pixelsPerDip);
            }
        }
    }

    private void DrawTickLabel(
        DrawingContext dc,
        int unit,
        double along,
        double inset,
        bool horizontal,
        Palette palette,
        double pixelsPerDip)
    {
        var text = new FormattedText(
            unit.ToString(CultureInfo.CurrentCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TickFace,
            11,
            palette.Label,
            pixelsPerDip);

        Point origin;
        if (horizontal)
        {
            origin = new Point(along + 4, inset);
            if (origin.X + text.Width > ActualWidth - 2)
                origin.X = along - text.Width - 4;
        }
        else if (_mode == MeasureMode.Vertical)
        {
            origin = new Point((ActualWidth - text.Width) / 2, along + 3);
        }
        else
        {
            origin = new Point(inset, along + 2);
        }

        var bounds = new Rect(origin, new Size(text.Width, text.Height));
        if (bounds.Left < 1 || bounds.Top < 1 || bounds.Right > ActualWidth - 1 || bounds.Bottom > ActualHeight - 1)
            return;
        if (bounds.IntersectsWith(_holeA) || bounds.IntersectsWith(_holeB))
            return;

        dc.DrawText(text, origin);
    }

    private void DrawHairline(DrawingContext dc)
    {
        if (_pointer is not { } pointer)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var palette = Palette.Create(_light, _opacity, _mode);
        double x = Math.Clamp(pointer.X, 0, ActualWidth);
        double y = Math.Clamp(pointer.Y, 0, ActualHeight);
        var hair = MakePen(palette.Hair, 1.0 / scaleX);
        var glow = MakePen(palette.HairGlow, 3.0 / scaleX);

        int ux = (int)Math.Round(x * (_physical ? scaleX : 1), MidpointRounding.AwayFromZero);
        int uy = (int)Math.Round(y * (_physical ? scaleY : 1), MidpointRounding.AwayFromZero);

        if (_mode == MeasureMode.Horizontal)
        {
            dc.DrawLine(glow, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawLine(hair, new Point(x, 0), new Point(x, ActualHeight));
            DrawTag(dc, ux.ToString(CultureInfo.CurrentCulture), new Point(x, ActualHeight / 2), palette, dpi.PixelsPerDip);
        }
        else if (_mode == MeasureMode.Vertical)
        {
            dc.DrawLine(glow, new Point(0, y), new Point(ActualWidth, y));
            dc.DrawLine(hair, new Point(0, y), new Point(ActualWidth, y));
            DrawTag(dc, uy.ToString(CultureInfo.CurrentCulture), new Point(ActualWidth / 2, y), palette, dpi.PixelsPerDip);
        }
        else
        {
            dc.DrawLine(glow, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawLine(glow, new Point(0, y), new Point(ActualWidth, y));
            dc.DrawLine(hair, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawLine(hair, new Point(0, y), new Point(ActualWidth, y));
            DrawTag(dc, $"{ux:N0}, {uy:N0}", new Point(x, y), palette, dpi.PixelsPerDip);
        }
    }

    private void DrawTag(DrawingContext dc, string text, Point anchor, Palette palette, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TagFace,
            12,
            palette.TagText,
            pixelsPerDip);

        if (!TryPlaceTag(anchor, formatted.Width, formatted.Height, out double x, out double y))
            return;

        var box = new Rect(x - 5, y - 2, formatted.Width + 10, formatted.Height + 4);
        dc.DrawRoundedRectangle(palette.TagFill, null, box, 5, 5);
        dc.DrawText(formatted, new Point(x, y));
    }

    private bool TryPlaceTag(Point anchor, double width, double height, out double x, out double y)
    {
        double midY = Math.Clamp(anchor.Y - height / 2, 4, Math.Max(4, ActualHeight - height - 4));
        var spots = new List<Point>
        {
            new(anchor.X + 10, midY),
            new(anchor.X - width - 10, midY)
        };

        foreach (var hole in new[] { _holeB, _holeA })
        {
            if (hole.IsEmpty)
                continue;

            spots.Add(new Point(hole.Left - width - 10, midY));
            spots.Add(new Point(hole.Right + 10, midY));
            double parkedX = Math.Clamp(anchor.X - width / 2, 4, Math.Max(4, ActualWidth - width - 4));
            spots.Add(new Point(parkedX, hole.Top - height - 8));
            spots.Add(new Point(parkedX, hole.Bottom + 8));
        }

        foreach (var spot in spots)
        {
            if (TagFits(spot.X, spot.Y, width, height))
            {
                x = spot.X;
                y = spot.Y;
                return true;
            }
        }

        x = 0;
        y = 0;
        return false;
    }

    private bool TagFits(double x, double y, double width, double height)
    {
        if (x < 3 || y < 2 || x + width > ActualWidth - 3 || y + height > ActualHeight - 2)
            return false;

        var box = new Rect(x - 6, y - 3, width + 12, height + 6);
        if (!_holeA.IsEmpty && box.IntersectsWith(_holeA))
            return false;
        if (!_holeB.IsEmpty && box.IntersectsWith(_holeB))
            return false;
        return true;
    }

    private static double Snap(double dip, double scale) => (Math.Round(dip * scale) + 0.5) / scale;

    private static Rect Round(Rect rect)
    {
        if (rect.IsEmpty)
            return Rect.Empty;

        return new Rect(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height));
    }

    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    private readonly struct Palette
    {
        public required Brush Fill { get; init; }
        public required Brush Edge { get; init; }
        public required Brush Minor { get; init; }
        public required Brush Mid { get; init; }
        public required Brush Major { get; init; }
        public required Brush Label { get; init; }
        public required Brush Hair { get; init; }
        public required Brush HairGlow { get; init; }
        public required Brush TagFill { get; init; }
        public required Brush TagText { get; init; }

        public static Palette Create(bool light, double opacity, MeasureMode mode)
        {
            byte glass = (byte)Math.Round(255 * Math.Clamp(opacity, 0.30, 0.90));
            if (mode == MeasureMode.Area)
                glass = (byte)Math.Max(28, Math.Round(glass * 0.55));

            if (light)
            {
                return new Palette
                {
                    Fill = Solid(glass, 255, 252, 248),
                    Edge = Solid(210, 15, 23, 42),
                    Minor = Solid(150, 15, 23, 42),
                    Mid = Solid(200, 15, 23, 42),
                    Major = Solid(255, 15, 118, 110),
                    Label = Solid(230, 15, 23, 42),
                    Hair = Solid(255, 217, 119, 6),
                    HairGlow = Solid(70, 217, 119, 6),
                    TagFill = Solid(236, 255, 255, 255),
                    TagText = Solid(255, 146, 64, 14)
                };
            }

            return new Palette
            {
                Fill = Solid(glass, 12, 18, 32),
                Edge = Solid(220, 226, 232, 240),
                Minor = Solid(150, 226, 232, 240),
                Mid = Solid(200, 226, 232, 240),
                Major = Solid(255, 45, 212, 191),
                Label = Solid(230, 241, 245, 249),
                Hair = Solid(255, 251, 191, 36),
                HairGlow = Solid(80, 251, 191, 36),
                TagFill = Solid(230, 15, 23, 42),
                TagText = Solid(255, 253, 224, 71)
            };
        }

        private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
