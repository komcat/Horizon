using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FAAM.UI;

/// <summary>
/// Confirms a station DOWN / UP change for OEE tracking. The engineer / technician types
/// their name (required — it signs the downtime record) and an optional reason (down) or
/// note (up). The signed-in operator is recorded separately by the caller's log line.
/// </summary>
public partial class StationStateWindow : Window
{
    public StationStateWindow(bool goingDown, string message)
    {
        InitializeComponent();
        PromptTitle.Text = goingDown ? "Declare station DOWN" : "Bring station UP";
        PromptMessage.Text = message;
        NoteLabel.Text = goingDown ? "Reason (optional)" : "Note — what was done (optional)";
        OkButton.Content = goingDown ? "Confirm DOWN" : "Confirm UP";
        OkButton.SetResourceReference(BackgroundProperty, goingDown ? "Danger" : "Ok");
        if (goingDown) FrameBorder.SetResourceReference(Border.BorderBrushProperty, "Danger");
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>The trimmed name entered (valid only when DialogResult is true).</summary>
    public string PersonName => NameBox.Text?.Trim() ?? string.Empty;

    /// <summary>The trimmed reason / note, or null when blank.</summary>
    public string? Note => string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Visibility = Visibility.Visible;   // a name signs the record — required
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NoteBox.Focus(); e.Handled = true; }
    }
}
