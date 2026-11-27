using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TastyEditor.App.Assets;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>The slice-2 scene host: a custom <see cref="FrameworkElement"/> that draws
/// the projected world in <see cref="OnRender"/> and owns the camera (uniform scale +
/// translation, Y-down like the Qt scene). Left-drag on empty space pans, the wheel
/// zooms anchored under the cursor, and <see cref="FitToView"/> frames the whole level.
/// Left-click selects the topmost primitive under the cursor (raised via
/// <see cref="SelectionChanged"/>); dragging a selected entity/deco moves it in
/// the model (the byte-faithful re-encode path lives in Core). A selected entity or
/// decoration also gets transform handles (R2): a knob to turn it, corner grips to
/// resize it — the drag previews live and writes the model once, on release, so undo
/// stays one step per gesture. Wall/area polygons show vertex handles; double-click
/// an edge to add a vertex, right-click one to remove it.</summary>
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
    private bool _movingLine;   // R4: drags a whole selected path (SceneLine) whose Source is editable
    private int _dragVert = -1;
    private bool _dragMoved;
    private Point _lastWorld;

    // R2 transform gestures: a rotate-knob or corner-grip drag on the selected entity/deco.
    // While dragging, ONLY the cross's display fields move (they are pure render inputs, never
    // read by the encoder), so there is no model write per mouse-move; on release the gesture
    // commits once through the same write lane the inspector uses, and undo sees one step.
    private enum GripMode { None, Rotate, Size }
    private GripMode _grip = GripMode.None;
    private SceneCross? _gripCr;
    private Point _gripAnchor;              // fixed screen point the box scales/turns about (the record position)
    private Point _gripU, _gripV;           // press-time unit vectors along the box's width / height
    private Point _gripRel;                 // press cursor minus the anchor (the ratio denominators)
    private double _gripStartScale = 1;     // deco uniform size at press
    private (double W, double H)? _gripAxes;  // deco per-axis scales at press (null: no free resize on this record)
    private double _gripStartWW = 1, _gripStartWH = 1;   // deco world box at press, for the live preview
    private double _gripStartMass = 1;      // entity mass at press
    private double _gripF = 1, _gripFx = 1, _gripFy = 1; // last drag factors (uniform / per axis)
    private double _gripDeg;                // live rotation under the knob
    private double _gripStartDeg;           // rotation at press
    private double _gripStartBearing;       // cursor bearing (degrees, clockwise from up) at press

    // Layers dock state: hidden level indices (a level is shown unless listed here).
    private readonly HashSet<int> _hidden = new();

    // Sprite rendering (S5-3): the WPF asset bridge for the current level, or null when
    // the assets tree is missing / foreign (TP2) — then entities/deco render as crosses
    // exactly as before (capability-gated, no regression). DotsOnly forces the cross
    // path so a dense level can be browsed without decoding sprites.
    private ISpriteSource? _sprites;
    private bool _dotsOnly;

    // TP1 goo face animation, mirroring the base editor's ~30fps `animate` loop (main.py): the pupils
    // track the live cursor (lerped toward look-at-mouse, or centered while the pointer hovers the goo,
    // clamped inside the eye), and the eyelid plays its weighted blink once each 4s. The timer only
    // runs while a goo sprite is actually shown, and only redraws on a tick that changes something.
    private System.Windows.Threading.DispatcherTimer? _gooTimer;
    private readonly System.Diagnostics.Stopwatch _gooClock = System.Diagnostics.Stopwatch.StartNew();
    private Point _gooMouse, _lastGooMouse;
    private bool _gooMouseInside;
    private bool _lastBlink;          // previous blink-window state, so the frame AFTER a blink ends repaints (clears the eyelid)
    private double _pupilFactor = 1.0;
    private Point _gooCenterScreen;   // last rendered goo body centre / radius (screen px), for hit-test
    private double _gooRadiusScreen;
    private bool _gooLive;

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
        UpdateGooAnimation();
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
    private static readonly Pen SelectPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(96, 165, 250)), 1.6));
    private static readonly Brush HandleBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(96, 165, 250)));
    private const double HandleGrabPx = 9;   // screen-pixel radius to grab a vertex handle
    private const double HandleHalfPx = 4;   // half-size of the drawn handle square
    private const double KnobOffsetPx = 24;  // rotate knob: distance above the box top, screen px
    private const double FallbackMinHalfPx = 10;  // no-sprite handle box: min half-size, stays grabbable
    // R3 final gizmo language: thin, calm strokes in the accent hue on the selected object only
    // (unselected objects keep their muted category outline) — not the bright 2px debug cyan of R2.
    private static readonly Pen GripPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 96, 165, 250)), 1.1));
    private static readonly Brush KnobBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(28, 30, 36)));
    // R2 feedback for a missing-sprite object: a facing tick on the selected cross, so the turn is
    // unmistakable even with no art (the cross itself scales to the footprint beside it).
    private static readonly Pen FacingPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(230, 235, 160, 90)), 1.4));
    private const double FacingTickPx = 10;

    private static Brush FreezeBrush(Brush b) { b.Freeze(); return b; }

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    public void SetLevel(LevelDoc doc)
    {
        _scene = doc.Scene;
        _selected = null;
        _moving = false;
        _movingPoly = false;
        _movingLine = false;
        _dragVert = -1;
        _dragMoved = false;
        _grip = GripMode.None;
        _gripCr = null;
        _hidden.Clear();
        RaiseSelection();
        FitToView();
        UpdateGooAnimation();
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
        _movingLine = false;
        _dragVert = -1;
        _dragMoved = false;
        _grip = GripMode.None;
        _gripCr = null;
        UpdateGooAnimation();
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

    /// <summary>R5: keep the same Tasty Planet 1 element selected across a re-projection. Every tp1
    /// write rebuilds the whole node tree, so the projected markers are fresh instances; a
    /// <see cref="TastyEditor.Core.Levels.Tp1Handle"/>'s path (its child-index chain from the root) is
    /// the element's stable identity under value edits. Selects the line or cross whose source resolves
    /// to <paramref name="path"/> — used by the inspector's tp1 commit path to re-anchor the selection.</summary>
    public bool SelectTp1Path(int[] path)
    {
        if (_scene is null || path is null) return false;
        object? best = null;
        foreach (var l in _scene.Lines)
            if (l.Source is TastyEditor.Core.Levels.Tp1Handle h && SameTp1Path(h.Path, path)) { best = l; break; }
        if (best is null)
            foreach (var c in _scene.Crosses)
                if (c.Source is TastyEditor.Core.Levels.Tp1Handle h && SameTp1Path(h.Path, path)) { best = c; break; }
        if (best is null) return false;
        _selected = best;
        RaiseSelection();
        InvalidateVisual();
        return true;
    }

    private static bool SameTp1Path(int[] a, int[] b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
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
            SceneProjector.ApplyEntityFacing(cr);   // element entities: keep facing as a mirror, not a spin
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
            SceneProjector.ApplyEntityFacing(cr);   // element entities: keep facing as a mirror, not a spin
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
        _moving = _movingPoly = _movingLine = false;
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

        // R4 path parity: a double-click on a selected path's segment inserts a new anchor at the
        // cursor, then grabs it so a continued drag positions it precisely.
        if (e.ClickCount == 2 && _selected is SceneLine slAdd && IsEditableLine(slAdd))
        {
            var wa = ToWorld(p);
            if (SceneProjector.TryAddPathPoint(slAdd.Source, new PointD(wa.X, wa.Y), HandleGrabPx / _scale, out int sidx))
            {
                slAdd.Points = SceneProjector.PathWorldPoints(slAdd.Source);
                _dragVert = sidx;
                _dragMoved = true;
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

        // R4: a press on a selected path's anchor handle grabs that spline point.
        if (_selected is SceneLine slSel && IsEditableLine(slSel))
        {
            int ai = NearestHandleAt(slSel.Points, p);
            if (ai >= 0)
            {
                _dragVert = ai;
                CaptureMouse();
                InvalidateVisual();
                return;
            }
        }

        // R2 transform handles on the selected marker: the knob above its box rotates, the
        // corner grips resize. Grabbed before the re-hittest so a handle sits "on top" of its
        // object even when the cursor misses the marker centre, exactly like vertex handles.
        if (_selected is SceneCross crSel && TryCrossQuad(crSel, out var sq, out var sa))
        {
            if (SceneProjector.IsRotatable(crSel.Source) && Dist(KnobPos(sq, sa), p) <= HandleGrabPx)
            {
                _grip = GripMode.Rotate;
                _gripCr = crSel;
                _gripAnchor = sa;
                _gripStartDeg = crSel.RotationDeg;
                _gripDeg = crSel.RotationDeg;
                _gripStartBearing = BearingDeg(p.X - sa.X, p.Y - sa.Y);
                CaptureMouse();
                InvalidateVisual();
                return;
            }
            if (CanResize(crSel))
            {
                int gi = NearestGrip(sq, p);
                if (gi >= 0)
                {
                    _grip = GripMode.Size;
                    _gripCr = crSel;
                    _gripAnchor = sa;
                    _gripRel = new Point(p.X - sa.X, p.Y - sa.Y);
                    GripAxes(sq, out _gripU, out _gripV);
                    _gripStartScale = SceneProjector.FieldGet(crSel.Source, "dsize") ?? 1.0;
                    _gripAxes = SceneProjector.FieldDecoAxes(crSel.Source);
                    _gripStartMass = crSel.Mass;
                    _gripStartWW = crSel.DecoWorldW ?? Len(sq[1].X - sq[0].X, sq[1].Y - sq[0].Y) / _scale;
                    _gripStartWH = crSel.DecoWorldH ?? Len(sq[3].X - sq[0].X, sq[3].Y - sq[0].Y) / _scale;
                    _gripF = _gripFx = _gripFy = 1.0;
                    CaptureMouse();
                    InvalidateVisual();
                    return;
                }
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
        else if (hit is SceneLine sl && IsEditableLine(sl)) _movingLine = true;
        else _panning = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        // Feed the goo face animation: the pupils look toward this point. Repaint immediately (WPF
        // coalesces repeated InvalidateVisual into one render pass, so this adds no cost) so the eyes
        // track the cursor without waiting for the next 33ms tick, which was the source of the lag.
        _gooMouse = p; _gooMouseInside = true;
        if (_gooLive) InvalidateVisual();

        // R2 grip drags preview on the cross's render inputs only — no model write until release.
        if (_grip != GripMode.None && _gripCr is { } gcr)
        {
            if (_grip == GripMode.Rotate)
            {
                double deg = _gripStartDeg + BearingDeg(p.X - _gripAnchor.X, p.Y - _gripAnchor.Y) - _gripStartBearing;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) deg = Math.Round(deg / 15.0) * 15.0;
                _gripDeg = deg;
                gcr.RotationDeg = deg;
            }
            else
            {
                GripFactors(p, out _gripF, out _gripFx, out _gripFy);
                bool free = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && gcr.Kind == SceneKind.Deco && _gripAxes is not null;
                double fw = free ? _gripFx : _gripF, fh = free ? _gripFy : _gripF;
                if (gcr.Kind == SceneKind.Deco)
                {
                    gcr.DecoWorldW = _gripStartWW * fw;
                    gcr.DecoWorldH = _gripStartWH * fh;
                }
                else if (SceneProjector.FieldGet(gcr.Source, "mass") is not null)
                {
                    // The game's render size is sqrt(area), so a visual factor f needs mass x f^2.
                    gcr.Mass = Math.Max(0.01, _gripStartMass * _gripF * _gripF);
                }
            }
            InvalidateVisual();
            return;
        }

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

        // R4: drag one anchor of the selected path (recomputes its smooth tangents + bbox live).
        if (_dragVert >= 0 && _selected is SceneLine slv && slv.Source is not null)
        {
            var w = ToWorld(p);
            if (SceneProjector.SetPathPointWorld(slv.Source, _dragVert, new PointD(w.X, w.Y)))
            {
                RefreshPath(slv);
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

        // R4: drag the whole selected path (shifts every anchor + the record bbox).
        if (_movingLine && _selected is SceneLine slm && slm.Source is not null)
        {
            var w = ToWorld(p);
            double dx = w.X - _lastWorld.X, dy = w.Y - _lastWorld.Y;
            if ((dx != 0 || dy != 0) && SceneProjector.MoveRecord(slm.Source, dx, dy))
            {
                RefreshSource(slm.Source);
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
        if (_grip != GripMode.None) CommitGrip();   // one model write per gesture, then undo/dirty as usual
        if (_dragMoved) Edited?.Invoke();
        _panning = false;
        _moving = false;
        _movingPoly = false;
        _movingLine = false;
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
        // R2 polygon parity: a right-click on a selected shape's vertex handle removes that
        // vertex (the Python editor's remove-vertex; a shape never drops below three corners and
        // a water box's corners are not free vertices, so those clicks fall through to the menu).
        if (_selected is ScenePoly spDel && IsEditablePoly(spDel) && NearestVertexHandle(spDel, p) >= 0)
        {
            var vw = ToWorld(p);
            if (SceneProjector.TryRemovePolyVertex(spDel.Source, spDel.ShapeIndex, new PointD(vw.X, vw.Y),
                    HandleGrabPx / _scale, out _))
            {
                spDel.Points = SceneProjector.PolyWorldPoints(spDel.Source, spDel.ShapeIndex);
                if (_dragVert >= spDel.Points.Count) _dragVert = -1;
                Edited?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
        // R4 path parity: a right-click on a selected path's anchor removes it (an open path keeps
        // at least two anchors, so the last two are never removed; those clicks fall through to the menu).
        if (_selected is SceneLine slDel && IsEditableLine(slDel) && NearestHandleAt(slDel.Points, p) >= 0)
        {
            var vw = ToWorld(p);
            if (SceneProjector.TryRemovePathPoint(slDel.Source, new PointD(vw.X, vw.Y), HandleGrabPx / _scale, out _))
            {
                slDel.Points = SceneProjector.PathWorldPoints(slDel.Source);
                if (_dragVert >= slDel.Points.Count) _dragVert = -1;
                Edited?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
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

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _gooMouseInside = false;   // the goo pupils snap back toward look-out on the next tick
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
            // Artless entity/deco (sprite did not resolve, or dots-only): draw the cross at the
            // record's real footprint, turned by its rotation, so the size and turn you set STAY
            // visible whether or not the object is selected (R2 feedback). The footprint matches the
            // handle box. A facing tick shows which way a rotatable record points, selected or not.
            if (cr.Kind == SceneKind.Entity || cr.Kind == SceneKind.Deco)
            {
                FallbackFootprint(cr, out double hw, out double hh);
                var m = Matrix.Identity;
                if (cr.RotationDeg != 0) m.Rotate(cr.RotationDeg);   // degrees, y-down clockwise (as the sprite matrix)
                var d0 = m.Transform(new Point(-hw, -hh));
                var d1 = m.Transform(new Point(hw, hh));
                var d2 = m.Transform(new Point(-hw, hh));
                var d3 = m.Transform(new Point(hw, -hh));
                dc.DrawLine(pen, new Point(c.X + d0.X, c.Y + d0.Y), new Point(c.X + d1.X, c.Y + d1.Y));
                dc.DrawLine(pen, new Point(c.X + d2.X, c.Y + d2.Y), new Point(c.X + d3.X, c.Y + d3.Y));
                if (SceneProjector.IsRotatable(cr.Source))
                {
                    var tf = m.Transform(new Point(0, -(hh + FacingTickPx)));
                    dc.DrawLine(FacingPen, c, new Point(c.X + tf.X, c.Y + tf.Y));
                }
                continue;
            }
            dc.DrawLine(pen, new Point(c.X - mark, c.Y - mark), new Point(c.X + mark, c.Y + mark));
            dc.DrawLine(pen, new Point(c.X - mark, c.Y + mark), new Point(c.X + mark, c.Y - mark));
        }

        // Non-sprite guides paint last so they stay readable over the art: paths, walls, defined
        // areas and water outlines are editor-only lines, never something a deco or entity should
        // hide. Outlines under, paths on top of them.
        foreach (var poly in _scene.Polys)
            if (!_hidden.Contains(poly.Layer)) DrawPoly(dc, PenFor(poly.Kind), poly.Points);
        foreach (var line in _scene.Lines)
            if (!_hidden.Contains(line.Layer)) DrawLine(dc, PenFor(line.Kind), line.Points);

        DrawSelection(dc, mark);
        dc.Pop();
    }

    /// <summary>Draw one entity/deco as its sprite, anchored at the record's world position and
    /// sized the way the game sizes it (entities: the coverage-aware ink-area law per engine —
    /// <see cref="TbEntityScale"/> / <see cref="Tp2EntityScale"/> / <see cref="Tp1EntityScale"/> off mass;
    /// decorations: the record's own world box, <c>dimensions/100</c> per axis). Returns false
    /// (caller draws the cross) when the sprite cannot be resolved/decoded. The geometry lives in
    /// <see cref="TryResolveSprite"/> / <see cref="CrossRenderSize"/> / <see cref="BuildCrossMatrix"/>
    /// so the R2 transform handles sit on exactly the box the sprite paints.</summary>
    private bool TryDrawSprite(DrawingContext dc, SceneCross cr)
    {
        if (!TryResolveSprite(cr, out var img, out int w, out int h, out double px, out double py, out double spriteOp))
            return false;
        bool entity = cr.Kind == SceneKind.Entity;
        CrossRenderSize(cr, entity, w, h, px, py, out double scX, out double scY, out double pivotX, out double pivotY);
        double ox = -pivotX * w, oy = -pivotY * h;        // pixmap offset in image-pixel space
        var c = ToScreen(cr.At);
        double sx = _scale * scX, sy = _scale * scY;   // axis magnitudes only; the flips are a
        // separate step inside BuildCrossMatrix, because the engine mirrors a decoration AFTER
        // rotating it.
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
        // Whole-sprite fade: the ElementEngine game multiplies a deco/entity's alpha by its
        // record colour's alpha byte (cr.Opacity) and the animation frame opacity (spriteOp),
        // which is what makes stains/decals semi-transparent. TP2 leaves cr.Opacity null and
        // bakes its alpha inside Tp2SpriteSource, so eff stays 1.0 there and nothing changes.
        double eff = Math.Clamp((cr.Opacity ?? 1.0) * spriteOp, 0.0, 1.0);
        if (eff <= 0.0) return true;            // fully transparent: draw nothing, suppress the cross
        var m = BuildCrossMatrix(cr, w, h, scX, scY, pivotX, pivotY);
        dc.PushTransform(new MatrixTransform(m));
        if (eff < 1.0) dc.PushOpacity(eff);
        dc.DrawImage(img, new Rect(ox, oy, w, h));
        if (eff < 1.0) dc.Pop();
        dc.Pop();
        if (cr.IsGoo) DrawGooFace(dc, w, h, scX, c);
        return true;
    }

    // The base editor's goo physics_scale: sqrt(area / (spriteBox * areacoverage)) with a 4.0x boost
    // so a starting-size goo stays visible (main.py draw_goo). areacoverage is read from the greygoo
    // entitydef through the tp1 resolver (0.49 in the shipped table), falling back to the same value
    // when no tp1 sprite bridge is present.
    private double GooScale(double area, int w, int h)
    {
        double cov = (_sprites as Tp1SpriteSource)?.Resolver is { } r ? r.EntityCoverage("greygoo") : 0.49;
        double ww = Math.Max(w, 1), hh = Math.Max(h, 1), box = ww * hh * cov;
        return box > 0 && area > 0 ? Math.Sqrt(area / box) * 4.0 : 1.0;
    }

    /// <summary>A plain TP1 entity/emitter render scale: the game sizes its sprite so the covered
    /// area equals the record's minarea/maxarea, divided by the entitydef's areacoverage fraction
    /// (main.py <c>physics_scale = sqrt(minarea / (w*h*areacoverage))</c>). Same base as <see cref="GooScale"/>
    /// without the goo's 4x editor boost. Using the ElementEngine <c>EntityRender</c> here (no
    /// coverage) under-sizes spots whose entitydef covers less than the full cell.</summary>
    private double Tp1EntityScale(double area, int w, int h, string? entityDef)
    {
        double cov = (_sprites as Tp1SpriteSource)?.Resolver is { } r ? r.EntityCoverage(entityDef ?? "") : 1.0;
        double ww = Math.Max(w, 1), hh = Math.Max(h, 1), box = ww * hh * cov;
        return box > 0 && area > 0 ? Math.Sqrt(area / box) : 1.0;
    }

    /// <summary>A TP2 (Back for Seconds) entity render scale: the same ink-area law as
    /// <see cref="Tp1EntityScale"/>, using the entity's own entityintersections.xml <c>coverage</c> — the
    /// opaque fraction of exactly the natwidth x natheight box the assembled sprite now fills. The
    /// ElementEngine elongation hump in <see cref="SpriteScale.EntityRender"/> is an eyeballed fit for
    /// that editor's lab props ("Tested on lab2a": candy, caliper, test tube) and it is wrong for the
    /// rigs here: it inflated a tyrannosaurus (394x103, aspect 3.8) by 1.55x over the plain area model
    /// while leaving an alamosaurus (aspect 5.8) at 1.0, so two animals in the same level sat at
    /// unrelated relative sizes. Dropping the hump and dividing by coverage moves a sparse rig up
    /// (ant 0.217 -> 2.1x, roman_man 0.281 -> 1.9x) and a solid one barely at all (pyramid 1.0), which is
    /// what makes equal masses look like equal amounts of creature. 1.0 when the type has no def.</summary>
    private double Tp2EntityScale(double area, int w, int h, string? entityName)
    {
        var r = (_sprites as Tp2SpriteSource)?.Resolver;
        double cov = r is null ? 1.0 : r.EntityCoverage(entityName ?? "");
        // The denominator is the engine's own box, not our canvas. coverage is measured over
        // natwidth x natheight, but an assembled rig carries transparent padding beyond that box
        // wherever a part's art RECT is larger than its ink (hoverguy's hair teardrop: an 85x59 cell
        // whose declared extent is 82x62 of ink), so dividing by the canvas silently shrank the sprite
        // by the padding -- hoverguy drew at 0.72x its mass, a dino at ~0.94x. The canvas is only a
        // fallback for types with no def entry.
        var nb = r?.NatBox(entityName ?? "");
        double ww = Math.Max(w, 1), hh = Math.Max(h, 1);
        if (nb is (double nw, double nh, double) && nw > 1 && nh > 1) { ww = nw; hh = nh; }
        double box = ww * hh * cov;
        return box > 0 && area > 0 ? Math.Sqrt(area / box) : 1.0;
    }

    /// <summary>A Tasty Blue (ElementEngine) entity render scale: the game sizes a sprite so the material
    /// you SEE equals the record's mass (<c>physics_scale = sqrt(area / (spriteW*spriteH*areacoverage))</c>,
    /// proven in tp1 editor/main.py and the basis of the perfect TP2 law). Here <c>areacoverage</c> is the
    /// ink fraction of the ACTUAL built bitmap (measured in <see cref="SpriteSource"/>), not the XML natbox:
    /// TB's natwidth/natheight is a physics box in different units from the drawn art (kayak natbox 1528x1748
    /// vs its assembled canvas 377x142, opposite aspect), so dividing by the natbox collapsed every sprite
    /// (the reported "way too small"). Scaling the tight canvas by 1/sqrt(coverage) grows a padded cell or
    /// sparse rig to the size whose ink equals its mass; a solid sprite (coverage ~1) barely moves. Falls
    /// back to the plain hump only when there is no TB sprite bridge to measure.</summary>
    private double TbEntityScale(double area, int w, int h, string? entityName)
    {
        if (entityName is null || _sprites is not SpriteSource ss) return SpriteScale.EntityRender(area, w, h);
        double cov = ss.GetCoverage(entityName, 0);
        double ww = Math.Max(w, 1), hh = Math.Max(h, 1);
        double box = ww * hh * cov;
        return box > 0 && area > 0 ? Math.Sqrt(area / box) : 1.0;
    }

    // The two eyes, pupils and eyelids are separate newgoo0 cells layered over the body exactly as
    // main.py draws them: eyes at body scale x1.25 offset +/- 0.18 of the body box (pupils x1.6 on the
    // eye, tracking the cursor and clamped inside the eye radius), then the blink eyelid over both on
    // the frames inside its 4s window. Caches the body centre/radius so the tick can hit-test hover.
    private void DrawGooFace(DrawingContext dc, int bw, int bh, double bodyScale, Point c)
    {
        if (_sprites is not Tp1SpriteSource tps) { _gooLive = false; return; }
        double eyeScale = bodyScale * 1.25;
        double offX = bw * _scale * bodyScale * 0.18, offY = bh * _scale * bodyScale * 0.18;
        _gooCenterScreen = c; _gooRadiusScreen = (bw / 2.0) * _scale * bodyScale; _gooLive = true;

        // The greygoo entitydef declares the goo's colour (effectr/g/b/a = 0.5 gray). It applies to
        // the goo's own surface — the base body (via cr.ColorRgba), the bump head and the eyelids —
        // NOT the face features: the eyes stay raw white and the pupils raw black, exactly as in-game.
        byte[]? tint = tps.Resolver.EntityTint("greygoo");

        // Bump detail (the 5th new-graphics layer): the newgoo0 "goohead" cell is a soft filled sphere
        // that sits INSIDE the hollow, scalloped-rim body (newgoo1..8), giving the goo its rounded
        // surface. Same 200x200 source as the body, so it is drawn at body scale, centred, between the
        // base and the eyes, tinted like the body.
        if (tps.TryGetCell("newgoo0", "goohead", tint, out var head, out int hw, out int hh))
            DrawGooPart(dc, head, hw, hh, bodyScale, c.X, c.Y);

        // Eyes and pupils are NOT tinted (null) — white sclera, black pupils.
        if (!tps.TryGetCell("newgoo0", "gooeyes", null, out var eye, out int ew, out int eh)) return;
        DrawGooPart(dc, eye, ew, eh, eyeScale, c.X - offX, c.Y + offY);
        DrawGooPart(dc, eye, ew, eh, eyeScale, c.X + offX, c.Y + offY);

        if (tps.TryGetCell("newgoo0", "goopupils", null, out var pup, out int pw, out int ph))
        {
            double pupilScale = eyeScale * 1.6;
            // max_d = eye_radius - pupil_radius - 2*zoom (new-mode clamp in main.py); dist is the cursor
            // offset from the eye centre scaled by the smoothed look-at factor (0 centred, 1 at mouse).
            double eyeR = (ew / 2.0) * _scale * eyeScale, pupilR = (pw / 2.0) * _scale * pupilScale;
            double maxD = Math.Max(0.0, eyeR - pupilR - 2.0 * _scale);
            foreach (int sign in new[] { -1, 1 })
            {
                double cx = c.X + sign * offX, cy = c.Y + offY;
                double dx = (_gooMouse.X - cx) * _pupilFactor, dy = (_gooMouse.Y - cy) * _pupilFactor;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > maxD && dist > 0) { double k = maxD / dist; dx *= k; dy *= k; }
                DrawGooPart(dc, pup, pw, ph, pupilScale, cx + dx, cy + dy);
            }
        }

        // Eyelid blink (main.py 2.5): only during the anim's 0.25s window of each 4s cycle. Tinted
        // like the body (the lid is the goo's own surface closing over the eye).
        double eyeElapsed = _gooClock.Elapsed.TotalSeconds % 4.0;
        if (eyeElapsed < 0.25 && tps.TryGetAnimationFrame("eyelid", eyeElapsed, tint, out var lid, out int lw, out int lh))
        {
            DrawGooPart(dc, lid, lw, lh, eyeScale, c.X - offX, c.Y + offY);
            DrawGooPart(dc, lid, lw, lh, eyeScale, c.X + offX, c.Y + offY);
        }
    }

    // Start/stop the ~30fps goo tick so it only costs while a goo sprite is actually on screen (and
    // never on the four binary families, whose projector leaves IsGoo false).
    private void UpdateGooAnimation()
    {
        if (_scene is { } sc && _sprites is Tp1SpriteSource && HasGoo(sc))
        {
            // Paint the goo body in its entitydef effect colour (greygoo 0.5,0.5,0.5 = gray, not the
            // raw white cell art). Only the body rides cr.ColorRgba; the eyes/pupils/eyelid are built
            // through untinted cells, so they stay white/black as in-game. Re-applied every call since
            // a re-projection hands back fresh crosses with a null tint.
            byte[]? tint = (_sprites as Tp1SpriteSource)?.Resolver.EntityTint("greygoo");
            if (tint is not null) foreach (var c in sc.Crosses) if (c.IsGoo) c.ColorRgba = tint;
            if (_gooTimer is null)
            {
                _gooTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(33),   // the base editor's after(33) cadence
                };
                _gooTimer.Tick += (_, _) => StepGooFace();
            }
            if (!_gooTimer.IsEnabled) { _gooClock.Restart(); _pupilFactor = 1.0; _gooLive = false; _gooTimer.Start(); }
        }
        else if (_gooTimer is { IsEnabled: true }) _gooTimer.Stop();
    }

    private static bool HasGoo(SceneGeometry s)
    {
        foreach (var c in s.Crosses) if (c.IsGoo) return true;
        return false;
    }

    // Advance the look-at smoothing toward its target and repaint only when a visible part changed
    // (a settled still cursor and an off-blink idle cost no redraw), mirroring the lerp 0.28 factor.
    private void StepGooFace()
    {
        double elapsed = _gooClock.Elapsed.TotalSeconds;
        bool blinkNow = elapsed % 4.0 < 0.25;
        double target = 1.0;
        if (_gooLive && _gooMouseInside)
        {
            double ddx = _gooMouse.X - _gooCenterScreen.X, ddy = _gooMouse.Y - _gooCenterScreen.Y;
            target = Math.Sqrt(ddx * ddx + ddy * ddy) < _gooRadiusScreen * 1.1 ? 0.0 : 1.0;
        }
        bool settling = Math.Abs(target - _pupilFactor) > 0.002;
        if (settling) _pupilFactor += (target - _pupilFactor) * 0.28;
        else _pupilFactor = target;
        // Repaint while the pupils are still easing, while a blink is playing, on the tick a blink
        // ENDS (else the last eyelid frame freezes on screen), and when the cursor has moved.
        if (settling || blinkNow || _lastBlink || _gooMouse != _lastGooMouse) InvalidateVisual();
        _lastBlink = blinkNow;
        _lastGooMouse = _gooMouse;
    }

    // One face part: scale the cell about its centre and drop it at the given screen point (the same
    // row-vector append order as BuildCrossMatrix — scale first, translate last lands the centre).
    private void DrawGooPart(DrawingContext dc, BitmapSource img, int w, int h, double sc, double cx, double cy)
    {
        var m = Matrix.Identity;
        m.Scale(_scale * sc, _scale * sc);
        m.Translate(cx, cy);
        dc.PushTransform(new MatrixTransform(m));
        dc.DrawImage(img, new Rect(-w / 2.0, -h / 2.0, w, h));
        dc.Pop();
    }

    /// <summary>Resolve the bitmap a cross paints, with its image size, pivot and frame opacity.
    /// False when there is no bridge, no name, or the cell cannot be decoded — the caller then
    /// draws the plain cross marker. Shared by the render pass and the handle geometry.</summary>
    private bool TryResolveSprite(SceneCross cr, out BitmapSource img, out int w, out int h,
        out double pivotX, out double pivotY, out double opacity)
    {
        img = null!; w = h = 0; pivotX = pivotY = 0.5; opacity = 1.0;
        if (_sprites is null || cr.SpriteName is not { Length: > 0 } name) return false;
        // The tint a sprite paints with is whatever the record itself carries: the player goo's body
        // colour (set on cr.ColorRgba in UpdateGooAnimation) and a tile's record r/g/b. A TP1 emitter has
        // no record tint, so it falls back to its entitydef effectr/g/b/a — but ONLY when its art is a
        // bright achromatic silhouette (EffectTintApplies). The effect colour recolors white blob/goo
        // cells; dark art (black dominoes) and already-coloured art (cars, people) must render their own
        // art, not be multiplied. Display-only, read fresh each call; no level bytes change.
        byte[]? tint = cr.ColorRgba;
        if (tint is null && cr.Kind == SceneKind.Entity && _sprites is Tp1SpriteSource tps)
        {
            var eff = tps.Resolver.EntityTint(name);
            if (eff is not null && tps.EffectTintApplies(name)) tint = eff;
        }
        return cr.Kind == SceneKind.Entity
            ? _sprites.TryGet(name, 0, tint, out img, out w, out h, out pivotX, out pivotY, out opacity)
            : _sprites.TryGetDeco(name, 0, cr.ColorRgba, out img, out w, out h, out pivotX, out pivotY, out opacity);
    }

    /// <summary>The cross's per-axis render scale and effective pivot — the sizing rules the game
    /// follows: an entity scales off its mass; a decoration fills its record's explicit world
    /// rectangle per axis (dimensions is centiunits of world space in all four games, not a
    /// multiplier of the sprite's pixel size; an axis with no dimension keeps the other axis's
    /// scale, so a half-specified record still draws at its natural aspect); a record with no
    /// dimensions at all paints 1 world unit = 1 pixel of the sprite's own box.</summary>
    private void CrossRenderSize(SceneCross cr, bool entity, int w, int h, double px, double py,
        out double scX, out double scY, out double pivotX, out double pivotY)
    {
        if (entity)
        {
            // The goo rides the base editor's physics_scale (mass over the greygoo areacoverage box,
            // times the 4x editor boost), NOT the generic entity hump. Tasty Planet 1's loose-art
            // renderer sizes EVERY entity off its entitydef areacoverage (main.py: physics_scale =
            // sqrt(minarea / (spriteW*spriteH*areacoverage))), so a plain emitter uses the same base
            // model minus the goo's visibility boost — not the ElementEngine (tb/f4/f5) elongation
            // hump, which omits areacoverage and renders TP1 spots at the wrong size (half).
            double sc = cr.IsGoo ? GooScale(cr.Mass, w, h)
                : _sprites is Tp1SpriteSource ? Tp1EntityScale(cr.Mass, w, h, cr.SpriteName)
                : _sprites is Tp2SpriteSource ? Tp2EntityScale(cr.Mass, w, h, cr.SpriteName)
                : TbEntityScale(cr.Mass, w, h, cr.SpriteName);
            scX = scY = sc; pivotX = px; pivotY = py;
        }
        else if (cr.DecoWorldW is not null || cr.DecoWorldH is not null)
        {
            double? dw = cr.DecoWorldW, dh = cr.DecoWorldH;
            double sxw = dw is > 0 && w > 0 ? dw.Value / w : double.NaN;
            double sxh = dh is > 0 && h > 0 ? dh.Value / h : double.NaN;
            scX = double.IsNaN(sxw) ? (double.IsNaN(sxh) ? 1.0 : sxh) : sxw;
            scY = double.IsNaN(sxh) ? (double.IsNaN(sxw) ? 1.0 : sxw) : sxh;
            pivotX = 0.5; pivotY = 0.5;
        }
        else
        {
            scX = scY = 1.0; pivotX = 0.5; pivotY = 0.5;
        }
    }

    /// <summary>Compose the sprite's screen matrix: scale (about the pivot-centred local origin)
    /// -> rotate -> mirror -> translate to the screen anchor. Matrix appends left-to-right so
    /// point' = p·S·R·F·T (scale applied first). One Matrix beats a TransformGroup + three child
    /// transforms/frame.</summary>
    private Matrix BuildCrossMatrix(SceneCross cr, int w, int h, double scX, double scY, double pivotX, double pivotY)
    {
        var m = Matrix.Identity;
        m.Scale(_scale * scX, _scale * scY);
        // tp1 mirrors the SOURCE cell before the turn (the PIL/tk recipe), so the flip is written
        // about the image's own axes and the turned result lands the way the game places it.
        if (cr.FlipBeforeRotate && (cr.FlipX || cr.FlipY))
            m.Scale(cr.FlipX ? -1.0 : 1.0, cr.FlipY ? -1.0 : 1.0);
        // Matrix.Rotate takes DEGREES (not radians): a leaf at -120 deg must spin -120, not the
        // -2 deg a stray *PI/180 conversion produced (which made every entity look unrotated).
        // Sign matches the oracle: Qt setRotation(-raw/100) and WPF's y-down screen are both
        // clockwise-positive, so RotationDeg = -raw/100 feeds straight through.
        if (cr.RotationDeg != 0) m.Rotate(cr.RotationDeg);
        // The ElementEngine mirror comes AFTER the rotation: ElementDecoration hands the batcher one
        // unchanged angle plus the two flag bytes (draw2 -> ImageMap::blitAlphaFxF*), and the flip only
        // rewrites the cell-space anchor, never the angle. A reflection conjugates a rotation to
        // its inverse, so rotating the sprite and mirroring the placed result is the image the
        // level author gets from the editor's Flip command. Element entities also set FlipX here, but
        // as the ~180 deg facing mirror the projector derives (SceneProjector.ApplyEntityFacing),
        // not a record flip word (entities have none) - it composes identically, keeping them upright.
        if (!cr.FlipBeforeRotate && (cr.FlipX || cr.FlipY))
            m.Scale(cr.FlipX ? -1.0 : 1.0, cr.FlipY ? -1.0 : 1.0);
        var c = ToScreen(cr.At);
        m.Translate(c.X, c.Y);
        return m;
    }

    // ---------------------------------------------- R2 transform handles ----

    /// <summary>Whether this selected marker shows corner grips: only where a size write path
    /// actually exists (a decoration's size field or an entity's mass) — never on a record the
    /// inspector itself could not resize.</summary>
    private static bool CanResize(SceneCross cr) =>
        SceneProjector.IsResizable(cr.Source) &&
        (SceneProjector.FieldGet(cr.Source, "dsize") is double || SceneProjector.FieldGet(cr.Source, "mass") is double);

    /// <summary>The cross's screen-pixel half-extents for its no-sprite footprint: a decoration's
    /// real world box (<c>dimensions/100</c> per axis — the very rectangle its sprite would fill) or
    /// an entity's <c>sqrt(mass)</c> footprint (the game's rendered area IS its mass), floored at
    /// <see cref="FallbackMinHalfPx"/> so the marker and grips stay visible / grabbable at any zoom.
    /// Shared by <see cref="TryCrossQuad"/> and the missing-sprite cross marker so they agree.</summary>
    private void FallbackFootprint(SceneCross cr, out double hw, out double hh)
    {
        if (cr.Kind == SceneKind.Deco && (cr.DecoWorldW is not null || cr.DecoWorldH is not null))
        {
            hw = (cr.DecoWorldW ?? cr.DecoWorldH!.Value) * _scale / 2.0;
            hh = (cr.DecoWorldH ?? cr.DecoWorldW!.Value) * _scale / 2.0;
        }
        else
        {
            hw = hh = Math.Sqrt(Math.Max(cr.Mass, 1.0)) * _scale / 2.0;
        }
        hw = Math.Max(hw, FallbackMinHalfPx);
        hh = Math.Max(hh, FallbackMinHalfPx);
    }

    /// <summary>The selected marker's screen quad — the four corners of exactly the box its
    /// sprite paints (top-left, top-right, bottom-right, bottom-left of the record's own frame).
    /// When no sprite resolves the box is built from the record's OWN display fields (a decoration's
    /// real world footprint, an entity's sqrt(mass) footprint) turned by its live rotation, so the
    /// handles still grow and orbit during an R2 gesture even on a missing sprite. The anchor is the
    /// record's own screen position: the matrix's fixed point, which every scale and turn moves about.</summary>
    private bool TryCrossQuad(SceneCross cr, out Point[] quad, out Point anchor)
    {
        anchor = ToScreen(cr.At);
        if (TryResolveSprite(cr, out _, out int w, out int h, out double px, out double py, out _))
        {
            bool entity = cr.Kind == SceneKind.Entity;
            CrossRenderSize(cr, entity, w, h, px, py, out double scX, out double scY, out double pivotX, out double pivotY);
            var m = BuildCrossMatrix(cr, w, h, scX, scY, pivotX, pivotY);
            double ox = -pivotX * w, oy = -pivotY * h;
            quad = new[]
            {
                m.Transform(new Point(ox, oy)), m.Transform(new Point(ox + w, oy)),
                m.Transform(new Point(ox + w, oy + h)), m.Transform(new Point(ox, oy + h)),
            };
            return true;
        }
        // No sprite to size against: fall back on the record's own footprint box (turned), shared
        // with the missing-sprite cross marker so the two always agree.
        FallbackFootprint(cr, out double hw, out double hh);
        var rm = Matrix.Identity;
        if (cr.RotationDeg != 0) rm.Rotate(cr.RotationDeg);   // degrees, y-down clockwise (as the sprite matrix)
        var a = anchor;   // copy the out param; a local function can't capture it directly
        Point R(double x, double y) { var t = rm.Transform(new Point(x, y)); return new Point(a.X + t.X, a.Y + t.Y); }
        quad = new[] { R(-hw, -hh), R(hw, -hh), R(hw, hh), R(-hw, hh) };
        return true;
    }

    /// <summary>The rotate knob: a constant screen-pixel offset outward from the box's top edge,
    /// along the record's own "up", so it tracks the object while it turns.</summary>
    private static Point KnobPos(Point[] q, Point anchor)
    {
        var tm = new Point((q[0].X + q[1].X) / 2, (q[0].Y + q[1].Y) / 2);
        double dx = tm.X - anchor.X, dy = tm.Y - anchor.Y;
        double len = Len(dx, dy);
        if (len < 0.5) { dx = 0; dy = -1; len = 1; }   // flat view: park the knob straight up
        return new Point(tm.X + dx / len * KnobOffsetPx, tm.Y + dy / len * KnobOffsetPx);
    }

    private static int NearestGrip(Point[] q, Point p)
    {
        int best = -1;
        double bd = HandleGrabPx;
        for (int i = 0; i < q.Length; i++)
        {
            double d = Dist(q[i], p);
            if (d <= bd) { bd = d; best = i; }
        }
        return best;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Len(dx, dy);
    }

    private static double Len(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    /// <summary>The cursor's bearing around a centre in degrees, clockwise from straight up —
    /// the same clockwise-positive convention the y-down screen rotation uses (see
    /// <see cref="BuildCrossMatrix"/>), so a knob drag's bearing change IS its rotation change.</summary>
    private static double BearingDeg(double dx, double dy) => Math.Atan2(dx, -dy) * 180.0 / Math.PI;

    /// <summary>Unit vectors along the box's own width and height edges at press time — the axes
    /// a free (Shift) resize scales along.</summary>
    private static void GripAxes(Point[] q, out Point u, out Point v)
    {
        double ux = q[1].X - q[0].X, uy = q[1].Y - q[0].Y;
        double vx = q[3].X - q[0].X, vy = q[3].Y - q[0].Y;
        double ul = Len(ux, uy), vl = Len(vx, vy);
        u = ul > 0.01 ? new Point(ux / ul, uy / ul) : new Point(1, 0);
        v = vl > 0.01 ? new Point(vx / vl, vy / vl) : new Point(0, 1);
    }

    /// <summary>Drag factors relative to the press: uniform = cursor distance over the distance
    /// at press (both from the fixed anchor), per-axis = cursor projections over the press
    /// projections along the box's own axes. All clamped, so a drag through the anchor shrinks to
    /// a tiny box instead of zeroing or inverting a record's dimensions.</summary>
    private void GripFactors(Point cursor, out double fu, out double fx, out double fy)
    {
        double rx = cursor.X - _gripAnchor.X, ry = cursor.Y - _gripAnchor.Y;
        double du = Dist(cursor, _gripAnchor), dd = Len(_gripRel.X, _gripRel.Y);
        fu = ClampGrip(dd > 1 && du > 1 ? du / dd : 1.0);
        double px = rx * _gripU.X + ry * _gripU.Y;
        double py = rx * _gripV.X + ry * _gripV.Y;
        double gx = _gripRel.X * _gripU.X + _gripRel.Y * _gripU.Y;
        double gy = _gripRel.X * _gripV.X + _gripRel.Y * _gripV.Y;
        fx = ClampGrip(Math.Abs(gx) > 1 ? px / gx : 1.0);
        fy = ClampGrip(Math.Abs(gy) > 1 ? py / gy : 1.0);
    }

    private static double ClampGrip(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.02, 50.0) : 1.0;

    /// <summary>Release of a knob/grip gesture: the single model write, through exactly the
    /// operations the inspector's rows use (the rotation field, the size field or its per-axis
    /// pair, the mass). The cross's render inputs are then re-read FROM the model, so a refused
    /// write snaps the preview back instead of leaving the picture and the file disagreeing.</summary>
    private void CommitGrip()
    {
        var cr = _gripCr;
        object? src = cr?.Source;
        bool ok = _grip switch
        {
            GripMode.Rotate => src is not null && SceneProjector.SetRotationDeg(src, _gripDeg),
            GripMode.Size => src is not null && CommitSize(src, cr!),
            _ => false,
        };
        if (ok) _dragMoved = true;
        if (cr is not null)
        {
            SceneProjector.RefreshRender(cr);
            if (src is not null && SceneProjector.GetRotationDeg(src) is double deg) cr.RotationDeg = deg;
        }
        _grip = GripMode.None;
        _gripCr = null;
    }

    // The size half of CommitGrip: a decoration takes its uniform scale knob, or — only while
    // Shift is held, and only because every deco record really stores the pair — its free axes.
    // An entity only ever has the one field, so its grip is uniform no matter what Shift does.
    private bool CommitSize(object src, SceneCross cr)
    {
        if (SceneProjector.FieldGet(src, "dsize") is double s0)
        {
            bool free = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                && cr.Kind == SceneKind.Deco && _gripAxes is not null;
            if (free)
            {
                var (w0, h0) = _gripAxes!.Value;
                return SceneProjector.FieldSetDecoAxes(src, w0 * _gripFx, h0 * _gripFy);
            }
            return SceneProjector.FieldSet(src, "dsize", s0 * _gripF);
        }
        if (SceneProjector.FieldGet(src, "mass") is double m0)
        {
            // The game's render size is sqrt(area), so a visual factor f means mass x f^2.
            return SceneProjector.FieldSet(src, "mass", m0 * _gripF * _gripF);
        }
        return false;
    }

    private void DrawSelection(DrawingContext dc, double mark)
    {
        switch (_selected)
        {
            case SceneCross cr:
                if (_hidden.Contains(cr.Layer)) break;
                var c = ToScreen(cr.At);
                dc.DrawEllipse(null, SelectPen, c, mark + 4, mark + 4);
                DrawTransformHandles(dc, cr);
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
                if (IsEditableLine(sl))
                    foreach (var pt in sl.Points)
                    {
                        var s = ToScreen(pt);
                        dc.DrawRectangle(HandleBrush, SelectPen,
                            new Rect(s.X - HandleHalfPx, s.Y - HandleHalfPx, 2 * HandleHalfPx, 2 * HandleHalfPx));
                    }
                break;
        }
    }

    /// <summary>The manipulation chrome over the selected marker: its box outline, a grip square
    /// on every corner (only where a size write exists) and the rotate knob on a stem above the
    /// top edge (only where a rotation write exists). Everything is constant screen pixels, like
    /// the vertex handles, so it stays grabbable at any zoom.</summary>
    private void DrawTransformHandles(DrawingContext dc, SceneCross cr)
    {
        bool rot = SceneProjector.IsRotatable(cr.Source);
        bool size = CanResize(cr);
        if (!rot && !size) return;
        if (!TryCrossQuad(cr, out var q, out var a)) return;
        var box = new StreamGeometry();
        using (var ctx = box.Open())
        {
            ctx.BeginFigure(q[0], false, true);
            ctx.LineTo(q[1], true, false);
            ctx.LineTo(q[2], true, false);
            ctx.LineTo(q[3], true, false);
        }
        box.Freeze();
        dc.DrawGeometry(null, GripPen, box);
        if (size)
            foreach (var pt in q)
                dc.DrawRectangle(HandleBrush, SelectPen,
                    new Rect(pt.X - HandleHalfPx, pt.Y - HandleHalfPx, 2 * HandleHalfPx, 2 * HandleHalfPx));
        if (rot)
        {
            var tm = new Point((q[0].X + q[1].X) / 2, (q[0].Y + q[1].Y) / 2);
            var k = KnobPos(q, a);
            dc.DrawLine(GripPen, tm, k);
            dc.DrawEllipse(KnobBrush, SelectPen, k, 5, 5);
        }
    }

    // ------------------------------------------------------- selection -----

    private void RaiseSelection() => SelectionChanged?.Invoke(Describe(_selected));

    private static string Describe(object? sel) => sel switch
    {
        null => "",
        SceneCross cr => DescribeKind(cr.Kind, cr.Label, SceneProjector.IsMovable(cr.Source)) + TransformHint(cr),
        ScenePoly p => DescribeKind(p.Kind, p.Label, SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex)),
        SceneLine l => DescribeKind(l.Kind, l.Label, SceneProjector.IsEditablePath(l.Source)),
        _ => "",
    };

    // The polygon hint lives on the poly description; crosses get theirs here because the
    // handles depend on the record behind the marker, not the marker itself.
    private static string TransformHint(SceneCross cr)
    {
        bool rot = SceneProjector.IsRotatable(cr.Source);
        bool size = CanResize(cr);
        if (!rot && !size) return "";
        return rot && size ? "  \u00b7  drag the knob to turn it, the corners to resize it"
            : rot ? "  \u00b7  drag the knob to turn it"
            : "  \u00b7  drag the corners to resize it";
    }

    private static string DescribeKind(SceneKind kind, string? label, bool movable)
    {
        // R1: the kind reaches the status bar as the word the games use, never as the enum name.
        var s = UiText.Kind(kind) + (string.IsNullOrEmpty(label) ? "" : $" “{label}”");
        return movable ? s + "  ·  drag to move, or edit it in the inspector" : s;
    }

    // ------------------------------------------------ polygon edit helpers --

    private static bool IsEditablePoly(ScenePoly sp) =>
        SceneProjector.IsEditablePoly(sp.Source, sp.ShapeIndex);

    // R4: an editable path is a SceneLine whose Source the projector attached (a tb/tp2 path record
    // or an f4/f5 ElemPathEdit handle); its spline anchors get the same vertex-handle lane.
    private static bool IsEditableLine(SceneLine sl) => SceneProjector.IsEditablePath(sl.Source);

    /// <summary>Index of the cached-point handle within a screen-pixel grab radius of
    /// <paramref name="screen"/>, or -1 when none is close enough (shared by polygons and paths).</summary>
    private int NearestHandleAt(List<PointD> pts, Point screen)
    {
        int best = -1;
        double bd = HandleGrabPx;
        for (int i = 0; i < pts.Count; i++)
        {
            var s = ToScreen(pts[i]);
            double d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            if (d <= bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>Index of the selected polygon's vertex handle within a screen-pixel grab
    /// radius of <paramref name="screen"/>, or -1 when none is close enough.</summary>
    private int NearestVertexHandle(ScenePoly sp, Point screen) => NearestHandleAt(sp.Points, screen);

    /// <summary>Re-read one polygon's cached world points from the model after an edit.
    /// Guarded on vertex count so a mismatch can never desync the overlay.</summary>
    private static void RefreshPoly(ScenePoly sp)
    {
        var pts = SceneProjector.PolyWorldPoints(sp.Source, sp.ShapeIndex);
        if (pts.Count == sp.Points.Count) sp.Points = pts;
    }

    // Re-read a path's cached anchors, guarded on count so an add/remove (which resyncs Points
    // directly at the gesture site) can never desync the overlay here.
    private static void RefreshPath(SceneLine sl)
    {
        var pts = SceneProjector.PathWorldPoints(sl.Source);
        if (pts.Count == sl.Points.Count) sl.Points = pts;
    }

    /// <summary>Refresh every projected polygon or path owned by a record (used after a whole-
    /// shape or whole-path move, since one record may render as several primitives).</summary>
    private void RefreshSource(object src)
    {
        if (_scene is null) return;
        foreach (var p in _scene.Polys)
            if (ReferenceEquals(p.Source, src)) RefreshPoly(p);
        foreach (var l in _scene.Lines)
            if (ReferenceEquals(l.Source, src)) RefreshPath(l);
    }

    /// <summary>Pull the selected polygon or path's cached world points back from the model and
    /// repaint. Used after an inspector field edit (e.g. water centre/size) so the on-screen
    /// shape tracks the numeric panel without a full re-projection or a selection change.</summary>
    public void SyncSelectionGeometry()
    {
        if (_selected is ScenePoly sp) RefreshPoly(sp);
        else if (_selected is SceneLine sl) RefreshPath(sl);
        InvalidateVisual();
    }

    /// <summary>Topmost primitive under a screen point: a marker's own centre stays the precise grab
    /// (nearest within a screen-constant radius), then a click anywhere on a marker's PAINTED sprite box
    /// selects it (so large or oddly-shaped art is easy to pick without hunting the centre pixel), then
    /// filled polygons (point-in-polygon), then lines within a screen-constant tolerance.</summary>
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

        // Full-sprite hit: walk the paint order top-down (last painted = topmost) and take the first
        // marker whose sprite box contains the cursor. Restricted to entity/deco records that actually
        // resolve art and skipped in dots-only mode, so an object is clickable exactly where it is drawn
        // and the artless fallback cross / guide lines keep selecting by their centre as before.
        if (!_dotsOnly)
        {
            var paint = _scene.PaintOrder();
            for (int i = paint.Count - 1; i >= 0; i--)
            {
                var cr = paint[i];
                if (_hidden.Contains(cr.Layer)) continue;
                if (cr.Kind != SceneKind.Entity && cr.Kind != SceneKind.Deco) continue;
                if (!TryResolveSprite(cr, out _, out _, out _, out _, out _, out _)) continue;
                if (TryCrossQuad(cr, out var quad, out _) && PointInQuad(quad, screen)) return cr;
            }
        }

        for (int i = _scene.Polys.Count - 1; i >= 0; i--)
            if (!_hidden.Contains(_scene.Polys[i].Layer) && PointInPoly(_scene.Polys[i].Points, w.X, w.Y)) return _scene.Polys[i];
        for (int i = _scene.Lines.Count - 1; i >= 0; i--)
            if (!_hidden.Contains(_scene.Lines[i].Layer) && NearPolyline(_scene.Lines[i].Points, w.X, w.Y, tol)) return _scene.Lines[i];
        return null;
    }

    /// <summary>Screen-space point-in-convex-quad test. The sprite box is a rotated/scaled rectangle
    /// (always convex), so a point is inside iff every edge turns the same way.</summary>
    private static bool PointInQuad(Point[] q, Point p)
    {
        bool pos = false, neg = false;
        for (int i = 0; i < q.Length; i++)
        {
            var a = q[i]; var b = q[(i + 1) % q.Length];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (cross > 0) pos = true; else if (cross < 0) neg = true;
            if (pos && neg) return false;
        }
        return true;
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
