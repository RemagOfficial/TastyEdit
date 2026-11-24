using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TastyEditor.Core.Assets;

namespace TastyEditor.App.Assets;

/// <summary>The WPF half of the TP2 ("Back for Seconds") asset pipeline. Executes a
/// <see cref="Tp2SpritePlan"/> against decoded atlases: crop each cell back onto its
/// original transparent canvas, multiply a layer by its per-part tint (egypt house tarps),
/// alpha-composite the layers bottom-first on the shared canvas, recolour the whole result by
/// the record's RGBA (planets, grapes, giants, candy) and fold in the frame opacity — the
/// pixel mirror of <c>tp2assets.build_sprite</c> + <c>ImageChops.multiply</c>/<c>alpha_composite</c>.
/// Atlases ship as a <c>.jpg</c> colour plate with a companion <c>&lt;atlas&gt;mask.png</c>
/// alpha (or a single RGBA <c>.png</c>). Results are frozen and cached by (name, frame, tint),
/// so a per-entity recoloured sprite is built once.</summary>
public sealed class Tp2SpriteSource : ISpriteSource
{
    private static readonly byte[] White = { 255, 255, 255, 255 };

    private readonly Tp2SpriteResolver _res;
    private readonly string _graphicsDir;

    private readonly Dictionary<string, AtlasPx?> _atlas = new();
    private readonly Dictionary<string, Built?> _built = new();
    private readonly Dictionary<string, Built?> _builtDeco = new();

    private sealed record AtlasPx(byte[] Px, int W, int H);
    private sealed record Built(BitmapSource Image, int W, int H, double PivotX, double PivotY);

    public Tp2SpriteSource(Tp2SpriteResolver res)
    {
        _res = res;
        _graphicsDir = res.Root.GraphicsDir;
    }

    public bool TryGet(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => Get(rawName, frame, tint, hard: false, out image, out w, out h, out pivotX, out pivotY, out opacity);

    // Deco lookup. Kept as a separate entry point (and cache) from entities so a deco-specific
    // render treatment can be added without touching entity sprites. Currently identical to
    // TryGet: the shipped game composites transparent tile pixels over nodraw (proven: mask ->
    // texture alpha in FUN_00422b70), so a deco's feather must stay transparent. Seamless dirt
    // comes from the tiles' opaque geometry overlapping, which is a projector/viewport concern,
    // not an alpha one.
    public bool TryGetDeco(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => Get(rawName, frame, tint, hard: false, out image, out w, out h, out pivotX, out pivotY, out opacity);

    private bool Get(string rawName, int frame, byte[]? tint, bool hard, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
    {
        image = null!; w = h = 0; pivotX = pivotY = 0.5; opacity = 1.0;
        var cache = hard ? _builtDeco : _built;
        string key = CacheKey(rawName, frame, tint);
        if (cache.TryGetValue(key, out var fast))
        {
            if (fast is null) return false;
            image = fast.Image; w = fast.W; h = fast.H; pivotX = fast.PivotX; pivotY = fast.PivotY;
            return true;
        }
        var plan = _res.BuildPlan(rawName, tint ?? White, frame);
        Built? b = plan is null ? null : Build(plan, hard);
        cache[key] = b;
        if (b is null) return false;
        image = b.Image; w = b.W; h = b.H; pivotX = b.PivotX; pivotY = b.PivotY;
        return true;
    }

    private static string CacheKey(string name, int frame, byte[]? tint)
    {
        if (tint is not { Length: >= 3 }) return $"{name}|{frame}";
        byte a = tint.Length >= 4 ? tint[3] : (byte)255;
        return $"{name}|{frame}|{tint[0]},{tint[1]},{tint[2]},{a}";
    }

    // ---- asset-browser surface (delegates to the same cascade the viewport draws with) ----
    public IReadOnlyCollection<string> CellNames => _res.CellNames;
    public IReadOnlyCollection<string> AnimNames => _res.AnimNames;
    public int CellCount => _res.CellCount;
    public int AnimCount => _res.AnimCount;
    public bool HasAnimations => _res.HasAnimations;

    public bool TryGetCellThumb(string cellName, out BitmapSource image)
    {
        image = null!;
        if (!_res.TryCellPlan(cellName, out var plan)) return false;
        var b = Build(plan);
        if (b is null) return false;
        image = b.Image;
        return true;
    }

    // ---- plan execution ----
    private Built? Build(Tp2SpritePlan plan, bool hard = false)
    {
        byte[]? acc = null; int W = 0, H = 0;
        foreach (var op in plan.Ops)
        {
            if (op.Kind == Tp2OpKind.Tint)
            {
                if (acc is not null && op.Tint is { } g) MultiplyRgb(acc, g.R, g.G, g.B);
                continue;
            }
            var layer = CropCell(op.Cell);
            if (layer is null) continue;
            var (px, w, h) = layer.Value;
            if (op.Tint is { } lt) MultiplyRgb(px, lt.Item1, lt.Item2, lt.Item3);
            if (acc is null) { acc = px; W = w; H = h; }
            else if (w == W && h == H) AlphaOver(acc, px, W, H);   // shared canvas: skip size mismatch (mirrors Python)
        }
        if (acc is null) return null;
        if (plan.AlphaScale is >= 0 and < 255) ScaleAlpha(acc, plan.AlphaScale);
        // No alpha editing here. The shipped game composites a transparent tile pixel over
        // whatever is behind it (nodraw), so a deco's feather MUST stay transparent; seamless
        // background coverage comes from the tiles' opaque geometry overlapping, not from
        // touching alpha (the engine's own mask-to-texture composite is what proved this).

        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, acc, W * 4);
        bmp.Freeze();
        return new Built(bmp, W, H, plan.Pivot.X, plan.Pivot.Y);
    }

    // Crop a resolved cell out of its atlas and paste it back onto its original-size
    // transparent canvas (tp2assets._cell_rgba). Returns BGRA bytes, or null if the atlas
    // cannot be decoded.
    private (byte[] Px, int W, int H)? CropCell(ResolvedSprite s)
    {
        var a = GetAtlas(s.Atlas);
        if (a is null) return null;
        int cw = s.X2 - s.X1, ch = s.Y2 - s.Y1;
        if (cw <= 0 || ch <= 0) return null;
        int w = s.OrigW > 0 ? s.OrigW : cw;
        int h = s.OrigH > 0 ? s.OrigH : ch;
        if (w <= 0 || h <= 0) return null;

        var dst = new byte[w * h * 4];
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
        return (dst, w, h);
    }

    // BGRA layout: [0]=B, [1]=G, [2]=R, [3]=A. ImageChops.multiply per channel //255, alpha kept.
    private static void MultiplyRgb(byte[] px, byte r, byte g, byte b)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(px[i] * b / 255);
            px[i + 1] = (byte)(px[i + 1] * g / 255);
            px[i + 2] = (byte)(px[i + 2] * r / 255);
        }
    }

