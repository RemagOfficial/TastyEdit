using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TastyEditor.App.Assets;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>The slice-2 scene host: a custom <see cref="FrameworkElement"/> that draws
/// the projected world in <see cref="OnRender"/> and owns the camera (uniform scale +
/// translation, Y-down like the Qt scene). Left-drag on empty space pans, the wheel
/// zooms anchored under the cursor, and <see cref="FitToView"/> frames the whole level.
/// Left-click selects the topmost primitive under the cursor (raised via
/// <see cref="SelectionChanged"/>); dragging a selected typed entity/deco moves it in
/// the model (the byte-faithful re-encode path lives in Core). Raw-span items (f4/f5)
/// and polygons select for inspection but do not yet move — that is slice 3+/4.</summary>
public sealed class ViewportHost : FrameworkElement
{
    private SceneGeometry? _scene;

    // World -> screen: screen = (world - _offset) * _scale.
    private double _scale = 1.0;
    private Point _offset = new(0, 0);

    private Point _lastMouse;
    private bool _panning;
    // Middle-button pan (S-D13): the camera move the left button used to share. The left button
    // owns selection AND element drags, so a left-drag aimed at empty space near an object kept
    // grabbing that object instead of panning; the middle button has no other binding.
    private bool _panMiddle;

    // Selection + move. _selected is the chosen SceneCross / ScenePoly / SceneLine.
    // _moving drags a selected cross whose Source is a movable entity/deco.
    // Polygon editing (slice D4.4): when a wall/area ScenePoly (its Source is a
    // field-re-serialisable record) is selected, _dragVert grabs one of its vertex
    // handles, and _movingPoly drags the whole shape. Both mutate the model in place
    // through SceneProjector; the encoder re-derives the bytes on save.
    private object? _selected;
    private bool _moving;
    private bool _movingPoly;
    private int _dragVert = -1;
    private bool _dragMoved;
    private Point _lastWorld;

    // Layers dock state: hidden level indices (a level is shown unless listed here).
    private readonly HashSet<int> _hidden = new();

    // Sprite rendering (S5-3): the WPF asset bridge for the current level, or null when
    // the assets tree is missing / foreign (TP2) — then entities/deco render as crosses
    // exactly as before (capability-gated, no regression). DotsOnly forces the cross
    // path so a dense level can be browsed without decoding sprites.
    private ISpriteSource? _sprites;
    private bool _dotsOnly;

    // Tiny-sprite fallback (S-D14): a sprite that shrinks under MinSpritePx on screen used to
    // degrade to a cross, which was the cheap path when zoomed to fit a whole level. Blitting the
    // real artwork at any size turned out to be fast enough on the largest shipped levels, so the
    // degrade is now a view option (on by default) rather than a hard rule: the cross still shows
    // for records whose sprite cannot be resolved at all.
    private bool _alwaysSprites = true;
    internal const double MinSpritePx = 3;

    // Onion-skin (slice 6): a SECOND level's geometry drawn faintly behind the live one so a
    // modder can line new content up against a reference. It is a read-only SceneGeometry only —
    // never a LevelDoc — so it cannot be edited or saved and cannot affect the I1 byte path. The
    // ghost is placed by a world-space offset + uniform scale relative to the primary level, both
    // nudged from the keyboard, so the two levels can be registered by eye.
    private SceneGeometry? _onion;
    private Point _onionOffset;         // world units
    private double _onionScale = 1.0;

    /// <summary>Raised on a right-click AFTER the viewport has updated the selection to whatever
    /// sits under the cursor, so the shell can pop a context menu (duplicate / delete / copy /
    /// paste / move-to-layer / bring-to-front) for the current selection.</summary>
    public event Action<Point>? ContextMenuRequested;

