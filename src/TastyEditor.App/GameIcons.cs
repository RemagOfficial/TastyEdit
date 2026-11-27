using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TastyEditor.App;

/// <summary>R7: the five family tiles that let the window itself say which game is open.
///
/// <para><see cref="Icons"/> draws the toolbar's line glyphs; this is the other half of the icon
/// story — the real <c>.ico</c> files that <c>tools/make_icon.py --games</c> generates next to the
/// executable. Explorer cannot vary a shared <c>.bin</c> association by file content (one extension,
/// one ProgId, one icon), so the per-game identity lives where the editor can still show it: the
/// window's title-bar and taskbar button while a level is open, and the installer's per-game
/// Start-Menu shortcuts.</para>
///
/// <para>Loading one is deliberately cosmetic: a missing tile — a partial install, someone running
/// the exe out of a build folder that never copied <c>icons\</c> — must fall back to the application
/// icon rather than throw on the way into a level. The decoded bitmap is the 32 px entry, which is
/// the size the taskbar and Alt-Tab ask for; the container carries 16 through 256 for everything
/// else.</para></summary>
internal static class GameIcons
{
    private const int DecodePx = 32;

    // Remembered per game, including the failures: a family with no tile is asked for on every
    // open, and re-trying the disk each time buys nothing.
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>The tile for one game family (the <c>GameProfile.Key</c> the open level reports), or
    /// null when there is nothing to show — no level open, or no tile installed for that family.</summary>
    public static ImageSource? For(string? gameKey)
    {
        if (string.IsNullOrEmpty(gameKey)) return null;
        if (Cache.TryGetValue(gameKey, out var known)) return known;

        ImageSource? found = null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icons", gameKey + ".ico");
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // read once; the file is not held open
                bmp.DecodePixelWidth = DecodePx;
                bmp.EndInit();
                bmp.Freeze();
                found = bmp;
            }
        }
        catch { found = null; }

        Cache[gameKey] = found;
        return found;
    }
}
