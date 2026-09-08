using System.Windows;
using System.Windows.Input;

namespace VoidClip.Views;

public partial class Prompt : Window
{
    public string Value => Input.Text;

    public Prompt()
    {
        InitializeComponent();
        MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (s, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; Close(); }
    }

    // ──────────────────────────────────────────────────────────
    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    public static string Text(Window owner, string title, string message, string initial = "")
    {
        var p = new Prompt { Owner = owner };
        p.TitleText.Text = title;
        p.MessageText.Text = message;
        p.MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        p.Input.Text = initial ?? "";
        p.Loaded += (s, e) => { p.Input.Focus(); p.Input.SelectAll(); };
        return p.ShowDialog() == true && !string.IsNullOrWhiteSpace(p.Input.Text)
            ? p.Input.Text.Trim()
            : null;
    }

    /// <summary>Yes/no confirmation.</summary>
    public static bool Confirm(Window owner, string title, string message,
                               string okText = "Confirm", bool danger = false)
    {
        var p = new Prompt { Owner = owner };
        p.TitleText.Text = title;
        p.MessageText.Text = message;
        p.Input.Visibility = Visibility.Collapsed;
        p.OkBtn.Content = okText;
        if (danger) p.OkBtn.Style = (Style)p.FindResource("DangerButton");
        p.Loaded += (s, e) => p.OkBtn.Focus();
        return p.ShowDialog() == true;
    }

    /// <summary>Message with a single dismiss button.</summary>
    public static void Info(Window owner, string title, string message)
    {
        var p = new Prompt { Owner = owner };
        p.TitleText.Text = title;
        p.MessageText.Text = message;
        p.Input.Visibility = Visibility.Collapsed;
        p.CancelBtn.Visibility = Visibility.Collapsed;
        p.OkBtn.Content = "Got it";
        p.ShowDialog();
    }
}
