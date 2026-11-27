using System.IO;

namespace TastyEditor.Core.Levels;

/// <summary>
/// Where the installed games' level folders are, without hardcoding one machine's drive layout.
/// The headless verification gates and the golden harness read stock levels to check themselves
/// against; this resolves those paths the way a player would expect them to be found.
/// </summary>
public static class GameInstall
{
    // Game key, the folder Steam creates under steamapps\common, and the level path inside it.
    // Back for Seconds keeps its levels directly under assets; the later games wrap them in
    // editabledirect.
    private static readonly (string Key, string Folder, string Rel)[] Known =
    {
        ("tp2", @"Tasty Planet Back for Seconds", @"assets\levels"),
        ("tb", @"Tasty Blue", @"assets\editabledirect\levels"),
        ("f4", @"Tasty Planet Forever", @"assets\editabledirect\levels"),
        ("f5", @"Tasty Planet Final Bite", @"assets\editabledirect\levels"),
        // The original Tasty Planet keeps its plain-XML levels directly under assets.
        ("tp1", @"Tasty Planet", @"assets\levels"),
    };

    /// <summary>
    /// The levels directory for a game key, or null when no install of it can be found. Order:
    /// <c>TPE_LEVELS_&lt;KEY&gt;</c> (an explicit path, which also covers a non-Steam copy), then
    /// every library Steam reports in <c>libraryfolders.vdf</c>, then the default Program Files
    /// library.
    /// </summary>
    public static string? LevelsDir(string gameKey)
    {
        var known = Array.Find(Known, k => string.Equals(k.Key, gameKey, StringComparison.OrdinalIgnoreCase));
        if (known.Key is null) return null;

        var env = Environment.GetEnvironmentVariable("TPE_LEVELS_" + gameKey.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        foreach (var root in LibraryRoots())
        {
            var dir = Path.Combine(root, "steamapps", "common", known.Folder, known.Rel);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>One actionable line for a gate that found nothing, so an empty run explains
    /// itself instead of looking like a format failure.</summary>
    public static string MissingHint(string gameKey) =>
        $"no {gameKey} level folder found - install the game, or set " +
        $"TPE_LEVELS_{gameKey.ToUpperInvariant()} to its levels directory";

    /// <summary>The games the editor knows about, as (key, display name) â€” the display name is the
    /// folder Steam installs them into. Drives the first-run setup listing.</summary>
    public static (string Key, string Name)[] KnownGames() =>
        System.Array.ConvertAll(Known, k => (k.Key, k.Folder));

    /// <summary>
    /// The install folder for a game key (the game's own directory, the parent of its level path), or
    /// null when no install is found. Detection is by folder presence only â€” it never launches or
    /// touches a game. Falls back to the levels path's parent when <c>TPE_LEVELS_&lt;KEY&gt;</c> names a
    /// non-Steam copy, so the setup dialog can still point at it.
    /// </summary>
    public static string? InstallDir(string gameKey)
    {
        var known = Array.Find(Known, k => string.Equals(k.Key, gameKey, StringComparison.OrdinalIgnoreCase));
        if (known.Key is null) return null;

        var env = Environment.GetEnvironmentVariable("TPE_LEVELS_" + gameKey.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetDirectoryName(env.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var root in LibraryRoots())
        {
            var dir = Path.Combine(root, "steamapps", "common", known.Folder);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>Steam library roots: the ones the Steam client records, plus its default install.</summary>
    private static IEnumerable<string> LibraryRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFiles.Length == 0) yield break;
        var standard = Path.Combine(programFiles, "Steam");

        foreach (var vdf in new[]
                 {
                     Path.Combine(standard, "config", "libraryfolders.vdf"),   // Steam, 2021 onwards
                     Path.Combine(standard, "SteamAppData.vdf"),               // older client
                 })
            foreach (var path in PathsInVdf(vdf))
                if (Directory.Exists(path) && seen.Add(path)) yield return path;

        if (Directory.Exists(standard) && seen.Add(standard)) yield return standard;
    }

    /// <summary>Every <c>"path" "..."</c> value in a Steam .vdf file. A regex is enough here: the
    /// library list is a flat set of those keys, and an unreadable file yields no roots instead of
    /// throwing at a gate.</summary>
    private static IEnumerable<string> PathsInVdf(string vdfPath)
    {
        if (!File.Exists(vdfPath)) yield break;
        string text;
        try { text = File.ReadAllText(vdfPath); }
        catch { yield break; }

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            // VDF values may carry escaped backslashes or forward slashes.
            yield return m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
        }
    }
}