    // Straight-alpha "over" (src onto dst in place), the model PIL alpha_composite uses.
    private static void AlphaOver(byte[] dst, byte[] src, int w, int h)
    {
        int n = w * h * 4;
        for (int i = 0; i < n; i += 4)
        {
            int sa = src[i + 3], da = dst[i + 3];
            int oa = sa + da * (255 - sa) / 255;
            if (oa == 0) { dst[i] = dst[i + 1] = dst[i + 2] = dst[i + 3] = 0; continue; }
            int inv = 255 - sa;
            for (int k = 0; k < 3; k++)
                dst[i + k] = (byte)((src[i + k] * sa + dst[i + k] * da * inv / 255) / oa);
            dst[i + 3] = (byte)oa;
        }
    }

    private static void ScaleAlpha(byte[] px, int eff)
    {
        for (int i = 3; i < px.Length; i += 4) px[i] = (byte)(px[i] * eff / 255);
    }

    // ---- atlas decode (jpg + companion mask, or an RGBA png), cached per atlas ----
    private AtlasPx? GetAtlas(string atlasName)
    {
        if (_atlas.TryGetValue(atlasName, out var cached)) return cached;
        AtlasPx? loaded = Load(atlasName);
        _atlas[atlasName] = loaded;
        return loaded;
    }

    private AtlasPx? Load(string atlasName)
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
                        int mstride = m.PixelWidth;
                        var mp = new byte[mstride * m.PixelHeight];
                        m.CopyPixels(mp, mstride, 0);
                        for (int i = 0, p = 3; i < w * h; i++, p += 4) px[p] = mp[i];
                    }
                    else for (int p = 3; p < px.Length; p += 4) px[p] = 0xFF;
                }
                else for (int p = 3; p < px.Length; p += 4) px[p] = 0xFF;
                return new AtlasPx(px, w, h);
            }
            if (File.Exists(png))
            {
                var bgra = Decode(png, PixelFormats.Bgra32);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);
                return new AtlasPx(px, w, h);
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    private static BitmapSource Decode(string path, PixelFormat fmt)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(dec.Frames[0], fmt, null, 0);
        conv.Freeze();
        return conv;
    }
}
