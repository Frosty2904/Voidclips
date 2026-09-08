using System.Windows;
using System.Windows.Input;
using VoidClip.Services;

namespace VoidClip.Views;

public partial class HotkeyDialog : Window
{
    private string _gesture;
    private bool _cleared;

    public HotkeyDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape) { DialogResult = false; Close(); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var gesture = HotkeyService.Describe(key, Keyboard.Modifiers);
        if (gesture == null) return;   // modifier on its own — keep waiting

        _gesture = gesture;
        GestureText.Text = HotkeyService.Pretty(gesture);

        // a bare key would swallow that key everywhere in Windows
        var bare = Keyboard.Modifiers == ModifierKeys.None;
        var functionKey = key >= Key.F1 && key <= Key.F24;
        if (bare && !functionKey)
        {
            WarnText.Text = "Add Ctrl, Alt, Shift or Win — a single key would be captured system-wide.";
            WarnText.Visibility = Visibility.Visible;
            OkBtn.IsEnabled = false;
        }
        else
        {
            WarnText.Visibility = Visibility.Collapsed;
            OkBtn.IsEnabled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _cleared = true;
        _gesture = "";
        DialogResult = true;
        Close();
    }

    /// <summary>Returns the new gesture, "" to clear, or null if cancelled.</summary>
    public static string Capture(Window owner, string title, string current)
    {
        var d = new HotkeyDialog { Owner = owner };
        d.TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(current))
        {
            d._gesture = current;
            d.GestureText.Text = HotkeyService.Pretty(current);
            d.OkBtn.IsEnabled = true;
        }
        if (d.ShowDialog() != true) return null;
        return d._cleared ? "" : d._gesture;
    }
}
