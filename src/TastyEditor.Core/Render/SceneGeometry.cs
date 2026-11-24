namespace TastyEditor.Core.Render;

// World-space display primitives. The scene has no UI dependency: the codecs'
// heterogeneous models (tb/tp2 semantic, f4/f5 raw spans) all project into these,
// and the WPF viewport draws them. Coordinates are the level's own world units with
// Y increasing downward — exactly the Qt QGraphicsScene convention the Python editor
// uses (items are positioned at (pos_x, pos_y); no axis flip), so a projection that
// mirrors the Python rebuild() renders a faithful picture.

public enum SceneKind { Wall, Area, Path, Entity, Deco, Water, PointList, Other }

/// <summary>Common identity for every projected primitive: which level (<c>Layer</c>)
/// it came from — for the layers dock's per-level visibility — and a scene-unique
/// <c>Id</c> (assigned by <see cref="SceneGeometry.AssignIds"/>) so the inspector can
/// name a selection without the UI holding the model object.</summary>
public interface ISceneElem
{
    SceneKind Kind { get; }
    string? Label { get; }
    int Layer { get; }
    int Id { get; }
}

/// <summary>A closed polygon in world coordinates (walls / defined areas).
/// When it came from a re-serialisable model record (a tb/tp2 wall or a tb area),
/// <c>Source</c> is that record and <c>ShapeIndex</c> is the index of the shape within
/// the record's shape list, so the viewport can read and edit its vertices in place
/// through <see cref="SceneProjector"/>. Element (f4/f5) polygons leave these null —
/// those records are opaque raw spans, not yet field-editable.</summary>
public sealed class ScenePoly : ISceneElem
{
    public SceneKind Kind { get; set; }
    public string? Label { get; set; }
    public int Layer { get; set; }
    public int Id { get; set; }
    public object? Source;
    public int ShapeIndex = -1;
    public List<PointD> Points = new();
}

/// <summary>An open polyline (path splines, point lists).</summary>
public sealed class SceneLine : ISceneElem
{
    public SceneKind Kind { get; set; }
    public string? Label { get; set; }
    public int Layer { get; set; }
    public int Id { get; set; }
    public List<PointD> Points = new();
}

/// <summary>A position marker (entities, whose bodies stay opaque for now).
/// <c>Source</c> is the originating model object when one exists and is safe to edit
/// in place (a typed tb/tp2 entity or deco); it is null for purely display markers
/// (f4/f5 raw-span items, wall fallbacks, point lists) so the viewport can offer a
/// drag only where a byte-faithful re-encode path already exists.</summary>
public sealed class SceneCross : ISceneElem
{
    public SceneKind Kind { get; set; }
    public string? Label { get; set; }
    public int Layer { get; set; }
    public int Id { get; set; }
    public PointD At;
    public object? Source;

    // Optional display metadata the projector sets so the viewport can draw a real
    // sprite instead of a cross. These are pure render inputs (no save coupling):
    // SpriteName keys the asset resolver, RotationDeg is the record's angle in degrees,
    // ColorRgba is a future tint input, and Pivot is the sprite's rotation anchor in
    // normalised [0,1] image space. Mass is a per-kind size scalar: for an Entity it is
    // the mass (rendered area, entity_render_scale), for a Deco it is the raw scaled
    // dimension (renderer divides by DIM_BASE). All default to the "no sprite" state so
    // existing cross-only projection stays exactly as before.
    public string? SpriteName;
    public double Mass = 1.0;
    public double RotationDeg;
    public byte[]? ColorRgba;
    public (double X, double Y)? Pivot;

    // Per-record sprite opacity in [0,1]. ElementEngine decorations (and the game's
    // build_sprite) fade the whole sprite by the record colour's alpha byte (eff =
    // colorAlpha/255 * frameOpacity), which is what makes stains and other decals semi-
    // transparent over the ground. Null means "fully opaque / not specified" so every
    // other projection path (TP2, which handles alpha inside its own sprite source) and
    // the existing entity render stay byte-for-byte unchanged.
    public double? Opacity;

    // TP2 decorations carry an explicit world-space rectangle (dimensions raw / 100, the
    // same raw->world convention as positions) that the texture is stretched to fill. When
    // these are set the viewport uses them (non-uniform) instead of the Mass/DIM_BASE scalar,
    // which is only correct for ~160px-native tiles. Left null for every other game so their
    // deco rendering path is unchanged.
    public double? DecoWorldW;
    public double? DecoWorldH;

