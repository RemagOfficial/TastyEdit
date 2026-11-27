using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TastyEditor.App.Panels;

// The picture-editor surface behind the entity panel (R7). It draws the entity's sprite scaled by a
// zoom / pan transform, and the collision shapes as editable vector objects held in image-pixel space
// (so a shape stays glued to the art at every zoom). The window talks to it through the Shapes / Crop /
// Selected properties and the Note / ShapesChanged / ViewChanged callbacks.

/// <summary>Which tool the editor surface is in. Mirrors the palette buttons in the panel.</summary>
internal enum ToolMode { Edit, Circle, Box, Crop, Pan, Erase }

/// <summary>A collision shape in image-pixel space (Y down). The panel converts to / from the game's
/// normalised form on load / save; the editor only ever moves these around on screen.</summary>
internal abstract class WShape
{
    public abstract Point Center { get; set; }
    public abstract void Translate(Vector d);
}

internal sealed class WCircle : WShape
{
    public double Cx, Cy, R;
    public override Point Center { get => new(Cx, Cy); set { Cx = value.X; Cy = value.Y; } }
    public override void Translate(Vector d) { Cx += d.X; Cy += d.Y; }
}

internal sealed class WRect : WShape
{
    public double Cx, Cy, W, H, Angle;   // Angle in degrees, about the centre
    public override Point Center { get => new(Cx, Cy); set { Cx = value.X; Cy = value.Y; } }
    public override void Translate(Vector d) { Cx += d.X; Cy += d.Y; }

    // The four un-rotated corners offset from the centre (by handle index 0..3), used for hit + resize.
    public Point Corner(int k, bool rotated)
    {
        double sx = k is 0 or 3 ? -0.5 : 0.5;
        double sy = k is 2 or 3 ? 0.5 : -0.5;
        double ox = sx * W, oy = sy * H;
        if (rotated && Math.Abs(Angle) > 1e-6)
        {
            double a = Angle * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            (ox, oy) = (ox * c - oy * s, ox * s + oy * c);
        }
        return new Point(Cx + ox, Cy + oy);
    }
}

/// <summary>The interactive canvas. Left-drag creates (Circle / Box / Crop tools) or selects / moves /
/// resizes (Edit tool) or erases (Erase tool); the middle mouse button and the Pan tool drag the view;
/// the wheel zooms about the cursor. Rendering is manual via <see cref="OnRender"/> so zoom and stroke
/// widths stay crisp at any scale.</summary>
internal sealed class ShapeEditor : FrameworkElement
{
    public BitmapSource? Image;
    public readonly List<WShape> Shapes = new();
    public int Selected = -1;
    private ToolMode _tool = ToolMode.Edit;
    public ToolMode Tool
    {
        get => _tool;
        set { if (_tool == value) return; _tool = value; ApplyCursor(); InvalidateVisual(); }
    }
    public Rect Crop;              // image-pixel rect (may be empty)
    public bool ShowCrop;

    // Callbacks the window wires up: status text, "shapes list changed" and "view changed".
    public Action<string, bool>? Note;
    public Action? ShapesChanged;
    public Action? ViewChanged;

    private const double HandlePx = 4.5;   // half-size of a square handle, screen px

    private double _zoom = 1.0;
    private Vector _pan;
    private bool _needFit;

    // one gesture at a time
    private enum Op { None, Create, Move, Resize, Pan, Crop }
    private Op _op = Op.None;
    private Point _startWorld, _lastScreen;
    private int _handle;
    private Point _anchor;             // fixed corner when resizing a rect
    private WShape? _working;          // temp shape being dragged out

    static ShapeEditor()
    {
        FocusableProperty.OverrideMetadata(typeof(ShapeEditor), new FrameworkPropertyMetadata(true));
    }

