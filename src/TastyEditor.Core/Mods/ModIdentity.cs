using System.Text.Json;
using System.Text.Json.Serialization;

namespace TastyEditor.Core.Mods;

/// <summary>
/// The <c>.tpm-mod.json</c> marker written inside every installed mod folder (and the pristine
/// vanilla copy). The archive's <see cref="ModMetadata"/> describes a mod as it travels; this
/// records what a folder on disk actually <em>is</em>, so the picker can show a friendly name and
/// tell a full merged mod apart from the untouched vanilla assets, even after the folder has been
/// renamed into the live <c>assets</c> slot.
/// </summary>
public sealed class ModIdentity
{
    /// <summary>The vanilla (pristine, unmodified) assets folder's canonical name.</summary>
    public const string VanillaName = "VANILLA";

    /// <summary>File name of the marker, written inside each assets folder.</summary>
    public const string FileName = ".tpm-mod.json";

    /// <summary>Human-facing name for the picker ("VANILLA" for the pristine copy).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Game key this folder belongs to.</summary>
    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    /// <summary>Version tag carried from the manifest, or empty for vanilla.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Whether this folder is the pristine vanilla backup or an installed mod.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindMod;

    public const string KindVanilla = "vanilla";
    public const string KindMod = "mod";

    public bool IsVanilla => string.Equals(Kind, KindVanilla, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Turn a mod name into a folder slug: trim, spaces to dashes, drop characters that
    /// are hostile in a path. Never empty (falls back to "mod"); never a reserved word.</summary>
    public static string Slug(string name)
    {
        var s = (name ?? "").Trim().Replace(' ', '-');
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            else sb.Append('-');
        }
        string slug = sb.ToString().Trim('-', '.');
        return slug.Length == 0 ? "mod" : slug;
    }

    public static ModIdentity? Read(string assetsDir)
    {
        var p = Path.Combine(assetsDir, FileName);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<ModIdentity>(File.ReadAllText(p), Opts); }
        catch { return null; }
    }

    public void Write(string assetsDir)
    {
        var p = Path.Combine(assetsDir, FileName);
        File.WriteAllText(p, JsonSerializer.Serialize(this, Opts));
    }

    /// <summary>The name a folder represents: its identity file if present, else derived from the
    /// folder name (<c>assets</c> → active name, <c>assets-X</c> → X, else the folder name).</summary>
    public static string DisplayFor(string assetsDir, string folderName)
    {
        var id = Read(assetsDir);
        if (id is not null && !string.IsNullOrWhiteSpace(id.Name)) return id.Name;
        // Migration: a folder named assets-<something> with no marker still reads as <something>.
        if (folderName.Equals("assets", StringComparison.OrdinalIgnoreCase)) return "";
        const string pre = "assets-";
        if (folderName.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
            return folderName[pre.Length..];
        return folderName;
    }
}
