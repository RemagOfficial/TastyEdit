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
        // Dev-only: render a few TP2 skeletal characters to _skel_<name>.png for pixel inspection.
        if (args.Length >= 1 && args[0] == "tp2skel")
        {
            Viewport.Smoke.Tp2SkelDump();
            return;
        }
        // Dev-only: prove the Tasty Blue skeletons.bin slice — report entity-name coverage over the stock
        // levels and render every rig composite to _tb_rig_<name>.png plus _tb_rig_sheet.png.
        if (args.Length >= 1 && args[0] == "tbskel")
        {
            Viewport.Smoke.TbSkelDump();
            return;
        }
        // Dev-only: audit the greedy rig-name fallback — every entity/deco name the installed levels use,
        // which pass of FindRig answered it, and whether a name that owns its art was stolen onto another
        // object's rig (writes _rigfall.txt).
        if (args.Length >= 1 && args[0] == "rigfall")
        {
            Viewport.Smoke.RigFallProbe();
            return;
        }
        // Dev-only: Forever / Final Bite engine-art port coverage — EngineArt/rig-name counts and per-tier
        // entity/deco resolution over stock levels (writes _f45_probe.txt).
        if (args.Length >= 1 && args[0] == "f45probe")
        {
            Viewport.Smoke.F45Probe();
            return;
        }
        // Dev-only: render Final Bite rigs to _f5_rig_<name>.png plus _f5_rig_sheet.png. With an
        // optional second arg it renders just rigs whose name contains that substring, in whatever
        // placement path they take (crop or affine), for targeted eyeballing.
        if (args.Length >= 1 && args[0] == "f5rig")
        {
            Viewport.Smoke.F5RigDump(args.Length >= 2 ? args[1] : null);
            return;
        }
        // Dev-only: dump every TP2 entity TYPE's mass values and BoxBounds to _tp2scale.txt so the
        // render-scale law can be fitted offline against entityintersections.xml.
        if (args.Length >= 1 && args[0] == "tp2scale")
        {
            Viewport.Smoke.Tp2ScaleDump();
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
        // Headless R4 element-creation proof (NewRecord): for one stock level per family and every
        // kind it supports, the untouched level stays byte-identical, a created record grows that kind
        // by exactly one, re-encodes stable (f4/f5 Counts in lockstep) and — where delete exists —
        // create-then-delete restores the original bytes. Not-supported kinds are honest skips.
        if (args.Length >= 1 && args[0] == "createops")
        {
            Viewport.Smoke.CreateOpsCheck();
            return;
        }
        // Headless R4 path-editing proof (SceneProjector path API): a whole-path move / add-point /
        // remove-point on one stock level per family, each re-encoding stable and (raw-span) confined
        // to the touched record with the section Count still walking.
        if (args.Length >= 1 && args[0] == "pathops")
        {
            Viewport.Smoke.PathOpsCheck();
            return;
        }
        // R5: the Tasty Planet 1 verbatim-XML contract over every shipped level (I1 byte-identical,
        // every edit class localised + re-decode stable + revert exact, detection clean).
        if (args.Length >= 1 && args[0] == "tp1check")
        {
            Viewport.Smoke.Tp1Check();
            return;
        }
        // R7: the verbatim contract over the shared entity / image tables the entity panel writes
        // (entitydefs.xml / imagemaps.xml): I1 byte-identical, create + remove restores the bytes,
        // edits re-decode stable, and a shipped def parses back. In-memory only — writes nothing.
        if (args.Length >= 1 && args[0] == "tp1tables")
        {
            Viewport.Smoke.Tp1TablesCheck();
            return;
        }
        // R6: the mod-system lifecycle proof over a throwaway sandbox (vanilla backup, merge-install,
        // activate/switch/revert, zip-slip refusal, delta publish, game-mismatch refusal). Self-
        // contained — needs no game install and writes nothing outside its temp folder.
        if (args.Length >= 1 && args[0] == "mods")
        {
            Viewport.Smoke.ModsCheck();
            return;
        }
        // D7 (R4): run every headless gate in one pass for CI-on-tag, exiting non-zero if any fails.
        if (args.Length >= 1 && args[0] == "allgates")
        {
            System.Environment.ExitCode = Viewport.Smoke.AllGates();
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
        if (args.Length >= 1 && args[0] == "entitydefs")    // copyaudit: dev  (CLI verb, not UI copy)
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
        // R1 copy-convention audit: lex the real string literals out of every App source (comments
        // skipped, \u escapes decoded) and refuse any of UiText.BannedWords in UI copy, so the
        // internal vocabulary cleaned out in R1 cannot leak back in through slices R2-R7. Read-only.
        if (args.Length >= 1 && args[0] == "copyaudit")
        {
            Viewport.Smoke.CopyAudit();
            return;
        }
        // Headless R2 transform-handle proof: the knob's turn, the corner grips (uniform, free axes,
        // entity mass) and the vertex removal each commit through the same operations the gestures
        // use; every write must stay inside its own record, survive a fresh read, and undo to the
        // loaded bytes exactly. One sampled level per game. Read-only, no paths needed.
        if (args.Length >= 1 && args[0] == "handles")
        {
            Viewport.Smoke.HandlesCheck();
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

        // R7 packaging: the installer's "associate .bin with TastyEdit" task and the uninstaller's
        // clean-up call these, so the registry layout has exactly one implementation — the one the
        // File menu already uses — rather than a second copy written in a setup script. Registering
        // takes the exe path from the process unless one is given (the setup program knows better
        // where the file ended up than a relative launch does).
        if (args.Length >= 1 && args[0] == "register-association")
        {
            string? exe = args.Length > 1 ? args[1] : Environment.ProcessPath;
            if (exe is not null) FileAssociation.Register(exe);   // nothing to point at: do nothing
            return;
        }
        if (args.Length >= 1 && args[0] == "unregister-association")
        {
            FileAssociation.Unregister();
            return;
        }

        var app = new App();

        // R3 theme: install the persisted palette (dark unless the config says otherwise) before any
        // window is built, so the first paint is already themed and the shell can read Theme.* tokens.
        Theme.Apply(app, RecentFiles.LoadDark());

        // Slice-7 error UX: turn any stray crash into the same friendly dialog a handled failure
        // shows, and log it, instead of a silent close or a Windows "has stopped working" box.
        app.DispatcherUnhandledException += (s, e) =>
        {
            ErrorUi.Crash(app.MainWindow, e.Exception);
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            ErrorUi.Crash(app.MainWindow, e.Exception);
        // A crash on a background thread (a decode task, a worker) never reaches the dispatcher, so
        // also route the AppDomain-wide handler through the same report + dialog instead of dying silently.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) ErrorUi.Crash(app.MainWindow, ex);
        };

        // Slice-7 file association / double-click: a level path passed on the command line
        // ("TastyEdit.exe C:\...\level.bin") opens straight away. Only a real existing
        // file is treated as a level; headless subcommands were handled above and return.
        string? startup = args.Length == 1 && System.IO.File.Exists(args[0]) ? args[0] : null;

        // R7: "--game tb" is what a per-game Start-Menu shortcut passes; the window takes it to that
        // game's level folder. Anything unrecognised is ignored rather than refused.
        string? startupGame = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--game") startupGame = args[i + 1];

        app.Run(new MainWindow(startup, startupGame));
    }
}
