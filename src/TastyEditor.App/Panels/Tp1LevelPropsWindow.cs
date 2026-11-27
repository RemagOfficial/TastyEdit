using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TastyEditor.App.Panels;

/// <summary>Modal "level properties" editor for Tasty Planet 1 (the root <c>&lt;level&gt;</c>
/// element's own attributes: width, height, growthrate, victorysize, ...).</summary>
///
/// <para>The window shows the attributes exactly as the file holds them — source order, source
/// spelling — and writes back only the rows whose text actually changed. The model logic stays in
/// the host: the window receives a snapshot of (key, value) pairs and reports the edited map; the
/// host performs the verbatim <c>Tp1Edit.SetAttr</c> writes so untouched bytes, quoting and
/// indentation never move.</para>
///
/// <para>Values are free text: the level file mixes plain ints (<c>levelnum="10"</c>), floats
/// (<c>width="3000.0"</c>) and scientific notation (<c>unitmultiplier="5.52e24"</c>), so the window
/// does not parse or re-format them — what is typed is what is stored. A row whose original value
/// was numeric warns (still allows the write) if the new text cannot parse as a number.</para>
public sealed class Tp1LevelPropsWindow : Window
{
    private readonly IReadOnlyList<(string Key, string Value)> _attrs;
    private readonly Action<IReadOnlyDictionary<string, string>> _apply;
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public Tp1LevelPropsWindow(IReadOnlyList<(string Key, string Value)> attrs,
                               Action<IReadOnlyDictionary<string, string>> apply)
    {
        _attrs = attrs;
        _apply = apply;

        Title = "Level properties";
        Width = 420;
        Height = 560;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);

        var form = new StackPanel { Margin = new Thickness(12) };
        form.Children.Add(new TextBlock
        {
            Text = "The level's own settings (the <level> element).",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var (key, value) in _attrs)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = key,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(label, 0);

            var box = new TextBox
            {
                Text = value,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = value,          // the original, so OK can tell what changed
            };
            Grid.SetColumn(box, 1);
            _fields[key] = box;

            row.Children.Add(label);
            row.Children.Add(box);
            form.Children.Add(row);
        }

        _hint.Text = "Only changed rows are written; everything else in the file stays byte-for-byte.";
        _hint.Opacity = 0.72;
        _hint.Foreground = Brushes.DarkGreen;
        form.Children.Add(_hint);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 4, 12, 12),
        };
        var ok = new Button { Content = "OK", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = form,
        };
        root.Children.Add(scroll);
        Content = root;
    }

    private void Accept()
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, _) in _attrs)
        {
            var box = _fields[key];
            string text = box.Text.Trim();
            if (text.Length == 0)
            {
                Say($"\u201c{key}\u201d cannot be empty \u2014 the game needs a value there.", true);
                return;
            }
            if (!string.Equals(text, (string)box.Tag!, StringComparison.Ordinal))
            {
                // Numeric-looking originals keep a soft guard: a typo like "3O00" would load as garbage.
                if (IsNumeric((string)box.Tag!) && !IsNumeric(text))
                {
                    Say($"\u201c{key}\u201d held a number (\u201c{box.Tag}\u201d); \u201c{text}\u201d does not "
                        + "parse as one, so the game may misread it.", true);
                    return;
                }
                changes[key] = text;
            }
        }
        if (changes.Count == 0) { Close(); return; }
        _apply(changes);
        Close();
    }

    private static bool IsNumeric(string s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out _);

    private void Say(string text, bool bad)
    {
        _hint.Text = text;
        _hint.Foreground = bad ? Brushes.Firebrick : Brushes.DarkGreen;
    }
}
