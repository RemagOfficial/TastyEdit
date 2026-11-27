using System.Text.Json;
using System.Text.Json.Serialization;

namespace TastyEditor.Core.Mods;

/// <summary>
/// The <c>metadata.json</c> document at the root of a <c>.tpm</c> archive: who made the mod,
/// what it is called, and — the field that drives every install decision — which game it targets.
/// A <c>.tpm</c> is a plain zip; this is the one required entry inside it.
/// </summary>
/// <remarks>
/// The JSON is written with explicit lower-case names so a third party can hand-edit the file
/// (the format is documented in <c>docs/MODFORMAT.md</c> and must stay stable across releases).
/// Unknown members are ignored on read so a future field never bricks an older editor.
/// </remarks>
public sealed class ModMetadata
{
    /// <summary>Human-facing mod name; also the base of the on-disk folder slug. Required.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Author credit line. Optional.</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    /// <summary>Free-form version tag (e.g. "1.0"). Optional.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Short description shown in the mod list. Optional.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// The game this mod is for: one of the registry keys <c>tp2</c> / <c>tb</c> / <c>f4</c> /
    /// <c>f5</c> / <c>tp1</c>. Required — an install refuses to merge a mod into a different game.
    /// </summary>
    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    /// <summary>True when a name is present and the game is one the editor knows about.</summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Name) && Levels.GameRegistry.KnownKeys.Contains(Game);

    // The read options ignore case + unknown members; the write options emit indented, camel
    // JSON with no BOM so the file stays hand-editable and diffs cleanly.
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ModMetadata FromJson(string json) =>
        JsonSerializer.Deserialize<ModMetadata>(json, ReadOpts) ?? new ModMetadata();

    public static ModMetadata FromBytes(byte[] utf8) =>
        JsonSerializer.Deserialize<ModMetadata>(utf8, ReadOpts) ?? new ModMetadata();

    public string ToJson() => JsonSerializer.Serialize(this, WriteOpts);
}
