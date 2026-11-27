using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public sealed class MainWindow : Window, ITilemapHost
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
    // R5: the Tasty Planet 1 asset resolver for the open level, when its art tree loaded. Its
    // entitydef / tiledef name sets are both the inspector's validated dropdowns and the
    // policing authority for an emitter's object type (an unknown name the game drops).
    private TastyEditor.Core.Assets.Tp1SpriteResolver? _tp1Resolver;
    private static readonly Dictionary<string, EntityCatalogue> _defCatalog = new(StringComparer.OrdinalIgnoreCase);
    private EditClipboard? _clipboard;      // slice 6 copy/paste (single-slot, model-independent)
    private readonly HistoryService _history = new();   // slice 7 undo/redo (byte-snapshot stack)
    private readonly RecentFiles _recent = new();        // slice 7 recent-files menu (persisted)
    private MenuItem? _undoItem, _redoItem;
    private MenuItem? _recentRoot;                        // "Open recent" submenu, rebuilt on demand
    private MenuItem? _assocItem;                         // .bin association toggle, label reflects state
    private MenuItem? _alwaysSpritesItem;                 // S-D14 sprite-at-any-size view toggle
    private MenuItem? _themeItem;                          // R3 dark/light chrome toggle
    private MenuItem? _modItem;                            // R6 top-level Mod menu (authoring)
    // R6 authoring: the mod project the editor currently saves into, when one is open. Mod creation
    // (this dev folder) is deliberately separate from mod loading (Manage mods), so nothing while
    // authoring reads or compares against a game install.
    private TastyEditor.Core.Mods.ModProject? _mod;
    private Button? _undoBtn, _redoBtn;                    // R3 toolbar mirror of the undo/redo state
    // Background tilemap: the single modeless editor window (created on demand) and the signal it
    // listens to so it re-syncs after every committed tp1 edit and after an undo/redo.
    private TilemapWindow? _tilemap;
    public event Action? Changed;
    // R7 entity authoring: the single TP1 entity-types window (opened from the Edit menu on a TP1
    // level) and the per-session "stop warning me" choice for deleting a type that shipped with the game.
    private Tp1EntityWindow? _tp1Entities;
    private bool _tp1SuppressVanillaWarn;
    // R4 creation: every "New <kind>" menu item and toolbar button, so a level load can grey the
    // kinds that this family has no storage for (TP2 areas/water, f4/f5 water) in one place.
    private readonly List<(FrameworkElement Item, SceneKind Kind)> _createGate = new();
    // R5: the two Tasty Planet 1 create commands (spawner / player start). They have no SceneKind of
    // their own (a spawner is an emitter, a start is the goo), so they are greyed on the profile alone.
    private readonly List<FrameworkElement> _tp1OnlyGate = new();
    // The inverse lane: affordances TP1 has no use for (multilevel files, onion-skin .bin references).
    // Hidden on TP1 levels, shown everywhere else — same hide-don't-grey rule as the create gate.
    private readonly List<FrameworkElement> _notTp1Gate = new();
    private Border? _start;                                // R3 start-screen overlay (shown with no level)
    private TextBlock? _dirtyChip;                         // R3 status-bar dirty indicator

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
    private ImageSource? _appIcon;

    public MainWindow(string? startupPath = null, string? startupGame = null)
    {
        MarkClean();
        // Explorer and the taskbar take the icon embedded in the apphost (<ApplicationIcon>); the
        // window itself is pointed at the same bitmap from the resource table so the title bar can
        // never disagree with it. Losing that lookup is cosmetic only, so it must not kill a load.
        // It is kept as the fallback for the family tiles the open level swaps in (R7).
        try { _appIcon = new BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }
        Icon = _appIcon;
        Width = 1280;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var open = new MenuItem { Header = "_Open level\u2026", InputGestureText = "Ctrl+O" };
        open.Click += (_, _) => OpenLevel();
        var startItem = new MenuItem { Header = "_Start screen", InputGestureText = "Ctrl+N" };
        startItem.Click += (_, _) => ShowStartScreen();
        var save = new MenuItem { Header = "_Save\u2026", InputGestureText = "Ctrl+S" };
        save.Click += (_, _) => SaveLevel();
        var saveHere = new MenuItem { Header = "Save (in place)\u2026", InputGestureText = "Ctrl+Shift+S" };
        saveHere.Click += (_, _) => SaveInPlace();
        _recentRoot = new MenuItem { Header = "Open _recent" };
        _recentRoot.SubmenuOpened += (_, _) => RebuildRecentMenu();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(startItem);
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
        var fit = new MenuItem { Header = "_Fit", InputGestureText = "F2" };
        fit.Click += (_, _) => _viewport.FitToView();
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(fit);
        // S-D14: decorations/entities whose sprite shrinks to a couple of pixels on screen used to
        // degrade to cross markers. Measured on the largest shipped levels the real blits are cheap
        // enough to always draw, so the degrade is a toggle (persisted) instead of a hard rule.
        // Records whose artwork can't be resolved still draw as crosses either way.
        _alwaysSpritesItem = new MenuItem { Header = "Always draw sprites", IsCheckable = true, IsChecked = _viewport.AlwaysSprites, InputGestureText = "F4" };
        _alwaysSpritesItem.Click += (_, _) => { _viewport.AlwaysSprites = _alwaysSpritesItem.IsChecked; _recent.AlwaysSprites = _alwaysSpritesItem.IsChecked; };
        view.Items.Add(_alwaysSpritesItem);
        // R3: flip the whole chrome between the dark palette (default) and light. Applied live; the
        // hand-built chrome surfaces follow by resource key, the standard controls re-resolve their styles.
        _themeItem = new MenuItem { Header = "Dark theme", IsCheckable = true, IsChecked = Theme.IsDark };
        _themeItem.Click += (_, _) => SetTheme(_themeItem.IsChecked);
        view.Items.Add(_themeItem);

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
        // R4: create a brand-new object of a supported kind. A submenu groups the shape kinds (each
        // drops a small default at the view centre that the handles + inspector then edit); kinds the
        // open level has no room for are greyed, mirroring the right-click "New here" and toolbar.
        var newShapes = new MenuItem { Header = "New" };
        newShapes.Items.Add(NewShapeItem("Wall", SceneKind.Wall));
        newShapes.Items.Add(NewShapeItem("Defined area", SceneKind.Area));
        newShapes.Items.Add(NewShapeItem("Water", SceneKind.Water));
        newShapes.Items.Add(NewShapeItem("Path", SceneKind.Path));
        newShapes.Items.Add(NewShapeItem("Decoration", SceneKind.Deco));
        // R5: Tasty Planet 1 has none of the shapes above (its objects are spawners, a player start,
        // background tiles and paths), so it offers its own two create commands instead — greyed for
        // every other game.
        newShapes.Items.Add(Tp1Item("Spawner", () => AddTp1Emitter(_viewport.ViewCentreWorld)));
        newShapes.Items.Add(Tp1Item("Player start", () => AddTp1Goo()));
        edit.Items.Add(newShapes);
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
        _notTp1Gate.Add(miMulti);
        // R5 background tilemap: a modeless editor for the level's <tilelayers>. TP1-only (greyed elsewhere).
        edit.Items.Add(Tp1Item("Background tilemap\u2026", () => OpenTilemap()));
        // TP1-only twin of Multilevel / Goals: the level's own <level> attributes, not a viewport element.
        edit.Items.Add(Tp1Item("Level properties\u2026", () => OpenLevelProps()));
        // R7 entity authoring (TP1-only): create / edit / remove the object types an emitter can name,
        // by editing the game's entity + image tables (and the art they point at).
        edit.Items.Add(Tp1Item("Entity types\u2026", () => OpenTp1EntityEditor()));

        var miOnion = new MenuItem { Header = "Load _onion-skin\u2026" };
        miOnion.Click += (_, _) => LoadOnion();
        var miOnionClear = new MenuItem { Header = "Clear onion-skin" };
        miOnionClear.Click += (_, _) => { _viewport.SetOnion(null); SetStatus("onion-skin cleared"); };
        view.Items.Add(new Separator());
        view.Items.Add(miOnion);
        view.Items.Add(miOnionClear);
        _notTp1Gate.Add(miOnion);
        _notTp1Gate.Add(miOnionClear);

        // R6 authoring: the top-level Mod menu. Create makes a development folder outside any game
        // install; while authoring, saves land inside it (no dialog); publish zips the whole folder
        // to a .tpm with no comparison against the game's own files. Loading mods stays in Manage
        // mods (the start screen), so creation and loading never touch each other.
        _modItem = new MenuItem { Header = "_Mod" };
        var miCreate = new MenuItem { Header = "Create mod\u2026" };
        miCreate.Click += (_, _) => OnCreateMod();
        var miAddAsset = new MenuItem { Header = "Add asset to mod\u2026" };
        miAddAsset.Click += (_, _) => OnAddAsset();
        var miPublish = new MenuItem { Header = "Publish mod as .tpm\u2026" };
        miPublish.Click += (_, _) => OnPublishMod();
        var miOpenFolder = new MenuItem { Header = "Open mod folder\u2026" };
        miOpenFolder.Click += (_, _) => OnOpenModFolder();
        var miResume = new MenuItem { Header = "Resume a mod project\u2026" };
        miResume.Click += (_, _) => OnResumeMod();
        var miStop = new MenuItem { Header = "Stop authoring" };
        miStop.Click += (_, _) => OnStopAuthoring();
        _modItem.Items.Add(miCreate);
        _modItem.Items.Add(miResume);
        _modItem.Items.Add(new Separator());
        _modItem.Items.Add(miAddAsset);
        _modItem.Items.Add(miPublish);
        _modItem.Items.Add(miOpenFolder);
        _modItem.Items.Add(new Separator());
        _modItem.Items.Add(miStop);

        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(_modItem);
        menu.Items.Add(view);

        // R3 compact toolbar: icon buttons with tooltips for the actions people actually use. Docked
        // just under the menu; the menu keeps every command plus its keyboard gesture (so the UI still
        // teaches itself) and the tray is only the fast path to the same handlers.
        var tray = BuildToolbar();

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
        var sideHost = new Border { Child = tabs };
        Theme.Ref(sideHost, Border.BackgroundProperty, Theme.KSurface);

        // R3 resizable split between the viewport and the tools dock (the old fixed 320px column).
        var splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Cursor = Cursors.SizeWE,
            ShowsPreview = true,
        };

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 220, MaxWidth = 640 });
        Grid.SetColumn(_viewport, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(sideHost, 2);
        mainGrid.Children.Add(_viewport);
        mainGrid.Children.Add(splitter);
        mainGrid.Children.Add(sideHost);

        // R3 status bar: the game · file · counts line on the left, a dirty chip on the right.
        _dirtyChip = new TextBlock
        {
            Text = "\u25CF  Saved",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0),
        };
        Theme.Ref(_dirtyChip, TextBlock.ForegroundProperty, Theme.KTextMuted);
        var statusHost = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_dirtyChip, Dock.Right);
        statusHost.Children.Add(_dirtyChip);
        statusHost.Children.Add(_status);
        var statusBorder = new Border { Child = statusHost, BorderThickness = new Thickness(0, 1, 0, 0) };
        Theme.Ref(statusBorder, Border.BackgroundProperty, Theme.KSurfaceAlt);
        Theme.Ref(statusBorder, Border.BorderBrushProperty, Theme.KBorder);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(tray, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        dock.Children.Add(menu);
        dock.Children.Add(tray);
        dock.Children.Add(statusBorder);
        dock.Children.Add(mainGrid);
        _overlay.Child = _overlayText;

        // R3 start screen: covers the window until a level is opened (or the user dismisses it).
        _start = BuildStartScreen();

        var root = new Grid();
        root.Children.Add(dock);
        root.Children.Add(_overlay);
        root.Children.Add(_start);
        Content = root;

        // Viewport ⇄ panels wiring.
        _viewport.SelectionChanged += OnSelection;
        _viewport.Edited += OnEdited;
        _viewport.ContextMenuRequested += ShowContextMenu;
        _layers.VisibilityChanged += () => _viewport.SetHiddenLayers(_layers.HiddenLayers);
        // R3: the inspector's collapsible cards read their initial state from, and write changes back
        // to, the persisted config so a collapsed section stays collapsed across sessions.
        _inspector.IsCollapsed = id => _recent.CollapsedSections.Contains(id);
        _inspector.OnCollapse = (id, on) => _recent.SetCollapsed(id, on);
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
        // R5: Tasty Planet 1 object card. An attribute write re-derives a number-vs-text value and
        // touches only that value's bytes; add / remove splice or cut one whole child element. Each
        // is its own undo step, then re-projects (nodes rebuild on every write) and re-shows so the
        // card reads the live tree. The dropdown names come from the loaded asset definitions.
        _inspector.ApplyTp1Attr = ApplyTp1Attr;
        _inspector.ApplyTp1Add = ApplyTp1Add;
        _inspector.ApplyTp1Remove = ApplyTp1Remove;
        _inspector.SetTp1Names(Tp1NameChoices);
        // A water field write already landed in the model; just redraw the rect + mark dirty.
        // Deliberately does NOT re-Show the inspector (that would rebuild the box mid-type).
        _inspector.WaterChanged = () =>
        {
            if (_doc is null) return;
            _doc.Dirty = true;
            MarkDirty();
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

        SetStatus("Open a level file (TP2 / Tasty Blue / Forever / Final Bite / Tasty Planet), or drag one onto this window.");

        // R3: with no level handed in on the command line, the start screen is the first contact; a
        // double-click open skips straight past it (OpenPathAsync hides it once the level lands).
        if (string.IsNullOrWhiteSpace(startupPath)) ShowStartScreen();

        // Slice-7 command-line / double-click open: queue once the window is laid out so the
        // dialogs, overlay and async continuation all have a live UI to attach to.
        if (!string.IsNullOrWhiteSpace(startupPath))
            Loaded += (_, _) =>
            {
                // A .tpm handed in (double-click / open-with) is a mod, not a level: bring up the
                // mod manager and import it straight away instead of trying to decode it.
                if (TastyEditor.Core.Mods.TpmFile.IsTpm(startupPath!))
                    new Panels.ModsWindow(importPath: startupPath!) { Owner = this }.ShowDialog();
                else
                    _ = OpenPathAsync(startupPath!);
            };

        // R7: the installer's per-game Start-Menu shortcuts arrive as "--game tb". Each one goes
        // straight to that game's level folder through the same picker the start screen's
        // "Browse levels…" button uses, so the shortcut is a jump rather than five copies of the
        // same blank window. It fires after the first paint, not during construction, because a
        // modal dialog shown too early lands on top of a window that has not drawn itself yet.
        // An unknown key is ignored — a stale shortcut must still open a working editor.
        if (string.IsNullOrWhiteSpace(startupPath)
            && startupGame is not null && GameRegistry.KnownKeys.Contains(startupGame))
        {
            string game = startupGame;
            ContentRendered += (_, _) => OpenFromGame(game);
        }

        // S-D10 renamed the product, and with it the executable, so a .bin handler written by an earlier
        // build now launches a different (and possibly deleted) program than the one the user is using.
        // Ask once, after the window has settled, whether to move it: the editor never rewrites the
        // registry on its own, because the association is the user's preference.
        // R4 stability: the very first settled launch instead greets the author with what installs were
        // found (folders only, never launches a game); that welcome is shown once, then the
        // stale-association prompt follows so the two dialogs never stack.
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (!_recent.SetupSeen)
                {
                    _recent.SetupSeen = true;   // mark seen first, so a force-closed welcome never re-nags
                    new FirstRunWindow { Owner = this }.ShowDialog();
                }
                CheckStaleAssociation();
            }));
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
            Filter = "Level files (*.bin;*.xml)|*.bin;*.xml|Binary level (*.bin)|*.bin|XML level (*.xml)|*.xml|All files (*.*)|*.*",
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
            _tp1Resolver = (sprites as Tp1SpriteSource)?.Resolver;
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
            UpdateCreateMenu();                     // R4: grey the create kinds this family lacks
            MarkClean();
            HideStartScreen();                        // R3: a level is open, retire the start screen
            ApplyGameIcon(doc.Profile.Key);           // R7: the window now carries that family's tile
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
        if (_doc is null) { SetStatus("Nothing to save yet \u2014 open a level first."); return; }
        // R6 authoring: while a mod project is open, Save goes into the mod folder \u2014 no dialog,
        // the game's own files are never written.
        if (_mod is not null)
        {
            SaveIntoMod();
            return;
        }
        if (!_doc.Dirty) { SetStatus("No changes to save."); return; }

        bool isXml = System.IO.Path.GetExtension(_doc.Path)
            .Equals(".xml", StringComparison.OrdinalIgnoreCase);
        var dlg = new SaveFileDialog
        {
            Title = "Save level",
            FileName = _doc.FileName,
            Filter = "Level files (*.bin;*.xml)|*.bin;*.xml|Binary level (*.bin)|*.bin|XML level (*.xml)|*.xml|All files (*.*)|*.*",
            // Preselect the extension of the file that was opened, so a name typed without one keeps
            // the level's own format (a Tasty Planet xml never silently saves as .bin).
            FilterIndex = isXml ? 3 : 2,
            DefaultExt = isXml ? "xml" : "bin",
            InitialDirectory = _recent.LastDirectory ?? System.IO.Path.GetDirectoryName(_doc.Path),
        };
        if (dlg.ShowDialog(this) != true) return;
        DoSave(dlg.FileName);
    }

    /// <summary>Ctrl+Shift+S: write straight back to the file the level was loaded from \u2014 always
    /// the real source path, even while authoring (the mod mirror is still refreshed after).</summary>
    private void SaveInPlace()
    {
        if (_doc is null) { SetStatus("Nothing to save yet — open a level first."); return; }
        if (!_doc.Dirty) { SetStatus("No changes to save."); return; }
        if (MessageBox.Show(this,
                $"Overwrite the level file in place?\n{_doc.Path}\n\nA copy of the current file is saved next to it first (a .bak side copy), so this can be undone by hand.",
                "Save in place", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DoSave(_doc.Path);
    }

    private void DoSave(string destPath)
    {
        if (_doc is null) return;
        try
        {
            // The destination folder may not exist yet (a mod dev folder that has not held this
            // level's subfolder before); WriteAllBytes would fail with "path could not be found".
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destPath))!);
            // Copy the file we are about to replace aside first (best-effort; a failed backup must not
            // stop the save), so an in-place write onto a shipped level is always recoverable.
            string? backup = BackupService.BeforeWrite(destPath);
            var report = _doc.SaveTo(destPath);
            MarkClean();
            _recent.Record(destPath);
            RebuildRecentMenu();
            // R6 authoring: while a mod project is open, every save also lands in the mod folder at
            // the path the game reads, so the project keeps whatever the creator last saved.
            if (_mod is not null && !PathsEqual(destPath, _mod.TargetFor(destPath)))
                MirrorIntoMod(destPath);
            // The written bytes are what Save produces now: record them as the current history
            // state so undo-after-save steps back through the edit list, not past the save.
            _history.Commit(_doc);
            UpdateHistoryUI();
            SetStatus(UiText.SavedLine(report.Changed, report.Stable, report.BytesChanged)
                + (backup is null ? "" : "  \u00b7  previous copy kept"));
            if (!report.Stable)
                MessageBox.Show(this,
                    "The saved file did not read back exactly as it should. This edit is " +
                    "unexpected \u2014 report it (the file on disk may differ from what the editor shows).",
                    "Save stability warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            // I/O errors (locked file, read-only folder, no permission) are the common case here.
            ErrorUi.Show(this, "Could not save level", $"Target:\n{destPath}", ex);
            SetStatus("Save failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------- mod authoring --
    // R6: creation lives here (a dev folder outside any game install), loading lives in the mod
    // manager. While a project is open, Save writes into the folder's mirrored path instead of the
    // game; publish zips the whole folder with no comparison against the original files.

    private void OnCreateMod()
    {
        var proj = CreateModWindow.ShowAndCreate(this, _doc?.Profile.Key);
        if (proj is null) return;
        _mod = proj;
        SetStatus($"Authoring \u201c{proj.Meta.Name}\u201d \u2014 saves now go into the mod folder at {proj.Root}");
    }

    private void OnResumeMod()
    {
        var dlg = new OpenFolderDialog { Title = "Select a mod folder (the one with its metadata.json)" };
        if (dlg.ShowDialog(this) != true) return;
        var proj = TastyEditor.Core.Mods.ModProject.TryOpen(dlg.FolderName);
        if (proj is null) { SetStatus("That folder is not a mod project (no metadata.json)."); return; }
        _mod = proj;
        SetStatus($"Authoring \u201c{proj.Meta.Name}\u201d \u2014 saves now go into the mod folder");
    }

    private void OnAddAsset()
    {
        if (_mod is null) { SetStatus("Create a mod first (Mod \u2022 Create mod\u2026)."); return; }
        var ofd = new OpenFileDialog { Title = "Add a file to the mod" };
        if (ofd.ShowDialog(this) != true) return;
        try
        {
            string? rel = _mod.AddAssetFile(ofd.FileName);
            SetStatus(rel is null ? "That file could not be read." : $"Added {rel} to the mod.");
        }
        catch (Exception ex) { SetStatus("Could not add that file: " + ex.Message); }
    }

    private void OnPublishMod()
    {
        if (_mod is null) { SetStatus("Create a mod first (Mod \u2022 Create mod\u2026)."); return; }
        var files = _mod.ListContents();
        if (files.Count == 0)
        {
            SetStatus("The mod folder is empty \u2014 save a level into it or add an asset first.");
            return;
        }
        var sfd = new SaveFileDialog
        {
            Title = "Publish the mod as a .tpm",
            Filter = "Tasty mod (*.tpm)|*.tpm",
            FileName = SafeFileName(_mod.Meta.Name) + ".tpm",
            DefaultExt = ".tpm",
        };
        if (sfd.ShowDialog(this) != true) return;
        try
        {
            // Full pack of the dev folder \u2014 no vanilla lookup, nothing to wait on for a folder
            // that only ever holds the creator's own files.
            int n = _mod.Publish(sfd.FileName);
            SetStatus($"Published {n} file(s) to {System.IO.Path.GetFileName(sfd.FileName)}.");
        }
        catch (Exception ex) { SetStatus("Publish failed: " + ex.Message); }
    }

    private void OnOpenModFolder()
    {
        if (_mod is null) { SetStatus("No mod project is open."); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_mod.Root}\"") { UseShellExecute = true }); }
        catch { SetStatus("Could not open the mod folder."); }
    }

    private void OnStopAuthoring()
    {
        if (_mod is null) { SetStatus("No mod project is open."); return; }
        string who = _mod.Meta.Name;
        _mod = null;
        SetStatus($"Stopped authoring \u201c{who}\u201d \u2014 the folder stays on disk, resume it from the Mod menu.");
    }

    /// <summary>Mod-menu Save path: write the level into the project at its mirrored path, no
    /// dialog. The doc's own file is untouched; the mirror is refreshed after any other save.</summary>
    private void SaveIntoMod()
    {
        if (_doc is null || _mod is null) return;
        if (!_doc.Dirty && System.IO.File.Exists(_mod.TargetFor(_doc.Path)))
        { SetStatus("Already in the mod \u2014 no changes to save."); return; }
        DoSave(_mod.TargetFor(_doc.Path));
    }

    /// <summary>Refresh the mod's copy of a level saved anywhere else (the game's own file, or a
    /// Save As to a hand-picked path). Best-effort: mod bookkeeping must never fail a real save.</summary>
    private void MirrorIntoMod(string destPath)
    {
        if (_mod is null) return;
        try
        {
            string target = _mod.TargetFor(destPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            System.IO.File.Copy(destPath, target, overwrite: true);
        }
        catch { }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                      System.IO.Path.GetFullPath(b).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Trim();
    }

    /// <summary>Unsaved-changes guard on every close path (the window ✕, Alt+F4, the taskbar, and any
    /// Application shutdown all arrive here): offer to save first, and abort the close if the user
    /// backs out or the save cannot complete, so an edit is never silently thrown away.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_doc?.Dirty == true)
        {
            var r = MessageBox.Show(this,
                "You have unsaved changes. Save them before closing?",
                "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                DoSave(_doc.Path);
                if (_doc.Dirty) { e.Cancel = true; return; }   // the save was blocked (locked / read-only)
            }
        }
        base.OnClosing(e);
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
            if (profileKey == "tp1")
            {
                var groot = Tp1GraphicsRoot.ForLevel(levelPath);
                // A loose level copy (the base editor's own levels folder, a backup) has no assets
                // tree beside it; fall back to the installed game's art so it still renders.
                if (groot is null || !groot.IsValid) groot = Tp1GraphicsRoot.ForInstall();
                var gres = Tp1SpriteResolver.TryLoad(groot);
                return gres is null ? null : new Tp1SpriteSource(gres);
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
        MarkDirty();
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

    // ------------------------------------------ Tasty Planet 1 object card (R5) --
    //
    // Every tp1 edit is a span-localised write on the level's own text (Tp1Edit), so only the touched
    // value's bytes move. A write rebuilds the whole node tree, so the commit re-projects and re-anchors
    // the selection on the edited element's stable path, then (for a structural change) re-reads the
    // card. One coalesced undo step per edit, exactly like the TP2 field panel.

    /// <summary>Write one attribute of a tp1 element. A field whose current value is a number and that
    /// receives a number keeps the file's own numeric style (SetNum); anything else is written as text
    /// (SetAttr). An emitter's / target's object type is policed against the game's definitions first.
    /// The commit re-projects without rebuilding the card, so a typed value is never torn down mid-edit.</summary>
    private void ApplyTp1Attr(Tp1Handle th, string key, string text)
    {
        if (_doc is null) return;
        var node = th.Node;
        if (node is null) return;
        text = text.Trim();

        // Entity-type policing (R5, mirrors S-D9): an emitter's or a target's object type must be one
        // this game actually has, or the loader reads it and then drops it — the object vanishes in game
        // while the file looks right. A type already used elsewhere in the level is trusted.
        if (key is "entitydef" && text.Length > 0
            && _tp1Resolver is { } r && !r.IsKnownEntity(text) && !Tp1LevelHasType(_doc.Model, text))
        {
            SetStatus($"\u201c{text}\u201d is not an object type this game has \u2014 the level would load " +
                      $"without it (not applied). Try: {Tp1Closest(text)}");
            return;
        }

        var cur = node.Attr(key)?.Value;
        double dv = 0;
        bool isNum = cur is not null
            && double.TryParse(cur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dv);
        try { if (isNum) Tp1Edit.SetNum(node, key, dv); else Tp1Edit.SetAttr(node, key, text); }
        catch { return; }
        CommitTp1(rebuildCard: false, status: $"{key} \u2192 \u201c{text}\u201d (save to write)");
    }

    /// <summary>Add one child element to a tp1 element (a new target / excluded list entry, or a timed
    /// release), cloned from the element's own style. Rebuilds the card so the fresh child is editable
    /// immediately.</summary>
    private void ApplyTp1Add(Tp1Handle th, string childTag)
    {
        if (_doc is null) return;
        var node = th.Node;
        if (node is null) return;
        Tp1Node? created = childTag switch
        {
            "affect" => Tp1Edit.AddAffect(node),
            "dontaffect" => Tp1Edit.AddDontAffect(node),
            "emit" => Tp1Edit.AddEmit(node),
            _ => null,
        };
        if (created is null) { SetStatus("Nothing could be added here"); return; }
        CommitTp1(rebuildCard: true, status: "Added \u2014 edit it below (Ctrl+Z to undo, save to write)");
    }

    /// <summary>Remove one child element (addressed by its own handle). The viewport selection stays on
    /// the parent that owns it, so the card is re-anchored to it and rebuilt without the removed child.</summary>
    private void ApplyTp1Remove(Tp1Handle th)
    {
        if (_doc is null) return;
        var node = th.Node;
        if (node is null) return;
        if (!Tp1Edit.Remove(node)) { SetStatus("Could not remove that"); return; }
        CommitTp1(rebuildCard: true, status: "Removed (Ctrl+Z to undo, save to write)");
    }

    // The selected element's tp1 path (its handle identity), read straight off the viewport selection.
    private int[]? SelectedTp1Path() => _viewport.SelectedElement switch
    {
        SceneCross cr => (cr.Source as Tp1Handle)?.Path,
        SceneLine sl => (sl.Source as Tp1Handle)?.Path,
        _ => null,
    };

    /// <summary>Commit a tp1 model write: mark dirty + one coalesced undo step, then re-project (the
    /// node tree was rebuilt) and re-anchor the same element by its path. <paramref name="rebuildCard"/>
    /// forces the inspector to re-read the element's children (a structural edit); a scalar edit keeps
    /// the card's layout so a value being typed is not disturbed.</summary>
    private void CommitTp1(bool rebuildCard, string? status)
    {
        if (_doc is null) return;
        int[]? reselect = SelectedTp1Path();      // capture before the re-project replaces the markers
        _doc.Dirty = true;
        MarkDirty();
        _history.Commit(_doc, "tp1", 2.0);
        UpdateHistoryUI();
        if (rebuildCard) _inspector.InvalidateTp1();
        _doc.Reproject();
        _viewport.SetScene(_doc.Scene);
        if (reselect is not null) _viewport.SelectTp1Path(reselect);
        _inspector.Show(_viewport.SelectedElement);
        Changed?.Invoke();      // keep the modeless tilemap window in sync
        if (status is not null) SetStatus(status);
    }

    // ------------------------------------------------ background tilemap ----
    // MainWindow is the tilemap window's host: it owns the live TP1 document, so every edit reads a
    // node off it, runs through the verbatim Tp1Edit lane and commits via CommitTp1 (which raises
    // Changed so the window re-syncs). The window itself holds no model logic.

    /// <summary>Open (or re-activate) the single modeless background-tilemap window. Only for a
    /// Tasty Planet 1 document — the tile layer model is TP1-only.</summary>
    private void OpenTilemap()
    {
        if (_doc?.Profile.Key != "tp1") { SetStatus("The background tilemap editor is for Tasty Planet 1 levels."); return; }
        if (_tilemap is null)
        {
            _tilemap = new TilemapWindow(this) { Owner = this };
            _tilemap.Closed += (_, _) => _tilemap = null;
            _tilemap.Show();
        }
        else _tilemap.Activate();
    }

    private Tp1Level? TilemapLevel => _doc?.Profile.Key == "tp1" ? _doc.Model as Tp1Level : null;

    /// <summary>Open the modal level-properties editor for the root <c>&lt;level&gt;</c> element's own
    /// attributes (TP1-only). The window holds a snapshot and reports the changed rows; every write
    /// re-resolves <c>lv.Root</c> fresh because each splice reindexes the tree and moves the spans.</summary>
    private void OpenLevelProps()
    {
        if (TilemapLevel is not { } lv) { SetStatus("Level properties are for Tasty Planet 1 levels."); return; }
        var attrs = new List<(string Key, string Value)>();
        foreach (var a in lv.Root.Attrs) attrs.Add((a.Key, a.Value));
        var win = new Tp1LevelPropsWindow(attrs, changes =>
        {
            foreach (var (key, value) in changes)
                Tp1Edit.SetAttr(lv.Root, key, value);     // fresh Root per write: Reindex replaces the nodes
            CommitTp1(rebuildCard: false, status: "Edited level properties (save to write)");
        })
        { Owner = this };
        win.ShowDialog();
    }

    // ------------------------------------------------ R7 entity authoring ----
    // TP1 object types live in the game's entitydefs.xml / imagemaps.xml tables (plus the PNGs they
    // name). The panel collects the edit; MainWindow owns the file writes: back up, splice via
    // Tp1Tables, mirror into an open mod, then reload the asset tree so the new type is placeable and
    // renders at once. Vanilla (shipped) types are editable directly; only deleting one is warned about.

    /// <summary>Open (or re-activate) the TP1 entity-types panel. TP1-only: the shared entity / image
    /// tables it edits exist only for a Tasty Planet 1 install.</summary>
    private void OpenTp1EntityEditor()
    {
        if (_doc?.Profile.Key != "tp1") { SetStatus("Entity authoring is for Tasty Planet 1 levels."); return; }
        var root = _tp1Resolver?.Root ?? Tp1GraphicsRoot.ForLevel(_doc.Path) ?? Tp1GraphicsRoot.ForInstall();
        string? entPath = root?.FindTable(Tp1Tables.EntityDefsFile);
        if (root is null || !root.IsValid || entPath is null)
        { SetStatus("Could not find the game's entity table to edit."); return; }
        string? mapPath = root.FindTable(Tp1Tables.ImageMapsFile);
        string? strPath = root.FindTable(Tp1Tables.StringsFile);

        if (_tp1Entities is not null) { _tp1Entities.Activate(); return; }

        // Types present now are "shipped"; the ones the author adds in this window are not, so only a
        // pre-existing type triggers the delete warning.
        var original = new HashSet<string>(ReadEntityNames(entPath), StringComparer.OrdinalIgnoreCase);

        var win = new Tp1EntityWindow(_tp1Resolver, root,
            load: name => Tp1Tables.ReadEntityDef(Tp1Tables.Load(entPath!), name),
            apply: (edit, art) => ApplyTp1Entity(root, entPath, mapPath, strPath, edit, art),
            remove: name => RemoveTp1Entity(root, entPath, strPath, name),
            isOriginal: name => original.Contains(name))
        { Owner = this };
        win._confirm = ConfirmVanillaDelete;
        win.Closed += (_, _) => _tp1Entities = null;
        _tp1Entities = win;
        win.Show();
    }

    // The entitydef names currently in a table file (the shipped set, snapshotted when the panel opens).
    private static IEnumerable<string> ReadEntityNames(string entPath)
    {
        var doc = Tp1Codec.ParseTree(System.IO.File.ReadAllBytes(entPath));
        foreach (var n in doc.Root.Kids("entitydef"))
            if (!string.IsNullOrEmpty(n.Get("name"))) yield return n.Get("name")!;
    }

    // Create / update one type: lay down any new art and its sheet entry first, then the entitydef, so
    // the imagemap the def names already exists by the time the tables reload.
    private bool ApplyTp1Entity(Tp1GraphicsRoot root, string? entPath, string? mapPath, string? strPath,
        Tp1EntityDefEdit edit, Tp1EntityWindow.ArtJob? art)
    {
        if (entPath is null) return false;
        try
        {
            if (art is not null)
            {
                if (mapPath is null) { SetStatus("The image table was not found."); return false; }
                string srcName = System.IO.Path.GetFileName(art.SourceFile);
                foreach (var dir in art.DestDirs)
                {
                    System.IO.Directory.CreateDirectory(dir);
                    string dest = System.IO.Path.Combine(dir, srcName);
                    System.IO.File.Copy(art.SourceFile, dest, overwrite: true);
                    if (_mod is not null) try { _mod.AddAssetFile(dest); } catch { }
                }
                BackupService.BeforeWrite(mapPath);
                var mdoc = Tp1Tables.Load(mapPath);
                Tp1Tables.UpsertImageMap(mdoc, new Tp1ImageMapEdit
                {
                    Name = art.MapName, Filename = art.FilenameRel,
                    Width = art.W, Height = art.H, FullImage = art.FullImage,
                    CellName = art.CellName, X1 = art.X1, Y1 = art.Y1, X2 = art.X2, Y2 = art.Y2,
                });
                Tp1Tables.Save(mapPath, mdoc);
                MirrorIntoMod(mapPath);
            }

            BackupService.BeforeWrite(entPath);
            var edoc = Tp1Tables.Load(entPath);
            Tp1Tables.UpsertEntityDef(edoc, edit);
            Tp1Tables.Save(entPath, edoc);
            MirrorIntoMod(entPath);

            // The friendly name lives in a separate table: without a strings.xml row the type renders but
            // shows no name in game, so the same save writes (or refreshes) that row too.
            if (strPath is not null && !string.IsNullOrEmpty(edit.Display))
            {
                BackupService.BeforeWrite(strPath);
                var sdoc = Tp1Tables.Load(strPath);
                if (Tp1Tables.UpsertString(sdoc, edit.Name, edit.Display))
                { Tp1Tables.Save(strPath, sdoc); MirrorIntoMod(strPath); }
            }

            ReloadTp1Assets(root);
            SetStatus($"Saved entity type \u201c{edit.Name}\u201d" + (art is not null ? " with new art" : "") + " \u00b7 tables written");
            return true;
        }
        catch (Exception ex) { SetStatus("Could not save the entity type: " + ex.Message); return false; }
    }

    private bool RemoveTp1Entity(Tp1GraphicsRoot root, string? entPath, string? strPath, string name)
    {
        if (entPath is null) return false;
        try
        {
            BackupService.BeforeWrite(entPath);
            var doc = Tp1Tables.Load(entPath);
            if (!Tp1Tables.RemoveEntityDef(doc, name)) return false;
            Tp1Tables.Save(entPath, doc);
            MirrorIntoMod(entPath);

            // Take the friendly-name row out with the type, so the string table does not keep a orphan.
            if (strPath is not null)
            {
                BackupService.BeforeWrite(strPath);
                var sdoc = Tp1Tables.Load(strPath);
                if (Tp1Tables.RemoveString(sdoc, name))
                { Tp1Tables.Save(strPath, sdoc); MirrorIntoMod(strPath); }
            }

            ReloadTp1Assets(root);
            return true;
        }
        catch (Exception ex) { SetStatus("Could not delete the entity type: " + ex.Message); return false; }
    }

    // Warn (once per session) before deleting a type that shipped with the game. A small modal with a
    // "don't warn again" box; the choice is remembered for the rest of the session.
    private bool ConfirmVanillaDelete(string who)
    {
        if (_tp1SuppressVanillaWarn) return true;
        bool proceed = false;
        var stopAsk = new CheckBox { Content = "Don't warn me again this session", Margin = new Thickness(0, 12, 0, 0) };
        var msg = new TextBlock
        {
            Text = $"\u201c{who}\u201d is a type that ships with the game. Deleting it edits the base " +
                   "entity table and can affect levels that already use it.\n\nDelete it anyway?",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 400, Margin = new Thickness(0, 0, 0, 4),
        };
        var yes = new Button { Content = "Delete", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 4, 10, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(yes); buttons.Children.Add(no);
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(msg); body.Children.Add(stopAsk); body.Children.Add(buttons);
        var dlg = new Window
        {
            Title = "Delete a vanilla entity type", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Content = body,
        };
        Theme.Ref(dlg, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(dlg, Window.ForegroundProperty, Theme.KText);
        yes.IsDefault = true;
        yes.Click += (_, _) => { proceed = true; dlg.Close(); };
        dlg.ShowDialog();
        if (stopAsk.IsChecked == true) _tp1SuppressVanillaWarn = true;
        return proceed;
    }

    /// <summary>Rebuild the TP1 sprite source + resolver from the (just-written) art tree and refresh
    /// every consumer, so a new or retyped entity renders and becomes selectable without reopening.</summary>
    private void ReloadTp1Assets(Tp1GraphicsRoot root)
    {
        var gres = Tp1SpriteResolver.TryLoad(root);
        if (gres is null) return;
        var src = new Tp1SpriteSource(gres);
        _spriteSource = src;
        _tp1Resolver = gres;
        _viewport.SetSpriteSource(src);
        _browser.SetSource(src);
        _inspector.SetSpriteCandidates(src.CellNames);
        _inspector.EntityCatalogFilterable = true;
        _inspector.SetEntityCandidates(gres.AllEntityNames().Select(n => new Tp2Entities.Def(n, n, "")).ToList());
        if (_doc is not null) { _doc.Reproject(); _viewport.SetScene(_doc.Scene); }
        _viewport.InvalidateVisual();
        if (AssetsTabSelected) _browser.RefreshIfVisible();
    }

    public IReadOnlyList<TileLayerInfo> Snapshot()
    {
        var list = new List<TileLayerInfo>();
        if (TilemapLevel is not { } lv) return list;
        int idx = 0;
        foreach (var layer in lv.TileLayers)
        {
            var cells = new List<TileCell>();
            foreach (var t in layer.Kids("tile"))
                cells.Add(new TileCell(t.Get("name") ?? "null", t.Get("flipx") == "true",
                                       t.Get("flipy") != "true", t.Num("angle") ?? 0));
            list.Add(new TileLayerInfo(idx, layer.Int("tileswide") ?? 0, layer.Int("tileshigh") ?? 0,
                                       layer.Num("tilewidth") ?? 256, layer.Num("tileheight") ?? 256, cells));
            idx++;
        }
        return list;
    }

    public void Paint(int layer, int cell, string name)
    {
        if (TilemapLevel is not { } lv || TileAt(lv, layer, cell) is not { } t) return;
        Tp1Edit.PaintTile(t, name);
        CommitTp1(rebuildCard: false, status: $"Painted \u201c{name}\u201d (save to write)");
    }

    public void Erase(int layer, int cell)
    {
        if (TilemapLevel is not { } lv || TileAt(lv, layer, cell) is not { } t) return;
        Tp1Edit.EraseTile(t);
        CommitTp1(rebuildCard: false, status: "Erased tile (save to write)");
    }

    public void AddLayer()
    {
        if (TilemapLevel is not { } lv) return;
        if (Tp1Edit.AddTileLayer(lv) is null) { SetStatus("Could not add a background layer"); return; }
        CommitTp1(rebuildCard: false, status: "Added a background layer (Ctrl+Z to undo, save to write)");
    }

    public void DeleteLayer(int layer)
    {
        if (TilemapLevel is not { } lv || LayerAt(lv, layer) is not { } node) return;
        if (!Tp1Edit.RemoveTileLayer(lv, node)) { SetStatus("A level keeps at least one background layer"); return; }
        CommitTp1(rebuildCard: false, status: "Removed a background layer (Ctrl+Z to undo, save to write)");
    }

    public void ResizeLayer(int layer, int wide, int high)
    {
        if (TilemapLevel is not { } lv || LayerAt(lv, layer) is not { } node) return;
        if (!Tp1Edit.ResizeTileLayer(lv, node, wide, high)) { SetStatus("Couldn\u2019t resize that layer"); return; }
        CommitTp1(rebuildCard: false, status: $"Resized layer to {wide}\u00d7{high} (save to write)");
    }

    public IReadOnlyList<string> TileNames =>
        _tp1Resolver is { } r ? r.TileNames.ToList() : (IReadOnlyList<string>)Array.Empty<string>();

    public BitmapSource? Thumb(string name) =>
        _spriteSource is Tp1SpriteSource tps && tps.TryGetCellThumb(name, out var img) ? img : null;

    private static Tp1Node? LayerAt(Tp1Level lv, int idx)
    {
        if (idx < 0) return null;
        int i = 0;
        foreach (var l in lv.TileLayers) { if (i++ == idx) return l; }
        return null;
    }

    private static Tp1Node? TileAt(Tp1Level lv, int layer, int cell)
    {
        if (cell < 0 || LayerAt(lv, layer) is not { } l) return null;
        int i = 0;
        foreach (var t in l.Kids("tile")) { if (i++ == cell) return t; }
        return null;
    }

    /// <summary>True when an object type already appears on an emitter in this level, so reusing it is
    /// safe even if the asset tables missed it.</summary>
    private static bool Tp1LevelHasType(object model, string name) =>
        model is Tp1Level lv && lv.Emitters.Any(e =>
            string.Equals(e.Get("entitydef"), name, System.StringComparison.Ordinal));

    /// <summary>A handful of the game's object types that share the rejected name's leading letters, so
    /// the status points at something usable rather than just refusing.</summary>
    private string Tp1Closest(string name)
    {
        var r = _tp1Resolver;
        if (r is null) return "(no object list loaded)";
        return string.Join(", ", r.AllEntityNames()
            .Where(n => n.StartsWith(name, System.StringComparison.OrdinalIgnoreCase)
                     || n.Contains(name, System.StringComparison.OrdinalIgnoreCase))
            .Take(6));
    }

    /// <summary>Feed the inspector's tp1 dropdowns: the loaded game's object types for an emitter, and
    /// its tile pictures for a background tile. Null (no art tree) leaves the field plain free text.</summary>
    private string[]? Tp1NameChoices(string kind)
    {
        var r = _tp1Resolver;
        if (r is null) return null;
        return kind switch
        {
            "entity" => r.EntityNames.ToArray(),
            "tile" => r.TileNames.ToArray(),
            _ => null,
        };
    }

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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        // R3 standard bindings (shown in the menu text so the shortcuts are discoverable): open and the
        // start screen work with no level loaded; F2 fits, F4 toggles the sprite-at-any-size view.
        if (ctrl && !alt && !shift && e.Key == Key.O) { OpenLevel(); e.Handled = true; return; }
        if (ctrl && !alt && !shift && e.Key == Key.N) { ShowStartScreen(); e.Handled = true; return; }
        if (!ctrl && !alt && e.Key == Key.F4) { ToggleAlwaysSprites(); e.Handled = true; return; }
        if (!ctrl && !alt && e.Key == Key.F2 && _doc is not null) { _viewport.FitToView(); e.Handled = true; return; }
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
        MarkDirty();
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
        if (clip is null) { SetStatus("Can't copy this \u2014 it is a type of object the editor can't duplicate yet."); return; }
        _clipboard = clip;
        SetStatus($"Copied {UiText.Kind(MapKind(clip.Kind))} on level {clip.Layer + 1}");
    }

    private void PasteClipboard()
    {
        if (_doc is null) return;
        if (_clipboard is null) { SetStatus("Nothing to paste \u2014 copy or duplicate an object first."); return; }
        var (kind, layer) = (_clipboard.Kind, _clipboard.Layer);
        var r = EditOps.Paste(_doc.Model, _clipboard);
        if (r != EditResult.Done) { SetStatus($"Couldn't paste \u2014 {Describe(r)}"); return; }
        AfterMutation(MapKind(kind), Math.Clamp(layer, 0, Math.Max(0, _doc.LayerCount - 1)), $"Pasted {UiText.Kind(MapKind(kind))}");
    }

    private void DuplicateSelection()
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("Nothing selected \u2014 select an object to duplicate first."); return; }
        var r = EditOps.Duplicate(_doc.Model, SourceOf(sel));
        if (r != EditResult.Done) { SetStatus($"Couldn't duplicate \u2014 {Describe(r)}"); return; }
        AfterMutation(sel.Kind, sel.Layer, $"Duplicated {UiText.Kind(sel.Kind)} beside the original");
    }

    private void DeleteSelection()
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("nothing selected to delete"); return; }
        var r = EditOps.Delete(_doc.Model, SourceOf(sel));
        if (r != EditResult.Done) { SetStatus($"delete: {Describe(r)}"); return; }
        _doc.Dirty = true;
        MarkDirty();
        _history.Commit(_doc);
        _doc.Reproject();
        _viewport.SetScene(_doc.Scene);        // SetScene clears the (now dangling) selection
        _inspector.Show(null);
        UpdateHistoryUI();
        SetStatus($"Deleted {UiText.Kind(sel.Kind)}");
    }

    /// <summary>Move the selected record to another level (copy → paste-into-layer → delete
    /// original). The kind/layer for re-selection come from the pre-move selection; the pasted
    /// clone is the last record of that kind in the destination layer.</summary>
    private void MoveSelectionToLayer(int targetLayer)
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        if (sel is null) { SetStatus("Nothing selected \u2014 select an object to move first."); return; }
        var r = EditOps.MoveToLayer(_doc.Model, SourceOf(sel), targetLayer);
        if (r != EditResult.Done) { SetStatus($"Couldn't move \u2014 {Describe(r)}"); return; }
        AfterMutation(sel.Kind, targetLayer, $"Moved {UiText.Kind(sel.Kind)} to level {targetLayer + 1}");
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
        // R5: in a Tasty Planet 1 level, "add an entity" means a spawner (an emitter), created its own
        // way (clone a sibling, no shipped-record body to copy), so it does not go through EntitySpawn.
        if (_doc.Profile.Key == "tp1") { AddTp1Emitter(at); return; }
        var sel = _viewport.SelectedElement;
        int layer = sel is not null && sel.Layer >= 0 ? sel.Layer : 0;
        // Prefill with the selected entity's own type: "add another of these, over there" is the
        // common case, and it is also the surest way to stay inside the legal name set.
        string? prefill = sel is SceneCross sc && sc.Kind == SceneKind.Entity ? sc.SpriteName : null;
        var dlg = new Panels.NewEntityWindow(_entityDefs, _doc.LayerCount, layer, at.X, at.Y, prefill) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var r = EntitySpawn.Add(_doc.Model, dlg.Layer, at.X, at.Y, dlg.TypeName, _entityDefs, out string? detail);
        if (r != EditResult.Done) { SetStatus($"Can't add entity \u2014 {(detail ?? Describe(r))}"); return; }
        AfterMutation(SceneKind.Entity, dlg.Layer,
            $"Added {dlg.TypeName} on level {dlg.Layer + 1} \u00b7 {detail}");
    }

    // ---------------------------------------------- R5 tp1 create (new object) --
    //
    // A tp1 object is added by cloning a same-file sibling (Tp1Edit.NewEmitter / NewGoo splice a copy
    // of an existing element's verbatim block, then rewrite its identifying attributes), so a created
    // element carries the level's own style and the write stays localised. The object type is policed
    // in the picker against the game's definitions. After the splice the scene is re-projected (the node
    // tree was rebuilt) and the fresh element is re-selected by its path so it is immediately editable.

    /// <summary>Add a new spawner (emitter) at a world point. Requires an existing spawner to copy the
    /// level's style from; the picker chooses its kind and (policed) object type.</summary>
    private void AddTp1Emitter(PointD at)
    {
        if (_doc is null) return;
        if (_doc.Model is not Tp1Level lv) return;
        if (!lv.Emitters.Any())
        {
            SetStatus("This level has no spawner to copy, so a new one can't be added here.");
            return;
        }
        string? prefill = (_viewport.SelectedElement as SceneCross)?.SpriteName;
        var dlg = new Panels.NewTp1EmitterWindow(_tp1Resolver?.EntityNames, at.X, -at.Y, prefill) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        // Data space is Y-up; the scene point came back Y-down, so negate it for the stored posy.
        var created = Tp1Edit.NewEmitter(lv, dlg.EmitterType, dlg.EntityDef, at.X, -at.Y);
        if (created is null) { SetStatus("Couldn't add a spawner here."); return; }
        AfterTp1Create(created, $"Added a {dlg.EmitterType} spawner for {dlg.EntityDef}");
    }

    /// <summary>Add a new player start (goo) at the view centre. TP1 levels normally hold exactly one;
    /// this supports placing a fresh one and is a single undoable splice.</summary>
    private void AddTp1Goo()
    {
        if (_doc is null) return;
        if (_doc.Model is not Tp1Level lv) return;
        var at = _viewport.ViewCentreWorld;
        var created = Tp1Edit.NewGoo(lv, at.X, -at.Y);
        if (created is null) { SetStatus("Couldn't add a player start."); return; }
        AfterTp1Create(created, "Added a player start");
    }

    // Shared commit for a created tp1 element: dirty + one undo step, re-project, then select the fresh
    // element by its path (it is the newest node) and open it in the inspector.
    private void AfterTp1Create(Tp1Node created, string status)
    {
        if (_doc is null) return;
        int[] path = Tp1Level.PathOf(created);
        _doc.Dirty = true;
        MarkDirty();
        _history.Commit(_doc, "tp1");
        UpdateHistoryUI();
        _inspector.InvalidateTp1();
        _doc.Reproject();
        _viewport.SetScene(_doc.Scene);
        _viewport.SelectTp1Path(path);
        _inspector.Show(_viewport.SelectedElement);
        SetStatus($"{status} \u2014 Ctrl+Z to undo, save to write");
    }

    // ------------------------------------------------------ R4 creation ----

    /// <summary>Create a default object of <paramref name="kind"/> (Wall / Area / Water / Path /
    /// Deco) on the layer of the current selection (or the first when nothing is selected), landed
    /// at <paramref name="at"/> (else the view centre). The Core entry point builds a minimal record
    /// — a typed default for Tasty Blue / TP2, a clone of a real shipped record for Forever /
    /// Final Bite — so the create-default-then-edit flow hands it straight to the handles + inspector.
    /// A kind this level type has no room for reports an honest status line and touches nothing.</summary>
    private void NewElement(SceneKind kind, PointD? at = null)
    {
        if (_doc is null) return;
        var sel = _viewport.SelectedElement;
        int layer = sel is not null && sel.Layer >= 0 ? sel.Layer : 0;
        var atw = at ?? _viewport.ViewCentreWorld;
        var r = EditOps.NewRecord(_doc.Model, kind, layer, atw, out _, null);
        if (r == EditResult.NotSupported) { SetStatus($"A {UiText.Kind(kind)} doesn't fit this type of level."); return; }
        if (r != EditResult.Done) { SetStatus($"Can't add {UiText.Kind(kind)} — {Describe(r)}"); return; }
        AfterMutation(kind, layer, $"Added {UiText.Kind(kind)} on level {layer + 1}");
    }

    /// <summary>Whether the open level's family can hold a new object of this kind at all. A kind a
    /// family has no storage for is greyed out (TP2 has no defined-area or water slots; the framed
    /// Forever / Final Bite levels have no water to create into). Creation that also needs a shipped
    /// record to copy still reports honestly at click time when a level happens to hold none.</summary>
    private bool CanCreateKind(SceneKind kind)
    {
        if (_doc is null) return false;
        return _doc.Profile.Key switch
        {
            "tb" => kind is SceneKind.Wall or SceneKind.Area or SceneKind.Water or SceneKind.Path or SceneKind.Deco,
            "tp2" => kind is SceneKind.Wall or SceneKind.Path or SceneKind.Deco,
            "f4" or "f5" => kind is SceneKind.Wall or SceneKind.Area or SceneKind.Path or SceneKind.Deco,
            // R5: a Tasty Planet 1 level holds no wall / area / water / decoration / standalone-path
            // storage — its objects are spawners, a player start, background tiles and behaviour paths,
            // created through their own commands. So none of the shape kinds applies.
            "tp1" => false,
            _ => false,
        };
    }

    // A Tasty Planet 1-only menu item (spawner / player start): registered for grey-out on load so it
    // appears only for a tp1 level.
    private MenuItem Tp1Item(string header, Action act)
    {
        // TP1-only (spawner / player start / background tilemap): hidden unless a tp1 level is open.
        // Built before any load, so start collapsed; UpdateCreateMenu reveals it once the family is known.
        var mi = new MenuItem { Header = header, Visibility = Visibility.Collapsed };
        mi.Click += (_, _) => act();
        _tp1OnlyGate.Add(mi);
        return mi;
    }

    // A submenu "New <kind>" item: wired to the create command, registered for grey-out on load.
    private MenuItem NewShapeItem(string header, SceneKind kind)
    {
        bool ok = CanCreateKind(kind);
        var mi = new MenuItem { Header = header, IsEnabled = ok, Visibility = ok ? Visibility.Visible : Visibility.Collapsed };
        mi.Click += (_, _) => NewElement(kind);
        _createGate.Add((mi, kind));
        return mi;
    }

    // A toolbar create button: same command as the menu item, same grey-out list.
    private Button CreateToolBtn(string icon, string tip, SceneKind kind)
    {
        bool ok = CanCreateKind(kind);
        var b = ToolBtn(icon, tip, (_, _) => NewElement(kind));
        b.IsEnabled = ok;
        b.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        _createGate.Add((b, kind));
        return b;
    }

    /// <summary>Re-grey every create affordance for the level just opened. Called after a load once
    /// <c>_doc</c> is set so the family is known.</summary>
    private void UpdateCreateMenu()
    {
        // Hide (not merely grey out) the create affordances the open family has no storage for, so a TP1
        // level shows only its spawner / player-start / tilemap and never the shape tools it cannot hold.
        foreach (var (item, kind) in _createGate)
        {
            bool ok = CanCreateKind(kind);
            item.IsEnabled = ok;
            item.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }
        bool tp1 = _doc?.Profile.Key == "tp1";
        foreach (var item in _tp1OnlyGate)
        {
            item.IsEnabled = tp1;
            item.Visibility = tp1 ? Visibility.Visible : Visibility.Collapsed;
        }
        // Multilevel and onion-skin are Tasty Blue / family concepts — a TP1 .bin has neither, so hide
        // them there instead of offering a button that can only fail.
        foreach (var item in _notTp1Gate)
        {
            item.IsEnabled = !tp1;
            item.Visibility = tp1 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static SceneKind MapKind(string kind) => kind switch
    {
        "wall" => SceneKind.Wall,
        "area" => SceneKind.Area,
        "water" => SceneKind.Water,
        "path" => SceneKind.Path,
        "pointlist" => SceneKind.PointList,
        "entity" => SceneKind.Entity,
        "deco" => SceneKind.Deco,
        _ => SceneKind.Other,
    };

    private static string Describe(EditResult r) => r switch
    {
        EditResult.NothingSelected => "nothing is selected",
        EditResult.NotSupported => "this kind of object can't be changed that way yet",
        EditResult.Failed => "the editor's safety checks refused this change \u2014 nothing was modified",
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

        // R4: create a default object of a supported kind right under the cursor. A kind the open family
        // cannot hold is hidden, mirroring the Edit menu + toolbar. Built fresh per right-click, so the
        // current game is already known here — no persistent gate entry is needed (that also stops the
        // transient items piling up in _tp1OnlyGate across successive right-clicks).
        var newHere = new MenuItem { Header = "New here" };
        var dropAt = _viewport.WorldAt(at);
        foreach (var (label, kind) in new (string, SceneKind)[]
                 {
                     ("Wall", SceneKind.Wall), ("Defined area", SceneKind.Area), ("Water", SceneKind.Water),
                     ("Path", SceneKind.Path), ("Decoration", SceneKind.Deco),
                 })
        {
            SceneKind k = kind;
            bool ok = CanCreateKind(k);
            var mi = new MenuItem { Header = label, IsEnabled = ok, Visibility = ok ? Visibility.Visible : Visibility.Collapsed };
            mi.Click += (_, _) => NewElement(k, dropAt);
            newHere.Items.Add(mi);
        }
        // R5: tp1's own create commands, at the point that was right-clicked. Hidden for other games.
        bool isTp1 = _doc.Profile.Key == "tp1";
        var visTp1 = isTp1 ? Visibility.Visible : Visibility.Collapsed;
        var tp1Spawn = new MenuItem { Header = "Spawner", IsEnabled = isTp1, Visibility = visTp1 };
        tp1Spawn.Click += (_, _) => AddTp1Emitter(dropAt);
        newHere.Items.Add(tp1Spawn);
        var tp1Goo = new MenuItem { Header = "Player start", IsEnabled = isTp1, Visibility = visTp1 };
        tp1Goo.Click += (_, _) => AddTp1Goo();
        newHere.Items.Add(tp1Goo);
        cm.Items.Add(newHere);

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
            MarkDirty(doc.Dirty);
            UpdateHistoryUI();
            Changed?.Invoke();      // re-sync the modeless tilemap window after undo/redo
            SetStatus($"{what}  \u00b7  {doc.Scene.Count} objects  \u00b7  {_history.UndoDepth} step(s) from load");
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

    /// <summary>Keep the Undo/Redo menu items (and their toolbar twins) honest after every state change.</summary>
    private void UpdateHistoryUI()
    {
        if (_undoItem is not null) _undoItem.IsEnabled = _history.CanUndo;
        if (_redoItem is not null) _redoItem.IsEnabled = _history.CanRedo;
        if (_undoBtn is not null) _undoBtn.IsEnabled = _history.CanUndo;
        if (_redoBtn is not null) _redoBtn.IsEnabled = _history.CanRedo;
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

    // A droppable / openable level: the four binary families use .bin, Tasty Planet 1 uses .xml.
    private static bool IsLevelFile(string f) =>
        f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
        f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool HasLevelData(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        return files.Any(IsLevelFile);
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
        var level = files.FirstOrDefault(IsLevelFile);
        if (level is null)
        {
            SetStatus("dropped files contain no level file — ignoring");
            return;
        }
        _ = OpenPathAsync(level);   // OpenPathAsync shows the discard prompt / busy overlay itself
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
        MarkDirty();
        _inspector.Show(_viewport.SelectedElement);
        _history.Commit(_doc);                 // drag-move / inspector position write landed
        UpdateHistoryUI();
    }

    private void SetStatus(string text)
    {
        _baseStatus = text;
        _status.Text = text;
    }

    // ------------------------------------------------------- R3 theme ------

    /// <summary>Flip the chrome palette at runtime. Hand-built surfaces follow by resource key and the
    /// standard controls re-resolve their (rebuilt) styles, so no window restart is needed and the open
    /// level is untouched. The choice is persisted.</summary>
    private void SetTheme(bool dark)
    {
        Theme.Apply(Application.Current, dark);
        _recent.Dark = dark;
        if (_themeItem is not null) _themeItem.IsChecked = dark;
    }

    private void ToggleAlwaysSprites()
    {
        bool on = !_viewport.AlwaysSprites;
        _viewport.AlwaysSprites = on;
        _recent.AlwaysSprites = on;
        if (_alwaysSpritesItem is not null) _alwaysSpritesItem.IsChecked = on;
    }

    // ------------------------------------------------------- R3 dirty ------
    // Every edit path funnels its title update through these so the status-bar chip can never drift
    // out of sync with the window's dirty marker.

    private void MarkDirty() { Title = BaseTitle + " *"; SetDirtyChip(true); }
    private void MarkClean() { Title = BaseTitle; SetDirtyChip(false); }
    private void MarkDirty(bool dirty) { if (dirty) MarkDirty(); else MarkClean(); }

    private void SetDirtyChip(bool dirty)
    {
        if (_dirtyChip is null) return;
        _dirtyChip.Text = dirty ? "\u25CF  Unsaved changes" : "\u25CF  Saved";
        Theme.Ref(_dirtyChip, TextBlock.ForegroundProperty, dirty ? Theme.KAccent : Theme.KTextMuted);
    }

    // ----------------------------------------------------- R3 toolbar ------

    /// <summary>A compact icon bar for the actions people reach for constantly. Each button mirrors a
    /// menu command (the menu keeps the full set + its keyboard gesture); the tooltip names the action
    /// and its shortcut so the toolbar teaches itself.</summary>
    /// <remarks>Built as a plain horizontal StackPanel, NOT a WPF ToolBar/ToolBarTray: measured with
    /// infinite width (which a DockPanel top lane does), the ToolBar's overflow Track gives every child
    /// a zero-width column, so the buttons stacked vertically and ate the viewport.</remarks>
    private Border BuildToolbar()
    {
        var tb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        tb.Children.Add(ToolBtn("open", "Open level  (Ctrl+O)", (_, _) => OpenLevel()));
        tb.Children.Add(ToolBtn("save", "Save as…  (Ctrl+S)", (_, _) => SaveLevel()));
        tb.Children.Add(ToolSep());
        _undoBtn = ToolBtn("undo", "Undo  (Ctrl+Z)", (_, _) => Undo());
        _redoBtn = ToolBtn("redo", "Redo  (Ctrl+Y)", (_, _) => Redo());
        _undoBtn.IsEnabled = false;
        _redoBtn.IsEnabled = false;
        tb.Children.Add(_undoBtn);
        tb.Children.Add(_redoBtn);
        tb.Children.Add(ToolSep());
        tb.Children.Add(ToolBtn("new", "New entity  (Ctrl+Shift+N)", (_, _) => AddEntity(_viewport.ViewCentreWorld)));
        // R4: create a default shape / path / decoration at the view centre, then edit it live.
        tb.Children.Add(CreateToolBtn("newwall", "New wall", SceneKind.Wall));
        tb.Children.Add(CreateToolBtn("newarea", "New defined area", SceneKind.Area));
        tb.Children.Add(CreateToolBtn("newwater", "New water", SceneKind.Water));
        tb.Children.Add(CreateToolBtn("newpath", "New path", SceneKind.Path));
        tb.Children.Add(CreateToolBtn("newdeco", "New decoration", SceneKind.Deco));
        tb.Children.Add(ToolBtn("dup", "Duplicate  (Ctrl+D)", (_, _) => DuplicateSelection()));
        tb.Children.Add(ToolBtn("delete", "Delete  (Del)", (_, _) => DeleteSelection()));
        tb.Children.Add(ToolSep());
        tb.Children.Add(ToolBtn("fit", "Fit to view  (F2)", (_, _) => _viewport.FitToView()));
        tb.Children.Add(ToolBtn("sprites", "Always draw sprites  (F4)", (_, _) => ToggleAlwaysSprites()));
        var onionBtn = ToolBtn("onion", "Load an onion-skin reference level", (_, _) => LoadOnion());
        var multiBtn = ToolBtn("multi", "Multilevel / goals", (_, _) => OpenMultiLevel());
        tb.Children.Add(onionBtn);
        tb.Children.Add(multiBtn);
        _notTp1Gate.Add(onionBtn);
        _notTp1Gate.Add(multiBtn);
        var levelBtn = ToolBtn("level", "Level properties", (_, _) => OpenLevelProps());
        _tp1OnlyGate.Add(levelBtn);
        levelBtn.Visibility = Visibility.Collapsed;     // TP1-only; UpdateCreateMenu reveals it on load
        tb.Children.Add(levelBtn);
        var tilemapBtn = ToolBtn("tilemap", "Background tilemap", (_, _) => OpenTilemap());
        _tp1OnlyGate.Add(tilemapBtn);
        tilemapBtn.Visibility = Visibility.Collapsed;   // TP1-only; UpdateCreateMenu reveals it on load
        tb.Children.Add(tilemapBtn);
        tb.Children.Add(ToolSep());
        tb.Children.Add(ToolBtn("theme", "Switch between the light and dark theme", (_, _) => SetTheme(!Theme.IsDark)));
        tb.Children.Add(ToolBtn("settings", "Register or remove the .bin file association", (_, _) => RegisterFileAssociation()));
        var band = new Border { Child = tb };
        Theme.Ref(band, Border.BackgroundProperty, Theme.KSurface);
        return band;
    }

    private Button ToolBtn(string icon, string tip, RoutedEventHandler onClick)
    {
        var b = new Button { ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        // Keyed style by resource reference so the flat tool button re-themes live on a theme switch.
        b.SetResourceReference(FrameworkElement.StyleProperty, Theme.KToolButton);
        var glyph = Icons.Make(icon, 18);
        b.Content = (object?)glyph ?? tip;
        b.Click += onClick;
        return b;
    }

    private Border ToolSep()
    {
        var s = new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4) };
        Theme.Ref(s, Border.BackgroundProperty, Theme.KBorder);
        return s;
    }

    // ------------------------------------------------- R3 start screen -----

    /// <summary>The start screen container: a full-window panel shown until a level is opened. Its
    /// content is rebuilt each time it is shown, so the recent list is always current.</summary>
    private Border BuildStartScreen()
    {
        var host = new Border { Visibility = Visibility.Collapsed };
        Theme.Ref(host, Border.BackgroundProperty, Theme.KWindow);
        return host;
    }

    private void ShowStartScreen()
    {
        if (_start is null) return;
        _start.Child = BuildStartContent();
        _start.Visibility = Visibility.Visible;
    }

    private void HideStartScreen()
    {
        if (_start is not null) _start.Visibility = Visibility.Collapsed;
    }

    private UIElement BuildStartContent()
    {
        // Two-column layout (recents left, games right) under a compact title band: the first cut
        // stacked everything in one tall column and scrolled like a phone screen.
        var body = new Grid
        {
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28),
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        title.Children.Add(new TextBlock { Text = "TastyEdit", FontSize = 26, FontWeight = FontWeights.SemiBold });
        title.Children.Add(StartMuted("Level editor for Tasty Planet, Back for Seconds, Tasty Blue, Forever and Final Bite."));
        Grid.SetColumnSpan(title, 3);
        body.Children.Add(title);

        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(StartHeader("Recent levels"));
        if (_recent.Paths.Count == 0)
            left.Children.Add(StartMuted("Nothing opened yet \u2014 choose a game to find a level, or drag a .bin onto the window."));
        else
            foreach (var p in _recent.Paths) left.Children.Add(RecentButton(p));
        Grid.SetColumn(left, 0);
        Grid.SetRow(left, 1);
        body.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        right.Children.Add(StartHeader("Open a level"));
        // Two game cards across; a third row appears once the fifth game joins, so the cards keep
        // their size instead of shrinking to force a fixed 2x2.
        var games = new UniformGrid { Columns = 2 };
        foreach (var key in new[] { "tp1", "tp2", "tb", "f4", "f5" }) games.Children.Add(GameCard(key));
        right.Children.Add(games);
        right.Children.Add(StartMuted("Opening a level from its game folder keeps every byte you do not touch."));
        Grid.SetColumn(right, 2);
        Grid.SetRow(right, 1);
        body.Children.Add(right);

        // R6: mod management is a first-class start-screen action, so a player can import and play a
        // custom campaign (or publish one) without ever opening a level first.
        var modBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var modsBtn = new Button { Content = "Manage mods\u2026", Padding = new Thickness(12, 5, 12, 5) };
        modsBtn.Click += (_, _) => new Panels.ModsWindow(_doc?.Profile.Key) { Owner = this }.ShowDialog();
        modBar.Children.Add(modsBtn);
        modBar.Children.Add(new TextBlock
        {
            Text = "  Import a .tpm to play a custom campaign, or publish your own.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.TextMutedBrush(),
        });
        Grid.SetColumnSpan(modBar, 3);
        Grid.SetRow(modBar, 2);
        body.Children.Add(modBar);

        return new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static TextBlock StartHeader(string text) => new()
    {
        Text = text,
        FontSize = Theme.FontSection,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 2, 0, 6),
    };

    private TextBlock StartMuted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
        Foreground = Theme.TextMutedBrush(),
    };

    private Button RecentButton(string path)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(GameBadge(GameRegistry.PathHint(path)));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        names.Children.Add(new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(path) });
        names.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? ""),
            FontSize = 11,
            Foreground = Theme.TextMutedBrush(),
        });
        row.Children.Add(names);
        var b = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 2),
            ToolTip = path,
        };
        string captured = path;
        b.Click += (_, _) => { HideStartScreen(); _ = OpenPathAsync(captured); };
        return b;
    }

    private UIElement GameCard(string key)
    {
        var prof = GameRegistry.Profile(key);
        string? dir = GameInstall.LevelsDir(key);
        var card = new Border
        {
            CornerRadius = new CornerRadius(Theme.Corner),
            Padding = new Thickness(10, 8, 10, 10),
            Margin = new Thickness(4),
            BorderThickness = new Thickness(1),
        };
        Theme.Ref(card, Border.BackgroundProperty, Theme.KSurface);
        Theme.Ref(card, Border.BorderBrushProperty, Theme.KBorder);
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(GameBadge(key));
        top.Children.Add(new TextBlock { Text = prof.Label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock
        {
            Text = dir is not null ? "Level folder found" : "Install not found on this PC",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 11,
            Foreground = dir is not null ? Theme.TextMutedBrush() : Theme.AccentBrush(),
        });
        var open = new Button { Content = "Browse levels\u2026", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        string captured = key;
        open.Click += (_, _) => OpenFromGame(captured);
        sp.Children.Add(open);
        card.Child = sp;
        return card;
    }

    private void OpenFromGame(string key)
    {
        string? dir = GameInstall.LevelsDir(key) ?? _recent.LastDirectory;
        var dlg = new OpenFileDialog
        {
            Filter = "Level files (*.bin;*.xml)|*.bin;*.xml|Binary level (*.bin)|*.bin|XML level (*.xml)|*.xml|All files (*.*)|*.*",
            Title = $"Open a {GameRegistry.Profile(key).Label} level",
            InitialDirectory = dir,
        };
        if (dlg.ShowDialog(this) == true) { HideStartScreen(); _ = OpenPathAsync(dlg.FileName); }
    }

    // R7: the window icon follows the open level, so the title bar, the taskbar button and Alt-Tab
    // say which game is being edited. Explorer cannot do this for a .bin file — one shared extension
    // has one icon — so it is the editor's own chrome that carries the family tile, and a family
    // whose tile is missing simply keeps the application icon.
    private void ApplyGameIcon(string? gameKey) => Icon = GameIcons.For(gameKey) ?? _appIcon ?? Icon;

    // A small rounded game badge (no external icon family, matches the theme). Colour is per game.
    private static Border GameBadge(string? gameKey)
    {
        var (label, fill) = GameChip(gameKey);
        var b = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
            Background = fill,
        };
        b.Child = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        return b;
    }

    private static (string, SolidColorBrush) GameChip(string? key) => key switch
    {
        "tp2" => ("TP2", Solid(94, 129, 214)),
        "tb" => ("TB", Solid(56, 169, 196)),
        "f4" => ("Forever", Solid(122, 180, 110)),
        "f5" => ("Final Bite", Solid(201, 138, 110)),
        "tp1" => ("TP1", Solid(170, 120, 178)),
        _ => ("Level", Solid(120, 124, 135)),
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
