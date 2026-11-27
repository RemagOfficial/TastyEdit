using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TastyEditor.Core.Assets;

namespace TastyEditor.App.Assets;

/// <summary>The WPF half of the asset pipeline: decodes an atlas image into 32bpp BGRA
/// pixels (handling the two shipping forms — a <c>.jpg</c> colour plate with a
/// companion <c>mask.png</c> alpha, as Tasty Blue / Forever ship, or a single RGBA
/// <c>.png</c> sheet, as Final Bite ships), crops a cell and pastes it back onto its
/// original transparent canvas, multiplies the result by the record's colour, and caches the
/// frozen result. Everything it returns is a frozen <see cref="BitmapSource"/>, safe to draw
/// from the viewport's render thread.</summary>
public sealed class SpriteSource : ISpriteSource
{
    private readonly SpriteResolver _res;
    private readonly string _graphicsDir;

    // atlas name -> decoded pixels + dimensions (decoded once, reused by every cell).
    private readonly Dictionary<string, AtlasPixels?> _atlas = new();
    // resolved-cell geometry -> the composed sprite bitmap.
    private readonly Dictionary<string, BitmapSource?> _cell = new();
    // sprite name + frame + record colour -> the built sprite. The viewport asks for every
    // marker on every repaint; this front cache skips resolve, crop-key building and the
    // colour multiply on the hot path.
    private readonly Dictionary<string, NamedSprite?> _byName = new();
    // name + frame -> the built bitmap's ink coverage (summed alpha / pixel count, tint-independent).
    // The viewport sizes a TB entity off this so the drawn material equals the record's mass, exactly
    // as TP2 divides by its entitydef coverage; the hump instead assumed a solid box and under-sized
    // every padded cell / sparse rig.
    private readonly Dictionary<string, double> _cov = new();

    public SpriteSource(SpriteResolver res)
    {
        _res = res;
        _graphicsDir = res.Root.GraphicsDir;
    }

    private sealed record AtlasPixels(byte[] Px, int W, int H);
    private sealed record NamedSprite(BitmapSource Image, int W, int H, double PivotX, double PivotY, double Opacity);

    /// <summary>The core resolver, exposed so the viewport can size a TB entity off its entitydef
    /// coverage (the same ink-area law TP2 uses) instead of the coverage-blind elongation hump.</summary>
    public SpriteResolver Resolver => _res;

