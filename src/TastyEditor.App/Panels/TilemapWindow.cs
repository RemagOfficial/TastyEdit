using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TastyEditor.App;

namespace TastyEditor.App.Panels;

/// <summary>One background tile cell as the tilemap window sees it: the sprite name plus the game's own
/// placement transform, resolved the way <c>SceneProjector</c> reads a tile — <see cref="FlipX"/> is the
/// raw <c>flipx</c> flag, <see cref="FlipY"/> is the always-on <c>transpose_v</c> mirror composed with
/// <c>flipy</c> (so a plain tile is mirrored unless <c>flipy="true"</c>), and <see cref="Angle"/> is the
/// raw <c>angle</c> in degrees. A blank cell reads name "null".</summary>
public readonly record struct TileCell(string Name, bool FlipX, bool FlipY, double Angle);

/// <summary>One background tile layer as the tilemap window sees it: its index, grid size, per-tile
/// world dimensions, and every cell laid out row-major (index = row*Wide + col).</summary>
public sealed record TileLayerInfo(int Index, int Wide, int High, double TileW, double TileH,
                                   IReadOnlyList<TileCell> Cells);

/// <summary>The narrow contract the modeless <see cref="TilemapWindow"/> talks to. It lives on
/// <c>MainWindow</c>, which owns the live TP1 document, so the window itself holds no model logic —
/// it only renders a snapshot and asks the host to make an edit (which then raises
/// <see cref="Changed"/> back so the view re-syncs, including after an undo).</summary>
public interface ITilemapHost
{
    /// <summary>Every background layer, in order, read from the live document.</summary>
    IReadOnlyList<TileLayerInfo> Snapshot();

    /// <summary>Paint <paramref name="cell"/> of <paramref name="layer"/> with a tiledef sprite name.</summary>
    void Paint(int layer, int cell, string name);

    /// <summary>Erase <paramref name="cell"/> back to the blank sentinel.</summary>
    void Erase(int layer, int cell);

    /// <summary>Add a new background layer (cloning the last, or a fresh single-cell layer).</summary>
    void AddLayer();

    /// <summary>Delete a layer; a no-op when it is the only one left (the host keeps ≥1).</summary>
    void DeleteLayer(int layer);

    /// <summary>Resize a layer's grid; 2D-aware and byte-reversible.</summary>
    void ResizeLayer(int layer, int wide, int high);

    /// <summary>The tile sprite names the level can paint (from the game's own definitions).</summary>
    IReadOnlyList<string> TileNames { get; }

    /// <summary>A thumbnail for a tile name, or null when it has no sprite (e.g. the blank cell).</summary>
    BitmapSource? Thumb(string name);

    /// <summary>Raised after any committed edit and on undo/redo, so the window re-reads a snapshot.</summary>
    event Action? Changed;
}

/// <summary>A modeless tilemap editor for a TP1 level's background layers. The centre is a mini
/// level viewport (a pan/zoom <see cref="TileSurface"/>) that draws one layer at a time the way the
/// game blits it; clicking or dragging paints the chosen tile wherever the pointer lands, right-click
/// erases. The top bar adds / deletes layers and resizes the grid (2D-aware). Every edit goes through
/// the host straight into the verbatim TP1 lane, so the main viewport updates live and untouched bytes
/// stay put. It edits the live document but never saves — Save stays the write path.</summary>
public sealed class TilemapWindow : Window
{
    private const string Blank = "null";