    public ViewportHost()
    {
        // True nearest-neighbour for every sprite blit drawn by this host: keeps pixel art
        // crisp AND (unlike LowQuality, which is a bilinear filter) does NOT blend a tile's
        // opaque border with the transparent atlas edge / CropCell phantom column. That bilinear
        // bleed was painting a soft, dark, feathered seam between background dirt tiles (the
        // dino1 "holes") that never appears in-game, where pixels blit hard. Only affects image
        // draws; the vector pens/lines are unchanged.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>Install the sprite bridge for the loaded level (null clears it back to
    /// cross-only markers). Safe to call before or after <see cref="SetLevel"/>.</summary>
    public void SetSpriteSource(ISpriteSource? sprites)
    {
        _sprites = sprites;
        InvalidateVisual();
    }

    /// <summary>When true, entities/deco draw as crosses even if sprites are available.</summary>
    public bool DotsOnly
    {
        get => _dotsOnly;
        set { _dotsOnly = value; InvalidateVisual(); }
    }

    /// <summary>When true (the default), an entity/deco with a resolvable sprite is blitted even if
    /// it is only a couple of pixels wide on screen; when false, those records fall back to the
    /// cross marker, which is the faster path on very dense levels at wide zoom. Records whose
    /// sprite cannot be resolved draw as crosses either way.</summary>
    public bool AlwaysSprites
    {
        get => _alwaysSprites;
        set { if (_alwaysSprites == value) return; _alwaysSprites = value; InvalidateVisual(); }
    }

    /// <summary>Reports a human-readable description of the current selection (or an
    /// empty string when nothing is selected) so the shell can show it in a status bar.</summary>
    public event Action<string>? SelectionChanged;

    /// <summary>Raised whenever the model was changed in place (a finished drag-move or
    /// an inspector edit), so the shell can mark the document dirty.</summary>
    public event Action? Edited;

    /// <summary>Raised at the start of a press/drag gesture, before any model mutation, so the
    /// shell can close the current undo-step group (slice 7). Fires on every left-button down.</summary>
    public event Action? DragGestureStarted;

    // ------------------------------------------------------- onion-skin -----

    /// <summary>Install (or clear with null) the reference level drawn faintly behind the live
    /// one. Passing null turns the onion-skin off and resets its placement.</summary>
    public void SetOnion(SceneGeometry? onion)
    {
        _onion = onion;
        if (onion is null) { _onionOffset = new Point(0, 0); _onionScale = 1.0; }
        InvalidateVisual();
    }

    public bool HasOnion => _onion is not null;

    /// <summary>Shift the ghost by a world-space delta (a keyboard nudge).</summary>
    public void NudgeOnion(double dx, double dy)
    {
        if (_onion is null) return;
        _onionOffset.X += dx; _onionOffset.Y += dy;
        InvalidateVisual();
    }

    /// <summary>Multiply the ghost's uniform scale about its own origin.</summary>
    public void ScaleOnion(double factor)
    {
        if (_onion is null) return;
        _onionScale = Math.Clamp(_onionScale * factor, 0.05, 20.0);
        InvalidateVisual();
    }

    // Map an onion world point into the primary world frame (scale about the ghost origin, then
    // translate by the nudge offset) so it flows through the same ToScreen the live scene uses.
    private PointD OnionToWorld(PointD p) =>
        new(p.X * _onionScale + _onionOffset.X, p.Y * _onionScale + _onionOffset.Y);

    private static readonly Color Bg = Color.FromRgb(28, 30, 36);
    private static readonly Pen WallPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(235, 235, 235)), 1.4));
    private static readonly Pen AreaPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(220, 90, 200, 120)), 1.2));
    private static readonly Pen WaterPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(230, 90, 160, 240)), 1.4));
    private static readonly Pen PathPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(220, 240, 210, 90)), 1.2));
    private static readonly Pen EntityPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(230, 230, 110, 110)), 1.2));
    private static readonly Pen DecoPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(160, 150, 150, 220)), 1.0));
    private static readonly Pen OtherPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(160, 170, 170, 170)), 1.0));
    private static readonly Pen SelectPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(90, 200, 255)), 2.0));
    private static readonly Brush HandleBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(90, 200, 255)));
    private const double HandleGrabPx = 9;   // screen-pixel radius to grab a vertex handle
    private const double HandleHalfPx = 4;   // half-size of the drawn handle square

    private static Brush FreezeBrush(Brush b) { b.Freeze(); return b; }

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    public void SetLevel(LevelDoc doc)
    {
        _scene = doc.Scene;
        _selected = null;
        _moving = false;
        _movingPoly = false;
        _dragVert = -1;
        _dragMoved = false;
        _hidden.Clear();
        RaiseSelection();
        FitToView();
    }

    /// <summary>The currently selected primitive (for the inspector), or null.</summary>
    public ISceneElem? SelectedElement => _selected as ISceneElem;

    /// <summary>Swap in a freshly projected scene (after a length-changing model edit that
    /// invalidated the previously projected handles) while preserving the layer-visibility set.
    /// Clears the current selection; the caller re-selects via <see cref="SelectNearestCross"/>.</summary>
    public void SetScene(SceneGeometry scene)
    {
        _scene = scene;
        _selected = null;
        _moving = false;
        _movingPoly = false;
        _dragVert = -1;
        _dragMoved = false;
    }

    /// <summary>Select the last projected primitive of <paramref name="kind"/> on <paramref name="layer"/>.
    /// A slice-6 mutation (duplicate / paste) always APPENDS its new record at the
    /// end of the source's per-layer list, and the projector emits a layer's records in list order,
    /// so after a re-project the freshly created object is the LAST element of that kind on that
    /// layer. Selecting by (kind, layer, last) is model-agnostic — it needs no type switch across
    /// the tp2 / tb / f4 / f5 record classes, exactly what the UI-free App shell must avoid. Returns
    /// false when the kind projects to nothing selectable (e.g. an f4/f5 wall that fell back to no
    /// poly).</summary>
    public bool SelectLastOfKind(SceneKind kind, int layer)
    {
        if (_scene is null) return false;
        object? best = null;
        foreach (var p in _scene.Polys) if (p.Kind == kind && p.Layer == layer) best = p;
        if (best is null)
            foreach (var l in _scene.Lines) if (l.Kind == kind && l.Layer == layer) best = l;
        if (best is null)
            foreach (var c in _scene.Crosses) if (c.Kind == kind && c.Layer == layer) best = c;
        if (best is null) return false;
        _selected = best;
        RaiseSelection();
        InvalidateVisual();
        return true;
    }

    /// <summary>Select the closest non-hidden marker of <paramref name="kind"/> on
    /// <paramref name="layer"/> to <paramref name="world"/>. Used to keep an edited object
    /// selected after a re-projection (an f4/f5 entity keeps its world position across a name
    /// splice, since only its byte offset — not its chained position — changes). Returns false
    /// when no candidate exists.</summary>
    public bool SelectNearestCross(SceneKind kind, int layer, PointD world)
    {
        if (_scene is null) return false;
        SceneCross? best = null;
        double bd = double.MaxValue;
        foreach (var c in _scene.Crosses)
        {
            if (c.Kind != kind || c.Layer != layer || _hidden.Contains(c.Layer)) continue;
            double dx = c.At.X - world.X, dy = c.At.Y - world.Y;
            double d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = c; }
        }
        if (best is null) return false;
        _selected = best;
        RaiseSelection();
        InvalidateVisual();
        return true;
    }

    /// <summary>Show only the levels not listed in <paramref name="hidden"/>.</summary>
    public void SetHiddenLayers(IEnumerable<int> hidden)
    {
        _hidden.Clear();
        foreach (var h in hidden) _hidden.Add(h);
        if (_selected is ISceneElem se && _hidden.Contains(se.Layer)) _selected = null;
        InvalidateVisual();
    }

    /// <summary>Push an absolute world position onto the selected movable marker — the
    /// inspector's X / Y edit path. Mutates the model through the same re-derivable
    /// handle the drag uses, so it stays byte-faithful; raises <see cref="Edited"/>.</summary>
    public bool ApplySelectionPosition(double x, double y)
    {
        if (_selected is SceneCross cr && SceneProjector.TrySetPos(cr.Source, x, y))
        {
            cr.At = new PointD(x, y);
            Edited?.Invoke();
            InvalidateVisual();
            return true;
        }
        return false;
    }

    /// <summary>Push an absolute rotation (world degrees) onto the selected rotatable decoration
    /// — the inspector's Rotation edit path. Splices the record's centidegree field in place via
    /// <see cref="SceneProjector"/>; raises <see cref="Edited"/> so the shell marks dirty and
    /// commits an undo step.</summary>
    public bool ApplySelectionRotation(double deg)
    {
        if (_selected is SceneCross cr && SceneProjector.SetRotationDeg(cr.Source, deg))
        {
            cr.RotationDeg = deg;
            Edited?.Invoke();
            InvalidateVisual();
            return true;
        }
        return false;
    }

    /// <summary>Add <paramref name="deltaDeg"/> world degrees to the selected decoration's
    /// rotation (the keyboard nudge path). No-op for a non-rotatable selection.</summary>
    public bool NudgeSelectionRotation(double deltaDeg)
    {
        if (_selected is SceneCross cr && SceneProjector.NudgeRotationDeg(cr.Source, deltaDeg))
        {
            cr.RotationDeg = SceneProjector.GetRotationDeg(cr.Source) ?? cr.RotationDeg;
            Edited?.Invoke();
            InvalidateVisual();
            return true;
        }
        return false;
    }

    /// <summary>Select the next movable marker (a typed entity/deco) after the current
    /// one, wrapping, and centre the camera on it. Lets the inspector walk entities
    /// without hunting for 12 px cross-hairs on a dense level.</summary>
    public void SelectNextMovable()
    {
        if (_scene is null) return;
        var movable = new List<SceneCross>();
        foreach (var c in _scene.Crosses)
            if (!_hidden.Contains(c.Layer) && SceneProjector.IsMovable(c.Source)) movable.Add(c);
        if (movable.Count == 0) return;
        int cur = _selected is SceneCross cr ? movable.IndexOf(cr) : -1;
        var next = movable[(cur + 1) % movable.Count];
        _selected = next;
        CenterOn(next.At);
        RaiseSelection();
        InvalidateVisual();
    }

    private void CenterOn(PointD w)
    {
        _offset = new Point(w.X - (ActualWidth / 2) / _scale, w.Y - (ActualHeight / 2) / _scale);
    }

    public void FitToView()
    {
        if (_scene is null || _scene.Count == 0 || ActualWidth < 1 || ActualHeight < 1)
        {
            InvalidateVisual();
            return;
        }
        var b = _scene.Bounds;
        if (b.IsEmpty) { InvalidateVisual(); return; }
        const double pad = 40;
        double sx = (ActualWidth - 2 * pad) / b.Width;
        double sy = (ActualHeight - 2 * pad) / b.Height;
        _scale = Math.Min(sx, sy);
        if (!double.IsFinite(_scale) || _scale <= 0) _scale = 1.0;
        double cxw = (b.Left + b.Right) / 2, cyw = (b.Top + b.Bottom) / 2;
        _offset = new Point(cxw - (ActualWidth / 2) / _scale, cyw - (ActualHeight / 2) / _scale);
        InvalidateVisual();
    }

    private Point ToScreen(PointD w) => new((w.X - _offset.X) * _scale, (w.Y - _offset.Y) * _scale);
    private Point ToWorld(Point s) => new(s.X / _scale + _offset.X, s.Y / _scale + _offset.Y);

    /// <summary>The world point under a viewport-relative screen point. Exposed for commands anchored
    /// at the cursor ("add entity here") so the camera maths stays in one place.</summary>
    public PointD WorldAt(Point s)
    {
        var p = ToWorld(s);
        return new PointD(p.X, p.Y);
    }

    /// <summary>The world point at the centre of the visible area - where a command with no cursor
    /// (a menu item, a shortcut) should put a new object so it is on screen where the user is looking.</summary>
    public PointD ViewCentreWorld
    {
        get
        {
            var p = ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));
            return new PointD(p.X, p.Y);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_scene is null) return;
        // Slice-7 undo grouping: a new gesture is its own undo step, never coalesced with the
        // previous one (the shell's history service honours this reset before the drag commits).
        DragGestureStarted?.Invoke();
        var p = e.GetPosition(this);
        _lastMouse = p;
        _dragMoved = false;
        _moving = _movingPoly = false;
        _dragVert = -1;

        // Double-click on a selected polygon's edge inserts a new vertex at the cursor,
        // then grabs it so a continued drag positions it precisely. Only fires when the
        // click is near an edge (world tolerance scaled from the pixel grab radius).
        if (e.ClickCount == 2 && _selected is ScenePoly spAdd && IsEditablePoly(spAdd))
        {
            var wa = ToWorld(p);
            if (SceneProjector.TryAddPolyVertex(spAdd.Source, spAdd.ShapeIndex, new PointD(wa.X, wa.Y), HandleGrabPx / _scale, out int idx))
            {
                spAdd.Points = SceneProjector.PolyWorldPoints(spAdd.Source, spAdd.ShapeIndex);
                _dragVert = idx;
                _dragMoved = true;   // the insert itself is an edit, commit it on mouse-up
                CaptureMouse();
                RaiseSelection();
                InvalidateVisual();
                return;
            }
        }

        // If an editable polygon is already selected, a press on one of its vertex
        // handles grabs that vertex (checked before re-hittesting so the handles sit
        // "on top" of the shape body).
        if (_selected is ScenePoly spSel && IsEditablePoly(spSel))
        {
            int vi = NearestVertexHandle(spSel, p);
            if (vi >= 0)
            {
                _dragVert = vi;
                CaptureMouse();
                InvalidateVisual();
                return;
            }
        }

        var hit = HitTest(p);
        bool changedSel = !ReferenceEquals(hit, _selected);
        _selected = hit;
        if (changedSel) RaiseSelection();

        var w = ToWorld(p);
        _lastWorld = w;
        if (hit is SceneCross cr && SceneProjector.IsMovable(cr.Source)) _moving = true;
        else if (hit is ScenePoly sp && IsEditablePoly(sp)) _movingPoly = true;
        else _panning = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_dragVert >= 0 && _selected is ScenePoly spv && spv.Source is not null)
        {
            var w = ToWorld(p);
            if (SceneProjector.SetPolyVertexWorld(spv.Source, spv.ShapeIndex, _dragVert, new PointD(w.X, w.Y)))
            {
                RefreshPoly(spv);
                _dragMoved = true;
                InvalidateVisual();
            }
            return;
        }

        if (_movingPoly && _selected is ScenePoly spm && spm.Source is not null)
        {
            var w = ToWorld(p);
            double dx = w.X - _lastWorld.X, dy = w.Y - _lastWorld.Y;
            if ((dx != 0 || dy != 0) && SceneProjector.MoveRecord(spm.Source, dx, dy))
            {
                RefreshSource(spm.Source);
                _dragMoved = true;
            }
            _lastWorld = w;
            InvalidateVisual();
            return;
        }

        if (_moving && _selected is SceneCross c)
        {
            var w = ToWorld(p);
            double dx = w.X - _lastWorld.X, dy = w.Y - _lastWorld.Y;
            if ((dx != 0 || dy != 0) && SceneProjector.TryMoveEntity(c.Source, dx, dy))
            {
                c.At = new PointD(c.At.X + dx, c.At.Y + dy);
                _dragMoved = true;
            }
            _lastWorld = w;
            InvalidateVisual();
            return;
        }

        if (!_panning && !_panMiddle) return;
        double pdx = p.X - _lastMouse.X, pdy = p.Y - _lastMouse.Y;
        _offset = new Point(_offset.X - pdx / _scale, _offset.Y - pdy / _scale);
        _lastMouse = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMoved) Edited?.Invoke();
        _panning = false;
        _moving = false;
        _movingPoly = false;
        _dragVert = -1;
        _dragMoved = false;
        if (!_panMiddle) ReleaseMouseCapture();   // a middle-drag still needs the capture
    }

    // Middle-button drag pans the camera no matter what is under the cursor: it never selects,
    // never moves a record and never raises Edited, so it cannot interfere with the left
    // (select/drag) or right (context menu) bindings.
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || _scene is null) return;
        _panMiddle = true;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle) return;
        _panMiddle = false;
        if (Mouse.LeftButton != MouseButtonState.Pressed && !_panning && !_moving && !_movingPoly && _dragVert < 0)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_scene is null) return;
        var p = e.GetPosition(this);
        // A right-click on empty space keeps the current selection (so the menu can act on it);
        // a right-click on an object selects that object first, exactly like a left-click.
        var hit = HitTest(p);
        if (hit is not null && !ReferenceEquals(hit, _selected))
        {
            _selected = hit;
            RaiseSelection();
            InvalidateVisual();
        }
        ContextMenuRequested?.Invoke(p);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_scene is null) return;
        Point anchorScreen = e.GetPosition(this);
        Point anchorWorld = ToWorld(anchorScreen);
        double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        _scale = Math.Clamp(_scale * factor, 1e-5, 1e6);
        _offset = new Point(anchorWorld.X - anchorScreen.X / _scale, anchorWorld.Y - anchorScreen.Y / _scale);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_scene is not null) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // OnRender output is NOT clipped to the element's own bounds, so a transformed
        // sprite near the edge would otherwise paint over the neighbouring dock/inspector.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_scene is null) { dc.Pop(); return; }

        // Reference ghost first, so the live level paints on top of it.
        if (_onion is not null) DrawOnion(dc);

        foreach (var line in _scene.Lines)
            if (!_hidden.Contains(line.Layer)) DrawLine(dc, PenFor(line.Kind), line.Points);
        foreach (var poly in _scene.Polys)
            if (!_hidden.Contains(poly.Layer)) DrawPoly(dc, PenFor(poly.Kind), poly.Points);

        // Marker arms are a fixed on-screen size: ToScreen already maps the centre to
        // device-independent pixels, so the half-length must be a pixel constant too (a
        // world-space 6/scale here would make the X grow as you zoom out).
        const double mark = 7;
        // Painted in the game's order, not file order: a decoration must sit under the entities of its
        // own layer and under the decorations the level authors stacked on top of it (SceneGeometry
        // .PaintOrder = the reference editor's zValues). Crosses itself stays in record order.
        foreach (var cr in _scene.PaintOrder())
        {
            if (_hidden.Contains(cr.Layer)) continue;
            // A resolvable entity/deco sprite replaces the cross; everything else
            // (unrelated kinds, missing assets, dots-only mode) keeps the marker.
            if (!_dotsOnly && (cr.Kind == SceneKind.Entity || cr.Kind == SceneKind.Deco)
                && TryDrawSprite(dc, cr)) continue;
            var pen = cr.Kind switch
            {
                SceneKind.Entity => EntityPen,
                SceneKind.Deco => DecoPen,
                _ => OtherPen,
            };
            var c = ToScreen(cr.At);
            dc.DrawLine(pen, new Point(c.X - mark, c.Y - mark), new Point(c.X + mark, c.Y + mark));
            dc.DrawLine(pen, new Point(c.X - mark, c.Y + mark), new Point(c.X + mark, c.Y - mark));
        }

        DrawSelection(dc, mark);
        dc.Pop();
    }

    /// <summary>Draw one entity/deco as its sprite, anchored at the record's world position and
    /// sized the way the game sizes it (entities: <see cref="SpriteScale.EntityRender"/> off mass;
    /// decorations: the record's own world box, <c>dimensions/100</c> per axis). Returns false
    /// (caller draws the cross) when there is no bridge, no name, or the sprite cannot be
    /// resolved/decoded. The pivot stays fixed at the record position; rotation is clockwise,
    /// matching Qt.</summary>
    private bool TryDrawSprite(DrawingContext dc, SceneCross cr)
    {
        if (_sprites is null || cr.SpriteName is not { Length: > 0 } name) return false;
        bool entity = cr.Kind == SceneKind.Entity;
        bool got = entity
            ? _sprites.TryGet(name, 0, cr.ColorRgba, out var img, out int w, out int h, out double px, out double py, out double spriteOp)
            : _sprites.TryGetDeco(name, 0, cr.ColorRgba, out img, out w, out h, out px, out py, out spriteOp);
        if (!got) return false;
        double scX, scY;
        double pivotX, pivotY;
        if (entity)
        {
            double sc = SpriteScale.EntityRender(cr.Mass, w, h);
            scX = scY = sc; pivotX = px; pivotY = py;
        }
        else if (cr.DecoWorldW is not null || cr.DecoWorldH is not null)
        {
            // A decoration fills its record's explicit world rectangle, per axis (non-uniform) -
            // dimensions.raw is centiunits of world space in all four games, not a multiplier of
            // the sprite's pixel size. An axis with no dimension keeps the other axis's scale, so a
            // half-specified record still draws at its natural aspect.
            double? dw = cr.DecoWorldW, dh = cr.DecoWorldH;
            double sxw = dw is > 0 && w > 0 ? dw.Value / w : double.NaN;
            double sxh = dh is > 0 && h > 0 ? dh.Value / h : double.NaN;
            scX = double.IsNaN(sxw) ? (double.IsNaN(sxh) ? 1.0 : sxh) : sxw;
            scY = double.IsNaN(sxh) ? (double.IsNaN(sxw) ? 1.0 : sxw) : sxh;
            pivotX = 0.5; pivotY = 0.5;
        }
        else
        {
            // No dimensions at all: the game draws the cell itself, so 1 world unit = 1 pixel of
            // the sprite's own box (the same scale an untouched record's dims encode).
            scX = scY = 1.0; pivotX = 0.5; pivotY = 0.5;
        }
        double ox = -pivotX * w, oy = -pivotY * h;        // pixmap offset in image-pixel space
        var c = ToScreen(cr.At);
        double sx = _scale * scX, sy = _scale * scY;   // axis magnitudes only; the flips are a
        // separate step below, because the engine mirrors a decoration AFTER rotating it.
        // Off-screen cull: a sprite whose screen box misses the viewport is a full texture
        // blit for nothing (dense levels have thousands). Generous half-extents (a full
        // w/h each side) so pivot/rotation never clip a partly-visible sprite; returning
        // true also suppresses the fallback cross, which would be off-screen too.
        double halfW = Math.Abs(w * sx), halfH = Math.Abs(h * sy);
        if (c.X + halfW < 0 || c.X - halfW > ActualWidth || c.Y + halfH < 0 || c.Y - halfH > ActualHeight)
            return true;
        // A sprite that lands under MinSpritePx on screen carries no detail; without the always-
        // sprites view option it degrades to the cheap cross, which is what kept a zoomed-out
        // whole level fast (culling can't help when all sprites are on-screen).
        if (!_alwaysSprites && (halfW < MinSpritePx || halfH < MinSpritePx)) return false;
        // Compose scale (about the pivot-centred local origin) -> rotate -> mirror -> translate to
        // the screen anchor. Matrix appends left-to-right so point' = p·S·R·F·T (scale applied
        // first). One MatrixTransform beats a TransformGroup + three child transforms/frame.
        // Whole-sprite fade: the ElementEngine game multiplies a deco/entity's alpha by its
        // record colour's alpha byte (cr.Opacity) and the animation frame opacity (spriteOp),
        // which is what makes stains/decals semi-transparent. TP2 leaves cr.Opacity null and
        // bakes its alpha inside Tp2SpriteSource, so eff stays 1.0 there and nothing changes.
        double eff = Math.Clamp((cr.Opacity ?? 1.0) * spriteOp, 0.0, 1.0);
        if (eff <= 0.0) return true;            // fully transparent: draw nothing, suppress the cross
        var m = Matrix.Identity;
        m.Scale(sx, sy);
        // Matrix.Rotate takes DEGREES (not radians): a leaf at -120 deg must spin -120, not the
        // -2 deg a stray *PI/180 conversion produced (which made every entity look unrotated).
        // Sign matches the oracle: Qt setRotation(-raw/100) and WPF's y-down screen are both
        // clockwise-positive, so RotationDeg = -raw/100 feeds straight through.
        if (cr.RotationDeg != 0) m.Rotate(cr.RotationDeg);
        // The mirror comes AFTER the rotation: ElementDecoration hands the batcher one unchanged
        // angle plus the two flag bytes (draw2 -> ImageMap::blitAlphaFxF*), and the flip only rewrites
        // the cell-space anchor (cellW - (cropX + offX*2)), never the angle. A reflection conjugates a
        // rotation to its inverse, so a post-rotation mirror about the placement axes is what reads as
        // "rotate the other way" in game - rotating the sprite first and mirroring the placed result is
        // the same image, and it is the one the level author gets from the editor's Flip command.
        // Entities never set these flags, so their path is unchanged.
        if (cr.FlipX || cr.FlipY) m.Scale(cr.FlipX ? -1.0 : 1.0, cr.FlipY ? -1.0 : 1.0);
        m.Translate(c.X, c.Y);
        dc.PushTransform(new MatrixTransform(m));
        if (eff < 1.0) dc.PushOpacity(eff);
        dc.DrawImage(img, new Rect(ox, oy, w, h));
        if (eff < 1.0) dc.Pop();
        dc.Pop();
        return true;
    }

    private void DrawSelection(DrawingContext dc, double mark)
    {
        switch (_selected)
        {
            case SceneCross cr:
                if (_hidden.Contains(cr.Layer)) break;
                var c = ToScreen(cr.At);
                dc.DrawEllipse(null, SelectPen, c, mark + 4, mark + 4);
                break;
            case ScenePoly sp:
                if (_hidden.Contains(sp.Layer)) break;
                DrawPoly(dc, SelectPen, sp.Points);
                if (IsEditablePoly(sp))
                    foreach (var pt in sp.Points)
                    {
                        var s = ToScreen(pt);
                        dc.DrawRectangle(HandleBrush, SelectPen,
                            new Rect(s.X - HandleHalfPx, s.Y - HandleHalfPx, 2 * HandleHalfPx, 2 * HandleHalfPx));
                    }
                break;
            case SceneLine sl:
                if (_hidden.Contains(sl.Layer)) break;
                DrawLine(dc, SelectPen, sl.Points);
                break;
        }
    }

    // ------------------------------------------------------- selection -----

    private void RaiseSelection() => SelectionChanged?.Invoke(Describe(_selected));

    private static string Describe(object? sel) => sel switch
    {
        null => "",
        SceneCross cr => DescribeKind(cr.Kind, cr.Label, SceneProjector.IsMovable(cr.Source)),
        ScenePoly p => DescribeKind(p.Kind, p.Label, SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex)),
        SceneLine l => DescribeKind(l.Kind, l.Label, false),
        _ => "",
    };

    private static string DescribeKind(SceneKind kind, string? label, bool movable)
    {
        var s = kind + (string.IsNullOrEmpty(label) ? "" : $" “{label}”");
        return movable ? s + "  ·  drag to move / edit" : s;
    }

    // ------------------------------------------------ polygon edit helpers --

    private static bool IsEditablePoly(ScenePoly sp) =>
        SceneProjector.IsEditablePoly(sp.Source, sp.ShapeIndex);

    /// <summary>Index of the selected polygon's vertex handle within a screen-pixel grab
    /// radius of <paramref name="screen"/>, or -1 when none is close enough.</summary>
    private int NearestVertexHandle(ScenePoly sp, Point screen)
    {
        int best = -1;
        double bd = HandleGrabPx;
        for (int i = 0; i < sp.Points.Count; i++)
        {
            var s = ToScreen(sp.Points[i]);
            double d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            if (d <= bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>Re-read one polygon's cached world points from the model after an edit.
    /// Guarded on vertex count so a mismatch can never desync the overlay.</summary>
    private static void RefreshPoly(ScenePoly sp)
    {
        var pts = SceneProjector.PolyWorldPoints(sp.Source, sp.ShapeIndex);
        if (pts.Count == sp.Points.Count) sp.Points = pts;
    }

    /// <summary>Refresh every projected polygon owned by a record (used after a whole-
    /// shape move, since one wall/area may carry several shapes).</summary>
    private void RefreshSource(object src)
    {
        if (_scene is null) return;
        foreach (var p in _scene.Polys)
            if (ReferenceEquals(p.Source, src)) RefreshPoly(p);
    }

    /// <summary>Pull the selected polygon's cached world points back from the model and
    /// repaint. Used after an inspector field edit (e.g. water centre/size) so the on-screen
    /// rect tracks the numeric panel without a full re-projection or a selection change.</summary>
    public void SyncSelectionGeometry()
    {
        if (_selected is ScenePoly sp) RefreshPoly(sp);
        InvalidateVisual();
    }

    /// <summary>Topmost primitive under a screen point: markers first (drawn last, and
    /// the most clickable), then filled polygons (point-in-polygon), then lines within a
    /// screen-constant tolerance.</summary>
    private object? HitTest(Point screen)
    {
        if (_scene is null) return null;
        var w = ToWorld(screen);
        double tol = 10.0 / _scale;

        SceneCross? best = null;
        double bestD = tol;
        foreach (var cr in _scene.Crosses)
        {
            if (_hidden.Contains(cr.Layer)) continue;
            double d = HypotDist(cr.At, w);
            if (d <= bestD) { bestD = d; best = cr; }
        }
        if (best is not null) return best;

        for (int i = _scene.Polys.Count - 1; i >= 0; i--)
            if (!_hidden.Contains(_scene.Polys[i].Layer) && PointInPoly(_scene.Polys[i].Points, w.X, w.Y)) return _scene.Polys[i];
        for (int i = _scene.Lines.Count - 1; i >= 0; i--)
            if (!_hidden.Contains(_scene.Lines[i].Layer) && NearPolyline(_scene.Lines[i].Points, w.X, w.Y, tol)) return _scene.Lines[i];
        return null;
    }

    private static double HypotDist(PointD a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool PointInPoly(List<PointD> pts, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var pi = pts[i]; var pj = pts[j];
            if ((pi.Y > y) != (pj.Y > y) &&
                x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool NearPolyline(List<PointD> pts, double x, double y, double tol)
    {
        for (int i = 1; i < pts.Count; i++)
            if (DistToSegment(x, y, pts[i - 1], pts[i]) <= tol) return true;
        return false;
    }

    private static double DistToSegment(double px, double py, PointD a, PointD b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double wx = px - a.X, wy = py - a.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 == 0 ? 0 : (wx * vx + wy * vy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = a.X + t * vx, cy = a.Y + t * vy;
        double dx = px - cx, dy = py - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Pen PenFor(SceneKind k) => k switch
    {
        SceneKind.Wall => WallPen,
        SceneKind.Area => AreaPen,
        SceneKind.Path => PathPen,
        SceneKind.Water => WaterPen,
        _ => OtherPen,
    };

    private void DrawPoly(DrawingContext dc, Pen pen, List<PointD> pts)
    {
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToScreen(pts[0]), false, true);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(ToScreen(pts[i]), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawLine(DrawingContext dc, Pen pen, List<PointD> pts)
    {
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToScreen(pts[0]), false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(ToScreen(pts[i]), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    // Faint single-pen ghost pass: one translucent stroke for every reference primitive, mapped
    // through the onion offset/scale into the primary world frame. Crosses are drawn as tiny
    // diamonds (cheap) rather than sprites, so the ghost reads as an outline overlay and costs
    // nothing on dense levels. Deliberately ignores per-layer hiding — the ghost is a whole
    // reference image, not part of the editable scene.
    private static readonly Pen GhostPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 200, 210, 230)), 1.0));
    private void DrawOnion(DrawingContext dc)
    {
        if (_onion is null) return;
        foreach (var line in _onion.Lines)
            DrawMapped(dc, GhostPen, line.Points);
        foreach (var poly in _onion.Polys)
            DrawMappedClosed(dc, GhostPen, poly.Points);
        const double gm = 3;
        foreach (var cr in _onion.Crosses)
        {
            var s = ToScreen(OnionToWorld(cr.At));
            dc.DrawLine(GhostPen, new Point(s.X - gm, s.Y), new Point(s.X + gm, s.Y));
            dc.DrawLine(GhostPen, new Point(s.X, s.Y - gm), new Point(s.X, s.Y + gm));
        }
    }

    private void DrawMapped(DrawingContext dc, Pen pen, List<PointD> pts)
    {
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToScreen(OnionToWorld(pts[0])), false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(ToScreen(OnionToWorld(pts[i])), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawMappedClosed(DrawingContext dc, Pen pen, List<PointD> pts)
    {
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToScreen(OnionToWorld(pts[0])), false, true);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(ToScreen(OnionToWorld(pts[i])), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
