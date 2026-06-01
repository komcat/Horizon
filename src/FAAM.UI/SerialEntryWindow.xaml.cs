using System.Windows;
using System.Windows.Input;

namespace FAAM.UI;

/// <summary>
/// Modal serial-number entry — the WPF face of the <c>dut.promptSerial</c> step.
/// Mirrors the Run page's DUT serial field: the operator types or scans a serial,
/// then OK (or Enter). Cancel returns no serial, which fails the program run.
/// </summary>
public partial class SerialEntryWindow : Window
{
    public SerialEntryWindow(string title, string message, string? defaultValue)
    {
        InitializeComponent();
        PromptTitle.Text = string.IsNullOrWhiteSpace(title) ? "DUT Serial" : title;
        PromptMessage.Text = message;
        SerialBox.Text = defaultValue ?? string.Empty;
        // Focus + select so a scanner gun or quick retype lands in the box.
        Loaded += (_, _) => { SerialBox.Focus(); SerialBox.SelectAll(); };
    }

    /// <summary>The trimmed serial the operator entered (valid only when DialogResult is true).</summary>
    public string Serial => SerialBox.Text?.Trim() ?? string.Empty;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SerialBox.Text))
            return;   // a blank serial is not a valid entry — keep the dialog open
        DialogResult = true;
    }

    private void OnSerialKeyDown(object sender, KeyEventArgs e)
    {
        // Many barcode scanners append Enter — treat it as OK.
        if (e.Key == Key.Enter)
            OnOk(sender, e);
    }
}
