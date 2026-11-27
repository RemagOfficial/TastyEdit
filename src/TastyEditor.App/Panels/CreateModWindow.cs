using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Mods;

namespace TastyEditor.App.Panels;

/// <summary>The Create mod dialog (R6 authoring): name the mod, credit yourself, pick the game and
/// where the development folder lives, then make it. The folder — a <c>metadata.json</c> plus an
/// <c>assets</c> child — is what the editor then saves edited levels into until publish turns it
/// into a .tpm, so nothing here touches a game install.</summary>
public sealed class CreateModWindow : Window
{
    private static readonly (string Key, string Name)[] Games = GameInstall.KnownGames();

    private readonly TextBox _name = new();
    private readonly TextBox _author = new();
    private readonly TextBox _version = new() { Text = "1.0" };
    private readonly ComboBox _game = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _location = new();
    // Until the user edits or browses the location itself, it tracks "<name> inside this home", so
    // the common path (type a name, press Create) needs no folder juggling.
    private bool _locationAuto = true;
    private bool _syncingLoc;   // set while SyncLocation writes the box, so its handler can tell
    private string _modHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TastyEdit Mods");

    /// <summary>The project that was created, valid after Ok.</summary>
    public ModProject? Result { get; private set; }

    private string GameKey => (_game.SelectedItem as ComboItem)?.Key ?? "";
    // (Not "Name" - that is FrameworkElement's own x:Name property on a Window.)
    private string ModName => _name.Text.Trim();

    private sealed record ComboItem(string Key, string Name)
    {
        // The combo's templates render the item through ToString; without this the picker would
        // show the record's debug form instead of the game name.
        public override string ToString() => Name;
    }

    private CreateModWindow(string? preselectGame)
    {
        Title = "Create a mod";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleCombo(_game);

        foreach (var (key, name) in Games)
            _game.Items.Add(new ComboItem(key, name));
        int want = IndexOfGame(preselectGame);
        _game.SelectedIndex = want >= 0 ? want : 0;

        // Default home for dev folders: Documents\TastyEdit Mods (created only on Ok, so simply
        // opening this dialog never leaves a stray folder behind). The location IS the mod's own
        // folder - appending the name to a picked folder used to double-nest when that folder was
        // already named after the mod.
        _name.Text = "My Mod";
        SyncLocation();
        _name.TextChanged += (_, _) => { if (_locationAuto) SyncLocation(); };
        _location.TextChanged += (_, _) =>
        {
            if (_syncingLoc) return;                         // a programmatic sync, not a user edit
            _locationAuto = false;
            _modHome = Path.GetDirectoryName(_location.Text.Trim()) ?? _modHome;
        };

        var form = new StackPanel { Margin = new Thickness(14, 12, 14, 0) };
        form.Children.Add(Row("Name", _name));
        form.Children.Add(Row("Author", _author));
        form.Children.Add(Row("Version", _version));
        form.Children.Add(Row("Game", _game));

        var browse = new Button { Content = "Browse\u2026", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) => BrowseLocation();
        var locRow = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var locLabel = new TextBlock { Text = "Location", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(locLabel, 0);
        Grid.SetColumn(_location, 1);
        Grid.SetColumn(browse, 2);
        locRow.Children.Add(locLabel);
        locRow.Children.Add(_location);
        locRow.Children.Add(browse);
        form.Children.Add(locRow);

        var hint = new TextBlock
        {
            Text = "A folder is made there for the mod. While authoring, saves go into it instead of "
                 + "the game — publish packages it as a .tpm when you are done.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 8, 14, 0),
            Opacity = 0.8,
        };

        var ok = new Button { Content = "Create", Width = 96, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        ok.Click += (_, _) => CreateProject();
        var cancel = new Button { Content = "Cancel", Width = 96, IsCancel = true, Padding = new Thickness(0, 4, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 12, 14, 12) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel();
        body.Children.Add(form);
        body.Children.Add(hint);
        body.Children.Add(buttons);
        Content = body;
    }

    /// <summary>Show the dialog; returns the created project, or null if the user cancelled.</summary>
    public static ModProject? ShowAndCreate(Window owner, string? preselectGame)
    {
        var w = new CreateModWindow(preselectGame) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void SyncLocation()
    {
        _syncingLoc = true;
        _location.Text = Path.Combine(_modHome, Sanitize(ModName.Length == 0 ? "My Mod" : ModName));
        _syncingLoc = false;
    }

    private void BrowseLocation()
    {
        // The picked folder IS the mod's development folder.
        var dlg = new OpenFolderDialog { Title = "Choose the mod's development folder" };
        if (Directory.Exists(_location.Text)) dlg.InitialDirectory = _location.Text;
        if (dlg.ShowDialog(this) != true) return;
        _locationAuto = false;
        _location.Text = dlg.FolderName;
        _modHome = Path.GetDirectoryName(dlg.FolderName) ?? _modHome;
    }

    private void CreateProject()
    {
        if (ModName.Length == 0) { Fail("Give the mod a name first."); return; }
        if (GameKey.Length == 0) { Fail("Pick which game this mod is for."); return; }
        if (string.IsNullOrWhiteSpace(_location.Text)) { Fail("Pick where the mod folder should be made."); return; }

        // The location names the mod folder itself - no name appended, so no double nesting.
        string root = _location.Text.Trim();
        try
        {
            if (ModProject.TryOpen(root) is not null || Directory.EnumerateFileSystemEntries(root).Any())
            {
                if (MessageBox.Show(this,
                        $"A mod project already exists at\n{root}\n\nReplace it? Its current files are removed from the mod.",
                        "Replace existing mod", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                DeleteTree(root);
            }
            Result = ModProject.Create(root, new ModMetadata
            {
                Name = ModName,
                Author = _author.Text.Trim(),
                Version = _version.Text.Trim(),
                Game = GameKey,
            }, overwrite: true);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Fail(string why) =>
        MessageBox.Show(this, why, "Create a mod", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Trim();
    }

    private static void DeleteTree(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { Thread.Sleep(150); Directory.Delete(dir, recursive: true); }
    }

    private int IndexOfGame(string? key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        for (int i = 0; i < _game.Items.Count; i++)
            if (((ComboItem)_game.Items[i]).Key == key) return i;
        return -1;
    }

    private static UIElement Row(string label, FrameworkElement field)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(t, 0);
        Grid.SetColumn(field, 1);
        g.Children.Add(t);
        g.Children.Add(field);
        return g;
    }
}
