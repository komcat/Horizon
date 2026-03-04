using System.Windows;

namespace Horizon.App;

public partial class InputDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(ResultText)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
