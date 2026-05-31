using System.Windows;
using System.Windows.Input;
using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// Operator login — pick an operator from the station directory (<c>users.json</c>)
/// and confirm with a PIN. On success the operator is pushed into
/// <see cref="Session"/> (and so to Layer 2 for DUT traceability) and the dialog
/// returns <c>true</c>.
///
/// Used two ways: as a gate after hardware connect (first login), and as a
/// re-login dialog from the shell after logout. Either way the engine stays up —
/// this window only sets identity. <see cref="ExitButton"/> returns <c>false</c>
/// (the caller decides whether that exits the app or just stays logged out).
/// </summary>
public partial class LoginWindow : Window
{
    private readonly EngineHost _host;

    public LoginWindow(EngineHost host)
    {
        _host = host;
        InitializeComponent();

        OperatorCombo.ItemsSource = host.Users.ActiveUsers;
        if (OperatorCombo.Items.Count > 0)
            OperatorCombo.SelectedIndex = 0;

        Loaded += (_, _) => PinBox.Focus();
    }

    private void OnOperatorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        PinBox.Clear();
        PinBox.Focus();
    }

    private void OnPinChanged(object sender, RoutedEventArgs e) => ErrorText.Text = string.Empty;

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnLogin(sender, e);
    }

    private void OnLogin(object sender, RoutedEventArgs e)
    {
        if (OperatorCombo.SelectedItem is not User user)
        {
            ErrorText.Text = "Select an operator.";
            return;
        }

        if (!_host.Users.VerifyPin(user.Id, PinBox.Password))
        {
            ErrorText.Text = "Incorrect PIN.";
            PinBox.Clear();
            PinBox.Focus();
            return;
        }

        _host.Session.LogIn(user);
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
