using System.IO;
using System.Text.Json;

namespace TastyEditor.App.Editing;

/// <summary>Recent-level list (roadmap slice 7 "recent files"), persisted as plain JSON under
/// <c>%LOCALAPPDATA%\TastyEdit\config.json</c> — the same AppData location the roadmap
/// pins for editor config. The list is most-recent-first, de-duplicated by full path, capped,
/// and pruned of missing files on load so the menu never offers a dead entry (they stay out of
/// the list rather than greyed, because pruning-at-load keeps the menu code trivial; a file
/// deleted mid-session will error with a clear message on click instead). Failures to read or
/// write the config are silently tolerated: recents are a convenience, never fatal.</summary>
public sealed class RecentFiles
{
    private const int Max = 10;

    public List<string> Paths { get; private set; } = new();

    // View options are stored here too (S-D14): this class owns config.json wholesale, so a second
    // writer for one more key would silently drop whatever the other one had written.
    private bool _alwaysSprites = true;

    /// <summary>Draw entity/deco sprites even when they land only a couple of pixels on screen
    /// (the alternative being the cross marker plus a faster repaint). Persisted; on by default.</summary>
    public bool AlwaysSprites
    {
        get => _alwaysSprites;
        set { if (_alwaysSprites == value) return; _alwaysSprites = value; Save(); }
    }

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TastyEdit");
    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>Where the config lived before the editor was renamed to TastyEdit. Read once if the
    /// new folder has nothing yet, so a rename does not silently cost the user their recent list
    /// (the next write lands in the new place and the old file is left alone).</summary>
    private static string LegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TastyLevelEditor", "config.json");

    /// <summary>Folder the last opened/saved level lived in (seeds file-dialog InitialDirectory).</summary>
    public string? LastDirectory =>
        Paths.Count > 0 ? Path.GetDirectoryName(Paths[0]) : null;

    public void Load()
    {
        try
        {
            string source = File.Exists(ConfigPath) ? ConfigPath
                          : File.Exists(LegacyConfigPath) ? LegacyConfigPath : "";
            if (source.Length == 0) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(source));
            if (doc.RootElement.TryGetProperty("recent", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var p = el.GetString();
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                        list.Add(p!);
                }
                Paths = list.Take(Max).ToList();
            }
            // Backing field, not the property: reading the config must not rewrite it.
            if (doc.RootElement.TryGetProperty("alwaysSprites", out var av)
                && (av.ValueKind == JsonValueKind.True || av.ValueKind == JsonValueKind.False))
                _alwaysSprites = av.GetBoolean();
        }
        catch { /* corrupt or locked config: start clean, keep the old file for a later look */ }
    }

    /// <summary>Remember a level file (open or save) and persist immediately — a modder's session
    /// may end with a crash we never get a close-event for.</summary>
    public void Record(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Paths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            Paths.Insert(0, full);
            if (Paths.Count > Max) Paths.RemoveRange(Max, Paths.Count - Max);
            Save();
        }
        catch { }
    }

    public void Clear()
    {
        Paths.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // Small object graph; serialise by hand so future config keys merge in easily.
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WritePropertyName("recent");
                w.WriteStartArray();
                foreach (var p in Paths) w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteBoolean("alwaysSprites", _alwaysSprites);
                w.WriteEndObject();
            }
            File.WriteAllText(ConfigPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { }
    }
}
