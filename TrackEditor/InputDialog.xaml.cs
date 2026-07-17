using System.Windows;

namespace TrackEditor;

public partial class InputDialog : Window
{
    public string Value => ValueBox.Text;

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Shows the dialog and returns the entered (trimmed, non-empty) text, or null if cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var dlg = new InputDialog(title, prompt, initial) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        string v = dlg.Value.Trim();
        return v.Length == 0 ? null : v;
    }
}
