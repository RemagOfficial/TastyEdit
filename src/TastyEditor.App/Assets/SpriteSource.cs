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

    public SpriteSource(SpriteResolver res)
    {
        _res = res;
        _graphicsDir = res.Root.GraphicsDir;
    }

    private sealed record AtlasPixels(byte[] Px, int W, int H);
    private sealed record NamedSprite(BitmapSource Image, int W, int H, double PivotX, double PivotY, double Opacity);

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
        if (!_res.TryResolve(rawName, frame, out var s) || !TryBuild(s, out var baseImg))
        {
            if (frame == 0) _byName[key] = null;
            return false;
        }
        var built = Tint(baseImg, tint);
        image = built;
        w = built.PixelWidth; h = built.PixelHeight;
        pivotX = s.PivotX; pivotY = s.PivotY; opacity = s.Opacity;
        if (frame == 0) _byName[key] = new NamedSprite(built, w, h, pivotX, pivotY, opacity);
        return true;
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
