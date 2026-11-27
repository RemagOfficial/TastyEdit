using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TastyEditor.Core.Assets;

namespace TastyEditor.App.Assets;

/// <summary>The WPF half of the Tasty Planet 1 asset pipeline. Executes a <see cref="Tp1Cell"/>
/// resolved by the UI-free <see cref="Tp1SpriteResolver"/> against a decoded PNG: crop the cell's
/// rectangle out of its atlas and paste it back onto its original transparent canvas at the crop
/// offset, then multiply the RGB by the record's colour and fold in its alpha — the pixel mirror of
/// the base editor's <c>asset_manager.get_*_image</c> + <c>get_tk_image</c>. Most TP1 atlases are
/// plain RGBA PNGs, but a transparent object ships as a flat <c>&lt;atlas&gt;.jpg</c> plus a grayscale
/// <c>&lt;atlas&gt;mask.png</c> (the same jpg+mask pair the Tasty Blue / TP2 atlases use); when that
/// companion exists its luminance becomes the sprite's alpha, so the object keeps its silhouette
/// instead of painting an opaque rectangle. Results
/// are frozen and cached by (name, frame, tint); a miss falls back to a cross marker.</summary>
public sealed class Tp1SpriteSource : ISpriteSource
{
    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

    private readonly Tp1SpriteResolver _res;
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AtlasPx?> _atlas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Built?> _built = new();
    // rawName -> whether its art is a bright achromatic silhouette (an entitydef effect colour is meant
    // to recolour white/gray art, not dark or already-coloured art). Cached so a sprite measures once.
    private readonly Dictionary<string, bool> _sil = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AtlasPx(byte[] Px, int W, int H);
    private sealed record Built(BitmapSource Image, int W, int H, double PivotX, double PivotY);

    /// <summary>The UI-free resolver behind this source. The shell reads its entity / tile name
    /// lists to feed the inspector's validated dropdowns and to police an emitter's object type.</summary>
    public Tp1SpriteResolver Resolver => _res;

    public Tp1SpriteSource(Tp1SpriteResolver res)
    {
        _res = res;
        IndexFiles(res.Root.GraphicsDir);
        foreach (var parent in new[] { Path.GetDirectoryName(res.Root.GraphicsDir) ?? "" })
            if (Directory.Exists(parent)) IndexFiles(parent);
    }

