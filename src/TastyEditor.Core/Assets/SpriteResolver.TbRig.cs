namespace TastyEditor.Core.Assets;

/// <summary>A 2D affine as the engine stores <c>DGUI::Matrix2df</c>: a row-major 2x3
/// <c>(a, b, tx, d, e, ty)</c> applied to COLUMN vectors, <c>p' = M·p</c> so
/// <c>p'x = a·x + b·y + tx</c>, <c>p'y = d·x + e·y + ty</c>. Proven against the Final Bite
/// <c>libmain.so</c> helpers (<c>operator *</c> @0077756c is the plain matrix product A·B, so a child
/// matrix goes on the RIGHT and is applied first; <c>rotate</c>/<c>translate</c> post-multiply and
/// <c>setScaleXY</c>/<c>setRotation</c>/<c>setTranslate</c> overwrite; angles are DEGREES).
///
/// <para>The rig compositor stores one of these per shape. Both placement paths share the same
/// convention: a shape's decoded cell bitmap has its FRAME CENTRE at the local origin, so a bitmap
/// pixel <c>(u, v)</c> lands at <c>M·((u - pw/2)/s, (v - ph/2)/s)</c> in rig space, where <c>s</c> is the
/// part's <see cref="TbPart.Sfr"/>. The crop path uses <see cref="Identity"/> with <c>s = 1</c> (which
/// reproduces the historical crop-offset paste exactly); the affine path uses <c>skin·link</c> with
/// <c>s = scalefromraw</c>, which is what separates bilateral limbs that share a single sheet cell.</para>
///
/// <para>The <c>s</c> divisor is not an approximation but the engine's own blit. Final Bite
/// <c>ImageMap::blitAlphaRectCellSourceFxFMat</c> (00765e48) builds the drawn quad as
/// <c>FINAL · scale(1/scalefromraw) · translate(ox - w/2, oy - h/2)</c> over the unit square, with
/// <c>ox, oy</c> the crop offset and <c>w, h</c> the ink size, both in ATLAS pixels. Read as
/// <c>FINAL · translate(ox/s - ow/2, oy/s - oh/2) · scale(1/s)</c>, that says two things outright: the
/// point <c>FINAL</c> rotates a limb about is the cell frame CENTRE (not the crop offset, which is why a
/// top-left anchor tears a rig apart), and <c>FINAL</c> maps coordinates measured in RAW art pixels — so
/// every bone joint and link offset in <c>skeletons.bin</c> is in raw units while the atlas stores the
/// art shrunk by <c>scalefromraw</c>.</para></summary>
public readonly record struct TbMat(double A, double B, double Tx, double D, double E, double Ty)
{
    public static readonly TbMat Identity = new(1, 0, 0, 0, 1, 0);

    /// <summary>Standard matrix product A·B (column-vector convention).</summary>
    public static TbMat Mul(TbMat m, TbMat n) => new(
        m.A * n.A + m.B * n.D,
        m.A * n.B + m.B * n.E,
        m.A * n.Tx + m.B * n.Ty + m.Tx,
        m.D * n.A + m.E * n.D,
        m.D * n.B + m.E * n.E,
        m.D * n.Tx + m.E * n.Ty + m.Ty);

    /// <summary><c>Matrix2df::rotate(deg)</c> written from identity: a pure rotation (cos, -sin; sin,
    /// cos) in degrees.</summary>
    public static TbMat Rotation(double deg)
    {
        double r = deg * (System.Math.PI / 180.0), c = System.Math.Cos(r), s = System.Math.Sin(r);
        return new TbMat(c, -s, 0, s, c, 0);
    }

    /// <summary><c>Matrix2df::setScaleXY</c> from identity: an independent per-axis scale. The Final Bite
    /// mirror sets <c>E = -1</c>, flipping a shape across its frame's horizontal centre line.</summary>
    public static TbMat Scale(double sx, double sy) => new(sx, 0, 0, 0, sy, 0);

    /// <summary>A pure translation matrix.</summary>
    public static TbMat Translation(double tx, double ty) => new(1, 0, tx, 0, 1, ty);

    /// <summary>Map a point through the affine.</summary>
    public (double X, double Y) Apply(double x, double y)
        => (A * x + B * y + Tx, D * x + E * y + Ty);

    /// <summary>True when the linear part is the identity, i.e. the transform only translates. The
    /// compositor fast-paths these as a plain rectangle paste, so every crop-placed part keeps its
    /// historical integer blit (and the rigs that never collide stay byte-identical).</summary>
    public bool IsTranslateOnly => A == 1 && B == 0 && D == 0 && E == 1;
}