    // Draw order, straight from the reference editor's QGraphicsItem zValues (both oracles use the
    // same formula): a decoration sits at layer*1000 + priority*0.001 and an entity 1000 higher, so
    // inside one layer every entity draws OVER the decorations and both are ordered by the record's
    // CHAINED priority (deco _prio_delta / entity priority, absolute = running sum down the section).
    // The priority term can never reach the 1000 offsets (observed range ~ -700..2 731), so the kind
    // offset and the layer always win - matching Qt's float zValue ordering exactly. Left at 0 the
    // list simply keeps projection order.
    public double ZOrder;

    // ElementDecoration::getFlipX / getFlipY (the two 4-byte booleans of the record's optional extra
    // block). The game mirrors the sprite about its own centre in LOCAL space, i.e. before rotation,
    // so a flipped deco with a rotation still leans the way the record says.
    public bool FlipX;
    public bool FlipY;
}

public readonly record struct PointD(double X, double Y);

/// <summary>An axis-aligned bound; the viewport's fit-to-view uses it.</summary>
public readonly record struct SceneBounds(double Left, double Top, double Right, double Bottom)
{
    public static readonly SceneBounds Empty = new(0, 0, 0, 0);
    public bool IsEmpty => Right <= Left || Bottom <= Top;
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>Everything the viewport needs to draw one level: three parallel primitive
/// lists plus a lazily computed bound. Projection is read-only — it never mutates the
/// lossless model, so opening a level can never break the byte-exact save path.</summary>
public sealed class SceneGeometry
{
    public List<ScenePoly> Polys = new();
    public List<SceneLine> Lines = new();
    public List<SceneCross> Crosses = new();

    public SceneBounds Bounds
    {
        get
        {
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            bool any = false;
            void Add(double x, double y)
            {
                any = true;
                if (x < l) l = x; if (x > r) r = x;
                if (y < t) t = y; if (y > b) b = y;
            }
            foreach (var p in Polys) foreach (var pt in p.Points) Add(pt.X, pt.Y);
            foreach (var p in Lines) foreach (var pt in p.Points) Add(pt.X, pt.Y);
            foreach (var p in Crosses) { Add(p.At.X - 4, p.At.Y - 4); Add(p.At.X + 4, p.At.Y + 4); }
            return any ? new SceneBounds(l, t, r, b) : SceneBounds.Empty;
        }
    }

    public int Count => Polys.Count + Lines.Count + Crosses.Count;

    /// <summary>The marker list in the game's draw order (see <see cref="SceneCross.ZOrder"/>); this is
    /// what the viewport paints. A stable sort keeps records that share a z in file order, exactly like
    /// Qt breaking ties by insertion.<para/>
    /// <c>Crosses</c> itself deliberately stays in RECORD order: the inspector, the hit-test and every
    /// verification gate address a record as "the n-th one" across a re-decode, and a priority edit
    /// legitimately moves its own record in the painted list - sorting the shared list would make such
    /// an index land on a neighbour. Only the renderer consumes this view.</summary>
    private List<SceneCross>? _paint;
    public IReadOnlyList<SceneCross> PaintOrder() => _paint ??= Crosses.OrderBy(c => c.ZOrder).ToList();

    /// <summary>Drop the cached paint view after mutating <see cref="Crosses"/> or a record's z.</summary>
    public void InvalidatePaintOrder() => _paint = null;

    /// <summary>Stamp a scene-unique 1-based Id onto every primitive in stable draw
    /// order (lines, polys, then crosses). Called once after projection; the Id lets the
    /// inspector reference a selection by number across the dock/viewport boundary.</summary>
    public void AssignIds()
    {
        int id = 1;
        foreach (var p in Polys) p.Id = id++;
        foreach (var l in Lines) l.Id = id++;
        foreach (var c in Crosses) c.Id = id++;
    }

    /// <summary>The primitive carrying <paramref name="id"/>, or null. Used by the
    /// inspector to resolve the current selection back to its geometry.</summary>
    public ISceneElem? FindById(int id)
    {
        foreach (var p in Polys) if (p.Id == id) return p;
        foreach (var l in Lines) if (l.Id == id) return l;
        foreach (var c in Crosses) if (c.Id == id) return c;
        return null;
    }
}
