using System.IO;
using TastyEditor.App.Assets;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>Headless audit of the rig-name fallback, for the class of bug where an object draws some
/// other object's picture. <see cref="TbSkeletons.FindRig"/> answers in two passes: a word-boundary
/// match (the rig, or the rig followed by <c>_</c>) and then a greedy bare-prefix match that exists for
/// Final Bite's concatenated <c>catfishatlantis1_7</c> spellings. When only the second pass answers, the
/// rig is a guess, and if the name has an atlas cell of its own the guess is wrong — Tasty Blue's
/// <c>fishbowl_side</c> and <c>fishfood</c> were being painted as the <c>fish</c> rig that way.
///
/// So: for every distinct entity and decoration name the installed levels actually use, report which
/// pass answered and whether the name owns its art. The verdict is not a flag check — it renders the name
/// through the real <see cref="SpriteSource"/> and through the rig's own spelling, and calls the name
/// hijacked when the two come out as the same picture while the name has art of its own. Read-only;
/// touches no level bytes.</summary>
public static partial class Smoke
{
    public static void RigFallProbe()
    {
        using var log = new StreamWriter("_rigfall.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        const int levelCap = 40;
        int grandHijack = 0;
        foreach (var game in new[] { "tb", "f4", "f5" })
        {
            // Tasty Blue's levels are small enough to sweep whole, and it is the family where a
            // decoration's name is a cell spelling by construction, so its coverage has to be complete.
            int cap = game == "tb" ? int.MaxValue : levelCap;
            var (g, dir) = Defaults.First(d => d.Game == game);
            if (!Directory.Exists(dir)) { Emit($"{game}: SKIP (no levels dir)"); continue; }
            var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
            if (levels.Length == 0) { Emit($"{game}: SKIP (no levels)"); continue; }

            var root = AssetsRoot.ForLevel(levels[0]);
            var res = root is null ? null : SpriteResolver.TryLoad(root);
            if (res is null) { Emit($"{game}: FAIL resolver null"); continue; }
            var rigs = res.Rigs;
            var ss = new SpriteSource(res);
            var white = new byte[] { 255, 255, 255, 255 };
            Emit($"== {g} == levels={levels.Length} rigs={(rigs?.Count ?? -1)} cells={res.CellCount}");
            if (rigs is not { Count: > 0 }) { Emit("   no rig table — the fallback cannot fire"); continue; }

            var names = new SortedDictionary<string, (int Recs, bool Entity, bool Deco)>(StringComparer.Ordinal);
            foreach (var lvl in levels.Take(cap))
            {
                List<SceneCross> ms;
                try { ms = LevelLoader.Load(lvl).Scene.Crosses
                    .Where(c => (c.Kind is SceneKind.Entity or SceneKind.Deco) && c.SpriteName is { Length: > 0 })
                    .ToList(); }
                catch { continue; }
                foreach (var c in ms)
                {
                    var cur = names.GetValueOrDefault(c.SpriteName!);
                    names[c.SpriteName!] = (cur.Recs + 1,
                        cur.Entity || c.Kind == SceneKind.Entity,
                        cur.Deco || c.Kind == SceneKind.Deco);
                }
            }

            int boundary = 0, hijack = 0, noRig = 0;
            var hijackList = new List<string>();
            foreach (var (name, (recs, isEntity, isDeco)) in names)
            {
                // the three spellings TryResolveRig consults, in its order
                string[] keys = { name, EntityName.CatalogBase(name), EntityName.SpeciesKey(name) };
                string? viaBoundary = null, viaBare = null;
                foreach (var k in keys)
                {
                    viaBoundary ??= TbSkeletons.FindRigBoundary(rigs, k);
                    viaBare ??= TbSkeletons.BarePrefixRig(rigs, k);
                }
                if (viaBoundary is not null) { boundary++; continue; }
                if (viaBare is null) { noRig++; continue; }

                // Only the greedy pass answered. What does the name own in the rest of the ladder?
                bool guess = res.TryResolveRig(name, out var plan, out bool isBareGuess) && isBareGuess;
                if (!guess) { noRig++; continue; }
                bool hasDirect = res.TryResolveDirectCell(name, 0, out _);
                bool hasStem = res.TryResolveFallbackCell(name, 0, out _);
                bool hasEngine = res.TryGetEngineArt(name, out string art) && art != name;
                hijack++;

                // Ground truth: does the viewport still paint the guessed rig's picture for this name?
                // The rig's own spelling (`<rig>_x`) always composites, so a matching size + ink means the
                // name is being drawn as that other object. A name with no art of its own is allowed to.
                string painted = "?";
                bool ownsArt = hasDirect || hasEngine;
                if (ss.TryGet(name, 0, white, out var mine, out int mw, out int mh, out _, out _, out _)
                    && ss.TryGet(viaBare + "_x", 0, white, out var rigImg, out int rw, out int rh, out _, out _, out _))
                {
                    bool samePicture = mw == rw && mh == rh && Ink(mine) == Ink(rigImg);
                    painted = samePicture ? (ownsArt ? "RIG(own art ignored!)" : "rig (only art)") : "own art";
                    if (samePicture && ownsArt) grandHijack++;
                }
                else painted = "BUILD FAIL";
                if (hijackList.Count < 60 || painted.Contains("ignored"))
                    hijackList.Add($"   {name} [{(isEntity ? "entity" : "")}{(isEntity && isDeco ? "+" : "")}{(isDeco ? "deco" : "")}] "
                                   + $"-> rig '{viaBare}' parts={plan.Parts.Count} paints={painted} "
                                   + $"engine={(hasEngine ? art : "-")} direct={hasDirect} stem={hasStem} records={recs}");
            }
            Emit($"   distinct names={names.Count}  boundary={boundary}  bare-prefix-only={hijack}  no rig={noRig}");
            foreach (var s in hijackList) Emit(s);
        }
        Emit($"names whose own art the greedy rig pass still paints over: {grandHijack}");
        // Wording matches the other gates so `allgates` reads the verdict, not just a human.
        Emit(grandHijack == 0 ? "RIGFALL GREEN" : $"RIGFALL FAILURES ({grandHijack} names painted as another object)");
    }
}