/// <summary>One art layer of a rig composite: the cell to paint, the affine <c>M</c> that places its
/// decoded original frame (frame centre at the local origin, rig space, x right / y down, rig origin at
/// 0,0), <c>N</c> — the rig-wide draw order the skeleton file carries (0 paints first, i.e. furthest
/// back) — and <c>Sfr</c>, the part's raw-units-per-atlas-pixel factor: a crop-placed part is measured in
/// atlas pixels and reads 1, while an affine Final Bite part is measured in RAW art pixels and carries the
/// cell's <c>scalefromraw</c>, because that is the unit the engine's joint and link numbers are in.
/// <c>InkCx</c>/<c>InkCy</c>/<c>InkHw</c>/<c>InkHh</c> are the cell's visible rectangle — its ink, not its
/// padded frame — in the same units as <c>M</c>, measured about the frame centre. Core derives them once from
/// the crop offset, the ink size and <c>Sfr</c> (see <see cref="InkRect"/>) and the compositor bounds its blit
/// with them, so the box a part paints into and the box the canvas was sized around cannot drift apart. That
/// bounding is what keeps the blit mapping a few thousand canvas pixels instead of nine million per part once
/// the frame is expanded to raw size.
/// Core computes the geometry; the WPF layer owns the pixels.</summary>
public readonly record struct TbPart(string Cell, TbMat M, int N, double Sfr = 1.0,
    double InkCx = 0, double InkCy = 0, double InkHw = 0, double InkHh = 0);

/// <summary>A rig resolved to its rest-pose assembly: <c>Parts</c> in paint order on a canvas
/// <c>W</c> x <c>H</c>, with the entity's world origin sitting at <c>(OriginX, OriginY)</c> inside it.
/// The canvas is the tight bounds of every part's ink rectangle (transformed by its affine), so the
/// bitmap carries no dead margin and the viewport's area-based render scale sees the same ink density
/// it would from a single cell.
///
/// <para><see cref="BaseScale"/> is the engine's own entity scale for this rig. A Final Bite composite is
/// assembled in raw art units so each limb keeps the authored resolution while the joint maths (which are
/// raw-unit) stay exact, but the engine draws the entity at ATLAS scale: the blit's <c>scalefromraw</c>
/// shrinks the raw frame back down. So the compositor multiplies the finished raw-size canvas by
/// <c>BaseScale</c> and hands back the size the game would show — for the ant a ~2700px raw assembly
/// becomes the ~175px sprite the level draws.</para></summary>
public sealed record TbRigPlan(
    string Rig, int W, int H, double OriginX, double OriginY, IReadOnlyList<TbPart> Parts, bool Fanout = false,
    double BaseScale = 1.0)
{
    public double PivotX => W > 0 ? OriginX / W : 0.5;
    public double PivotY => H > 0 ? OriginY / H : 0.5;
}

/// <summary>The skeleton half of <see cref="SpriteResolver"/>: read <c>skeletons.bin</c> and turn an
/// entity name into the part list its rig owns. Pure geometry — no pixels, no WPF — so it is checkable
/// headless against the Python reference decoder.</summary>
public sealed partial class SpriteResolver
{
    /// <summary>A composite bigger than this means the rig placed a part somewhere absurd; refuse it and
    /// let the caller fall back rather than allocate a multi-megapixel canvas for one marker. The ceiling
    /// is generous because a Final Bite rig is assembled at RAW art resolution (the unit its joint numbers
    /// are in) and only shrunk to atlas scale afterwards, so a legitimately large creature like the dragon
    /// legitimately spans a few thousand raw pixels — and the per-part blit is now bounded by the ink, not
    /// the padded frame, so the cost is set by the art, not by this box.</summary>
    private const int MaxRigCanvas = 8192;

