using System.IO;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>A loaded level: which game it is, the lossless decoded model, its read-only
/// display projection, and the on-disk bytes it was loaded from. The doc owns the save
/// path so an edit can be proven byte-localised and the *edited* file shown to still
/// round-trip losslessly — the same invariants the golden corpus enforces, checked live
/// in-app before anything is written (slice 3's acceptance gate).</summary>
public sealed class LevelDoc
{
    public string Path { get; private set; }
    public GameProfile Profile { get; }
    public object Model { get; private set; }
    public SceneGeometry Scene { get; private set; }
    public byte[] Original { get; private set; }

    /// <summary>Set when the model has been changed since the last load/save.</summary>
    public bool Dirty { get; set; }

    public LevelDoc(string path, GameProfile profile, object model, SceneGeometry scene, byte[] original)
    {
        Path = path; Profile = profile; Model = model; Scene = scene; Original = original;
    }

    public string GameLabel => Profile.Label;
    public string FileName => System.IO.Path.GetFileName(Path);

    public int LayerCount => Model switch
    {
        TbLevel tb => tb.Layers.Count,
        Tp2Level tp => tp.Layers.Count,
        ElementLevel el => el.Layers.Count,
        Tp1Level t1 => t1.Root.Child("tilelayers")?.Children.Count ?? 0,
        _ => 0,
    };

    /// <summary>Re-serialise the model to bytes with the profile's codec (no write).</summary>
    public byte[] Encode() => Profile.Codec.Encode(Model);

    /// <summary>Rebuild the read-only scene projection from the current model. Needed after a
    /// length-changing edit inside an opaque span (an f4/f5 entity name splice resizes the block
    /// and shifts every later record's byte offset, invalidating the projected handles) so the
    /// viewport and selection operate on freshly walked geometry.</summary>
    public void Reproject() => Scene = SceneProjector.Project(Model);

    /// <summary>Undo / redo landing point (the UI-thread half). The snapshot model is decoded on
    /// a worker thread by the shell (the f4/f5 decode is not free) and installed here; the scene
    /// is re-projected and the dirty flag re-derived by re-encoding once and comparing against
    /// the bytes on disk — so undoing back to the loaded state clears the star.</summary>
    public void ApplyRestoredModel(object model)
    {
        Model = model;
        Reproject();
        var now = Encode();
        Dirty = !(now.Length == Original.Length && now.AsSpan().SequenceEqual(Original));
    }

    /// <summary>Encode the model, prove the diff vs what is on disk is localised and that
    /// the edited bytes themselves round-trip, then write. Only trusts the codec's already
    /// golden-verified re-encode path; the extra checks guard the *edit*, not the codec.</summary>
    public SaveReport SaveTo(string destPath)
    {
        byte[] outBytes = Encode();
        var report = SaveReport.Compare(Original, outBytes);

        // Stability: re-decode the bytes we are about to write and re-encode — an edited
        // level must itself be byte-exact lossless, or the edit landed outside the model.
        var re = Profile.Codec.Encode(Profile.Decode(outBytes));
        report.Stable = re.Length == outBytes.Length && re.AsSpan().SequenceEqual(outBytes);

        File.WriteAllBytes(destPath, outBytes);
        Path = destPath;
        Original = outBytes;   // rebase so the next save diffs against this write
        Dirty = false;
        return report;
    }
}

/// <summary>Result of a save: how the encoded bytes differ from the bytes we loaded, and
/// whether the edited file re-encodes to itself. The size-preserved + changed-count is the
/// in-app echo of the golden I3 edit-localisation proof.</summary>
public sealed class SaveReport
{
    public int SizeIn;
    public int SizeOut;
    public bool SizePreserved;
    public int BytesChanged;
    public int FirstDiff = -1;
    public int LastDiff = -1;
    public bool Stable;

    public bool Changed => BytesChanged > 0 || !SizePreserved;

    public static SaveReport Compare(byte[] a, byte[] b)
    {
        var r = new SaveReport { SizeIn = a.Length, SizeOut = b.Length };
        r.SizePreserved = a.Length == b.Length;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
            {
                if (r.FirstDiff < 0) r.FirstDiff = i;
                r.LastDiff = i;
                r.BytesChanged++;
            }
        if (!r.SizePreserved)
        {
            if (r.FirstDiff < 0) r.FirstDiff = n;
            r.LastDiff = Math.Max(a.Length, b.Length) - 1;
        }
        return r;
    }

    /// <summary>The developer-facing one-liner (byte count, diff range, size, round-trip verdict).
    /// The status bar does NOT use this — R1 moved the status line to <see cref="UiText.SavedLine"/>
    /// — but the headless gates print it, and it is what a bug report needs.</summary>
    public string Summarize()
    {
        if (!Changed) return "saved (identical to disk)";
        string span = FirstDiff >= 0 ? $", changed bytes @ {FirstDiff}..{LastDiff}" : "";
        string size = SizePreserved ? "size preserved" : $"size {SizeIn}->{SizeOut}";
        string stab = Stable ? "re-decode stable" : "RE-DECODE DRIFT";   // copyaudit: dev  (gate log, never the UI)
        return $"saved {BytesChanged} byte(s){span}, {size}, {stab}";    // copyaudit: dev
    }
}

/// <summary>The open pipeline: read bytes, let Core detect the game (path hint + trial
/// decode), decode to the lossless model, then project to world-space geometry for the
/// viewport. All format knowledge stays in Core; the app never branches on the game.</summary>
public static class LevelLoader
{
    public static LevelDoc Load(string path)
    {
        var data = File.ReadAllBytes(path);
        // Detect and decode in one step: the f4/f5 tiling oracle is expensive enough that
        // trial-decoding to identify the game and then decoding again would double the work.
        var (profile, model) = GameRegistry.DetectAndDecode(path, data, out _);
        var scene = SceneProjector.Project(model);
        return new LevelDoc(path, profile, model, scene, data);
    }
}
