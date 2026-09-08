using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoidClip.Views;

public partial class CategoryDialog : Window
{
    /// <summary>Palette drawn from the obsidian theme, plus a few neighbours.</summary>
    private static readonly string[] Palette =
    {
        "#8B3DFF", "#A855F7", "#5B21B6", "#C026D3",
        "#FF2E63", "#FF5C7A", "#C1123F", "#F97316",
        "#3B82F6", "#38BDF8", "#1D4ED8", "#06B6D4",
        "#22C55E", "#EAB308", "#94A3B8", "#E11D48"
    };

    private string _selected = Palette[0];
    private readonly List<Border> _swatches = new();

    public string CategoryName => NameBox.Text.Trim();
    public string CategoryColor => _selected;

    public CategoryDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            if (e.Key == Key.Enter) Ok_Click(null, null);
        };
        BuildSwatches();
    }

    private void BuildSwatches()
    {
        foreach (var hex in Palette)
        {
            var col = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(col),
                Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = hex
            };
            b.MouseLeftButtonUp += (s, e) => Select(hex);
            _swatches.Add(b);
            Swatches.Children.Add(b);
        }
        Select(_selected);
    }

    private void Select(string hex)
    {
        _selected = hex;
        foreach (var s in _swatches)
        {
            var on = (string)s.Tag == hex;
            s.BorderBrush = on ? Brushes.White : Brushes.Transparent;
            s.Effect = on ? (System.Windows.Media.Effects.Effect)FindResource("GlowPurple") : null;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    // ──────────────────────────────────────────────────────────
    public static (string name, string color)? Show(Window owner, string title, string okText,
                                                    string name = "", string color = null)
    {
        var d = new CategoryDialog { Owner = owner };
        d.TitleText.Text = title;
        d.OkBtn.Content = okText;
        d.NameBox.Text = name;
        if (!string.IsNullOrWhiteSpace(color)) d.Select(color);
        d.Loaded += (s, e) => { d.NameBox.Focus(); d.NameBox.SelectAll(); };
        return d.ShowDialog() == true ? (d.CategoryName, d.CategoryColor) : null;
    }
}
