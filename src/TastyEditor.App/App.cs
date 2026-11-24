using System.Windows;
using TastyEditor.App.Editing;

namespace TastyEditor.App;

/// <summary>Package-free WPF entry point (ROADMAP Phase D slice 2). The static Main
/// is the application entry: builds the singleton App, then shows the main window.
/// A deliberate hand-written entry (no XAML build task) keeps the offline build to
/// only the shared WindowsDesktop framework.</summary>
public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless projection check: `TastyEditor.App smoke <path...>` prints the
        // detect/decode/project result per level without opening a window. With no
        // paths it sweeps one representative stock level per game.
        if (args.Length >= 1 && args[0] == "smoke")
        {
            Viewport.Smoke.Run(args.Length > 1 ? args[1..] : null);
            return;
        }
        if (args.Length >= 2 && args[0] == "dump")
        {
            Viewport.Smoke.Dump(args[1]);
            return;
        }
        // Headless save-path proof (untouched I1 + a localised entity edit); with no
        // paths it sweeps one representative stock level per game.
        if (args.Length >= 1 && args[0] == "savecheck")
        {
            Viewport.Smoke.SaveCheck(args.Length > 1 ? args[1..] : null);
            return;
        }
        // Headless wall/area polygon-edit proof (untouched I1 + localised vertex / move
        // edits that re-decode stable); with no paths it samples one editable level per game.
        if (args.Length >= 1 && args[0] == "polycheck")
        {
            Viewport.Smoke.PolyCheck(args.Length > 1 ? args[1..] : null);
            return;
        }
        // Headless water-rectangle edit proof (untouched I1 + localised move / corner-resize
        // edits that re-decode stable); with no paths it samples one stock level per game.
        if (args.Length >= 1 && args[0] == "watercheck")
        {
            Viewport.Smoke.WaterCheck(args.Length > 1 ? args[1..] : null);
            return;
        }
        // Headless asset-loader proof (S5-1): resolve sample species + decode/probe atlases
        // for each ElementEngine install. No paths needed (it sweeps the stock installs).
        if (args.Length >= 1 && args[0] == "assets")
        {
            Viewport.Smoke.AssetsCheck();
            return;
        }
        // Headless entity-display-field proof (S5-2): cross-check the committed tb sample's
        // entity name/rotation/mass/priority against the Python oracle and assert the
        // projector carries them onto the crosses. No paths needed.
        if (args.Length >= 1 && args[0] == "entfields")
        {
            Viewport.Smoke.EntFields();
            return;
        }
        if (args.Length >= 1 && args[0] == "alphaprobe")
        {
            Viewport.Smoke.AlphaProbe(args);
            return;
        }
        // Headless sprite-render proof (S5-3): resolve every projected entity/deco sprite
        // through the WPF decode layer and check sizes. No paths needed.
        if (args.Length >= 1 && args[0] == "sprites")
        {
            Viewport.Smoke.SpritesCheck();
            return;
        }
        // Headless asset-browser data-path proof (S5-4): enumerate + build cell thumbnails
        // for each ElementEngine install. No paths needed (sweeps the stock installs).
        if (args.Length >= 1 && args[0] == "browser")
        {
            Viewport.Smoke.BrowserCheck();
            return;
        }
        // Headless TP2 asset-cascade oracle check (S5-7): dump the ported resolver's
        // resolution signature for every cell / curated / real-level entity name so a Python
        // run of tp2assets.py can diff it against the reference. No paths needed.
        if (args.Length >= 1 && args[0] == "tp2assets")
        {
            Viewport.Smoke.Tp2AssetsCheck();
            return;
        }
        // Headless sprite-reassignment write-path proof (S5-5): reassign one entity name and
        // one decoration sprite on stock TP2 levels and prove each edit persists, is re-decode
        // stable, and reverts to the exact original bytes (fully localised). No paths needed.
        if (args.Length >= 1 && args[0] == "tp2reassign")
        {
            Viewport.Smoke.Tp2ReassignCheck();
            return;
        }
        // Headless decoration sprite-reassignment proof for Tasty Blue + Forever + Final Bite
        // (S5-5): repoint one decoration on a stock level of each to an existing and a new cell
        // name, proving persistence, no run-length cascade, tile-table growth and re-decode
        // stability. No paths needed.
        if (args.Length >= 1 && args[0] == "decoedit")
        {
            Viewport.Smoke.DecoEditCheck();
            return;
        }
        // Headless S-D13 decoration facing (flip) proof for all four families: the write lands and
        // re-decodes, touches no neighbour record, keeps the edited file lossless, and reverting the
        // flags reproduces the shipped bytes — proving the optional 24-byte block is materialised and
        // dropped exactly as ElementDecoration::writeBinary emits it. No paths needed.
        if (args.Length >= 1 && args[0] == "flips")
        {
            Viewport.Smoke.FlipCheck();
            return;
        }
        // Headless entity sprite-reassignment proof for Tasty Blue + Forever + Final Bite
        // (S5-6): repoint the first entity on a stock level of each to another existing entity
        // name and to a fresh name, proving persistence, that exactly one entity changed (no
        // framing cascade) and re-decode stability; and for the opaque-block games that an
        // invalid (no-underscore) name is refused with the bytes untouched. No paths needed.
        if (args.Length >= 1 && args[0] == "entityedit")
        {
            Viewport.Smoke.EntityEditCheck();
            return;
        }
        // Headless slice-6 mutation proof (duplicate / delete): for one stock level per family
        // and every kind it supports, assert each op re-decodes stable, grows/shrinks that kind
        // by exactly one, and leaves every other record's resolved position untouched (which
        // for the chained raw-span families proves the delta re-fold on delete). No paths needed.
        if (args.Length >= 1 && args[0] == "editops")
        {
            Viewport.Smoke.EditOpsCheck();
            return;
        }
        // Headless slice-6 multilevel XML proof: load each shipped levels/multilevels/*.xml, prove
        // save∘load is idempotent on the text and that stage count + resolved zoom survive the
        // round-trip (the .NET port of tp2multi.py / tbmulti.py). No paths needed.
        if (args.Length >= 1 && args[0] == "multi")
        {
            Viewport.Smoke.MultiLevelCheck();
            return;
        }
        // Headless slice-6 mutation-integrity proof beyond per-kind dup/del: f4/f5 section Count
        // stays in lockstep with its record list across a duplicate + delete, and a move-to-layer
        // on a multi-layer level re-decodes stable while shifting exactly one record. No paths.
        if (args.Length >= 1 && args[0] == "moveops")
        {
            Viewport.Smoke.MoveOpsCheck();
            return;
        }
        // Headless slice-7 undo/redo proof: per family, duplicate → undo restores the exact
        // loaded bytes (dirty clears) → redo re-applies the edit byte-for-byte → undo returns to
        // the oldest state. No paths needed.
        if (args.Length >= 1 && args[0] == "undo")
        {
            Viewport.Smoke.UndoCheck();
            return;
        }
        // Headless D6 decoration move + rotate proof for Tasty Blue / Forever / Final Bite: a move
        // changes exactly one deco's absolute position (successor keeps the chain), a rotation
        // changes exactly one record, both are byte-localised + re-decode stable, and the move
        // round-trips through the undo history. No paths needed.
        if (args.Length >= 1 && args[0] == "decodops")
        {
            Viewport.Smoke.DecoOpsCheck();
            return;
        }
        // Headless entity move + rotate proof for Tasty Blue / Forever / Final Bite (and TP2): a
        // move changes exactly one entity's absolute position (later records keep the chain), a
        // rotation changes exactly one record, both are byte-localised + re-decode stable, and the
        // move round-trips through the undo history. No paths needed.
        if (args.Length >= 1 && args[0] == "entops")
        {
            Viewport.Smoke.EntityOpsCheck();
            return;
        }
        // Headless TP2 inspector field-parity proof (D7): for one stock level, edit each typed
        // entity field (mass, priority, persists, colour, emitter toggle, follow-path) and each
        // deco field (size-x, priority, colour, rotation), asserting each persists through a
        // re-decode, re-encodes stable, and restores the exact original bytes when reverted. No paths.
        if (args.Length >= 1 && args[0] == "tp2fields")
        {
            Viewport.Smoke.Tp2FieldsCheck();
            return;
        }
        // Headless raw-span field-parity proof (tb / Forever / Final Bite): for one stock level per
        // family, edit each addressable entity field (mass, priority, colour) and deco field (size,
        // colour) through the unified facade, asserting each change is byte-localised, persists
        // through a re-decode, re-encodes stable and restores the exact original bytes when reverted.
        if (args.Length >= 1 && args[0] == "fields")
        {
            Viewport.Smoke.FieldsCheck();
            return;
        }
        // Diagnostic: run the named tail walk over every entity tail in each stock level and
        // classify the trailing (un-named) bytes, to find a systematic trailer vs a per-record quirk.
        if (args.Length >= 1 && args[0] == "tailsurvey")
        {
            Viewport.Smoke.TailSurvey();
            return;
        }
        // Headless structural behaviour-tail proof (S-D8f): for one stock level per tail family, toggle
        // every presence gate off and back on, rename an embedded engine name and grow / shrink the cell
        // colour ramp, asserting each edit resizes the record by exactly the right amount, re-decodes in
        // the new state with no unnamed residual, re-encodes stable and restores the stock bytes EXACTLY
        // when reverted (so the length-changing write leaked nowhere else). No paths needed.
        if (args.Length >= 1 && args[0] == "tailedit")
        {
            Viewport.Smoke.TailStructCheck();
            return;
        }
        // Diagnostic: print every section's count / end before a gate is enabled and again after the
        // written bytes are re-decoded, to see what a length-changing edit does to the whole-file tiling.
        if (args.Length >= 1 && args[0] == "tailframe")
        {
            Viewport.Smoke.TailFrame(args.Length >= 2 ? args[1] : null);
            return;
        }
        // Diagnostic: the entity TYPE namespace (S-D9) — every def name the shipped levels use, how it
        // intersects the atlas cell list the entity picker offers, and a cross-check against the game
        // binary's compiled-in def strings. Read-only, no paths needed.
        if (args.Length >= 1 && args[0] == "entitytypes")
        {
            Viewport.Smoke.EntityTypes();
            return;
        }
        // Proof: the entity TYPE catalogue behind the apply-side guard (S-D9) never refuses a type a
        // shipped level uses, and does refuse the names the game would drop (atlas cells, fabricated,
        // case-flipped, suffixed). Read-only, no paths needed.
        if (args.Length >= 1 && args[0] == "entitydefs")
        {
            Viewport.Smoke.EntityDefsCheck();
            return;
        }
        // Proof: adding a brand-new entity (S-D10) is seen by the loader's own record walk, survives a
        // save/reload, lands where asked, refuses a type the game cannot make, and a spawn followed by
        // a delete restores the original bytes exactly. Read-only, no paths needed.
        if (args.Length >= 1 && args[0] == "entitynew")
        {
            Viewport.Smoke.EntityNewCheck();
            return;
        }
        // Proof for the inspector fix (S-D10): a field row must be re-read from the record after a write
        // (the tail view is a copy, so refilling from the cache shows a revert), and the .bin open-command
        // parse that drives the rename prompt must cut the exe, not the last quote. Read-only.
        if (args.Length >= 1 && args[0] == "refill")
        {
            Viewport.Smoke.RefillCheck();
            return;
        }
        // Proof for the decoration render fix (S-D11): a deco is stretched to its record's world box
        // (dimensions/100, the same centiunits as its position) and tinted by the record colour. The
        // shipped levels and the atlas cells are the oracle. Read-only.
        if (args.Length >= 1 && args[0] == "decofit")
        {
            Viewport.Smoke.DecoFitCheck();
            return;
        }
        // Diagnostic: dump one full entity behaviour tail end-to-end (raw hex + named walk fields)
        // so the true record structure / trailer can be read directly. `taildump <levelNameFragment> [wantLen]`.
        if (args.Length >= 2 && args[0] == "taildump")
        {
            Viewport.Smoke.TailDump(args[1], args.Length >= 3 ? int.Parse(args[2]) : 0);
            return;
        }
        // Headless position-scale diagnostic (entity/deco/wall/path raw coords + rendered
        // bounds). Optional explicit level paths; with none it samples barrel1a + one stock tb.
        if (args.Length >= 1 && args[0] == "poscheck")
        {
            Viewport.Smoke.PosCheck(args.Length > 1 ? args[1..] : null);
            return;
        }
        // Headless per-phase timing of the GUI open pipeline (finds what blocks the UI).
        if (args.Length >= 1 && args[0] == "loadtime")
        {
            Viewport.Smoke.LoadTime(args.Length > 1 ? args[1..] : null);
            return;
        }

        var app = new App();

        // Slice-7 error UX: turn any stray crash into the same friendly dialog a handled failure
        // shows, and log it, instead of a silent close or a Windows "has stopped working" box.
        app.DispatcherUnhandledException += (s, e) =>
        {
            ErrorUi.Crash(app.MainWindow, e.Exception);
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            ErrorUi.Crash(app.MainWindow, e.Exception);

        // Slice-7 file association / double-click: a level path passed on the command line
        // ("TastyEdit.exe C:\...\level.bin") opens straight away. Only a real existing
        // file is treated as a level; headless subcommands were handled above and return.
        string? startup = args.Length == 1 && System.IO.File.Exists(args[0]) ? args[0] : null;
        app.Run(new MainWindow(startup));
    }
}
