using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TastyEditor.Core.Levels;

namespace TastyEditor.App.Panels;

/// <summary>First-run setup dialog (R4 stability checklist). On the very first launch it shows which
/// of the supported games were found on this machine and where their level folders live, so a new
/// modder knows the editor can already see their installs before they open anything.</summary>
///
/// <para>It is deliberately read-only about the file system: it checks for the existence of the game
/// and level folders the same way the verification sweeps resolve them (an environment override first,
/// then the libraries the game platform reports), and nothing else. It never starts a game, never
/// writes to a game folder, and there is nothing here that has to be configured to use the editor —
/// a level can be opened by path regardless of what this list says. Its whole job is to answer one
/// question, "did it find my games?", and then get out of the way; it is shown once and dismissed for
/// good (the choice is remembered in the editor's own settings file).</para>
public sealed class FirstRunWindow : Window
{
    // The install folders the editor looks for, and where each level folder sits inside one.
    private static readonly (string Key, string Name)[] Games = GameInstall.KnownGames();

    private readonly StackPanel _list = new() { Margin = new Thickness(16, 4, 16, 4) };
    private int _found;

    public FirstRunWindow()
    {
        Title = "Welcome to TastyEdit";
        Width = 560;
        Height = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // A Window subclass does not pick up the implicit WindowStyle, so point the chrome at the
        // theme brushes by key or its background disagrees with the app-wide text colour.
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);

        var intro = new TextBlock
        {
            Text = "TastyEdit reads level files straight from your game installs. Here is what it "
                + "found on this machine. Nothing is changed and no game is started \u2014 you can also "
                + "just open any .bin file by hand.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 4),
        };

        var heading = new TextBlock
        {
            Text = "Detected installs",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 12, 16, 2),
        };

        Scan();

        var re = new Button { Content = "Re-scan", Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
        re.Click += (_, _) => Scan();

        var hint = new TextBlock
        {
            Text = "A game you own but did not find here? Start it once so its folder exists, or set the "
                + "editor\u2019s override for that game to point at its level folder. Neither is required "
                + "to open a level by path.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.72,
            Margin = new Thickness(16, 10, 16, 4),
        };

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 16),
        };
        var done = new Button { Content = "Get started", Width = 110, IsDefault = true, IsCancel = true };
        done.Click += (_, _) => Close();
        btns.Children.Add(re);
        btns.Children.Add(new Control { Width = 8 });   // spacer; keeps Re-scan left, Done right
        btns.Children.Add(done);

        var body = new StackPanel();
        body.Children.Add(intro);
        body.Children.Add(heading);
        body.Children.Add(_list);
        body.Children.Add(hint);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        });
        Content = root;
    }

    // Rebuild the listing from a fresh folder scan. Detection is folder-presence only, so this can be
    // pressed again after installing a game without relaunching.
    private void Scan()
    {
        _list.Children.Clear();
        _found = 0;
        foreach (var (key, name) in Games)
        {
            string? dir = GameInstall.InstallDir(key);
            if (dir is not null) _found++;
            _list.Children.Add(Row(name, dir));
        }

        var summary = new TextBlock
        {
            Text = _found == 0
                ? "No installs were found \u2014 you can still open a level by browsing to its file."
                : $"Found {_found} of {Games.Length} installs.",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 8, 16, 0),
            Foreground = _found == 0 ? Brushes.DarkOrange : Brushes.DarkGreen,
        };
        _list.Children.Add(summary);
    }

    // One game: its name, then either the folder it lives in with its level path, or a plain "not
    // found". Written as a two-line block so a long path wraps instead of clipping.
    private static UIElement Row(string name, string? installDir)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = installDir is null ? $"\u25cb  {name}" : $"\u25cf  {name}",
            FontWeight = FontWeights.SemiBold,
            Foreground = installDir is null ? Brushes.Gray : Brushes.DarkGreen,
        });
        string detail = installDir is null
            ? "not found on this machine"
            : installDir;
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 1, 0, 0),
        });
        return panel;
    }
}
