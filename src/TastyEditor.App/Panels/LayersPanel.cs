using System.Windows;
using System.Windows.Controls;

namespace TastyEditor.App.Panels;

/// <summary>The levels dock: one checkbox per level (layer) of the open file, all shown
/// by default. Toggling raises <see cref="VisibilityChanged"/> so the shell can push the
/// hidden set to the viewport. Mirrors the Python LayersDock's per-level visibility —
/// level, not "layer" in the graphics sense, matching the games' own terminology.</summary>
public sealed class LayersPanel : StackPanel
{
    private readonly List<(int Layer, CheckBox Box)> _boxes = new();

    public event Action? VisibilityChanged;

    public LayersPanel()
    {
        Margin = new Thickness(8, 6, 8, 6);
    }

    /// <summary>Level indices whose checkbox is unticked (hidden from the viewport).</summary>
    public IEnumerable<int> HiddenLayers => _boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Layer);

    /// <summary>Rebuild the list for a freshly opened level with <paramref name="count"/> levels.</summary>
    public void Build(int count)
    {
        Children.Clear();
        _boxes.Clear();
        Children.Add(new TextBlock
        {
            Text = "Levels",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        for (int i = 0; i < count; i++)
        {
            var cb = new CheckBox
            {
                Content = $"Level {i + 1}",
                IsChecked = true,
                Margin = new Thickness(0, 1, 0, 1),
            };
            cb.Checked += (_, _) => VisibilityChanged?.Invoke();
            cb.Unchecked += (_, _) => VisibilityChanged?.Invoke();
            _boxes.Add((i, cb));
            Children.Add(cb);
        }
    }
}