    private readonly ITilemapHost _host;
    private readonly ComboBox _layerBox = new() { Width = 150, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _wide = new() { Width = 46, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _high = new() { Width = 46, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _deleteBtn = new() { Content = "Delete layer", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
    private readonly WrapPanel _palette = new() { Orientation = Orientation.Vertical };
    private readonly TileSurface _surface;

    private readonly List<Border> _swatches = new();
    private int _currentLayer;
    private string _brush = Blank;      // the tile the left-click paints
    private bool _suppress;             // guards the layer-combo reentrancy during refresh

    public TilemapWindow(ITilemapHost host)
    {
        _host = host;

        Title = "Background tilemap";
        Width = 900;
        Height = 660;
        MinWidth = 560;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleCombo(_layerBox);

        _layerBox.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            _currentLayer = Math.Max(0, _layerBox.SelectedIndex);
            Refresh();
        };
        var addBtn = new Button { Content = "Add layer", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        addBtn.Click += (_, _) => _host.AddLayer();
        _deleteBtn.Click += (_, _) => _host.DeleteLayer(_currentLayer);

        var applySize = new Button { Content = "Apply size", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        applySize.Click += (_, _) => ApplySize();
        var fitBtn = new Button { Content = "Fit", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };

        // Top bar: layer selector + structural buttons on the left, grid-size controls on the right.
        var leftBar = new StackPanel { Orientation = Orientation.Horizontal };
        leftBar.Children.Add(Lbl("Layer"));
        leftBar.Children.Add(_layerBox);
        leftBar.Children.Add(addBtn);
        leftBar.Children.Add(_deleteBtn);

        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBar.Children.Add(Lbl("Wide"));
        rightBar.Children.Add(_wide);
        rightBar.Children.Add(Lbl("High"));
        rightBar.Children.Add(_high);
        rightBar.Children.Add(applySize);
        rightBar.Children.Add(fitBtn);

        var bar = new Grid { Margin = new Thickness(8, 8, 8, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftBar, 0);
        Grid.SetColumn(rightBar, 1);
        bar.Children.Add(leftBar);
        bar.Children.Add(rightBar);

        // Left palette: an erase swatch then every tile the level can paint.
        var paletteHead = new TextBlock { Text = "Tiles", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var palettePanel = new DockPanel { LastChildFill = true, Width = 96, Margin = new Thickness(8, 0, 0, 8) };
        DockPanel.SetDock(paletteHead, Dock.Top);
        var paletteScroll = new ScrollViewer
        {
            Content = palettePanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        palettePanel.Children.Add(paletteHead);
        palettePanel.Children.Add(_palette);

        // Center: the pan/zoom tile viewport (one layer at a time).
        _surface = new TileSurface(_host)
        {
            OnPaint = i =>
            {
                if (string.Equals(_brush, Blank, StringComparison.Ordinal)) _host.Erase(_currentLayer, i);
                else _host.Paint(_currentLayer, i, _brush);
            },
            OnErase = i => _host.Erase(_currentLayer, i),
            OnPick = name => SelectBrush(string.IsNullOrEmpty(name) ? Blank : name),
        };
        fitBtn.Click += (_, _) => _surface.Fit();
        var surfaceHost = new Border
        {
            BorderThickness = new Thickness(1),
            Child = _surface,
            Margin = new Thickness(8, 0, 8, 8),
        };
        Theme.Ref(surfaceHost, Border.BorderBrushProperty, Theme.KBorder);

        var hint = new TextBlock
        {
            Text = "left-drag paint · right-click erase · middle-click pick · middle-drag pan · wheel zoom",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(10, 0, 10, 6),
        };

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(paletteScroll, 0);
        Grid.SetColumn(surfaceHost, 1);
        split.Children.Add(paletteScroll);
        split.Children.Add(surfaceHost);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(hint);
        dock.Children.Add(split);
        Content = dock;

        BuildPalette();
        _host.Changed += OnHostChanged;
        Closed += (_, _) => _host.Changed -= OnHostChanged;
        Loaded += (_, _) => { Refresh(); _surface.Fit(); };
    }

    private void OnHostChanged() => Dispatcher.Invoke(Refresh);

    private static TextBlock Lbl(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 4, 0),
        Opacity = 0.8,
    };

    // The tile brush list, built once from the host's name set. The first swatch erases (the blank
    // sentinel); the rest paint a real tile. Clicking one selects the brush.
    private void BuildPalette()
    {
        _palette.Children.Clear();
        _swatches.Clear();
        AddSwatch(Blank, "Erase (blank)");
        foreach (var n in _host.TileNames)
            if (!string.Equals(n, Blank, StringComparison.Ordinal))
                AddSwatch(n, n);
    }

    private void AddSwatch(string name, string tip)
    {
        var img = new Image
        {
            Source = _host.Thumb(name),
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            Child = img,
            Width = 46,
            Height = 46,
            Margin = new Thickness(0, 0, 0, 4),
            Background = img.Source is null ? Theme.SurfaceAltBrush() : Theme.SurfaceBrush(),
            BorderThickness = new Thickness(1),
            ToolTip = tip,
            Cursor = Cursors.Hand,
            Tag = name,
        };
        b.SetResourceReference(Border.BorderBrushProperty, name == _brush ? Theme.KAccent : Theme.KBorder);
        b.MouseLeftButtonUp += (_, _) => SelectBrush(name);
        _swatches.Add(b);
        _palette.Children.Add(b);
    }

    private void SelectBrush(string name)
    {
        _brush = name;
        _surface.SetBrush(name);
        foreach (var b in _swatches)
            b.SetResourceReference(Border.BorderBrushProperty, (string)b.Tag == _brush ? Theme.KAccent : Theme.KBorder);
    }

    private void ApplySize()
    {
        if (!int.TryParse(_wide.Text, out int w) || !int.TryParse(_high.Text, out int h) || w < 1 || h < 1)
        {
            _wide.BorderBrush = _high.BorderBrush = Brushes.Firebrick;
            return;
        }
        _wide.BorderBrush = _high.BorderBrush = Theme.BorderBrush();
        _host.ResizeLayer(_currentLayer, w, h);
    }

    // Re-read the whole document state and re-sync the layer list, size boxes, delete-enable and view.
    private void Refresh()
    {
        var layers = _host.Snapshot();

        _suppress = true;
        if (_layerBox.Items.Count != layers.Count)
        {
            _layerBox.Items.Clear();
            for (int i = 0; i < layers.Count; i++) _layerBox.Items.Add($"Layer {i + 1}");
        }
        _currentLayer = layers.Count == 0 ? 0 : Math.Min(_currentLayer, layers.Count - 1);
        _layerBox.SelectedIndex = _currentLayer;
        _suppress = false;

        _deleteBtn.IsEnabled = layers.Count > 1;

        if (layers.Count == 0) { _surface.SetLayer(-1, null); return; }
        _surface.SetLayer(_currentLayer, layers[_currentLayer]);
        var info = layers[_currentLayer];
        _wide.Text = info.Wide.ToString();
        _high.Text = info.High.ToString();
    }
}

/// <summary>A lightweight pan / zoom render surface for one tile layer, drawn like the main viewport:
/// the tiles at their world aspect on an infinite canvas, vertically mirrored to match the game's
/// <c>transpose_v</c> blit, over a checker that shows through blank cells. It owns navigation (wheel
/// zoom-to-cursor, middle-drag pan) and hit-testing (a click anywhere over a cell — including a
/// transparent part of its sprite — maps to that cell), and reports paints / erases by index so the
/// window can route them to the host. It holds no model state itself.</summary>
public sealed class TileSurface : FrameworkElement
{
    private const double MinScale = 0.02, MaxScale = 24;

    private readonly ITilemapHost _host;
    private readonly Dictionary<string, BitmapSource?> _thumbs = new(StringComparer.Ordinal);

    private int _layer = -1;
    private IReadOnlyList<TileCell> _cells = Array.Empty<TileCell>();
    private int _wide, _high;
    private double _tw = 256, _th = 256;

    private double _scale = 1;         // screen px per world unit (cell px = tw * scale)
    private Vector _origin;            // screen px position of the grid's top-left corner
    private bool _pendingFit;

    private bool _panning;             // middle button held
    private bool _panMoved;            // middle dragged past the click threshold (a real pan, not a pick)
    private Point _downPos;            // where the middle button went down
    private Point _last;
    private bool _stroke;              // a paint / erase drag in progress
    private bool _strokeErase;
    private int _lastCell = -1;        // dedupe repeated paints within one stroke
    private string _brush = "null";

    // Below this pointer travel (device-independent px) a middle-button press is an eyedropper pick,
    // not a pan; above it the drag pans the view as before.
    private const double PickThreshold = 4;

    /// <summary>Invoked with a cell index to paint it with the current brush (the window erases if the
    /// brush is the blank sentinel).</summary>
    public Action<int>? OnPaint;
    /// <summary>Invoked with a cell index to erase it (right-click / right-drag).</summary>
    public Action<int>? OnErase;
    /// <summary>Invoked with a tile name when a middle-click eyedropper picks the tile under the cursor
    /// (the window makes it the current brush). A middle-drag pans instead and never fires this.</summary>
    public Action<string>? OnPick;

    public TileSurface(ITilemapHost host)
    {
        _host = host;
        Focusable = true;
        ClipToBounds = true;             // tiles enlarged or rotated past the grid must not bleed over the chrome
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public void SetBrush(string name) => _brush = name;

    /// <summary>Point the surface at a layer's snapshot; refits when the grid geometry changes.</summary>
    public void SetLayer(int layer, TileLayerInfo? info)
    {
        bool refit = layer != _layer;
        _layer = layer;
        if (info is null) { _cells = Array.Empty<TileCell>(); _wide = _high = 0; InvalidateVisual(); return; }
        refit |= info.Wide != _wide || info.High != _high || info.TileW != _tw || info.TileH != _th;
        _cells = info.Cells; _wide = info.Wide; _high = info.High; _tw = info.TileW; _th = info.TileH;
        if (_thumbs.Count > 512) _thumbs.Clear();
        if (refit) Fit(); else InvalidateVisual();
    }

    private double CellW => _tw * _scale;
    private double CellH => _th * _scale;

    /// <summary>Scale and centre the whole grid inside the control.</summary>
    public void Fit()
    {
        if (_wide <= 0 || _high <= 0 || ActualWidth < 1 || ActualHeight < 1) { _pendingFit = true; return; }
        double gw = _tw * _wide, gh = _th * _high;
        if (gw <= 0 || gh <= 0) return;
        const double m = 20;
        _scale = Math.Clamp(Math.Min((ActualWidth - 2 * m) / gw, (ActualHeight - 2 * m) / gh), MinScale, MaxScale);
        _origin = new Vector((ActualWidth - gw * _scale) / 2, (ActualHeight - gh * _scale) / 2);
        _pendingFit = false;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_pendingFit && ActualWidth > 1 && ActualHeight > 1) Fit();
    }

    private int CellAt(Point p)
    {
        if (_wide <= 0 || _high <= 0 || CellW <= 0 || CellH <= 0) return -1;
        int col = (int)Math.Floor((p.X - _origin.X) / CellW);
        int srow = (int)Math.Floor((p.Y - _origin.Y) / CellH);   // 0 = top of the view
        if (col < 0 || srow < 0 || col >= _wide || srow >= _high) return -1;
        // The level lays row 0 at the BOTTOM (SceneProjector: At.Y = -(bot + (row+0.5)*th)), so the
        // top screen row is the highest XML row. Map back to the stored (XML) row-major index.
        int xmlRow = _high - 1 - srow;
        return xmlRow * _wide + col;
    }

    private void Stroke(Point p, bool erase)
    {
        int idx = CellAt(p);
        if (idx < 0 || idx == _lastCell) return;
        _lastCell = idx;
        if (erase) OnErase?.Invoke(idx);
        else OnPaint?.Invoke(idx);
    }

    // ---- pointer ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (CellAt(p) < 0) return;               // let empty space fall through (a stray click paints nothing)
        CaptureMouse(); _stroke = true; _strokeErase = false; _lastCell = -1;
        Stroke(p, false); e.Handled = true; Focus();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var p = e.GetPosition(this);
        if (CellAt(p) < 0) return;
        CaptureMouse(); _stroke = true; _strokeErase = true; _lastCell = -1;
        Stroke(p, true); e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;   // left / right are handled by their own overrides
        var p = e.GetPosition(this);
        CaptureMouse(); _panning = true; _panMoved = false; _downPos = _last = p;
        Cursor = Cursors.SizeAll; e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_panning)
        {
            // Only actually pan once the pointer has travelled far enough to be a deliberate drag; a
            // near-stationary press+release stays a pick (the view must not jump when eyedropping).
            if (!_panMoved && (p - _downPos).Length > PickThreshold) _panMoved = true;
            if (_panMoved) { _origin += p - _last; InvalidateVisual(); }
            _last = p;
        }
        else if (_stroke)
        {
            Stroke(p, _strokeErase);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panning && !_panMoved)
        {
            // Middle-click eyedropper: adopt the tile under the cursor as the brush.
            int idx = CellAt(e.GetPosition(this));
            if (idx >= 0 && idx < _cells.Count) OnPick?.Invoke(_cells[idx].Name);
        }
        if (_panning || _stroke) { ReleaseMouseCapture(); _panning = false; _stroke = false; _lastCell = -1; Cursor = Cursors.Cross; }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_wide <= 0) return;
        var p = e.GetPosition(this);
        double ratio = e.Delta > 0 ? 1.12 : 1 / 1.12;
        double ns = Math.Clamp(_scale * ratio, MinScale, MaxScale);
        if (Math.Abs(ns - _scale) < 1e-9) return;
        double k = ns / _scale;
        _origin = new Vector(p.X - (p.X - _origin.X) * k, p.Y - (p.Y - _origin.Y) * k);
        _scale = ns;
        _pendingFit = false;
        InvalidateVisual();
    }

    // ---- painting ----

    private BitmapSource? Thumb(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "null") return null;
        if (!_thumbs.TryGetValue(name, out var img)) { img = _host.Thumb(name); _thumbs[name] = img; }
        return img;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(Theme.SurfaceBrush(), null, new Rect(0, 0, W, H));
        if (_wide <= 0 || _high <= 0) return;

        double cw = CellW, ch = CellH;
        if (cw <= 0.3 || ch <= 0.3) return;      // too small to be worth drawing per cell

        var grid = new Pen(Theme.BorderBrush(), 1);
        grid.Freeze();
        var blankBrush = Theme.SurfaceAltBrush();
        var filledBrush = Theme.SurfaceBrush();

        int c0 = Math.Max(0, (int)Math.Floor((_origin.X * -1) / cw));
        int r0 = Math.Max(0, (int)Math.Floor((_origin.Y * -1) / ch));
        int c1 = Math.Min(_wide - 1, (int)Math.Floor((W - _origin.X) / cw));
        int r1 = Math.Min(_high - 1, (int)Math.Floor((H - _origin.Y) / ch));

        for (int srow = r0; srow <= r1; srow++)
        {
            // Lay the grid out the way the level does: XML row 0 sits at the BOTTOM, so the top
            // screen row is the highest XML row (this vertical flip is also what made angled tiles
            // read as mirrored while row 0 was drawn at the top).
            int xmlRow = _high - 1 - srow;
            double y = _origin.Y + srow * ch;
            for (int col = c0; col <= c1; col++)
            {
                double x = _origin.X + col * cw;
                var r = new Rect(x, y, cw, ch);
                int ci = xmlRow * _wide + col;
                var cell = ci < _cells.Count ? _cells[ci] : default;
                string nm = cell.Name ?? "null";
                bool blank = nm.Length == 0 || nm == "null";
                dc.DrawRectangle(blank ? blankBrush : filledBrush, grid, r);
                if (blank) continue;

                var img = Thumb(nm);
                if (img is null) continue;
                // Match the game's blit (SceneProjector, FlipBeforeRotate): mirror the SOURCE cell about
                // its own axes first (the always-on transpose_v plus any flipx), THEN rotate clockwise —
                // a reflection conjugates a rotation, so this order (not rotate-then-mirror) is what the
                // player sees. Build one matrix in local rect space: centre, flip, turn, un-centre, place.
                double cx = cw / 2, cy = ch / 2;
                var m = Matrix.Identity;
                m.Translate(-cx, -cy);
                m.Scale(cell.FlipX ? -1.0 : 1.0, cell.FlipY ? -1.0 : 1.0);
                if (cell.Angle != 0) m.Rotate(cell.Angle);
                m.Translate(cx, cy);
                m.Translate(x, y);
                dc.PushTransform(new MatrixTransform(m));
                dc.DrawImage(img, new Rect(0, 0, cw, ch));
                dc.Pop();
            }
        }

        // Frame the whole grid so its extent is legible against the checker background.
        var bounds = new Rect(_origin.X, _origin.Y, _wide * cw, _high * ch);
        var frame = new Pen(Theme.TextMutedBrush(), 1.25);
        frame.Freeze();
        dc.DrawRectangle(null, frame, bounds);
    }
}
