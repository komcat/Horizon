using System.Windows;

namespace Horizon.Station.UI;

/// <summary>
/// Modal yes/no dialog for a workflow <c>flow.askUser</c> step — shown by
/// <see cref="OperatorPrompt"/>. Confirm continues the run; Cancel declines it.
/// </summary>
public partial class OperatorPromptWindow : Window
{
    public OperatorPromptWindow(string title, string message)
    {
        InitializeComponent();
        PromptTitle.Text = string.IsNullOrWhiteSpace(title) ? "Confirm" : title;
        PromptMessage.Text = message;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
