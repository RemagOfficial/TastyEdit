using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TastyEditor.App.Assets;
using TastyEditor.App.Editing;
using TastyEditor.App.Panels;
using TastyEditor.App.Viewport;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App;

/// <summary>The slice-3 shell: Open… loads a level of any supported game (detection and
/// decode live in Core), the viewport renders + navigates it, a levels dock toggles
/// per-level visibility, an inspector edits the selected item's position, and Save writes
/// the model back through the byte-faithful codec with a live edit-localisation + re-decode
/// report. Still code-only (no XAML) for a deterministic offline build.</summary>
public sealed class MainWindow : Window
{
    /// <summary>The product name in the title bar (S-D10: the editor is TastyEdit, the name carried
    /// through the Python prototypes it replaces). The dirty marker is appended to this everywhere.
    /// The build's version rides along (beta): a screenshot of a crash now names the build it came
    /// from, which is the only version reporting a pre-release editor has.</summary>
    private static readonly string BaseTitle = BuildBaseTitle();

    private static string BuildBaseTitle()
    {
        var info = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // The SDK can suffix "+<sha>" once the folder is a git repo; the user only cares about the version.
        string v = (info ?? "").Split('+')[0].Trim();
        return v.Length == 0 ? "TastyEdit" : $"TastyEdit {v}";
    }