    // Recursively map every image file by name and name-without-extension (case-insensitive),
    // the same candidate set asset_manager.scan_dir registers, so an imagemap "filename" resolves
    // whether it is written "goo1", "goo1.png" or nested under newgraphics/oldgraphics/EA.
    private void IndexFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f);
            if (Array.IndexOf(Exts, ext.ToLowerInvariant()) < 0) continue;
            string noExt = Path.GetFileNameWithoutExtension(f);
            _files.TryAdd(noExt, f);
            _files.TryAdd(Path.GetFileName(f), f);
        }
    }

    public bool TryGet(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => Get(rawName, tint, out image, out w, out h, out pivotX, out pivotY, out opacity);

    public bool TryGetDeco(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => Get(rawName, tint, out image, out w, out h, out pivotX, out pivotY, out opacity);

    private bool Get(string rawName, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
    {
        image = null!; w = h = 0; pivotX = pivotY = 0.5; opacity = 1.0;
        string key = CacheKey(rawName, tint);
        if (_built.TryGetValue(key, out var fast))
        {
            if (fast is null) return false;
            image = fast.Image; w = fast.W; h = fast.H; pivotX = fast.PivotX; pivotY = fast.PivotY;
            return true;
        }
        var cell = _res.ResolveEntity(rawName) ?? _res.ResolveTile(rawName);
        Built? b = cell is null ? null : Build(cell, tint);
        _built[key] = b;
        if (b is null) return false;
        image = b.Image; w = b.W; h = b.H; pivotX = b.PivotX; pivotY = b.PivotY;
        return true;
    }

    private static string CacheKey(string name, byte[]? tint)
    {
        if (tint is not { Length: >= 3 }) return name;
        byte a = tint.Length >= 4 ? tint[3] : (byte)255;
        return $"{name}|{tint[0]},{tint[1]},{tint[2]},{a}";
    }

    // ---- asset-browser surface ----
    public IReadOnlyCollection<string> CellNames => _res.CellNames;
    public IReadOnlyCollection<string> AnimNames => _res.AnimNames;
    public int CellCount => _res.CellCount;
    public int AnimCount => _res.AnimCount;
    public bool HasAnimations => _res.HasAnimations;

    public bool TryGetCellThumb(string cellName, out BitmapSource image)
    {
        image = null!;
        var cell = _res.ResolveEntity(cellName) ?? _res.ResolveTile(cellName);
        if (cell is null) return false;
        var b = Build(cell, null);
        if (b is null) return false;
        image = b.Image;
        return true;
    }

    // The goo face parts are named cells on a shared sheet (newgoo0), not addressable by an
    // entity/tile name; build one directly and cache it under a '#' key that can't collide with a
    // normal sprite cache key. The goo's entitydef effect tint (greygoo 0.5,0.5,0.5) is passed in so
    // every part — bump, eyes, pupils, eyelid — is tinted consistently with the body, not just the
    // raw cell art. Results are frozen (Build already freezes), so reuse is safe.
    public bool TryGetCell(string mapName, string cellName, byte[]? tint, out BitmapSource image, out int w, out int h)
    {
        image = null!; w = h = 0;
        var cell = _res.ResolveCell(mapName, cellName);
        if (cell is null) return false;
        string key = $"#{mapName}/{cellName}" + TintSuffix(tint);
        if (_built.TryGetValue(key, out var cached))
        {
            if (cached is null) return false;
            image = cached.Image; w = cached.W; h = cached.H; return true;
        }
        var b = Build(cell, tint);
        _built[key] = b;
        if (b is null) return false;
        image = b.Image; w = b.W; h = b.H;
        return true;
    }

    // The eyelid blink is a weighted animation on newgoo0; resolve the frame current at `elapsed`
    // through the resolver (same weight walk as the base editor), then build that cell. Cached by the
    // cell's own rectangle plus the tint so repeated ticks reuse the frozen bitmap.
    public bool TryGetAnimationFrame(string animName, double elapsed, byte[]? tint, out BitmapSource image, out int w, out int h)
    {
        image = null!; w = h = 0;
        var cell = _res.GetAnimationFrameCell(animName, elapsed);
        if (cell is null) return false;
        string key = $"#{cell.Atlas}:{cell.X1},{cell.Y1},{cell.X2},{cell.Y2}" + TintSuffix(tint);
        if (_built.TryGetValue(key, out var cached))
        {
            if (cached is null) return false;
            image = cached.Image; w = cached.W; h = cached.H; return true;
        }
        var b = Build(cell, tint);
        _built[key] = b;
        if (b is null) return false;
        image = b.Image; w = b.W; h = b.H;
        return true;
    }

    // Cache-key discriminator for a tinted cell build so a gray and a white variant of the same cell
    // never collide (an all-255 / null tint yields the empty suffix, matching the untinted key).
    private static string TintSuffix(byte[]? tint)
        => tint is { Length: >= 3 }
            ? $"|{tint[0]},{tint[1]},{tint[2]},{(tint.Length >= 4 ? tint[3] : (byte)255)}"
            : "";

    // ---- pixel build: crop the cell onto its original canvas, tint, fade ----
    private Built? Build(Tp1Cell c, byte[]? tint)
    {
        var dst = CropPixels(c, out int w, out int h);
        if (dst is null) return null;

        // tint is [r,g,b,a] in 0..255; multiply the crop's RGB and scale its alpha (an all-255
        // tint is a no-op, so untinted sprites stay byte-identical to the atlas crop). Whatever tint
        // reaches here is already a decision to paint (the goo body's gray, a tile's record r/g/b, or a
        // gated emitter effect colour from EffectTintApplies) — Build just applies it.
        byte tr = tint is { Length: >= 3 } ? tint[0] : (byte)255;
        byte tg = tint is { Length: >= 3 } ? tint[1] : (byte)255;
        byte tb = tint is { Length: >= 3 } ? tint[2] : (byte)255;
        byte ta = tint is { Length: >= 4 } ? tint[3] : (byte)255;
        if (tr != 255 || tg != 255 || tb != 255)
            for (int i = 0; i < dst.Length; i += 4)
            {
                dst[i] = (byte)(dst[i] * tb / 255);
                dst[i + 1] = (byte)(dst[i + 1] * tg / 255);
                dst[i + 2] = (byte)(dst[i + 2] * tr / 255);
            }
        if (ta != 255)
            for (int i = 3; i < dst.Length; i += 4) dst[i] = (byte)(dst[i] * ta / 255);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
        bmp.Freeze();
        return new Built(bmp, w, h, 0.5, 0.5);
    }

    // Crop a cell's rectangle back onto its original transparent canvas (Bgra32, w*h*4). Null when the
    // atlas is absent or the rect is degenerate. Shared by Build and the silhouette measurement.
    private byte[]? CropPixels(Tp1Cell c, out int w, out int h)
    {
        w = h = 0;
        var a = GetAtlas(c.Atlas);
        if (a is null) return null;
        int cw = c.X2 - c.X1, ch = c.Y2 - c.Y1;
        if (cw <= 0 || ch <= 0) return null;
        w = c.OrigW > 0 ? c.OrigW : cw;
        h = c.OrigH > 0 ? c.OrigH : ch;
        if (w <= 0 || h <= 0) return null;

        var dst = new byte[w * h * 4];
        int astride = a.W * 4;
        for (int y = 0; y < ch; y++)
        {
            int sy = c.Y1 + y; if ((uint)sy >= (uint)a.H) continue;
            int dy = c.CropY + y; if ((uint)dy >= (uint)h) continue;
            for (int x = 0; x < cw; x++)
            {
                int sx = c.X1 + x; if ((uint)sx >= (uint)a.W) continue;
                int dx = c.CropX + x; if ((uint)dx >= (uint)w) continue;
                int si = sy * astride + sx * 4, di = (dy * w + dx) * 4;
                dst[di] = a.Px[si]; dst[di + 1] = a.Px[si + 1];
                dst[di + 2] = a.Px[si + 2]; dst[di + 3] = a.Px[si + 3];
            }
        }
        return dst;
    }

    /// <summary>Whether an emitter's entitydef effect colour should paint its sprite. The effect colour
    /// recolors objects that ship as a WHITE/gray silhouette (the goo, the pale blob cells) — so it is
    /// honoured only when the resolved art is bright AND achromatic. Dark art (black dominoes) and
    /// already-coloured art (cars, people) fail this and render their own art instead. Read-only.</summary>
    public bool EffectTintApplies(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return false;
        if (_sil.TryGetValue(rawName, out var seen)) return seen;
        var cell = _res.ResolveEntity(rawName);
        bool ok = cell is not null && IsBrightSilhouette(CropPixels(cell!, out _, out _));
        _sil[rawName] = ok;
        return ok;
    }

    // A sprite is a bright silhouette when it carries almost no hue AND is light: an entitydef effect is a
    // recolour of white art, so a dark (black) sprite must not be multiplied (that would crush it darker),
    // and a coloured sprite already has its own hues. rgba is Bgra32: [i]=B,[i+1]=G,[i+2]=R,[i+3]=A.
    private static bool IsBrightSilhouette(byte[]? rgba)
    {
        if (rgba is null || rgba.Length == 0) return false;
        int opaque = 0, colored = 0; long sum = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 16) continue;   // near-transparent: not part of the visible silhouette
            opaque++;
            byte b = rgba[i], g = rgba[i + 1], r = rgba[i + 2];
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx - mn > 24) colored++;      // >24/255 channel spread = the pixel carries real hue
            sum += (r * 299 + g * 587 + b * 114) / 1000;   // Rec.601 luminance
        }
        // <=5% hue-carrying pixels (achromatic) AND mean luminance >=120 (light, not a black sprite).
        return opaque > 0 && colored * 20 <= opaque && sum >= (long)opaque * 120;
    }

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
            if (!ResolveFile(atlasName, out var path) || path is null || !File.Exists(path)) return null;
            var px = LoadRgba(path, out int w, out int h);
            if (px is null) return null;
            ApplyMask(atlasName, px, w, h);
            return new AtlasPx(px, w, h);
        }
        catch (Exception) { return null; }
    }

    // A map's filename may be written with or without an extension; ResolveFile matches the
    // indexed candidates (nested under newgraphics/oldgraphics/EA alike).
    private bool ResolveFile(string atlasName, out string? path)
    {
        if (_files.TryGetValue(atlasName, out path)) return true;
        foreach (var e in Exts)
            if (_files.TryGetValue(atlasName + e, out path)) return true;
        return path is not null;
    }

    // The transparency companion <atlas>mask.png (grayscale): its luminance replaces the flat
    // jpg's opaque alpha, mirroring tbassets._atlas's rgb.putalpha(mask.convert("L")). Absent for
    // the RGBA-png atlases, which already carry their own alpha and need no mask.
    private void ApplyMask(string atlasName, byte[] px, int w, int h)
    {
        string stem = Path.GetFileNameWithoutExtension(atlasName);
        string? mask = null;
        if (!_files.TryGetValue(stem + "mask", out mask)) _files.TryGetValue(stem + "mask.png", out mask);
        if (mask is null || !File.Exists(mask)) return;
        var mp = LoadRgba(mask, out int mw, out int mh);
        if (mp is null) return;
        for (int y = 0; y < h; y++)
        {
            int sy = mw == w && mh == h ? y : (int)((long)y * mh / h);   // nearest-sample a size mismatch
            if (sy < 0 || sy >= mh) continue;
            for (int x = 0; x < w; x++)
            {
                int sx = mw == w && mh == h ? x : (int)((long)x * mw / w);
                if (sx < 0 || sx >= mw) continue;
                int mi = (sy * mw + sx) * 4, di = (y * w + x) * 4;
                px[di + 3] = (byte)((mp[mi] * 299 + mp[mi + 1] * 587 + mp[mi + 2] * 114) / 1000);
            }
        }
    }

    // Decode any image file to a tight Bgra32 pixel buffer (mask or atlas), or null on failure.
    private static byte[]? LoadRgba(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth; h = conv.PixelHeight; int stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            return px;
        }
        catch (Exception) { return null; }
    }
}
