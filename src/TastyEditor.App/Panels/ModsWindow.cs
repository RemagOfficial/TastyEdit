using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Mods;

namespace TastyEditor.App.Panels;

/// <summary>The mod manager (R6) — loading only. A player picks a game, imports a <c>.tpm</c>, and
/// installs it; the game then runs the custom campaign. Playing, reverting and deleting are all
/// folder moves inside one game's own directory: the pristine assets are kept as a backup, each mod
/// gets a full merged copy, and switching is just which folder is named <c>assets</c>. Nothing
/// outside the selected game folder is touched, and no game is launched.</summary>
///
/// <para>Creating a mod lives apart from this window, in the editor's Mod menu: a development
/// folder collects the creator's saved levels and added assets, and publish zips it into the
/// <c>.tpm</c> files imported here.</para>
///
/// <para>Opened from the start screen, so mods are manageable with no level loaded. When a level does
/// happen to be open, its game is pre-selected for convenience.</para>
public sealed class ModsWindow : Window
{
    private static readonly (string Key, string Name)[] Games = GameInstall.KnownGames();

    private readonly ComboBox _game = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _installLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ListBox _mods = new() { MinHeight = 160 };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8 };

    // The install folder currently managed. Resolved from the game pick, or pointed at by hand when
    // the game is not in a location the editor can auto-find.
    private string? _installDir;
    private string GameKey => (_game.SelectedItem as ComboItem)?.Key ?? "";
    private List<InstalledMod> _installed = new();
    private StackPanel _actions = new();

    private sealed record ComboItem(string Key, string Name)
    {
        // The combo's row/selection templates bind to the item content, which WPF renders through
        // ToString; without this the picker would show the record's debug form instead of the game.
        public override string ToString() => Name;
    };

    public ModsWindow(string? preselectGame = null, string? importPath = null)
    {
        Title = "Manage mods";
        Width = 620;
        Height = 620;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleCombo(_game);
        Theme.StyleBox(_mods);

        foreach (var (key, name) in Games)
            _game.Items.Add(new ComboItem(key, name));
        // A mod file handed in (double-click / open) names its own game, so pre-select that.
        string? meta = importPath is not null ? TpmFile.ReadMetadata(importPath)?.Game : null;
        string? want = preselectGame ?? meta ?? (GameInstall.InstallDir("tb") is not null ? "tb" : null);
        _game.SelectedIndex = IndexOfGame(want) >= 0 ? IndexOfGame(want) : 0;
        _game.SelectionChanged += (_, _) => OnGameChanged();

        var pick = new Button { Content = "Locate game folder…", Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += (_, _) => LocateFolder();

        var gameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 12, 14, 0) };
        gameRow.Children.Add(new TextBlock { Text = "Game", VerticalAlignment = VerticalAlignment.Center });
        gameRow.Children.Add(new Control { Width = 8 });
        gameRow.Children.Add(_game);
        gameRow.Children.Add(pick);

        var top = new StackPanel { Margin = new Thickness(14, 0, 14, 0) };
        top.Children.Add(gameRow);
        top.Children.Add(_installLine);

        var listHead = new TextBlock { Text = "Installed in this game", FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 10, 14, 2) };

        _actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 8, 14, 0) };
        _actions.Children.Add(ActionButton("Import mod…", OnImport));
        _actions.Children.Add(ActionButton("Play this", OnSelect));
        _actions.Children.Add(ActionButton("Revert to vanilla", OnRevert));
        _actions.Children.Add(ActionButton("Delete", OnDelete));
        _actions.Children.Add(ActionButton("Refresh", (_, _) => Refresh()));

        var closeBtn = new Button { Content = "Close", Width = 96, IsCancel = true, Margin = new Thickness(14, 12, 14, 12) };
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => Close();
        var closeRow = new DockPanel { LastChildFill = false };
        closeRow.Children.Add(closeBtn);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(listHead);
        body.Children.Add(_mods);
        body.Children.Add(_detail);
        body.Children.Add(_actions);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(closeRow, Dock.Bottom);
        root.Children.Add(closeRow);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        });
        Content = root;

        OnGameChanged();
        // An mod file passed straight in imports right away; the game folder is already resolved
        // above so the install has somewhere to go.
        if (importPath is not null) Dispatcher.BeginInvoke(new Action(() => { _ = ImportFileAsync(importPath); }));
    }

    private Button ActionButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        b.Click += onClick;
        return b;
    }

    private int IndexOfGame(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < _game.Items.Count; i++)
            if (((ComboItem)_game.Items[i]).Key == key) return i;
        return -1;
    }

    // ----------------------------------------------------------------- state --

    private void OnGameChanged()
    {
        _installDir = GameKey.Length == 0 ? null : GameInstall.InstallDir(GameKey);
        if (_installDir is null)
            _installLine.Text = "No install found for this game. Use Locate game folder… if it is elsewhere.";
        else
            _installLine.Text = _installDir;
        Refresh();
    }

    private void LocateFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's folder (the one that holds its assets)" };
        if (dlg.ShowDialog(this) != true) return;
        if (!ModInstall.IsManageable(dlg.FolderName))
        {
            Say("That folder has no 'assets' to manage. Pick the game's own folder.", true);
            return;
        }
        _installDir = dlg.FolderName;
        _installLine.Text = _installDir;
        Refresh();
    }

    private void Refresh()
    {
        _installed.Clear();
        _mods.Items.Clear();
        if (_installDir is null || !ModInstall.IsManageable(_installDir))
        {
            _mods.IsEnabled = false;
            _detail.Text = "Point at a game folder to see its mods.";
            return;
        }
        _mods.IsEnabled = true;
        _installed = ModInstall.ScanInstalled(_installDir);
        foreach (var m in _installed)
            _mods.Items.Add(Label(m));
        if (_installed.Count > 0)
            _mods.SelectedIndex = 0;
        _detail.Text = $"The game always reads the folder named 'assets'. Playing a mod just renames folders — "
                     + $"your original assets are kept safe as 'assets-vanilla'.";
    }

    private static string Label(InstalledMod m)
    {
        string tag = m.IsActive ? "   \u25c0 playing now" : m.IsVanilla ? "   \u25cb original (vanilla)" : "";
        string ver = string.IsNullOrEmpty(m.Version) ? "" : $"  v{m.Version}";
        return (m.IsVanilla ? "Vanilla" : m.Name) + ver + tag;
    }

    private InstalledMod? Selected() =>
        _mods.SelectedIndex >= 0 && _mods.SelectedIndex < _installed.Count ? _installed[_mods.SelectedIndex] : null;

    // -------------------------------------------------------------- actions --

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        var ofd = new OpenFileDialog
        {
            Title = "Select a mod file",
            Filter = "Tasty mod (*.tpm)|*.tpm|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        _ = ImportFileAsync(ofd.FileName);
    }

    private async Task ImportFileAsync(string path)
    {
        if (!Ready()) return;
        var (ok, meta, err) = TpmFile.Validate(path);
        if (!ok || meta is null) { Say(err ?? "That file is not a valid mod.", true); return; }

        // The mod names its game; if it disagrees with the folder, confirm before installing.
        if (!string.Equals(meta.Game, GameKey, StringComparison.OrdinalIgnoreCase))
        {
            string target = GameName(GameKey), wants = GameName(meta.Game);
            if (Confirm($"This mod is made for {wants}, but you have {target} selected.\n\n"
                       + $"Install it into {target} anyway?") != true)
                return;
        }

        var pv = ModInstall.PreviewInstall(_installDir!, path, meta.Game);
        if (Confirm($"Install \u201c{meta.Name}\u201d?\n\n"
                   + $"Adds {pv.New} new file(s), replaces {pv.Replace} existing file(s). "
                   + "Your original assets stay saved as vanilla, so you can switch back at any time.") != true)
            return;

        // Installing copies the whole vanilla tree and overlays the mod - for a real game that is
        // gigabytes - so it runs off the UI thread behind a busy cursor instead of freezing the window.
        string root = _installDir!;
        InstallReport? rep = await RunBusyAsync(() => Task.FromResult(
            ModInstall.InstallFromTpm(root, path, meta.Game, activate: true)));
        if (rep is null) return;
        Refresh();
        SelectByName(meta.Name);
        Say($"Installed and now playing \u201c{meta.Name}\u201d (+{rep.Added} new, {rep.Replaced} replaced).", false);
    }

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        var m = Selected();
        if (m is null) { Say("Choose a mod below first.", true); return; }
        if (m.IsActive) { Say($"\u201c{(m.IsVanilla ? "Vanilla" : m.Name)}\u201d is already playing.", false); return; }
        Guard(() =>
        {
            ModInstall.Activate(_installDir!, m.FolderName);
            Refresh();
            SelectByName(m.Name);
            Say($"Now playing \u201c{(m.IsVanilla ? "Vanilla" : m.Name)}\u201d.", false);
        });
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (Confirm("Switch the game back to its original (vanilla) assets? Any installed mod stays "
                  + "listed here and can be played again.") != true) return;
        Guard(() =>
        {
            ModInstall.RevertVanilla(_installDir!);
            Refresh();
            Say("The game is back to its original assets.", false);
        });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        var m = Selected();
        if (m is null) { Say("Choose a mod to delete.", true); return; }
        if (m.IsVanilla) { Say("The original assets backup cannot be deleted.", true); return; }
        if (m.IsActive) { Say("Play a different mod (or revert to vanilla) before deleting this one.", true); return; }
        if (Confirm($"Delete the installed mod \u201c{m.Name}\u201d?\n\n"
                  + "This removes its folder from the game. Your original assets stay untouched, and "
                  + "any other installed mod is left in place.") != true) return;

        string root = _installDir!, folder = m.FolderName, who = m.Name;
        // Deleting a merged copy is a whole-tree delete (again, gigabytes), so it runs off the UI thread.
        bool done = await RunBusyAsync(() =>
        {
            ModInstall.Delete(root, folder);
            return Task.FromResult(true);
        }) == true;
        if (done) { Refresh(); Say($"Deleted \u201c{who}\u201d.", false); }
    }

    // ------------------------------------------------------------------ util --

    private bool Ready()
    {
        if (_installDir is null || !ModInstall.IsManageable(_installDir))
        {
            Say("Point at a game folder first.", true);
            return false;
        }
        return true;
    }

    private void Guard(Action work)
    {
        Cursor = Cursors.Wait;
        try { work(); }
        catch (ModInstallException ex) { Say(ex.Message, true); }
        catch (Exception ex) { Say($"Could not do that: {ex.Message}", true); }
        finally { Cursor = Cursors.Arrow; }
    }

    // Runs a heavy mod operation off the UI thread behind a busy cursor, disabling the controls so a
    // long copy / diff / delete cannot be re-entered, and funnels any failure into one plain sentence.
    // Returns default (null / false) when the work threw, so callers can skip their UI follow-up.
    private async Task<T?> RunBusyAsync<T>(Func<Task<T>> work)
    {
        SetBusy(true);
        try { return await Task.Run(work); }
        catch (ModInstallException ex) { Say(ex.Message, true); return default; }
        catch (Exception ex) { Say($"Could not do that: {ex.Message}", true); return default; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool on)
    {
        Cursor = on ? Cursors.Wait : Cursors.Arrow;
        _game.IsEnabled = !on;
        _mods.IsEnabled = !on;
        _actions.IsEnabled = !on;
    }

    private void SelectByName(string name)
    {
        for (int i = 0; i < _installed.Count; i++)
            if (string.Equals(_installed[i].Name, name, StringComparison.OrdinalIgnoreCase)) { _mods.SelectedIndex = i; return; }
    }

    private string GameName(string key)
    {
        foreach (var (k, n) in Games) if (k == key) return n;
        return key;
    }

    private void Say(string text, bool bad) => _detail.Text = text;

    private bool? Confirm(string message) =>
        MessageBox.Show(this, message, "Manage mods", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