    /// <summary>Resolve <paramref name="rawName"/> (frame <paramref name="frame"/>) and
    /// build its sprite bitmap. Returns false when the name maps to no cell or the atlas
    /// cannot be decoded, so the caller draws its fallback marker. Never throws.
    /// <paramref name="tint"/> is the record's RGBA colour: the ElementEngine multiplies the
    /// sprite's RGB by it (a per-record recolour - tinted coral, coloured sails) while keeping
    /// its alpha; the alpha byte itself is a fade the viewport applies separately, so it is
    /// deliberately not multiplied in here.</summary>
    public bool TryGet(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
    {
        image = null!; w = h = 0; pivotX = pivotY = 0.5; opacity = 1.0;
        // Hot path (called for every marker on every repaint): a record's sprite is static, so
        // cache the built bitmap by name+frame+colour and skip resolve and the pixel multiply.
        string key = CacheKey(rawName, frame, tint);
        if (_byName.TryGetValue(key, out var fast))
        {
            if (fast is null) return false;
            image = fast.Image; w = fast.W; h = fast.H;
            pivotX = fast.PivotX; pivotY = fast.PivotY; opacity = fast.Opacity;
            return true;
        }
        if (!TryBuildNamed(rawName, frame, tint, out image, out w, out h, out pivotX, out pivotY, out opacity))
        {
            if (frame == 0) _byName[key] = null;
            return false;
        }
        if (frame == 0)
        {
            _byName[key] = new NamedSprite(image, w, h, pivotX, pivotY, opacity);
            _cov[$"{rawName}|0"] = InkCoverage(image, w, h);
        }
        return true;
    }

    /// <summary>The ink coverage (opaque fraction) of a sprite the viewport already built, or 1.0 when it
    /// has not been resolved yet (so the caller falls back to a plain box-area scale). Coverage is
    /// tint-independent, so the frame-0 cache serves every record colour.</summary>
    public double GetCoverage(string rawName, int frame) =>
        _cov.TryGetValue($"{rawName}|{frame}", out var c) && c > 0 ? c : 1.0;

    // Fraction of the bitmap that carries material: summed alpha over 255 divided by the pixel count.
    // Counting partial alpha (not just a non-zero test) matches a feathered mask the way the game's
    // own areacoverage does, so a soft-edged cell is not credited as fully solid.
    private static double InkCoverage(BitmapSource img, int w, int h)
    {
        if (w <= 0 || h <= 0) return 1.0;
        int stride = w * 4;
        var px = new byte[stride * h];
        img.CopyPixels(px, stride, 0);
        long acc = 0;
        for (int i = 3; i < px.Length; i += 4) acc += px[i];
        double total = (double)w * h * 255.0;
        return total > 0 ? Math.Clamp(acc / total, 0.001, 1.0) : 1.0;
    }

    /// <summary>The entity-name cascade in the order the shipped data supports it: (1) the entity's
    /// <c>skeletons.bin</c> rig, composited from its parts, whenever the rig owns more than one part,
    /// (2) the art the executable itself registers the entity with, (3) a cell the tables name directly,
    /// (4) the rig again for a single-part rig, (5) the art stem and then the fuzzy spelling.
    ///
    /// The rig leads because a multi-part rig IS the creature while its cells are pieces of one sheet:
    /// Tasty Blue's <c>canoe</c> cell is the hull alone, so answering with that cell drew a boat with no
    /// paddlers in it, even though the <c>canoe</c> rig carries the hull, two rowers, four arm segments
    /// and two paddles. A one-part rig is the same art as its cell, so those still go through the tables
    /// first and keep whatever pivot and opacity the cell's animation carries. Tasty Blue needs tiers 1
    /// and 5 — none of its 326 entity TYPEs is a cell name and its animationdefs.xml is an empty stub, so
    /// before these only 10 of 460 sampled entity marks drew art.
    ///
    /// Tier 2 leads tier 3 because a TYPE that happens to spell a cell name is a coincidence, not a
    /// decision: the executable's registration record is the game saying what it builds the entity from,
    /// and it outranks an edit-distance guess. Measured over the shipped levels the two agree for every
    /// name but <c>crust1_base</c> and <c>crust2_base</c>, whose cells (<c>crust</c>, <c>crust2</c>) are
    /// horizon bands that no entity registration references at all, while the records name
    /// <c>rock_middle1</c> / <c>rock_middle2</c>. The re-entry uses the art name rather than resolving it
    /// to a cell outright, so an entity registered against a rig (<c>crab_normal</c> → <c>crab</c>) still
    /// composites and one registered against a cell keeps that cell's pivot and animation opacity.</summary>
    private bool TryBuildNamed(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => TryBuildNamed(rawName, frame, tint, false, out image, out w, out h, out pivotX, out pivotY, out opacity);

    private bool TryBuildNamed(string rawName, int frame, byte[]? tint, bool viaEngine, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
    {
        image = null!; w = h = 0; pivotX = pivotY = 0.5; opacity = 1.0;
        bool hasRig = _res.TryResolveRig(rawName, out var plan);
        bool rigFirst = hasRig && plan!.Parts.Count > 1;
        if (rigFirst && TryBuildRig(plan, out var whole))
            return Finish(Tint(whole, tint), plan.PivotX, plan.PivotY, 1.0,
                out image, out w, out h, out pivotX, out pivotY, out opacity);
        // One re-entry, flagged so the substituted name cannot substitute again.
        if (!viaEngine && _res.TryGetEngineArt(rawName, out string art)
            && !string.Equals(art, rawName, StringComparison.Ordinal)
            && TryBuildNamed(art, frame, tint, true, out image, out w, out h, out pivotX, out pivotY, out opacity))
            return true;
        if (_res.TryResolveDirectCell(rawName, frame, out var s) && TryBuild(s, out var solo))
            return Finish(Tint(solo, tint), s.PivotX, s.PivotY, s.Opacity,
                out image, out w, out h, out pivotX, out pivotY, out opacity);
        if (hasRig && TryBuildRig(plan, out var rig))
            return Finish(Tint(rig, tint), plan.PivotX, plan.PivotY, 1.0,
                out image, out w, out h, out pivotX, out pivotY, out opacity);
        if (_res.TryResolveFallbackCell(rawName, frame, out s) && TryBuild(s, out var last))
            return Finish(Tint(last, tint), s.PivotX, s.PivotY, s.Opacity,
                out image, out w, out h, out pivotX, out pivotY, out opacity);
        return false;
    }

    private static bool Finish(BitmapSource built, double px, double py, double opac, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
    {
        image = built; w = built.PixelWidth; h = built.PixelHeight;
        pivotX = px; pivotY = py; opacity = opac;
        return true;
    }

    /// <summary>Composite a rig's parts onto one transparent canvas, in the file's own paint order.
    /// Each part reuses the single-cell decode (so a cell shared by many entities is decoded once); the
    /// decoded bitmap is the cell's whole original frame with its ink already at the crop offset, and it
    /// is placed by the part's affine (<c>M</c>, frame centre at the local origin) into the canvas. A part
    /// that only translates — every crop-placed rig — goes through the exact integer paste, so the rigs
    /// the crop path already renders correctly are pixel-identical to before; only a rotated or mirrored
    /// part takes the slower inverse-map blit. The record's colour is multiplied over the whole result
    /// afterwards, exactly once, the way the engine tints the assembled shape rather than each layer.</summary>
    private bool TryBuildRig(TbRigPlan plan, out BitmapSource image)
    {
        image = null!;
        int W = plan.W, H = plan.H;
        if (W <= 0 || H <= 0) return false;
        var acc = new byte[W * H * 4];
        bool any = false;
        foreach (var p in plan.Parts)
        {
            if (!_res.TryResolveCellName(p.Cell, out var s) || !TryBuild(s, out var part)) continue;
            int pw = part.PixelWidth, ph = part.PixelHeight;
            var px = new byte[pw * ph * 4];
            part.CopyPixels(px, pw * 4, 0);
            if (p.M.IsTranslateOnly && p.Sfr == 1.0)
            {
                if (Blit(acc, W, H, px, pw, ph,
                        (int)Math.Round(plan.OriginX - pw / 2.0 + p.M.Tx),
                        (int)Math.Round(plan.OriginY - ph / 2.0 + p.M.Ty)))
                    any = true;
            }
            else if (AffineBlit(acc, W, H, px, pw, ph, p.M, plan.OriginX, plan.OriginY, p.Sfr,
                        p.InkCx, p.InkCy, p.InkHw, p.InkHh))
                any = true;
        }
        if (!any) return false;
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, acc, W * 4);
        bmp.Freeze();
        // The assembly is in raw art units; the engine draws it at atlas scale (the blit's scalefromraw),
        // so shrink the finished composite rather than each part. WPF's TransformedBitmap is the cheap
        // shrink here; the raw composite is what makes the limbs legible at all — a leg cell is a 13x33
        // sliver at atlas resolution but a properly shaped limb at raw resolution, and reducing the
        // assembled creature keeps that shape instead of the sliver's pixel count.
        if (plan.BaseScale > 0 && plan.BaseScale < 1.0 - 1e-9)
        {
            var sc = new System.Windows.Media.Imaging.TransformedBitmap(bmp,
                new System.Windows.Media.ScaleTransform(plan.BaseScale, plan.BaseScale));
            sc.Freeze();
            image = sc;
            return true;
        }
        image = bmp;
        return true;
    }

    /// <summary>Paint one part at an integer offset, straight-alpha-over onto the buffer. For a part that
    /// does not rotate or scale (the crop path) this is a plain rectangle blit with clipping. Fully
    /// transparent source pixels are skipped, which is what makes the padded frames free.</summary>
    private static bool Blit(byte[] dst, int W, int H, byte[] src, int pw, int ph, int ox, int oy)
    {
        int x0 = Math.Max(0, ox), x1 = Math.Min(W, ox + pw);
        int y0 = Math.Max(0, oy), y1 = Math.Min(H, oy + ph);
        bool touched = false;
        for (int y = y0; y < y1; y++)
        {
            int sy = y - oy;
            for (int x = x0; x < x1; x++)
            {
                int si = (sy * pw + (x - ox)) * 4;
                int sa = src[si + 3];
                if (sa == 0) continue;
                Composite(dst, (y * W + x) * 4, src, si, sa);
                touched = true;
            }
        }
        return touched;
    }

    /// <summary>Paint one part through its affine <c>m</c>: for each canvas pixel in the transformed
    /// INK bounding box, inverse-map to the source bitmap (nearest sample) and alpha-over. This is
    /// what places a rotated or <c>scaleY = -1</c> mirrored limb, the Final Bite rig case the integer
    /// paste cannot separate. The bitmap's frame centre is the local origin, so a source pixel
    /// <c>(u, v)</c> lands at <c>origin + m·((u - pw/2)/sfr, (v - ph/2)/sfr)</c>.
    ///
    /// <para><c>sfr</c> is the part's raw-units-per-atlas-pixel factor and is the engine's own: its blit
    /// composes <c>FINAL</c> with <c>scale(1/scalefromraw)</c>, so a rig's joint and link numbers are written
    /// in RAW art pixels while the decoded bitmap is in atlas pixels. Dividing by <c>sfr</c> here (and
    /// measuring the frame extents in raw units) is what lets one leg cell be hung six times at six authored
    /// angles and land as six connected legs. A crop-placed part passes 1, which reduces to the historical
    /// centre-relative paste.</para>
    ///
    /// <para><c>inkCx</c>/<c>inkCy</c>/<c>inkHw</c>/<c>inkHh</c> bound the loop with the cell's VISIBLE
    /// rectangle rather than its padded frame. That is not a nicety: once the frame is expanded to raw size a
    /// Final Bite cell's padding is ~3000x3000, so walking the frame would map nine million empty canvas pixels
    /// per part. The rectangle is Core's, computed from the crop offset, the ink size and <c>sfr</c> by
    /// <see cref="TbPart"/> — it is the same box the canvas was sized around, and a box derived from anything
    /// else paints into the wrong corner of it and clips the creature to a fragment.</para>
    /// <param name="sfr">The part's raw-units-per-atlas-pixel factor.</param>
    /// <param name="inkCx">Ink rectangle centre, x, in the part's units about the frame centre.</param>
    /// <param name="inkCy">Ink rectangle centre, y, in the part's units about the frame centre.</param>
    /// <param name="inkHw">Ink half-width in the part's units.</param>
    /// <param name="inkHh">Ink half-height in the part's units.</param>
    /// <returns>True when at least one destination pixel was touched.</returns></summary>
    private static bool AffineBlit(byte[] dst, int W, int H, byte[] src, int pw, int ph, TbMat m,
        double originX, double originY, double sfr, double inkCx, double inkCy, double inkHw, double inkHh)
    {
        double det = m.A * m.E - m.B * m.D;
        if (Math.Abs(det) < 1e-9) return false;              // degenerate (zero-area) shape: skip
        double idet = 1.0 / det;
        if (sfr <= 0) sfr = 1.0;
        if (inkHw <= 0) inkHw = pw / 2.0 / sfr;              // no ink recorded: fall back to the frame
        if (inkHh <= 0) inkHh = ph / 2.0 / sfr;

        // Ink rectangle in the part's units about the frame centre, then its transformed bounding box in
        // canvas space.
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        double[] cu = { inkCx - inkHw, inkCx + inkHw, inkCx - inkHw, inkCx + inkHw };
        double[] cv = { inkCy - inkHh, inkCy - inkHh, inkCy + inkHh, inkCy + inkHh };
        for (int i = 0; i < 4; i++)
        {
            var (rx, ry) = m.Apply(cu[i], cv[i]);
            double dx = originX + rx, dy = originY + ry;
            if (dx < bx0) bx0 = dx; if (dx > bx1) bx1 = dx;
            if (dy < by0) by0 = dy; if (dy > by1) by1 = dy;
        }
        int x0 = Math.Max(0, (int)Math.Floor(bx0)), x1 = Math.Min(W, (int)Math.Ceiling(bx1));
        int y0 = Math.Max(0, (int)Math.Floor(by0)), y1 = Math.Min(H, (int)Math.Ceiling(by1));

        bool touched = false;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                // canvas -> rig (subtract origin), then invert the affine to RAW centre-relative units,
                // then scale back to atlas pixels and shift by the half frame to a bitmap pixel.
                double rx = x - originX, ry = y - originY;
                double pxr = rx - m.Tx, pyr = ry - m.Ty;
                int su = (int)Math.Floor(((m.E * pxr - m.B * pyr) * idet) * sfr + pw / 2.0);
                int sv = (int)Math.Floor(((-m.D * pxr + m.A * pyr) * idet) * sfr + ph / 2.0);
                if (su < 0 || sv < 0 || su >= pw || sv >= ph) continue;
                int si = (sv * pw + su) * 4;
                int sa = src[si + 3];
                if (sa == 0) continue;
                Composite(dst, (y * W + x) * 4, src, si, sa);
                touched = true;
            }
        return touched;
    }

    /// <summary>Straight-alpha-over one source texel onto one destination texel (BGRA).</summary>
    private static void Composite(byte[] dst, int di, byte[] src, int si, int sa)
    {
        int da = dst[di + 3];
        int oa = sa + da * (255 - sa) / 255;
        int inv = 255 - sa;
        dst[di] = (byte)((src[si] * sa + dst[di] * da * inv / 255) / oa);
        dst[di + 1] = (byte)((src[si + 1] * sa + dst[di + 1] * da * inv / 255) / oa);
        dst[di + 2] = (byte)((src[si + 2] * sa + dst[di + 2] * da * inv / 255) / oa);
        dst[di + 3] = (byte)oa;
    }

    /// <summary>Cache key for a built sprite. An absent or white colour is the identity multiply,
    /// so it shares one entry with the untinted request (mirrors <c>tbassets.build_sprite</c>,
    /// which skips the multiply when the tint is white).</summary>
    private static string CacheKey(string name, int frame, byte[]? tint)
    {
        if (tint is not { Length: >= 3 } || (tint[0] == 255 && tint[1] == 255 && tint[2] == 255))
            return $"{name}|{frame}";
        return $"{name}|{frame}|{tint[0]},{tint[1]},{tint[2]}";
    }

    /// <summary>Mirror of <c>ImageChops.multiply(sprite, (r,g,b,255))</c> followed by
    /// <c>putalpha(original)</c>: each colour channel scales by the record's, the alpha (and so
    /// the sprite's own shape and the mask's feather) is untouched.</summary>
    private static BitmapSource Tint(BitmapSource src, byte[]? tint)
    {
        if (tint is not { Length: >= 3 } || (tint[0] == 255 && tint[1] == 255 && tint[2] == 255))
            return src;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        // Bgra32, straight (non-premultiplied) alpha, so a plain per-channel multiply is exact.
        (int B, int G, int R) = (tint[2], tint[1], tint[0]);
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(px[i] * B / 255);
            px[i + 1] = (byte)(px[i + 1] * G / 255);
            px[i + 2] = (byte)(px[i + 2] * R / 255);
        }
        var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        outBmp.Freeze();
        return outBmp;
    }

    /// <summary>Build the sprite for an already-resolved cell (used by the browser).</summary>
    public bool TryGet(ResolvedSprite s, out BitmapSource image) => TryBuild(s, out image);

    // ---- Asset-browser surface (S5-4). Read-only: enumerate the install's cells/anims and
    // build their thumbnails through the same decode/crop path the viewport draws with. ----
    public IReadOnlyCollection<string> CellNames => _res.CellNames;
    public IReadOnlyCollection<string> AnimNames => _res.AnimNames;
    public int CellCount => _res.CellCount;
    public int AnimCount => _res.AnimCount;
    public bool HasAnimations => _res.AnimCount > 0;

    /// <summary>Thumbnail for an exact cell name (no species-key mangling). Returns false
    /// when the cell or its atlas is unavailable, so the browser shows a placeholder.</summary>
    public bool TryGetCellThumb(string cellName, out BitmapSource image)
    {
        image = null!;
        return _res.TryResolveCellName(cellName, out var s) && TryBuild(s, out image);
    }

    private bool TryBuild(ResolvedSprite s, out BitmapSource image)
    {
        image = null!;
        string key = $"{s.Atlas}|{s.X1},{s.Y1},{s.X2},{s.Y2}|{s.OrigW}x{s.OrigH}@{s.CropX},{s.CropY}";
        if (_cell.TryGetValue(key, out var cached)) { image = cached!; return image is not null; }

        var atlas = GetAtlas(s.Atlas);
        BitmapSource? built = atlas is null ? null : Crop(atlas, s);
        _cell[key] = built;
        image = built!;
        return image is not null;
    }

    /// <summary>Test/diagnostic hook: decode an atlas and bucket its pixels by alpha.
    /// A jpg+mask pair (or an RGBA png) must yield both transparent and opaque regions;
    /// this is how the headless <c>assets</c> check proves the alpha composite really ran.</summary>
    public (int W, int H, int Opaque, int Clear, int Partial)? ProbeAtlas(string atlasName)
    {
        var a = GetAtlas(atlasName);
        if (a is null) return null;
        int op = 0, cl = 0, pa = 0;
        for (int p = 3; p < a.Px.Length; p += 4)
        {
            byte al = a.Px[p];
            if (al == 0) cl++; else if (al == 255) op++; else pa++;
        }
        return (a.W, a.H, op, cl, pa);
    }

    private AtlasPixels? GetAtlas(string atlasName)
    {
        if (_atlas.TryGetValue(atlasName, out var cached)) return cached;
        AtlasPixels? loaded = Load(atlasName);
        _atlas[atlasName] = loaded;
        return loaded;
    }

    private AtlasPixels? Load(string atlasName)
    {
        try
        {
            string jpg = Path.Combine(_graphicsDir, atlasName + ".jpg");
            string mask = Path.Combine(_graphicsDir, atlasName + "mask.png");
            string png = Path.Combine(_graphicsDir, atlasName + ".png");

            if (File.Exists(jpg))
            {
                var rgb = Decode(jpg, PixelFormats.Bgra32);
                int w = rgb.PixelWidth, h = rgb.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                rgb.CopyPixels(px, stride, 0);
                if (File.Exists(mask))
                {
                    var m = Decode(mask, PixelFormats.Gray8);
                    if (m.PixelWidth == w && m.PixelHeight == h)
                    {
                        int mstride = m.PixelWidth;                 // Gray8 = 1 byte/px
                        var mp = new byte[mstride * m.PixelHeight];
                        m.CopyPixels(mp, mstride, 0);
                        for (int i = 0, p = 3; i < w * h; i++, p += 4) px[p] = mp[i];
                    }
                    else
                    {
                        for (int p = 3; p < px.Length; p += 4) px[p] = 0xFF;
                    }
                }
                else
                {
                    for (int p = 3; p < px.Length; p += 4) px[p] = 0xFF;
                }
                return new AtlasPixels(px, w, h);
            }
            if (File.Exists(png))
            {
                var bgra = Decode(png, PixelFormats.Bgra32);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);
                return new AtlasPixels(px, w, h);
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    private static BitmapSource Decode(string path, PixelFormat fmt)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = dec.Frames[0];
        var conv = new FormatConvertedBitmap(frame, fmt, null, 0);
        conv.Freeze();
        return conv;
    }

    private static BitmapSource? Crop(AtlasPixels a, ResolvedSprite s)
    {
        int cw = s.X2 - s.X1, ch = s.Y2 - s.Y1;
        if (cw <= 0 || ch <= 0) return null;
        int w = s.OrigW > 0 ? s.OrigW : cw;
        int h = s.OrigH > 0 ? s.OrigH : ch;
        if (w <= 0 || h <= 0) return null;

        var dst = new byte[w * h * 4];               // transparent canvas
        int astride = a.W * 4;
        for (int y = 0; y < ch; y++)
        {
            int sy = s.Y1 + y; if ((uint)sy >= (uint)a.H) continue;
            int dy = s.CropY + y; if ((uint)dy >= (uint)h) continue;
            for (int x = 0; x < cw; x++)
            {
                int sx = s.X1 + x; if ((uint)sx >= (uint)a.W) continue;
                int dx = s.CropX + x; if ((uint)dx >= (uint)w) continue;
                int si = sy * astride + sx * 4, di = (dy * w + dx) * 4;
                dst[di] = a.Px[si]; dst[di + 1] = a.Px[si + 1];
                dst[di + 2] = a.Px[si + 2]; dst[di + 3] = a.Px[si + 3];
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
