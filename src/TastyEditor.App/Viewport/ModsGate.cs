using System.IO;
using System.IO.Compression;
using TastyEditor.Core.Mods;

namespace TastyEditor.App.Viewport;

/// <summary>Headless R6 mod-system proof (<c>mods</c>). Builds a throwaway sandbox that stands in
/// for a game install — a fake <c>assets</c> tree and a small hand-made <c>.tpm</c> — and drives the
/// whole lifecycle through the Core engine: the authoring project (a dev folder published whole,
/// with no vanilla lookup), vanilla backup, merge-install, activate/switch/revert, zip-slip refusal,
/// delta publish, and a game-mismatch refusal. It never touches a real install and
/// removes its scratch folder when done. Bounded on purpose (a handful of tiny files).</summary>
public static partial class Smoke
{
    public static void ModsCheck()
    {
        using var log = new StreamWriter("_mods.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        int pass = 0, fail = 0;
        void Count(string what, bool ok)
        {
            if (ok) pass++; else fail++;
            Emit($"{(ok ? "OK  " : "FAIL")} {what}");
        }

        string root = Path.Combine(Path.GetTempPath(), "tpe_mods_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string assets = Path.Combine(root, "assets");
            string tpm = Path.Combine(root, "testmod.tpm");
            string src = Path.Combine(root, "_modsrc");
            string esc = Path.Combine(root, "_slip");

            // A vanilla install: one level file + one graphic that must survive untouched.
            Write(Path.Combine(assets, "levels", "a.bin"), "AAA");
            Write(Path.Combine(assets, "graphics", "keep.png"), "KEEP");

            // The mod: replaces a.bin, adds new.xml.
            Write(Path.Combine(src, "levels", "a.bin"), "MOD");
            Write(Path.Combine(src, "levels", "new.xml"), "NEW");
            var meta = new ModMetadata { Name = "Test Mod", Game = "tb", Version = "1.0", Author = "gate" };
            int packed = TpmFile.Create(tpm, src, meta);
            Count($"author: packed both files ({packed})", packed == 2);

            // Validate accepts the good file.
            var (vok, vmeta, verr) = TpmFile.Validate(tpm);
            Count($"validate: good mod ok, game=tb (err={verr ?? "none"})", vok && vmeta?.Game == "tb");

            // EnsureVanilla → a byte-equal pristine copy.
            string van = ModInstall.EnsureVanilla(root, "tb");
            Count("ensure vanilla: assets-vanilla made",
                Directory.Exists(van) && Read(Path.Combine(van, "levels", "a.bin")) == "AAA");

            // Preview: 1 new, 1 replace.
            var pv = ModInstall.PreviewInstall(root, tpm, "tb");
            Count($"preview: new={pv.New} replace={pv.Replace} (want 1/1)", pv.New == 1 && pv.Replace == 1);

            // Install + activate.
            var rep = ModInstall.InstallFromTpm(root, tpm, "tb", activate: true);
            int assetsCount = Directory.EnumerateDirectories(root)
                .Count(d => Path.GetFileName(d).Equals("assets", StringComparison.OrdinalIgnoreCase));
            Count($"install: added={rep.Added} replaced={rep.Replaced} (want 1/1)", rep.Added == 1 && rep.Replaced == 1);
            Count("install: exactly one live assets folder", assetsCount == 1);
            Count("install: mod file overwrote vanilla a.bin", Read(Path.Combine(assets, "levels", "a.bin")) == "MOD");
            Count("install: added file present", Read(Path.Combine(assets, "levels", "new.xml")) == "NEW");
            Count("install: untouched vanilla keep.png survived", Read(Path.Combine(assets, "graphics", "keep.png")) == "KEEP");
            Count("install: active name is the mod", ModInstall.ActiveName(root) == "Test Mod");

            // Switch back to vanilla: a.bin is original again, mod preserved for a later swap.
            ModInstall.RevertVanilla(root);
            Count("revert: vanilla a.bin restored", Read(Path.Combine(assets, "levels", "a.bin")) == "AAA");
            Count("revert: new.xml gone (not in vanilla)", !File.Exists(Path.Combine(assets, "levels", "new.xml")));
            Count("revert: active name is VANILLA", ModInstall.ActiveName(root) == "VANILLA");
            var installed = ModInstall.ScanInstalled(root);
            Count($"switch list: mod still installed + selectable ({installed.Count} folders)",
                installed.Any(m => m.Name == "Test Mod") && installed.Any(m => m.IsVanilla));
            Count("switch list: exactly one active", installed.Count(m => m.IsActive) == 1);

            // Re-activate the mod (pure rename now, no re-copy).
            var modFolder = installed.First(m => m.Name == "Test Mod").FolderName;
            ModInstall.Activate(root, modFolder);
            Count("re-activate: mod live again, a.bin=MOD", Read(Path.Combine(assets, "levels", "a.bin")) == "MOD");

            // Locked live assets. A running game keeps files inside its assets folder open, and
            // Windows refuses to rename a directory that has a live handle anywhere in it, so holding
            // one file open here is the same refusal a real install hits. The previous trick — park a
            // child process in the folder as its CWD — only blocked the rename in roughly half of the
            // runs, which made the gate flip between a genuine refusal and a fully successful install
            // and then re-run the lifecycle on top of that state. Installing a different mod must
            // refuse up front, name the lock, leave the folder byte-identical, and complete once the
            // handle closes.
            string lockDir = Path.Combine(root, "_lockgame");
            string lockFile = Path.Combine(lockDir, "assets", "levels", "a.bin");
            Write(lockFile, "AAA");
            string tpmLock = Path.Combine(root, "lockmod.tpm");
            WriteMeta(tpmLock, "Lock Mod", "tb");   // same shape as tpm; content irrelevant to the refusal
            {
                using var hold = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Prove the premise before trusting the refusal: the rename the engine probes must
                // actually be the thing that fails.
                string liveAssets = Path.Combine(lockDir, "assets");
                bool held = false;
                try { Directory.Move(liveAssets, liveAssets + "~wait"); Directory.Move(liveAssets + "~wait", liveAssets); }
                catch (IOException) { held = true; }
                Count("locked: the held file really blocks the assets rename", held);

                bool locked = false; string lockMsg = "";
                try { ModInstall.InstallFromTpm(lockDir, tpmLock, "tb", activate: true); }
                catch (ModInstallException ex) { locked = true; lockMsg = ex.Message; }
                Count("locked: install refuses while the live assets is held open", locked);
                Count($"locked: refusal names the lock ({lockMsg[..Math.Min(40, lockMsg.Length)]}\u2026)",
                    lockMsg.Contains("locked"));
                Count("locked: live assets untouched after refusal",
                    Directory.EnumerateDirectories(lockDir).Count(d =>
                        Path.GetFileName(d).Equals("assets", StringComparison.OrdinalIgnoreCase)) == 1
                    && Read(Path.Combine(lockDir, "assets", "levels", "a.bin")) == "AAA"
                    && !Directory.Exists(lockDir + "~probe")
                    && !Directory.EnumerateFiles(Path.Combine(lockDir, "assets"), ".tpm-rename-probe*").Any());
            }   // the handle closes here; the folder is free again
            // With the lock gone the same install completes (one live assets, mod playing).
            var repLock = ModInstall.InstallFromTpm(lockDir, Path.Combine(root, "lockmod.tpm"), "tb", activate: true);
            Count("locked: install succeeds once the lock releases",
                repLock.Activated && ModInstall.ActiveName(lockDir) == "Lock Mod");
            try { Directory.Delete(lockDir, recursive: true); } catch { }

            // Publish the live mod folder back to a .tpm: only the changed/added files (delta).
            string tpm2 = Path.Combine(root, "roundtrip.tpm");
            int pub = TpmFile.Create(tpm2, assets, new ModMetadata { Name = "Test Mod", Game = "tb" }, deltaFrom: van);
            var payload = TpmFile.ListPayload(tpm2);
            Count($"publish delta: 2 files, keep.png excluded (got {pub}: {string.Join(",", payload)})",
                pub == 2 && payload.Contains("levels/a.bin") && payload.Contains("levels/new.xml")
                && !payload.Contains("graphics/keep.png"));

            // Zip-slip: an entry escaping the assets root must abort the extract with nothing written.
            string slip = Path.Combine(root, "slip.tpm");
            using (var z = ZipFile.Open(slip, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("metadata.json");
                using (var s = e.Open()) { var b = System.Text.Encoding.UTF8.GetBytes(meta.ToJson()); s.Write(b); }
                var evil = z.CreateEntry("assets/../evil.txt");
                using (var s = evil.Open()) { var b = System.Text.Encoding.UTF8.GetBytes("X"); s.Write(b); }
            }
            Directory.CreateDirectory(esc);
            bool threw = false;
            try { TpmFile.ExtractAssets(slip, esc); } catch (IOException) { threw = true; }
            Count("zip-slip: escaping entry refused", threw && !File.Exists(Path.Combine(root, "evil.txt")));

            // Game mismatch: a tb mod refuses to install into a tp2-targeted folder.
            bool refused = false;
            try { ModInstall.InstallFromTpm(root, tpm, "tp2", activate: false); }
            catch (ModInstallException) { refused = true; }
            Count("mismatch: wrong-game install refused", refused);

            // Missing metadata: not a mod file.
            string bogus = Path.Combine(root, "bogus.tpm");
            using (var z = ZipFile.Open(bogus, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
            var (bok, _, berr) = TpmFile.Validate(bogus);
            Count($"no-metadata: rejected (err={berr ?? "none"})", !bok);

            // ---- first install into a base game: exactly one vanilla copy, no parked duplicate ----
            string root3 = Path.Combine(root, "_firstinstall");
            string liveA = Path.Combine(root3, "assets");
            Write(Path.Combine(liveA, "levels", "a.bin"), "AAA");
            Write(Path.Combine(liveA, "graphics", "keep.png"), "KEEP");
            ModInstall.InstallFromTpm(root3, tpm, "tb", activate: true);
            // The live folder now carries the mod; the pristine backup is the renamed original.
            Count("first install: mod applied over the base game",
                Read(Path.Combine(liveA, "levels", "a.bin")) == "MOD"
                && Read(Path.Combine(liveA, "graphics", "keep.png")) == "KEEP");
            // No 'assets-active-*' phantom (the old copy-then-park path left a second vanilla).
            bool noPhantom = !Directory.EnumerateDirectories(root3)
                .Any(d => Path.GetFileName(d).StartsWith("assets-active", StringComparison.OrdinalIgnoreCase));
            // Exactly one pristine vanilla folder on disk.
            int vanillaCopies = Directory.EnumerateDirectories(root3)
                .Count(d => (ModIdentity.Read(d)?.IsVanilla == true)
                            || Path.GetFileName(d).Equals(ModInstall.VanillaFolder, StringComparison.OrdinalIgnoreCase));
            Count($"first install: single vanilla backup, no duplicate (vanillaCopies={vanillaCopies})",
                noPhantom && vanillaCopies == 1);
            Count("first install: exactly one live assets folder",
                Directory.EnumerateDirectories(root3)
                    .Count(d => Path.GetFileName(d).Equals("assets", StringComparison.OrdinalIgnoreCase)) == 1);

            // ---- authoring: a dev folder outside any install, published whole, no vanilla lookup ----
            string proj = Path.Combine(root, "_devproj");
            var project = ModProject.Create(proj, new ModMetadata { Name = "Made Mod", Game = "tb", Version = "1.0", Author = "gate" });
            Count("project: dev folder made with manifest + assets dir",
                File.Exists(Path.Combine(proj, "metadata.json")) && Directory.Exists(Path.Combine(proj, "assets")));
            // A saved level from a managed assets tree mirrors onto its game-relative path.
            string gameLevel = Path.Combine(assets, "levels", "a.bin");
            Count("project: level mirrors to its game-relative path",
                project.TargetFor(gameLevel) == Path.Combine(proj, "assets", "levels", "a.bin"));
            Write(project.TargetFor(gameLevel), "EDIT");
            // A path that nests assets folders (a dev folder's own assets holding a copied game
            // tree) mirrors to the innermost one - never doubles the segment.
            string nested = Path.Combine(proj, "assets", "assets", "editabledirect", "levels", "france1_3.bin");
            Count("project: nested assets path mirrors without doubling assets\\",
                project.TargetFor(nested)
                    == Path.Combine(proj, "assets", "editabledirect", "levels", "france1_3.bin"));
            // A level opened from inside the mod re-saves in place - TargetFor is idempotent.
            string inMod = Path.Combine(proj, "assets", "levels", "a.bin");
            Count("project: a path already inside the mod is its own target",
                project.TargetFor(inMod) == Path.GetFullPath(inMod));
            // A file from outside any managed tree still gets a sane default (levels/<name>).
            Count("project: outside file mirrors under assets/levels",
                Path.GetFileName(project.TargetFor(Path.Combine(root, "stray.bin"))) == "stray.bin"
                && project.TargetFor(Path.Combine(root, "stray.bin")).Contains(Path.DirectorySeparatorChar + "assets" + Path.DirectorySeparatorChar));
            string loose = Path.Combine(root, "extras", "badge.png");
            Write(loose, "BADGE");
            string? addedRel = project.AddAssetFile(loose);
            Count($"project: added asset keeps a usable path (got {addedRel})", addedRel == "badge.png");
            string tpm3 = Path.Combine(root, "published.tpm");
            int packed3 = project.Publish(tpm3);
            var p3 = TpmFile.ListPayload(tpm3);
            Count($"project: publish packs the whole folder (got {packed3}: {string.Join(",", p3)})",
                packed3 == 2 && p3.Contains("levels/a.bin") && p3.Contains("badge.png"));
            var pmeta = TpmFile.ReadMetadata(tpm3);
            Count($"project: published manifest survives the round trip (game={pmeta?.Game})",
                pmeta?.Name == "Made Mod" && pmeta.Game == "tb");
            // The published file is an ordinary import: install it into a second throwaway install
            // (kept separate so the switch/delete tests below keep their own sandbox state) and play.
            string root2 = Path.Combine(root, "_playinstall");
            Write(Path.Combine(root2, "assets", "levels", "a.bin"), "AAA");
            var rep3 = ModInstall.InstallFromTpm(root2, tpm3, "tb", activate: true);
            Count($"project: published mod installs (added={rep3.Added}, replaced={rep3.Replaced})",
                rep3.Added == 1 && rep3.Replaced == 1
                && Read(Path.Combine(root2, "assets", "levels", "a.bin")) == "EDIT"
                && Read(Path.Combine(root2, "assets", "badge.png")) == "BADGE");
            var reopened = ModProject.TryOpen(proj);
            Count("project: folder re-opens from its manifest", reopened?.Meta.Name == "Made Mod");
            bool refusedClobber = false;
            try { ModProject.Create(proj, new ModMetadata { Name = "Other", Game = "tb" }); }
            catch (ModInstallException) { refusedClobber = true; }
            Count("project: refuses to clobber an existing folder", refusedClobber);

            // ---- delete: refuse the live and vanilla folders, remove a stored mod copy for good ----
            bool refusedActive = false;
            try { ModInstall.Delete(root, "assets"); } catch (ModInstallException) { refusedActive = true; }
            Count("delete: refuses the live assets folder", refusedActive);
            bool refusedVan = false;
            try { ModInstall.Delete(root, ModInstall.VanillaFolder); } catch (ModInstallException) { refusedVan = true; }
            Count("delete: refuses the vanilla backup", refusedVan);
            ModInstall.RevertVanilla(root);   // park the active mod so its copy is deletable
            var stored = ModInstall.ScanInstalled(root)
                .FirstOrDefault(m => m.Name == "Test Mod" && !m.IsActive);
            bool deleted = false;
            if (stored is not null)
            {
                ModInstall.Delete(root, stored.FolderName);
                deleted = !Directory.Exists(Path.Combine(root, stored.FolderName));
            }
            Count("delete: removes a stored mod folder", deleted);
            Count("delete: the game still has exactly one live assets folder",
                Directory.EnumerateDirectories(root)
                    .Count(d => Path.GetFileName(d).Equals("assets", StringComparison.OrdinalIgnoreCase)) == 1);
        }
        catch (Exception ex)
        {
            fail++;
            // The message alone was not enough to tell a real regression from a timing flake in this
            // gate's own setup, so the frame that threw goes out too (only ever on a failure).
            Emit($"FAIL mods: {ex.GetType().Name}: {ex.Message}");
            Emit(ex.StackTrace ?? "<no stack>");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Emit("");
        Emit($"MODS: {pass} pass, {fail} fail");
        Emit(fail == 0 ? "MODS GREEN" : "MODS FAILURES");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "<missing>";

    /// <summary>A one-file .tpm (a single added level) built straight from a manifest — used where
    /// only the container shape matters (the locked-assets refusal never reaches the extract).</summary>
    private static void WriteMeta(string tpmPath, string name, string game)
    {
        var m = new ModMetadata { Name = name, Game = game, Version = "1.0", Author = "gate" };
        string s = Path.Combine(Path.GetTempPath(), "tpe_meta_" + Guid.NewGuid().ToString("N"));
        Write(Path.Combine(s, "levels", "lock.xml"), "L");
        TpmFile.Create(tpmPath, s, m);
        try { Directory.Delete(s, recursive: true); } catch { }
    }
}