    public ShapeEditor() { RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); }

    private Point S(Point w) => new(w.X * _zoom + _pan.X, w.Y * _zoom + _pan.Y);
    private Point W(Point s) => new((s.X - _pan.X) / _zoom, (s.Y - _pan.Y) / _zoom);

    public double ZoomPercent => _zoom;

    // ---- public commands (the tool buttons + zoom row call these) ----

    public void SetImage(BitmapSource? img)
    {
        Image = img; Selected = -1; _op = Op.None; _working = null;
        if (img is not null) _needFit = true;
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void FitToView()
    {
        if (Image is null || ActualWidth < 1 || ActualHeight < 1) return;
        double iw = Image.PixelWidth, ih = Image.PixelHeight;
        double z = Math.Min(ActualWidth / iw, ActualHeight / ih) * 0.95;
        if (z <= 0 || double.IsNaN(z) || double.IsInfinity(z)) z = 1;
        _zoom = z;
        _pan = new Vector((ActualWidth - iw * z) / 2, (ActualHeight - ih * z) / 2);
        _needFit = false;
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void SetZoom(double z) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), z / _zoom);
    public void ZoomBy(double f) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), f);

    private void ZoomAt(Point screen, double f)
    {
        if (Image is null) return;
        double nz = Math.Clamp(_zoom * f, 0.05, 40.0);
        var w = W(screen);
        _zoom = nz;
        _pan = new Vector(screen.X - w.X * _zoom, screen.Y - w.Y * _zoom);
        InvalidateVisual(); ViewChanged?.Invoke();
    }

    public void ClearShapes()
    {
        Shapes.Clear(); Selected = -1; _working = null;
        ShapesChanged?.Invoke(); InvalidateVisual();
    }

    public bool DeleteSelected()
    {
        if (Selected < 0 || Selected >= Shapes.Count) return false;
        Shapes.RemoveAt(Selected); Selected = -1;
        ShapesChanged?.Invoke(); InvalidateVisual();
        return true;
    }

    // ---- rendering ----

    protected override void OnRender(DrawingContext dc)
    {
        var box = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_needFit && ActualWidth > 1 && ActualHeight > 1) FitToView();
        if (Image is null) return;
        double t = 1.0 / _zoom;   // keep on-screen strokes ~1px regardless of zoom
        var imgRect = new Rect(S(new Point(0, 0)), new Size(Image.PixelWidth * _zoom, Image.PixelHeight * _zoom));
        dc.DrawImage(Image, imgRect);
        dc.DrawRectangle(null, PenOf(Color.FromRgb(90, 90, 96), t), imgRect);

        if (ShowCrop && !Crop.IsEmpty)
            dc.DrawRectangle(null, PenOf(Color.FromRgb(0, 191, 255), t, true), RectScreen(Crop));

        for (int i = 0; i < Shapes.Count; i++)
            dc.DrawGeometry(null, PenOf(i == Selected ? Color.FromRgb(255, 69, 0) : Color.FromArgb(180, 255, 140, 0), t, false), GeometryOf(Shapes[i]));

        if (_working is not null)
            dc.DrawGeometry(null, PenOf(Color.FromRgb(255, 69, 0), t), GeometryOf(_working));

        if (Selected >= 0 && Selected < Shapes.Count) DrawHandles(dc, Shapes[Selected]);
    }

    private static Pen PenOf(Color c, double thickness, bool dashed = false)
    {
        var p = new Pen(new SolidColorBrush(c), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        p.Freeze();
        return p;
    }

    private Rect RectScreen(Rect world) => new(S(world.TopLeft), S(world.BottomRight));

    private Geometry GeometryOf(WShape s)
    {
        if (s is WCircle c)
            return new EllipseGeometry(S(new Point(c.Cx, c.Cy)), c.R * _zoom, c.R * _zoom);
        var r = (WRect)s;
        var fig = new PathFigure { StartPoint = S(r.Corner(0, true)), IsClosed = true };
        for (int k = 1; k < 4; k++) fig.Segments.Add(new LineSegment(S(r.Corner(k, true)), true));
        var pg = new PathGeometry(); pg.Figures.Add(fig);
        return pg;
    }

    private void DrawHandles(DrawingContext dc, WShape s)
    {
        var brush = Brushes.White; var pen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1);
        foreach (var hp in HandleScreens(s))
            dc.DrawRectangle(brush, pen, new Rect(hp.X - HandlePx, hp.Y - HandlePx, HandlePx * 2, HandlePx * 2));
    }

    private IEnumerable<Point> HandleScreens(WShape s)
    {
        if (s is WCircle c) { yield return S(new Point(c.Cx + c.R, c.Cy)); }
        else if (s is WRect r) { for (int k = 0; k < 4; k++) yield return S(r.Corner(k, true)); }
    }

    // ---- hit-testing (all in image-pixel space unless noted) ----

    private int HitShape(Point w)
    {
        double slop = 8.0 / _zoom;
        for (int i = Shapes.Count - 1; i >= 0; i--)
        {
            if (Shapes[i] is WCircle c) { if ((w - new Point(c.Cx, c.Cy)).Length <= c.R + slop) return i; }
            else if (Shapes[i] is WRect r)
            {
                var v = w - new Point(r.Cx, r.Cy);
                if (Math.Abs(r.Angle) > 1e-6)
                {
                    double a = -r.Angle * Math.PI / 180.0, co = Math.Cos(a), si = Math.Sin(a);
                    v = new Vector(v.X * co - v.Y * si, v.X * si + v.Y * co);
                }
                if (Math.Abs(v.X) <= r.W / 2 + slop && Math.Abs(v.Y) <= r.H / 2 + slop) return i;
            }
        }
        return -1;
    }

    // Screen-space handle test (handles are a fixed size on screen). Index matches HandleScreens order.
    private int HitHandle(Point screen)
    {
        if (Selected < 0 || Selected >= Shapes.Count) return -1;
        int k = 0;
        foreach (var hp in HandleScreens(Shapes[Selected]))
        {
            if (Math.Abs(screen.X - hp.X) <= HandlePx + 2 && Math.Abs(screen.Y - hp.Y) <= HandlePx + 2) return k;
            k++;
        }
        return -1;
    }

    private static Rect BoxOf(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void ApplyCursor() => Cursor = Tool switch
    {
        ToolMode.Pan => Cursors.SizeAll,
        ToolMode.Edit => Cursors.Arrow,
        ToolMode.Erase => Cursors.Arrow,
        _ => Cursors.Cross,
    };

    // ---- mouse ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Image is null) { Note?.Invoke("Load an image first (Choose image…).", true); return; }
        var screen = e.GetPosition(this);
        var w = W(screen);
        switch (Tool)
        {
            case ToolMode.Circle:
            case ToolMode.Box:
                _op = Op.Create; _startWorld = w;
                _working = Tool == ToolMode.Circle ? new WCircle { Cx = w.X, Cy = w.Y } : new WRect { Cx = w.X, Cy = w.Y };
                CaptureMouse(); break;
            case ToolMode.Crop:
                _op = Op.Crop; _startWorld = w; CaptureMouse(); break;
            case ToolMode.Pan:
                _op = Op.Pan; _lastScreen = screen; CaptureMouse(); break;
            case ToolMode.Erase:
                int he = HitShape(w);
                if (he >= 0)
                {
                    Shapes.RemoveAt(he);
                    Selected = Selected == he ? -1 : Selected > he ? Selected - 1 : Selected;
                    ShapesChanged?.Invoke(); InvalidateVisual();
                }
                break;
            default: // Edit
                int hs = HitHandle(screen);
                if (hs >= 0)
                {
                    _op = Op.Resize; _handle = hs;
                    var sel = Shapes[Selected];
                    if (sel is WRect r) _anchor = r.Corner((hs + 2) % 4, false);
                    CaptureMouse();
                }
                else
                {
                    int hi = HitShape(w);
                    if (hi >= 0) { Selected = hi; _op = Op.Move; _startWorld = w; CaptureMouse(); ShapesChanged?.Invoke(); InvalidateVisual(); }
                    else { Selected = -1; InvalidateVisual(); }
                }
                break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        if (_op == Op.None) return;
        var w = W(screen);
        switch (_op)
        {
            case Op.Pan:
                _pan += screen - _lastScreen; _lastScreen = screen; InvalidateVisual(); break;
            case Op.Create when _working is WCircle c:
            {
                var b = BoxOf(_startWorld, w);
                c.Cx = b.X + b.Width / 2; c.Cy = b.Y + b.Height / 2; c.R = Math.Max(b.Width, b.Height) / 2;
                InvalidateVisual(); break;
            }
            case Op.Create when _working is WRect r:
            {
                var b = BoxOf(_startWorld, w);
                r.Cx = b.X + b.Width / 2; r.Cy = b.Y + b.Height / 2; r.W = b.Width; r.H = b.Height;
                InvalidateVisual(); break;
            }
            case Op.Crop:
                Crop = BoxOf(_startWorld, w); ShowCrop = true; InvalidateVisual(); break;
            case Op.Move when Selected >= 0:
                Shapes[Selected].Translate(w - _startWorld); _startWorld = w; InvalidateVisual(); break;
            case Op.Resize when Selected >= 0:
                if (Shapes[Selected] is WCircle cc) cc.R = Math.Max(1, (w - new Point(cc.Cx, cc.Cy)).Length);
                else if (Shapes[Selected] is WRect rr)
                {
                    var b = BoxOf(_anchor, w);
                    rr.Angle = 0; rr.Cx = b.X + b.Width / 2; rr.Cy = b.Y + b.Height / 2; rr.W = b.Width; rr.H = b.Height;
                }
                InvalidateVisual(); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_op == Op.Create && _working is not null)
        {
            bool ok = _working is WCircle c ? c.R > 1 : _working is WRect r && (r.W > 1 && r.H > 1);
            if (ok) { Shapes.Add(_working); Selected = Shapes.Count - 1; ShapesChanged?.Invoke(); }
            _working = null; InvalidateVisual();
        }
        _op = Op.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (Image is null) return;
        _op = Op.Pan; _lastScreen = e.GetPosition(this); CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_op == Op.Pan) { _op = Op.None; if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true; }
    }
}