    // The faithful FK rest-pose compositor <see cref="CollectAffine"/> reproduces
    // SkeletonBoneState::updateTree and ImageBoneLinkState::updateAnim exactly, and is the live path for
    // colliding Final Bite rigs. It is only coherent once the art is measured in the unit the engine uses
    // (raw art pixels, i.e. TbPart.Sfr) about the frame centre; see the blit note on <see cref="TbMat"/>.
    // An earlier joint-fanout heuristic that fitted a scale to the crop positions is gone: the engine's own
    // numbers needed no fitting, only the right unit and the right origin.

    public bool HasRigs => _rigs is { Count: > 0 };
    public int RigCount => _rigs?.Count ?? 0;
    public IReadOnlyDictionary<string, TbBone>? Rigs => _rigs;

    /// <summary>Why the skeleton table was refused (absent file or parse error), null when it loaded.
    /// The failure is tolerated by design, so the gate needs somewhere to read the reason from.</summary>
    public string? RigError { get; init; }

    /// <summary>Whether <c>skeletons.bin</c> parsed with the Final Bite record layout — the only dialect
    /// whose joint <c>Mode</c> and per-shape scale were confirmed against the decompiled
    /// <c>SkeletonBoneState::updateTree</c> / <c>ImageBoneLinkState::updateAnim</c>. The affine rest-pose
    /// compositor is confined to these rigs so the Blue / Forever crop layout is never regressed.</summary>
    public bool RigsAreFinalBite { get; init; }