    private readonly ViewportHost _viewport = new();
    private readonly LayersPanel _layers = new();
    private readonly InspectorPanel _inspector = new();
    private readonly AssetBrowserPanel _browser = new();
    private TabControl? _tabs;
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4) };

    private LevelDoc? _doc;
    private ISpriteSource? _spriteSource;
    private string _baseStatus = "";
    // Entity TYPE catalogue for the open level's install (S-D9): the type names its shipped levels
    // use, i.e. the ones the game demonstrably has a definition for. An entity set to anything else
    // is parsed from the file and then dropped by the loader, so it vanishes in game.
    private EntityCatalogue _entityDefs = EntityCatalogue.Empty;
    private static readonly Dictionary<string, EntityCatalogue> _defCatalog = new(StringComparer.OrdinalIgnoreCase);
    private EditClipboard? _clipboard;      // slice 6 copy/paste (single-slot, model-independent)
    private readonly HistoryService _history = new();   // slice 7 undo/redo (byte-snapshot stack)
    private readonly RecentFiles _recent = new();        // slice 7 recent-files menu (persisted)
    private MenuItem? _undoItem, _redoItem;
    private MenuItem? _recentRoot;                        // "Open recent" submenu, rebuilt on demand
    private MenuItem? _assocItem;                         // .bin association toggle, label reflects state
    private MenuItem? _alwaysSpritesItem;                 // S-D14 sprite-at-any-size view toggle

    // Busy overlay shown while a level decodes on a worker thread. The f4/f5 entity-block
    // tiling oracle can take a long time on very large levels; without this the window
    // would report "Not Responding" and look like it had crashed.
    private readonly TextBlock _overlayText = new()
    {
        FontSize = 16,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Border _overlay = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
        Visibility = Visibility.Collapsed,
    };
    private bool _loading;

    public MainWindow(string? startupPath = null)
    {
        Title = BaseTitle;
        // Explorer and the taskbar take the icon embedded in the apphost (<ApplicationIcon>); the
        // window itself is pointed at the same bitmap from the resource table so the title bar can
        // never disagree with it. Losing that lookup is cosmetic only, so it must not kill a load.
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }
        Width = 1280;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var open = new MenuItem { Header = "_Open level\u2026" };
        open.Click += (_, _) => OpenLevel();
        var save = new MenuItem { Header = "_Save\u2026", InputGestureText = "Ctrl+S" };
        save.Click += (_, _) => SaveLevel();
        var saveHere = new MenuItem { Header = "Save (in place)\u2026", InputGestureText = "Ctrl+Shift+S" };
        saveHere.Click += (_, _) => SaveInPlace();
        _recentRoot = new MenuItem { Header = "Open _recent" };
        _recentRoot.SubmenuOpened += (_, _) => RebuildRecentMenu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(open);
        file.Items.Add(_recentRoot);
        file.Items.Add(new Separator());
        file.Items.Add(save);
        file.Items.Add(saveHere);
        file.Items.Add(new Separator());
        _assocItem = new MenuItem { Header = "Register .bin association\u2026" };
        _assocItem.Click += (_, _) => RegisterFileAssociation();
        file.Items.Add(_assocItem);
        RefreshAssocLabel();
        var fit = new MenuItem { Header = "_Fit" };
        fit.Click += (_, _) => _viewport.FitToView();
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(fit);
        // S-D14: decorations/entities whose sprite shrinks to a couple of pixels on screen used to
        // degrade to cross markers. Measured on the largest shipped levels the real blits are cheap
        // enough to always draw, so the degrade is a toggle (persisted) instead of a hard rule.
        // Records whose artwork can't be resolved still draw as crosses either way.
        _alwaysSpritesItem = new MenuItem { Header = "Always draw sprites", IsCheckable = true, IsChecked = _viewport.AlwaysSprites };
        _alwaysSpritesItem.Click += (_, _) => { _viewport.AlwaysSprites = _alwaysSpritesItem.IsChecked; _recent.AlwaysSprites = _alwaysSpritesItem.IsChecked; };
        view.Items.Add(_alwaysSpritesItem);

        // Slice-6 object mutations + onion-skin, mirrored from the keyboard and the viewport's
        // right-click menu so there are three equivalent entry points for each command.
        var miCopy = new MenuItem { Header = "_Copy", InputGestureText = "Ctrl+C" };
        miCopy.Click += (_, _) => CopySelection();
        var miPaste = new MenuItem { Header = "_Paste", InputGestureText = "Ctrl+V" };
        miPaste.Click += (_, _) => PasteClipboard();
        var miDup = new MenuItem { Header = "_Duplicate", InputGestureText = "Ctrl+D" };
        miDup.Click += (_, _) => DuplicateSelection();
        var miDel = new MenuItem { Header = "_Delete", InputGestureText = "Del" };
        miDel.Click += (_, _) => DeleteSelection();
        var edit = new MenuItem { Header = "_Edit" };
        var miUndo = new MenuItem { Header = "_Undo", InputGestureText = "Ctrl+Z" };
        miUndo.Click += (_, _) => Undo();
        var miRedo = new MenuItem { Header="_Redo", InputGestureText = "Ctrl+Y" };
        miRedo.Click += (_, _) => Redo();
        _undoItem = miUndo;
        _redoItem = miRedo;
        edit.Items.Add(miUndo);
        edit.Items.Add(miRedo);
        edit.Items.Add(new Separator());
        edit.Items.Add(miCopy);
        edit.Items.Add(miPaste);
        edit.Items.Add(miDup);
        edit.Items.Add(miDel);
        edit.Items.Add(new Separator());
        // S-D10: spawn an entity that does not exist in the level yet. It is a menu item as well as a
        // right-click command because the two differ only in where the object lands (view centre vs
        // the cursor) - the rule about which types are legal is the same one the inspector uses.
        var miNew = new MenuItem { Header = "New entity\u2026", InputGestureText = "Ctrl+Shift+N" };
        miNew.Click += (_, _) => AddEntity(_viewport.ViewCentreWorld);
        edit.Items.Add(miNew);
        edit.Items.Add(new Separator());
        var miMulti = new MenuItem { Header = "Multilevel / Goals\u2026" };
        miMulti.Click += (_, _) => OpenMultiLevel();
        edit.Items.Add(miMulti);

        var miOnion = new MenuItem { Header = "Load _onion-skin\u2026" };
        miOnion.Click += (_, _) => LoadOnion();
        var miOnionClear = new MenuItem { Header = "Clear onion-skin" };
        miOnionClear.Click += (_, _) => { _viewport.SetOnion(null); SetStatus("onion-skin cleared"); };
        view.Items.Add(new Separator());
        view.Items.Add(miOnion);
        view.Items.Add(miOnionClear);

        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);

        // Right dock: a tab control switching between the level tools (levels list +
        // inspector, scrollable) and the S5-4 asset browser. Kept in one column so the
        // viewport stays dominant; the browser builds its thumbnails only when first shown.
        var side = new StackPanel();
        side.Children.Add(_layers);
        side.Children.Add(new Separator { Margin = new Thickness(4) });
        side.Children.Add(_inspector);
        var levelTab = new ScrollViewer
        {
            Content = side,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var tabs = new TabControl();
        _tabs = tabs;
        tabs.Items.Add(new TabItem { Header = "Level", Content = levelTab });
        tabs.Items.Add(new TabItem { Header = "Assets", Content = _browser });
        tabs.SelectionChanged += (_, _) =>
        {
            if ((tabs.SelectedItem as TabItem)?.Header as string == "Assets")
                _browser.RefreshIfVisible();
        };
        var sideHost = new Border
        {
            Child = tabs,
            Width = 320,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 63, 70)),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_viewport, 0);
        Grid.SetColumn(sideHost, 1);
        mainGrid.Children.Add(_viewport);
        mainGrid.Children.Add(sideHost);

        var statusBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 43, 50)),
            Child = _status,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        dock.Children.Add(menu);
        dock.Children.Add(statusBorder);
        dock.Children.Add(mainGrid);
        _overlay.Child = _overlayText;
        var root = new Grid();
        root.Children.Add(dock);
        root.Children.Add(_overlay);
        Content = root;

        // Viewport ⇄ panels wiring.
        _viewport.SelectionChanged += OnSelection;
        _viewport.Edited += OnEdited;
        _viewport.ContextMenuRequested += ShowContextMenu;
        _layers.VisibilityChanged += () => _viewport.SetHiddenLayers(_layers.HiddenLayers);
        _inspector.RequestNext = () => _viewport.SelectNextMovable();
        _inspector.ApplyPos = (x, y) => _viewport.ApplySelectionPosition(x, y);
        _inspector.ApplyRotation = deg => _viewport.ApplySelectionRotation(deg);
        _inspector.ApplyFlip = (fx, fy) => ApplyFlip(fx, fy);
        _inspector.ApplySprite = ApplySpriteToSelection;
        // TP2 field parity (D7): a scalar / colour / follow-path write lands in the model through
        // SceneProjector, then redraws + marks dirty + commits one coalesced undo step. These
        // deliberately do NOT re-Show the inspector (that would rebuild the box mid-type).
        _inspector.ApplyTp2Scalar = ApplyTp2Field;
        _inspector.ApplyTp2Color = ApplyTp2Color;
        _inspector.ApplyTp2Path = ApplyTp2Path;
        _inspector.Tp2PathCandidates = Tp2PathNames;
        // Named behaviour-tail field edit: an in-place write at the field's tail-relative offset,
        // routed by the field's kind through the facade (which clamps it to the record's own span).
        _inspector.ApplyTailField = ApplyTailField;
        // Structural behaviour edits: a block's on/off gate, an embedded engine name, the cell-colour
        // count. Each resizes the record, so it is its own undo step and rebuilds the inspector.
        _inspector.ApplyTailGate = (f, on) => ApplyTailStruct(f, on);
        _inspector.ApplyTailString = (f, s) => ApplyTailStruct(f, s);
        _inspector.ApplyTailCount = (f, n) => ApplyTailStruct(f, n);
        // A water field write already landed in the model; just redraw the rect + mark dirty.
        // Deliberately does NOT re-Show the inspector (that would rebuild the box mid-type).
        _inspector.WaterChanged = () =>
        {
            if (_doc is null) return;
            _doc.Dirty = true;
            Title = BaseTitle + " *";
            _viewport.SyncSelectionGeometry();
            // Each keystroke is its own model write; coalesce a typing burst into one undo step.
            _history.Commit(_doc, "water", 2.0);
            UpdateHistoryUI();
        };

        PreviewKeyDown += OnPreviewKeyDown;

        // Slice-7 undo grouping: starting a press/drag closes the previous coalesced burst so a
        // new gesture is always its own undo step.
        _viewport.DragGestureStarted += () => _history.CloseBurst();

        // Slice-7 drag-and-drop: dropping a .bin anywhere on the window opens it.
        AllowDrop = true;
        Drop += OnWindowDrop;
        DragEnter += OnWindowDragEnter;
        DragOver += OnWindowDragOver;

        _recent.Load();
        RebuildRecentMenu();
        // View options live in the same config file, so they are applied once the config is read.
        _viewport.AlwaysSprites = _recent.AlwaysSprites;
        if (_alwaysSpritesItem is not null) _alwaysSpritesItem.IsChecked = _recent.AlwaysSprites;

        SetStatus("Open a .bin level (TP2 / Tasty Blue / Forever / Final Bite), or drag one onto this window.");

        // Slice-7 command-line / double-click open: queue once the window is laid out so the
        // dialogs, overlay and async continuation all have a live UI to attach to.
        if (!string.IsNullOrWhiteSpace(startupPath))
            Loaded += (_, _) => _ = OpenPathAsync(startupPath!);

        // S-D10 renamed the product, and with it the executable, so a .bin handler written by an earlier
        // build now launches a different (and possibly deleted) program than the one the user is using.
        // Ask once, after the window has settled, whether to move it: the editor never rewrites the
        // registry on its own, because the association is the user's preference.
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(CheckStaleAssociation));
    }

    /// <summary>Offer to move a stale per-user .bin handler onto this executable (S-D10 rename).</summary>
    private void CheckStaleAssociation()
    {
        string? why = FileAssociation.StaleReason();
        if (why is null) return;
        var exe = Environment.ProcessPath ?? "";
        if (MessageBox.Show(this,
                $"Your .bin files are set to open with an editor setup where {why}, so double-clicking a "
                + "level does not reach this build.\n\n"
                + $"Open level files with this build instead?\n\n{exe}\n\n"
                + "This writes only per-user (HKCU) registry keys — no admin rights, reversible from "
                + "this same menu — and it never touches game files.",
                "Update .bin association", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            SetStatus(".bin association left as it is — File ▸ Register .bin association… can move it later");
            return;
        }
        try
        {
            FileAssociation.Register(exe);
            RefreshAssocLabel();
            // Say so if the write did not take (a policy-blocking registry, a roaming profile quirk) —
            // otherwise the next launch asks the same question and it looks like the app is nagging.
            string? still = FileAssociation.StaleReason();
            SetStatus(still is null ? $".bin association updated for TastyEdit (was: {why})"
                : $".bin association was not fixed ({still}) — Windows may be blocking the per-user write");
        }
        catch (Exception ex)
        {
            ErrorUi.Show(this, "Could not update the association", null, ex);
        }
    }

    private async void OpenLevel()
    {
        if (_loading) return;
        if (_doc?.Dirty == true &&
            MessageBox.Show(this, "Discard unsaved changes?", "Open level",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var dlg = new OpenFileDialog
        {
            Filter = "Level files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Open level",
            InitialDirectory = _recent.LastDirectory,
        };
        if (dlg.ShowDialog(this) != true) return;
        await OpenPathAsync(dlg.FileName);
    }

    /// <summary>Load one level file (dialog, recent list, drag-drop and command-line all funnel
    /// here). Decodes on a worker thread — the f4/f5 tiling oracle and the asset resolver are
    /// pure and UI-free — then wires the document into every panel and seeds the undo history
    /// from the on-disk bytes (state 0). Failures are reported with the file name and never
    /// leave a half-installed document behind.</summary>
    private async System.Threading.Tasks.Task OpenPathAsync(string path)
    {
        if (_loading) return;
        if (_doc?.Dirty == true &&
            MessageBox.Show(this, "Discard unsaved changes?", "Open level",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show(this, $"The file no longer exists:\n{path}", "Open level",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _loading = true;
        _overlayText.Text = $"Loading {System.IO.Path.GetFileName(path)}…";
        _overlay.Visibility = Visibility.Visible;
        try
        {
            var (doc, sprites, defs) = await System.Threading.Tasks.Task.Run(
                () => { var d = LevelLoader.Load(path);
                        return (d, BuildSprites(path, d.Profile.Key), EntityCatalog(path, d.Profile.Decode)); });

            _doc = doc;
            _entityDefs = defs;
            _viewport.SetLevel(doc);
            _viewport.SetSpriteSource(sprites);
            _browser.SetSource(sprites);
            _spriteSource = sprites;
            bool tp2 = doc.Profile.Key == "tp2";
            // Sprite reassignment (entity + decoration) is now offered for every family
            // (Tp2Edit / TbEdit / ElemEdit, S5-5 decos + S5-6 entities). TP2 entities use the
            // curated, definition-checked dropdown below; Tasty Blue / ElementEngine entities and
            // all decorations use the free-text atlas-cell list here (their names are validated
            // for byte-credibility inside the edit call, not against a curated catalogue).
            _inspector.SpritesEditable = true;
            _inspector.SetSpriteCandidates(sprites is not null
                ? (tp2 ? sprites.CellNames.Concat(sprites.AnimNames) : sprites.CellNames)
                : null);
            // Entity reassignment is constrained to names that carry a real definition (the
            // curated catalogue) plus any type already used in this level; an arbitrary sprite
            // (e.g. the bonus-level button) has no entity definition and crashes the game.
            if (tp2 && doc.Model is Tp2Level tp2lvl)
            {
                _inspector.EntityCatalogFilterable = false;
                var tp2Defs = new List<Tp2Entities.Def>(Tp2Entities.All);
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in Tp2Entities.All) known.Add(d.Name);
                foreach (var l in tp2lvl.Layers)
                    foreach (var e in l.Entities)
                        if (!string.IsNullOrWhiteSpace(e.Type) && known.Add(e.Type!))
                            tp2Defs.Add(new Tp2Entities.Def(e.Type!, e.Type!, ""));
                _inspector.SetEntityCandidates(tp2Defs);
            }
            else
            {
                // tb / f4 / f5: the harvested definition list (hundreds of names, so it is offered
                // as a searchable combo and re-checked on apply rather than being choice-only).
                _inspector.EntityCatalogFilterable = true;
                _inspector.SetEntityCandidates(defs.Names.Select(n => new Tp2Entities.Def(n, n, "")).ToList());
            }
            if (AssetsTabSelected) _browser.RefreshIfVisible();
            _layers.Build(doc.LayerCount);
            _viewport.SetHiddenLayers(_layers.HiddenLayers);
            _inspector.Show(null);
            _history.Seed(doc);                       // state 0 = the bytes on disk
            _recent.Record(doc.Path);                 // slice 7 recent files
            RebuildRecentMenu();
            UpdateHistoryUI();
            Title = BaseTitle;
            SetStatus($"{doc.GameLabel}  ·  {doc.FileName}  ·  {doc.LayerCount} level(s), {doc.Scene.Count} objects");
        }
        catch (Exception ex)
        {
            // Detection and decode failures both land here: show what was attempted and the
            // most likely cause instead of a bare message (slice 7 error UX).
            ErrorUi.Show(this, "Could not open level", $"File:\n{path}", ex);
            SetStatus("Open failed: " + ex.Message);
        }
        finally
        {
            _overlay.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    private void SaveLevel()
    {
        if (_doc is null) { SetStatus("Nothing to save yet — open a level first."); return; }
        if (!_doc.Dirty) { SetStatus("No changes to save."); return; }

        var dlg = new SaveFileDialog
        {
            Title = "Save level",
            FileName = _doc.FileName,
            Filter = "Level files (*.bin)|*.bin|All files (*.*)|*.*",
            InitialDirectory = _recent.LastDirectory ?? System.IO.Path.GetDirectoryName(_doc.Path),
        };
        if (dlg.ShowDialog(this) != true) return;
        DoSave(dlg.FileName);
    }

    /// <summary>Ctrl+Shift+S: write straight back to the file the level was loaded from (with an
    /// explicit confirm — it overwrites the shipped level), instead of navigating a dialog.</summary>
    private void SaveInPlace()
    {
        if (_doc is null) { SetStatus("Nothing to save yet — open a level first."); return; }
        if (!_doc.Dirty) { SetStatus("No changes to save."); return; }
        if (MessageBox.Show(this,
                $"Overwrite the level file in place?\n{_doc.Path}\n\nThe original bytes are not kept by the editor.",
                "Save in place", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DoSave(_doc.Path);
    }

    private void DoSave(string destPath)
    {
        if (_doc is null) return;
        try
        {
            var report = _doc.SaveTo(destPath);
            Title = BaseTitle;
            _recent.Record(destPath);
            RebuildRecentMenu();
            // The written bytes are what Save produces now: record them as the current history
            // state so undo-after-save steps back through the edit list, not past the save.
            _history.Commit(_doc);
            UpdateHistoryUI();
            SetStatus(report.Summarize());
            if (!report.Stable)
                MessageBox.Show(this,
                    "The saved file did not re-decode to identical bytes. This edit is " +
                    "unexpected — report it (the level on disk may differ from the model).",
                    "Save stability warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // I/O errors (locked file, read-only folder, no permission) are the common case here.
            ErrorUi.Show(this, "Could not save level", $"Target:\n{destPath}", ex);
            SetStatus("Save failed: " + ex.Message);
        }
    }

    /// <summary>Build the level's sprite bridge from its asset tree, dispatching on the
    /// detected format. ElementEngine (tb/f4/f5) resolves through <see cref="AssetsRoot"/>
    /// (built/graphics + editabledirect); TP2 (Back for Seconds) resolves through
    /// <see cref="Tp2AssetsRoot"/> (graphics + parent tables) and additionally tints sprites by
    /// each record's colour. When no usable asset tree is found (a repo sample or an
    /// incomplete install) this returns null and the viewport keeps drawing crosses — no crash,
    /// no regression.</summary>
    private static ISpriteSource? BuildSprites(string levelPath, string profileKey)
    {
        try
        {
            if (profileKey == "tp2")
            {
                var troot = Tp2AssetsRoot.ForLevel(levelPath);
                var tres = troot is null ? null : Tp2SpriteResolver.TryLoad(troot);
                return tres is null ? null : new Tp2SpriteSource(tres);
            }
            var root = AssetsRoot.ForLevel(levelPath);
            var res = root is null ? null : SpriteResolver.TryLoad(root);
            return res is null ? null : new SpriteSource(res);
        }
        catch { return null; }
    }

    /// <summary>Sprite reassignment (S5-5 decos, S5-6 entities). Writes the chosen name into the
    /// selected record through the codec-proven path for its family, updates the live marker so the
    /// viewport redraws the new sprite, and marks the document dirty. The save path then re-encodes
    /// the whole model and proves the byte edit is stable.
    ///
    /// <para>TP2 writes a typed model field (entity prefab / deco tile index). Tasty Blue rewrites
    /// an entity's leading length-prefixed name in its per-record <c>Raw</c>, and a decoration's
    /// fixed sprite head (<see cref="TbEdit"/>). The ElementEngine games (Forever / Final Bite)
    /// rewrite a decoration head in place, or splice an entity's variable-length name inside the
    /// section's opaque block (<see cref="ElemEdit"/>) — the latter resizes the block, so the scene
    /// is re-projected afterwards to refresh the now-stale handles.</para></summary>
    private void ApplySpriteToSelection(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;

        // A TP2 entity must reference a name that has a real definition; a bare sprite (e.g. the
        // bonus-level button) would crash the game. Decorations are validated inside their own edit
        // call instead (they name an atlas cell, which is a different namespace entirely).
        if (cr.Source is Tp2Entity && _doc.Model is Tp2Level tp2
            && !Tp2Entities.IsEntity(name) && !LevelHasEntityType(tp2, name))
        {
            SetStatus($"\u201c{name}\u201d has no entity definition \u2014 not applied");
            return;
        }

        // S-D9: the same rule on ElementEngine/Tasty Blue entities, and it is stricter than it looks —
        // the loader does not crash on an unknown type, it silently drops the entity (see EntityDefs).
        // So a type the harvested catalogue has never seen, and that no entity in this level already
        // carries, would leave the file looking right in the editor and empty in the game.
        if (cr.Source is TbEntity or EntityEditHandle
            && _entityDefs.Count >= 8
            && !_entityDefs.Knows(name) && !LevelHasEntityType(_doc.Model, name))
        {
            SetStatus($"\u201c{name}\u201d has no entity definition in this game \u2014 the level would " +
                      $"load without it (not applied). Try: {ClosestDefs(name)}");
            return;
        }

        // An f4/f5 entity edit splices a variable-length name inside the section's opaque block,
        // resizing it and shifting every later record's byte offset — the projected handles go
        // stale, so the scene must be re-projected. tb / TP2 edits are in-place.
        bool elemEntity = cr.Source is EntityEditHandle;

        bool ok = (_doc.Model, cr.Source) switch
        {
            (Tp2Level, Tp2Entity e)            => Tp2Edit.SetEntitySprite(e, name),
            (Tp2Level tp, Tp2Deco d)           => Tp2Edit.SetDecoSprite(tp, d, name),
            (TbLevel tb, TbEntity e)           => TbEdit.SetEntitySprite(tb, e, name),
            (TbLevel tb, TbDeco d)             => TbEdit.SetDecoSprite(tb, d, name),
            (ElementLevel, EntityEditHandle h) => ElemEdit.SetEntitySprite(h, name),
            (ElementLevel, DecoEditHandle h)   => ElemEdit.SetDecoSprite(h, name),
            _ => false,
        };
        if (!ok)
        {
            SetStatus($"\u201c{name}\u201d cannot be applied to this object");
            return;
        }

        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _history.Commit(_doc, "sprite");
        UpdateHistoryUI();

        if (elemEntity)
        {
            // Re-walk the resized block and keep the edited entity selected (its world position is
            // unchanged — only its byte offset moved), so viewport and inspector stay in sync.
            var at = cr.At;
            int layer = cr.Layer;
            _doc.Reproject();
            _viewport.SetScene(_doc.Scene);
            _viewport.SelectNearestCross(SceneKind.Entity, layer, at);
            _inspector.Show(_viewport.SelectedElement);
        }
        else
        {
            cr.SpriteName = name;                 // render input only; the save reads the model
            _viewport.InvalidateVisual();
            _inspector.Show(cr);
        }
        SetStatus($"sprite → “{name}” (save to write)");
    }

    /// <summary>The install's entity TYPE catalogue (the definition names its shipped levels use),
    /// harvested once per levels folder and kept for the session — a whole game's corpus scans in
    /// well under a second (see <see cref="EntityDefs"/> and the <c>entitytypes</c> gate). The
    /// install's own levels folder is used when the asset root resolves (so a modded copy saved
    /// somewhere else still gets the full catalogue); otherwise the level's own folder. A folder
    /// holding only one or two levels proves barely anything, so an obviously thin result is not
    /// treated as authority (the apply-side guard stands down under 8 names).</summary>
    private static EntityCatalogue EntityCatalog(string levelPath, Func<byte[], object> decode)
    {
        string levelsDir = InstallLevelsDir(levelPath)
                       ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(levelPath))
                       ?? "";
        if (levelsDir.Length == 0) return EntityCatalogue.Empty;
        lock (_defCatalog)
        {
            if (_defCatalog.TryGetValue(levelsDir, out var hit)) return hit;
        }
        var got = EntityDefs.Harvest(levelsDir, decode);
        lock (_defCatalog) { _defCatalog[levelsDir] = got; }
        return got;
    }

    /// <summary><c>assets/editabledirect/levels</c> for the install a level belongs to, or null when
    /// there is no such folder (a loose copy of a level file, a TP2-style layout).</summary>
    private static string? InstallLevelsDir(string levelPath)
    {
        try
        {
            var aroot = AssetsRoot.ForLevel(levelPath);
            if (aroot is null) return null;
            var d = System.IO.Path.Combine(aroot.EditableDir, "levels");
            return System.IO.Directory.Exists(d) ? d : null;
        }
        catch { return null; }
    }

    /// <summary>True when this type already appears on an entity in the level, so reusing it is
    /// safe even if a catalogue missed it (e.g. brain_future1).</summary>
    private static bool LevelHasEntityType(object model, string name) =>
        EntityDefs.Names(model).Any(n => n == name);

    /// <summary>A few catalogue names that share the rejected name's species prefix, for a status
    /// message that points at something usable instead of just saying “no” (the suggestion rule
    /// itself lives with the catalogue, since the spawn path refuses for the same reason).</summary>
    private string ClosestDefs(string name) => string.Join(", ", _entityDefs.Nearest(name));

    // ------------------------------------------------ TP2 field parity (D7) --
    //
    // Each handler writes a typed TP2 field through SceneProjector (the codec re-serialises it,
    // so the edit is length-stable), refreshes the projected cross's render inputs, then commits
    // a coalesced undo step. No re-Show: the inspector already updated its own widgets in place.

    private void ApplyTp2Field(string key, double v)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;
        if (!SceneProjector.FieldSet(cr.Source, key, v)) return;
        // A priority edit is a DRAW-ORDER edit: the record's z (and, on the chained raw-span families,
        // the z of every record after it) moves, so the scene has to be re-projected rather than
        // refreshed in place. Anything else (mass, size, rotation) only changes the sprite, so
        // RefreshRender keeps the viewport live without a rebuild.
        if (key is "prio" or "dprio")
        {
            var at = cr.At;
            int layer = cr.Layer;
            SceneKind kind = cr.Kind;
            _doc.Reproject();
            _viewport.SetScene(_doc.Scene);
            _viewport.SelectNearestCross(kind, layer, at);
            _inspector.Show(_viewport.SelectedElement);
        }
        else SceneProjector.RefreshRender(cr);
        CommitTp2();
    }

    private void ApplyTp2Color(byte r, byte g, byte b, byte a)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;
        if (!SceneProjector.FieldColorSet(cr.Source, r, g, b, a)) return;
        SceneProjector.RefreshRender(cr);   // render inputs; the save reads the model
        CommitTp2();
    }

    /// <summary>Write a decoded NAMED behaviour-tail field of the selected entity in place. The facade
    /// dispatches on the field kind (int / f64 / f32 / bool) and re-checks the record's span bounds, so
    /// it never resizes the record or bleeds into the next one. The tail doesn't feed the drawn cross,
    /// so no RefreshRender — just dirty + commit (coalesced) so it undoes with Ctrl+Z.</summary>
    private void ApplyTailField(TailField f, double v)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;
        if (!SceneProjector.FieldTailSet(cr.Source, f, v)) return;
        CommitTp2();
    }

    /// <summary>Apply a STRUCTURAL behaviour-tail edit to the selected entity: turn one of its behaviour
    /// blocks on or off, rename the path / defined area / point list / scriptable / linked entity it
    /// names, or resize its cell-colour ramp. The facade rebuilds the record's tail and only commits it
    /// once the framing oracle still accepts the file, so a refused change leaves every byte untouched
    /// and the panel simply snaps back.
    ///
    /// <para>Unlike a scalar write this changes the record's byte length: in an Forever / Final Bite
    /// entity block that shifts every later record's offset, so the scene is re-projected and the
    /// selection re-found by position (the entity does not move — its behaviour changed). The inspector
    /// rows are rebuilt either way, because enabling a block makes its fields exist and disabling one
    /// takes them away.</para></summary>
    private void ApplyTailStruct(TailField f, object value)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;

        if (!SceneProjector.FieldTailReshape(cr.Source, f, value))
        {
            // The guarded write committed nothing, so say what the game's loader objected to rather than
            // letting the row snap back silently.
            string why = SceneProjector.FieldTailWhy(cr.Source, f, value);
            SetStatus($"\u201c{f.Group} \u00b7 {f.Name}\u201d stays as it is: "
                + (why.Length > 0 ? why : "this change can't be made on this object"));
            _inspector.InvalidateTail();
            _inspector.Show(cr);
            return;
        }

        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _history.Commit(_doc, "behaviour");      // its own undo step — a resize is not a typing burst
        UpdateHistoryUI();

        string what = value switch
        {
            bool on => on ? $"{f.Group} enabled" : $"{f.Group} disabled",
            string => $"{f.Name} renamed",
            _ => $"{f.Name} resized",
        };
        if (cr.Source is EntityEditHandle)
        {
            var at = cr.At;
            int layer = cr.Layer;
            _doc.Reproject();
            _viewport.SetScene(_doc.Scene);
            _viewport.SelectNearestCross(SceneKind.Entity, layer, at);
            _inspector.InvalidateTail();
            _inspector.Show(_viewport.SelectedElement);
        }
        else
        {
            _viewport.InvalidateVisual();
            _inspector.InvalidateTail();
            _inspector.Show(cr);
        }
        SetStatus($"{what} (save to write)");
    }

    private void ApplyTp2Path(string? name)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is not Tp2Entity e) return;
        if (!SceneProjector.Tp2PathSet(e, _doc.Model as Tp2Level, cr.Layer, name)) return;
        if (SceneProjector.GetPos(e) is PointD p) cr.At = p;   // following a path respawns at its start
        CommitTp2();
    }

    /// <summary>Path names in the selected element's layer (an entity can only follow a path
    /// that exists on its own layer, matching tp2editor._layer_paths).</summary>
    private string[] Tp2PathNames(ISceneElem el)
    {
        if (_doc?.Model is not Tp2Level tp) return System.Array.Empty<string>();
        int li = el.Layer;
        if (li < 0 || li >= tp.Layers.Count) return System.Array.Empty<string>();
        var names = new System.Collections.Generic.List<string>();
        foreach (var pth in tp.Layers[li].Paths)
            if (!string.IsNullOrEmpty(pth.PathName)) names.Add(pth.PathName!);
        return names.ToArray();
    }

    private void CommitTp2()
    {
        if (_doc is null) return;
        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _viewport.InvalidateVisual();
        _history.Commit(_doc, "tp2", 2.0);        // coalesce a typing burst into one undo step
        UpdateHistoryUI();
    }

    /// <summary>Write the selected decoration's facing (the two ElementDecoration flip words).
    /// A record that never carried the optional extra block gains it here, and one whose last set
    /// word goes back to zero loses it — both byte-for-byte what ElementDecoration::writeBinary
    /// emits, so an editor-saved level reads identically in game.
    ///
    /// <para>Creating or dropping the block changes the record's length. For a Forever / Final Bite
    /// decoration that means every later record in the layer's opaque section block moves, so the
    /// scene is re-projected and the decoration re-found by position (it does not move — only its
    /// bytes shifted). A Tasty Blue / TP2 record is its own carrier, so nothing else needs redoing.</para></summary>
    private void ApplyFlip(bool fx, bool fy)
    {
        if (_doc is null) return;
        if (_viewport.SelectedElement is not SceneCross cr || cr.Source is null) return;

        object? source = cr.Source;
        if (!SceneProjector.TrySetFlip(source, fx, fy, out bool resized))
        {
            SetStatus("This object has no facing to edit.");
            _inspector.Show(cr);
            return;
        }

        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _history.Commit(_doc, "flip");            // a toggle is its own undo step, not a typing burst
        UpdateHistoryUI();

        if (resized && source is DecoEditHandle)
        {
            var at = cr.At;
            int layer = cr.Layer;
            _doc.Reproject();
            _viewport.SetScene(_doc.Scene);
            _viewport.SelectNearestCross(SceneKind.Deco, layer, at);
            _inspector.Show(_viewport.SelectedElement);
        }
        else
        {
            cr.FlipX = fx; cr.FlipY = fy;         // render input only; the save reads the model
            _viewport.InvalidateVisual();
            _inspector.Show(cr);
        }
    }

    // ------------------------------------------------ slice-6 mutations ----

    /// <summary>Window-level shortcuts. Guarded so a Ctrl/C key typed into an inspector TextBox
    /// (position, sprite name, water fields) is never stolen from the caret, and onion nudges
    /// only fire while a ghost is actually loaded.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.S) { if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) SaveInPlace(); else SaveLevel(); e.Handled = true; return; }
        // Undo/redo fire even outside the doc-null guard below, but need a document; guarded inside.
        if (ctrl && !alt && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && !alt && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (!inText && _doc is not null)
        {
            if (ctrl && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.V) { PasteClipboard(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.D) { DuplicateSelection(); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.N) { AddEntity(_viewport.ViewCentreWorld); e.Handled = true; return; }
            if (!ctrl && !alt && e.Key == Key.Delete) { DeleteSelection(); e.Handled = true; return; }
            // D6 decoration rotation: [ / ] turn the selected deco 1°, Shift 15°. Only fires
            // on a rotatable marker so it never steals those keys elsewhere.
            if (!ctrl && !alt && (e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets)
                && _viewport.SelectedElement is SceneCross csx && SceneProjector.IsRotatable(csx.Source))
            {
                double step = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 15 : 1)
                              * (e.Key == Key.OemOpenBrackets ? -1 : 1);
                if (_viewport.NudgeSelectionRotation(step)) SetStatus($"rotation {step:+0;-0}\u00b0 (save to write)");
                e.Handled = true; return;
            }
        }
        if (_viewport.HasOnion && !inText && shift)
        {
            const double nudge = 10;
            switch (e.Key)
            {
                case Key.Left: _viewport.NudgeOnion(-nudge, 0); e.Handled = true; break;
                case Key.Right: _viewport.NudgeOnion(nudge, 0); e.Handled = true; break;
                case Key.Up: _viewport.NudgeOnion(0, -nudge); e.Handled = true; break;
                case Key.Down: _viewport.NudgeOnion(0, nudge); e.Handled = true; break;
                case Key.Add: _viewport.ScaleOnion(1.1); e.Handled = true; break;
                case Key.Subtract: _viewport.ScaleOnion(1 / 1.1); e.Handled = true; break;
            }
        }
    }

    /// <summary>Every length-changing edit invalidates the projected handles (an f4/f5 block
    /// splice shifts later byte offsets; a chained deco/entity delta moved), so the shared
    /// post-op path re-projects the scene, swaps it into the viewport, re-selects the object the
    /// user just created/moved (by kind + owning layer — the appended record is always last), and
    /// re-shows the inspector on the fresh handle.</summary>
    private void AfterMutation(SceneKind kind, int layer, string status)
    {
        if (_doc is null) return;
        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _history.Commit(_doc);
        _doc.Reproject();
        _viewport.SetScene(_doc.Scene);
        _viewport.SelectLastOfKind(kind, layer);
        _inspector.Show(_viewport.SelectedElement);
        UpdateHistoryUI();
        SetStatus(status);
    }

    /// <summary>The model record behind a projected selection. <c>Source</c> lives on the concrete
    /// SceneCross / ScenePoly (not the ISceneElem interface), so unwrap it here; a SceneLine
    /// (paths / point-lists) has no editable source and returns null, which every EditOps entry
    /// point reports as NothingSelected / not-copyable.</summary>
    private static object? SourceOf(ISceneElem? sel) => sel switch
    {
        SceneCross cr => cr.Source,
        ScenePoly sp => sp.Source,
        _ => null,
    };

    private void CopySelection()
    {
        if (_doc is null) return;
        var src = SourceOf(_viewport.SelectedElement);
        var clip = EditOps.Copy(_doc.Model, src);
        if (clip is null) { SetStatus("copy: selection has no copyable record"); return; }
        _clipboard = clip;
        SetStatus($"copied {clip.Kind} (layer {clip.Layer})");
    }

    private void PasteClipboard()
    {
        if (_doc is null) return;
        if (_clipboard is null) { SetStatus("clipboard is empty"); return; }
        var (kind, layer) = (_clipboard.Kind, _clipboard.Layer);
        var r = EditOps.Paste(_doc.Model, _clipboard);
        if (r != EditResult.Done) { SetStatus($"paste: {Describe(r)}"); return; }
        AfterMutation(MapKind(kind), Math.Clamp(layer, 0, Math.Max(0, _doc.LayerCount - 1)), $"pasted {kind}");
    }

    private void DuplicateSelection()
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("nothing selected to duplicate"); return; }
        var r = EditOps.Duplicate(_doc.Model, SourceOf(sel));
        if (r != EditResult.Done) { SetStatus($"duplicate: {Describe(r)}"); return; }
        AfterMutation(sel.Kind, sel.Layer, $"duplicated {sel.Kind} (+{EditOps.DupOffset} offset)");
    }

    private void DeleteSelection()
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("nothing selected to delete"); return; }
        var r = EditOps.Delete(_doc.Model, SourceOf(sel));
        if (r != EditResult.Done) { SetStatus($"delete: {Describe(r)}"); return; }
        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _history.Commit(_doc);
        _doc.Reproject();
        _viewport.SetScene(_doc.Scene);        // SetScene clears the (now dangling) selection
        _inspector.Show(null);
        UpdateHistoryUI();
        SetStatus($"deleted {sel.Kind}");
    }

    /// <summary>Move the selected record to another level (copy → paste-into-layer → delete
    /// original). The kind/layer for re-selection come from the pre-move selection; the pasted
    /// clone is the last record of that kind in the destination layer.</summary>
    private void MoveSelectionToLayer(int targetLayer)
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("nothing selected to move"); return; }
        var r = EditOps.MoveToLayer(_doc.Model, SourceOf(sel), targetLayer);
        if (r != EditResult.Done) { SetStatus($"move: {Describe(r)}"); return; }
        AfterMutation(sel.Kind, targetLayer, $"moved {sel.Kind} → level {targetLayer + 1}");
    }

    /// <summary>Add a brand-new entity at a world point (S-D10).</summary>
    ///
    /// <para>Everything that decides whether this is legal lives in Core
    /// (<see cref="EntitySpawn.Add"/>): the type has to be one the game has a definition for, and its
    /// record body has to be copied from a real shipped record of that same type, because
    /// <c>readBinary</c> is virtual per prefab. The dialog only offers names it can prove, and the
    /// status line reports which shipped record the body came from, so nothing here silently invents
    /// bytes. After the append the scene is re-projected (an f4/f5 block splice shifts every later
    /// offset) and the new record is selected so the author can immediately edit its fields.</para>
    private void AddEntity(PointD at)
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        int layer = sel is not null && sel.Layer >= 0 ? sel.Layer : 0;
        // Prefill with the selected entity's own type: "add another of these, over there" is the
        // common case, and it is also the surest way to stay inside the legal name set.
        string? prefill = sel is SceneCross sc && sc.Kind == SceneKind.Entity ? sc.SpriteName : null;
        var dlg = new Panels.NewEntityWindow(_entityDefs, _doc.LayerCount, layer, at.X, at.Y, prefill) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var r = EntitySpawn.Add(_doc.Model, dlg.Layer, at.X, at.Y, dlg.TypeName, _entityDefs, out string? detail);
        if (r != EditResult.Done) { SetStatus($"add entity: {(detail ?? Describe(r))}"); return; }
        AfterMutation(SceneKind.Entity, dlg.Layer,
            $"added {dlg.TypeName} on level {dlg.Layer + 1} · {detail}");
    }

    private static SceneKind MapKind(string kind) => kind switch
    {
        "wall" => SceneKind.Wall,
        "area" => SceneKind.Area,
        "water" => SceneKind.Water,
        "entity" => SceneKind.Entity,
        "deco" => SceneKind.Deco,
        _ => SceneKind.Other,
    };

    private static string Describe(EditResult r) => r switch
    {
        EditResult.NothingSelected => "nothing selected",
        EditResult.NotSupported => "not supported for this object/family",
        EditResult.Failed => "refused by the byte-safety gate",
        _ => r.ToString(),
    };

    // ------------------------------------------------------- onion-skin ----

    /// <summary>Load a SECOND level purely as a background reference (projected geometry only,
    /// never a document) so the live level can be traced against it. It may be any supported
    /// game; the ghost is drawn faintly and nudged/scaled with Shift+arrows / Shift++/-.</summary>
    private async void LoadOnion()
    {
        if (_loading) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Level files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Load onion-skin reference level",
        };
        if (dlg.ShowDialog(this) != true) return;
        string path = dlg.FileName;
        _loading = true;
        _overlayText.Text = $"Loading onion {System.IO.Path.GetFileName(path)}…";
        _overlay.Visibility = Visibility.Visible;
        try
        {
            var scene = await System.Threading.Tasks.Task.Run(
                () => SceneProjector.Project(LevelLoader.Load(path).Model));
            _viewport.SetOnion(scene);
            SetStatus($"onion: {System.IO.Path.GetFileName(path)}  ·  Shift+arrows nudge, Shift+ +/- scale");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load onion level",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _overlay.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    // ---------------------------------------------------- context menu -----

    /// <summary>Right-click menu for the current selection: the four mutation commands, a
    /// move-to-level submenu (when the level has more than one), and a jump to the inspector for
    /// sprite/field reassignment. Items the selection cannot do are greyed rather than hidden so
    /// the menu stays spatially stable.</summary>
    private void ShowContextMenu(Point at)
    {
        if (_doc is null) return;
        bool hasSel = _viewport.SelectedElement is not null;
        bool hasClip = _clipboard is not null;
        var cm = new ContextMenu();

        var mUndo = new MenuItem { Header = "Undo", InputGestureText = "Ctrl+Z", IsEnabled = _history.CanUndo };
        mUndo.Click += (_, _) => Undo();
        var mRedo = new MenuItem { Header = "Redo", InputGestureText = "Ctrl+Y", IsEnabled = _history.CanRedo };
        mRedo.Click += (_, _) => Redo();
        cm.Items.Add(mUndo);
        cm.Items.Add(mRedo);
        cm.Items.Add(new Separator());

        var mDup = new MenuItem { Header = "Duplicate", IsEnabled = hasSel };
        mDup.Click += (_, _) => DuplicateSelection();
        var mDel = new MenuItem { Header = "Delete", IsEnabled = hasSel };
        mDel.Click += (_, _) => DeleteSelection();
        var mCopy = new MenuItem { Header = "Copy", IsEnabled = hasSel };
        mCopy.Click += (_, _) => CopySelection();
        var mPaste = new MenuItem { Header = "Paste", IsEnabled = hasClip };
        mPaste.Click += (_, _) => PasteClipboard();
        cm.Items.Add(mDup);
        cm.Items.Add(mDel);
        cm.Items.Add(mCopy);
        cm.Items.Add(mPaste);

        // S-D10: spawn at the point that was clicked, which is the whole reason the command lives
        // here as well as in the Edit menu.
        cm.Items.Add(new Separator());
        var mAdd = new MenuItem { Header = "Add entity here\u2026" };
        mAdd.Click += (_, _) => AddEntity(_viewport.WorldAt(at));
        cm.Items.Add(mAdd);

        if (hasSel && _doc.LayerCount > 1)
        {
            cm.Items.Add(new Separator());
            var move = new MenuItem { Header = "Move to level" };
            for (int li = 0; li < _doc.LayerCount; li++)
            {
                int target = li;
                var mi = new MenuItem { Header = $"Level {li + 1}", IsEnabled = li != _viewport.SelectedElement!.Layer };
                mi.Click += (_, _) => MoveSelectionToLayer(target);
                move.Items.Add(mi);
            }
            cm.Items.Add(move);
        }

        cm.Items.Add(new Separator());
        var insp = new MenuItem { Header = "Edit in inspector ▸", IsEnabled = hasSel };
        insp.Click += (_, _) =>
        {
            _inspector.Show(_viewport.SelectedElement);
            if (_tabs is not null) _tabs.SelectedIndex = 0;
        };
        cm.Items.Add(insp);

        cm.PlacementTarget = _viewport;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
        cm.HorizontalOffset = at.X;
        cm.VerticalOffset = at.Y;
        cm.IsOpen = true;
    }

    /// <summary>Open the multilevel / goals dialog, pre-seeded with the multilevel XMLs that
    /// reference the currently loaded level (so the user picks from a dropdown rather than
    /// browsing). The stage key is the loaded .bin stem; the search folder is the sibling
    /// <c>multilevels</c> directory of the level's own folder. With no level loaded the dialog
    /// still opens and falls back to manual loading.</summary>
    private void OpenMultiLevel()
    {
        string? stageName = _doc is null ? null : System.IO.Path.GetFileNameWithoutExtension(_doc.Path);
        string? mlDir = _doc is null ? null : System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_doc.Path) ?? "", "multilevels");
        var w = new Panels.MultiLevelWindow(stageName, mlDir) { Owner = this };
        w.ShowDialog();
    }

    // ------------------------------------------------- slice-7 undo/redo ----

    /// <summary>Step the document back one recorded state. The snapshot is decoded on a worker
    /// thread (f4/f5 decode is not free) behind the busy overlay, then the live document swaps
    /// model + scene in place — camera, layer visibility and the sprite source all survive
    /// because the profile never changes. The selection cannot survive (every projected handle
    /// is a new object), so it clears.</summary>
    private void Undo()
    {
        if (_doc is null || _loading) return;
        if (!_history.CanUndo) { SetStatus("nothing to undo"); return; }
        Restore(_history.Undo()!, "undo");
    }

    private void Redo()
    {
        if (_doc is null || _loading) return;
        if (!_history.CanRedo) { SetStatus("nothing to redo"); return; }
        Restore(_history.Redo()!, "redo");
    }

    private async void Restore(byte[] snapshot, string what)
    {
        var doc = _doc!;
        _loading = true;
        _overlayText.Text = $"{char.ToUpperInvariant(what[0])}{what[1..]}…";
        _overlay.Visibility = Visibility.Visible;
        try
        {
            // Decode off the UI thread, then apply the result synchronously back here.
            var model = await System.Threading.Tasks.Task.Run(() => doc.Profile.Decode(snapshot));
            doc.ApplyRestoredModel(model);
            _viewport.SetScene(doc.Scene);        // clears the now-stale selection
            _inspector.Show(null);
            Title = doc.Dirty ? BaseTitle + " *" : BaseTitle;
            UpdateHistoryUI();
            SetStatus($"{what}  ·  {doc.Scene.Count} objects  ·  {_history.UndoDepth} step(s) from load");
        }
        catch (Exception ex)
        {
            ErrorUi.Show(this, "Undo/redo failed", "The edit history is still intact; try the other direction.", ex);
        }
        finally
        {
            _overlay.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    /// <summary>Keep the Undo/Redo menu items honest after every state change.</summary>
    private void UpdateHistoryUI()
    {
        if (_undoItem is not null) _undoItem.IsEnabled = _history.CanUndo;
        if (_redoItem is not null) _redoItem.IsEnabled = _history.CanRedo;
    }

    // ------------------------------------------------- slice-7 recent files --

    private void RebuildRecentMenu()
    {
        if (_recentRoot is null) return;
        _recentRoot.Items.Clear();
        _recentRoot.IsEnabled = _recent.Paths.Count > 0;
        foreach (var p in _recent.Paths)
        {
            string path = p;
            bool missing = !System.IO.File.Exists(path);
            var mi = new MenuItem
            {
                Header = System.IO.Path.GetFileNameWithoutExtension(path) + "   (" +
                         System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "") + ")",
                ToolTip = missing ? path + "  (file no longer present)" : path,
                IsEnabled = !missing,
            };
            mi.Click += (_, _) => { _ = OpenPathAsync(path); };
            _recentRoot.Items.Add(mi);
        }
        if (_recent.Paths.Count > 0)
        {
            _recentRoot.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear list" };
            clear.Click += (_, _) => { _recent.Clear(); RebuildRecentMenu(); };
            _recentRoot.Items.Add(clear);
        }
    }

    // ------------------------------------------------ slice-7 drag & drop ----

    private static bool HasLevelData(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        return files.Any(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasLevelData(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e) => OnWindowDragOver(sender, e);

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var bin = files.FirstOrDefault(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        if (bin is null)
        {
            SetStatus("dropped files contain no .bin level — ignoring");
            return;
        }
        _ = OpenPathAsync(bin);   // OpenPathAsync shows the discard prompt / busy overlay itself
    }

    // ---------------------------------------------- slice-7 file associate ---

    /// <summary>Keep the association menu label in sync with the registry state. A handler that names
    /// this editor but is broken (missing ProgId, exe moved) still offers <i>Register</i>, because that
    /// is the action which repairs it — offering "Remove" there would leave the user with nothing to
    /// click after they had said no to the startup prompt.</summary>
    private void RefreshAssocLabel()
    {
        if (_assocItem is null) return;
        _assocItem.Header = FileAssociation.IsRegistered() && !FileAssociation.IsStale()
            ? "Remove .bin association\u2026" : "Register .bin association\u2026";
    }

    private void RegisterFileAssociation()
    {
        var exe = Environment.ProcessPath ?? "";
        // Same rule as the menu label: a handler that names this editor but cannot launch it is treated
        // as "not registered", so this item repairs it instead of offering only to delete it.
        bool registered = FileAssociation.IsRegistered() && !FileAssociation.IsStale();
        if (registered)
        {
            if (MessageBox.Show(this,
                    "This editor is currently set as your .bin opener. Remove that preference?\n\n" +
                    "It only clears the editor's own per-user registry keys (HKCU\\Software\\Classes); " +
                    "nothing machine-wide or game-related is touched.",
                    "Remove .bin association", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            try
            {
                FileAssociation.Unregister();
                RefreshAssocLabel();
                MessageBox.Show(this, "Removed the editor's .bin preference for this user.",
                    "Remove .bin association", MessageBoxButton.OK, MessageBoxImage.Information);
                SetStatus(".bin user association removed");
            }
            catch (Exception ex) { ErrorUi.Show(this, "Could not remove the association", null, ex); }
            return;
        }

        if (MessageBox.Show(this,
                $"Always open .bin files with this editor (for your Windows user account)?\n\n{exe}\n\n" +
                "This writes only per-user (HKCU) registry keys — no admin rights, reversible from " +
                "this same menu or Windows Settings, and it never touches game files.",
                "Register .bin association", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        try
        {
            FileAssociation.Register(exe);
            RefreshAssocLabel();
            MessageBox.Show(this,
                "Done. Double-clicking a .bin in Explorer will now open it in this editor. " +
                "Choose “Register .bin association…” again to undo.",
                "Register .bin association", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus(".bin user association registered");
        }
        catch (Exception ex)
        {
            ErrorUi.Show(this, "Could not register the association", null, ex);
        }
    }

    private bool AssetsTabSelected =>
        (_tabs?.SelectedItem as TabItem)?.Header as string == "Assets";

    private void OnSelection(string description)
    {
        _inspector.Show(_viewport.SelectedElement);
        SetStatus(string.IsNullOrEmpty(description)
            ? _baseStatus
            : $"{_baseStatus}   —   {description}");
    }

    private void OnEdited()
    {
        if (_doc is null) return;
        _doc.Dirty = true;
        Title = BaseTitle + " *";
        _inspector.Show(_viewport.SelectedElement);
        _history.Commit(_doc);                 // drag-move / inspector position write landed
        UpdateHistoryUI();
    }

    private void SetStatus(string text)
    {
        _baseStatus = text;
        _status.Text = text;
    }
}