    /// <summary>Load the skeleton table, tolerating everything that can be wrong with it: absent file
    /// (Tasty Planet 1 / 2 installs), a truncated file, a newer record layout. A rig is only ever an
    /// improvement over a cross marker, so a table that does not parse to the byte is dropped whole
    /// rather than half-trusted.</summary>
    internal static (Dictionary<string, TbBone>? Rigs, TbSkeletons.Dialect Dialect, string? Error)
        ReadSkeletons(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, default, "absent: " + path);
            var rigs = TbSkeletons.Read(File.ReadAllBytes(path), out var won);
            return rigs.Count > 0 ? (rigs, won, null) : (null, default, "empty table");
        }
        catch (Exception ex) { return (null, default, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Build the rest-pose assembly for <paramref name="rawName"/>'s rig. False when the install
    /// has no skeletons, no rig owns the name, or none of its shapes map to a cell the atlas has.
    ///
    /// Two placement paths share one <see cref="TbRigPlan"/>:
    /// <list type="bullet">
    /// <item>Every shape a rig uses was cut out of one composite: cells share an original frame and their
    /// crop offsets say where each piece sat inside it, so pasting each ink back at its crop offset
    /// rebuilds the creature exactly as drawn. That is the crop path — the identity affine — and it is
    /// what every Tasty Blue / Forever rig and every non-colliding Final Bite rig uses.</item>
    /// <item>But a Final Bite rig draws ONE arm and mirrors it: bilateral limbs reuse the SAME cell with
    /// the SAME crop, so the crop path pastes them onto one rectangle and one hides the other (124 such
    /// rigs). The engine instead separates them with the hierarchical 2x3 stack it builds in
    /// <c>updateTree</c>/<c>updateAnim</c> — each bone's <c>skin</c> matrix accumulated up the joint tree,
    /// times each shape's own link <c>scale·translate·rotate</c>. That is the affine path. It was validated
    /// headlessly: on the unique-crop shapes it reproduces the crop layout at scale 1.0 within ~0.3px, and
    /// on the colliding limbs it places them at distinct bilateral positions.</item>
    /// </list>
    /// The affine path is taken only for a Final Bite rig that actually collides, so the 134 rigs the
    /// crop path already renders correctly are untouched.</summary>
    public bool TryResolveRig(string rawName, out TbRigPlan plan)
    {
        plan = null!;
        if (_rigs is not { Count: > 0 } || string.IsNullOrEmpty(rawName)) return false;

        // Rig names carry the pre-alias art spelling (attackhelicopter, greatwhiteshark) while the level
        // record's TYPE is `<rig>_<set>`, so match the raw name first and only then the stripped forms.
        string? rigName = TbSkeletons.FindRig(_rigs, rawName)
            ?? TbSkeletons.FindRig(_rigs, EntityName.CatalogBase(rawName))
            ?? TbSkeletons.FindRig(_rigs, EntityName.SpeciesKey(rawName));
        if (rigName is null || !_rigs.TryGetValue(rigName, out var root)) return false;

        var placed = new List<TbPart>();
        // A Final Bite rig that reuses ONE cell for several bilateral limbs (the ant draws a single leg and
        // hangs six off it; the badger mirrors one paw onto four) cannot be separated by the crop path —
        // every copy pastes onto the SAME rectangle, so one hides the other. The engine separates them with
        // its hierarchical stack: each bone's <c>skin</c> matrix accumulated up the joint tree, times each
        // shape's own link <c>scale·translate·rotate</c>. That is <see cref="CollectAffine"/>, and it is the
        // whole story once the parts are measured in raw art units about the frame centre — the ant's six
        // legs then land on the thorax already splayed and bent, because the joint angles and the link
        // rotations are the artist's own. Every other rig — Blue / Forever, and Final Bite rigs with no cell
        // collision — stays on the exact crop paste below, so those rigs are untouched.
        bool colliding = RigsAreFinalBite && HasCellCollision(root);
        if (colliding)
            CollectAffine(root, TbMat.Identity, placed);
        else
            Collect(root, placed);
        if (placed.Count == 0) return false;

        // The file's own paint order, lowest first; OrderBy is stable, so shapes that share an N keep
        // their authored sibling order.
        var ordered = new List<TbPart>(placed.Count);
        foreach (var p in placed.OrderBy(p => p.N)) ordered.Add(p);

        // Tight bounds over each part's transformed INK rectangle (the visible content inside its padded
        // frame). It is the SAME rectangle Core hands the compositor to paint into, which is the point: if the
        // canvas were sized around one box and the parts drawn into another, the sprite would come out clipped
        // to whichever fragments overlapped. Corners map through M to rig space.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in ordered)
        {
            foreach (var corner in new[]
            {
                (p.InkCx - p.InkHw, p.InkCy - p.InkHh), (p.InkCx + p.InkHw, p.InkCy - p.InkHh),
                (p.InkCx - p.InkHw, p.InkCy + p.InkHh), (p.InkCx + p.InkHw, p.InkCy + p.InkHh),
            })
            {
                var (rx, ry) = p.M.Apply(corner.Item1, corner.Item2);
                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (ry < minY) minY = ry;
                if (ry > maxY) maxY = ry;
            }
        }
        int W = (int)Math.Ceiling(maxX - minX), H = (int)Math.Ceiling(maxY - minY);
        if (W <= 0 || H <= 0 || W > MaxRigCanvas || H > MaxRigCanvas) return false;

        // The engine's entity scale: the composite above is in raw art units, and the blit shrinks raw back
        // to atlas pixels by scalefromraw. One rig mixes cells at several dips (0.064 / 0.032 / 0.016), so the
        // assembly is authored at the finest of them and the whole is reduced by the LARGEST factor — never
        // upscale a part beyond the resolution it was cut at. A crop-placed rig has Sfr 1 everywhere, so this
        // is 1.0 and its historical output is untouched.
        double baseScale = 0.0;
        foreach (var p in ordered)
            if (p.Sfr > baseScale) baseScale = p.Sfr;
        if (baseScale <= 0.0) baseScale = 1.0;

        plan = new TbRigPlan(rigName, W, H, -minX, -minY, ordered, colliding, baseScale);
        return true;
    }

    /// <summary>Walk the joint tree and emit every shape it owns on the crop path. Each one is placed by
    /// its own image map record: the frame is pasted at its crop offset inside the composite, which puts
    /// the ink exactly where the artist drew it, so the creature is assembled coherently (never scattered).
    /// Centring each part's OWN frame rather than a shared one is what keeps a rig correct when it borrows
    /// a part from another rig's sheet (canoe's paddle lives in kayak's 377x200 frame, not canoe's 264x140).
    ///
    /// Bilateral limbs are the one thing the crop offset cannot separate: Final Bite draws ONE arm and
    /// mirrors it, so the far-side copy reuses the SAME cell+crop and would paste on top of the near-side
    /// copy and hide it (the "missing limb on one side" bug). Those rigs are handed to
    /// <see cref="CollectAffine"/> instead. The discriminator kept here is the engine's own: a shape whose
    /// <c>S2 &lt; 0</c> is the mirrored twin (<see cref="TbShape"/>), so it is emitted with the engine's
    /// <c>E = -1</c> flip — a reflection across the composite's long axis through the rig origin — which
    /// puts it on the far side. Near-side copies (<c>S2 &gt; 0</c>) keep the plain identity paste, so every
    /// non-mirrored rig stays byte-identical.</summary>
    private void Collect(TbBone bn, List<TbPart> outParts)
    {
        foreach (var sh in bn.Shapes)
        {
            if (!FindCell(sh.Image, out var c)) continue;   // art this install does not have
            // S2 < 0 marks the far-side mirror twin: flip it across the horizontal axis (the engine's own
            // E = -1) so both sides of a bilateral limb show, tidily attached. Blue / Forever never set it.
            var m = sh.S2 < 0 ? TbMat.Scale(1, -1) : TbMat.Identity;
            var (ix, iy, hw, hh) = InkRect(c, 1.0);
            outParts.Add(new TbPart(c.Name, m, sh.N, 1.0, ix, iy, hw, hh));
        }
        foreach (var k in bn.Children)
            Collect(k, outParts);
    }

    /// <summary>True when two shapes a rig draws resolve to the SAME cell (so the crop path would paste
    /// them onto one rectangle and hide one). Only such rigs need — and only these get — the affine stack.
    /// The count is over resolved cell names, not raw image spellings, because a base art and its
    /// <c>_dipN</c> variant collapse to one cell that the crop path would double-paste.</summary>
    private bool HasCellCollision(TbBone bn)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Collides(bn, seen);

        bool Collides(TbBone b, HashSet<string> seen)
        {
            foreach (var sh in b.Shapes)
                if (FindCell(sh.Image, out var c) && !seen.Add(c.Name)) return true;
            foreach (var k in b.Children)
                if (Collides(k, seen)) return true;
            return false;
        }
    }

    /// <summary>Walk the joint tree accumulating each bone's <c>skin</c> matrix exactly as the engine's
    /// <c>SkeletonBoneState::updateTree</c> does, then compose every shape with its
    /// <c>ImageBoneLinkState::updateAnim</c> link matrix (<c>scale · translate · rotate</c>), emitting one
    /// affine-carrying part per shape. This is the engine's own composite
    /// <c>FINAL = base · skin · link</c> read out of <c>ImageBoneLinkState::draw</c> (00a57cdc), which
    /// multiplies the caller's matrix by the link at <c>+0x14</c>, and <c>drawFromBone</c>, which passes the
    /// skin at <c>+0x80</c> in as that matrix. <c>base</c> is the entity's world placement and per-frame
    /// scale; the editor draws one entity at a time and centres the canvas on the rig origin, so it is the
    /// identity here — but it is NOT the missing factor that used to scatter limbs. That was the unit: the
    /// emitted part carries <c>Sfr = scalefromraw</c> because the joint and link numbers are in RAW art
    /// pixels, which is what the blit hands <c>FINAL</c> (see <see cref="TbMat"/>).
    ///
    /// updateTree (bind pose): both <c>world</c> (+0x68) and <c>skin</c> (+0x80) start as copies of the
    /// parent's <c>world</c>; then for a POLAR joint (<c>Mode == 0</c>) rotate both by the joint angle and
    /// translate <c>world</c> along its own axis by the joint length, while a CARTESIAN joint leaves
    /// <c>skin</c> unrotated and translates <c>world</c> by the (x, y) offset. Children recurse with the
    /// <c>world</c> matrix; each shape's art hangs off <c>skin</c> (this bone's offset positions its
    /// CHILDREN, not its own art — the art's own position comes from <c>skin</c> times the shape's B/C).</summary>
    private void CollectAffine(TbBone bn, TbMat parentWorld, List<TbPart> outParts)
    {
        TbMat skin, world;
        if (bn.Mode == 0)                                  // POLAR: P1 = angle(deg), P2 = length
        {
            skin = TbMat.Mul(parentWorld, TbMat.Rotation(bn.P1));
            world = TbMat.Mul(skin, TbMat.Translation(bn.P2, 0));
        }
        else                                               // CARTESIAN (or unknown): P1, P2 = offset
        {
            skin = parentWorld;
            world = TbMat.Mul(parentWorld, TbMat.Translation(bn.P1, bn.P2));
        }
        foreach (var sh in bn.Shapes)
        {
            if (!FindCell(sh.Image, out var c)) continue;   // art this install does not have
            // link = S(S1, S2) · T(B, C) · R(A)  (setScaleXY overwrite, then translate, then rotate)
            var link = TbMat.Mul(
                TbMat.Mul(TbMat.Scale(sh.S1, sh.S2), TbMat.Translation(sh.B, sh.C)),
                TbMat.Rotation(sh.A));
            var (ix, iy, hw, hh) = InkRect(c, c.ScaleFromRaw);
            outParts.Add(new TbPart(c.Name, TbMat.Mul(skin, link), sh.N, c.ScaleFromRaw, ix, iy, hw, hh));
        }
        foreach (var k in bn.Children)
            CollectAffine(k, world, outParts);
    }

    /// <summary>Map a skeleton shape's art spelling onto the cell the atlas actually stores. The exact
    /// key first (every Tasty Blue / Forever shape, and the Final Bite shapes whose art has no lighting
    /// variants), then the normalised spelling, then the <c>_dip1..4</c> variants Final Bite bakes into
    /// <c>imagemaps.xml</c>: its skeletons still name the base art (<c>alien_drone_body</c>) while the
    /// atlas only carries <c>alien_drone_body_dip1..4</c>, so an exact-key lookup stranded every such
    /// rig. The dips are the same silhouette under different lighting, so the rest pose takes the base
    /// spelling when the atlas has one (no phantom part when a creature mixes plain and dipped art) and
    /// otherwise the first variant, matching what <c>ScanEngineArt</c> already feeds the rig tier.</summary>
    private bool FindCell(string image, out Cell cell)
    {
        if (_cells.TryGetValue(image, out var exact)) { cell = exact; return true; }
        if (_cellNorm.TryGetValue(EntityName.Norm(image), out var norm)
            && _cells.TryGetValue(norm, out var nc)) { cell = nc; return true; }
        if (_cells.TryGetValue(image + "_dip1", out var d1)) { cell = d1; return true; }
        for (int k = 2; k <= 4; k++)
            if (_cells.TryGetValue($"{image}_dip{k}", out var dk)) { cell = dk; return true; }
        cell = null!;
        return false;
    }

    /// <summary>The frame the decoder hands back: the recorded original size, or the ink box itself for a
    /// cell that carries none (which is what <c>SpriteSource.Crop</c> builds, so the two must agree).</summary>
    private static double FrameW(Cell c) => c.OrigW > 0 ? c.OrigW : c.W;
    private static double FrameH(Cell c) => c.OrigH > 0 ? c.OrigH : c.H;

    /// <summary>Where a cell's ink actually sits, in the unit its part's affine is measured in, relative to
    /// the frame centre (x right, y down). The decoder pastes the ink at the crop offset inside the original
    /// frame, so the ink spans <c>[CropX, CropX + inkW]</c> in ATLAS pixels: shifting by half the frame puts
    /// it about the pivot the engine rotates a limb around, and dividing by <c>scalefromraw</c> puts it in
    /// raw art pixels like the joint numbers. A crop-placed part passes <c>sfr = 1</c>, which leaves the
    /// atlas-pixel geometry alone.
    ///
    /// <para>Both the canvas bounds and the per-part blit come from here, which matters: the blit walks the
    /// transformed ink rectangle instead of the whole padded frame (nine million empty pixels per part at raw
    /// size), so if the two disagreed the canvas would be sized for where the parts should land while each
    /// part painted into a box centred somewhere else — a sprite clipped to a fragment of one limb.</para>
    /// <returns>Centre and half-extents, in the part's units about the frame centre.</returns></summary>
    private static (double Cx, double Cy, double Hw, double Hh) InkRect(Cell c, double sfr)
    {
        if (sfr <= 0) sfr = 1.0;
        double ow = FrameW(c), oh = FrameH(c);
        double cx = ((c.OrigW > 0 ? c.CropX : 0) + c.W / 2.0 - ow / 2.0) / sfr;
        double cy = ((c.OrigH > 0 ? c.CropY : 0) + c.H / 2.0 - oh / 2.0) / sfr;
        return (cx, cy, c.W / 2.0 / sfr, c.H / 2.0 / sfr);
    }
}
