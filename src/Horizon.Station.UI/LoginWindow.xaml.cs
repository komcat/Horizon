using System.Windows;
using System.Windows.Input;
using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Station.UI;

/// <summary>
/// Sign-in — the gate in front of the whole shell. The operator declares a
/// <see cref="AccessLevel"/>, types their employee number, and enters that
/// <b>level's</b> shared password (see <see cref="UserStore.SignIn"/>): the number
/// says who is at the machine, the level says what they may reach, and the password
/// proves they are entitled to that level.
///
/// On success the user is pushed into <see cref="Session"/> — which unlocks the shell
/// and stamps Layer 2 for DUT traceability — and the dialog returns <c>true</c>.
///
/// Used two ways: as the gate after hardware connect (first sign-in), and as a
/// re-login dialog from the shell after logout. Either way the engine stays up; this
/// window only sets identity. <see cref="ExitButton"/> returns <c>false</c> and the
/// caller decides whether that exits the app or just leaves the station locked.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly EngineHost _host;

    public LoginWindow(EngineHost host)
    {
        _host = host;
        InitializeComponent();

        LevelCombo.ItemsSource = Enum.GetValues<AccessLevel>();
        LevelCombo.SelectedItem = AccessLevel.Operator;
        UpdatePasswordHint();

        Loaded += (_, _) => EmployeeBox.Focus();
    }

    private AccessLevel SelectedLevel
        => LevelCombo.SelectedItem is AccessLevel level ? level : AccessLevel.Operator;

    private void OnLevelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ClearError();
        PasswordBox.Clear();
        UpdatePasswordHint();
    }

    /// <summary>
    /// A level with no password set signs in on the employee number alone — say so in
    /// the label rather than leaving the operator wondering what to type. The box itself
    /// stays enabled regardless, because the built-in recovery Admin needs its own
    /// password typed even when the Admin level has none set.
    /// </summary>
    private void UpdatePasswordHint()
    {
        if (PasswordLabel == null) return;   // fires once during InitializeComponent

        var set = _host.Users.HasLevelPassword(SelectedLevel);
        PasswordLabel.Text = set ? "Level password" : "Level password (none set for this level — leave blank)";

        // The box stays enabled either way: the built-in recovery Admin still needs its
        // own password typed even when the Admin level itself has none set.
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e) => ClearError();

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => ClearError();

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnLogin(sender, e);
    }

    private void ClearError()
    {
        if (ErrorText != null) ErrorText.Text = string.Empty;
    }

    private void OnLogin(object sender, RoutedEventArgs e)
    {
        var result = _host.Users.SignIn(SelectedLevel, EmployeeBox.Text, PasswordBox.Password);
        if (!result.IsSuccess)
        {
            ErrorText.Text = result.Message;
            PasswordBox.Clear();
            (string.IsNullOrWhiteSpace(EmployeeBox.Text) ? (System.Windows.Controls.Control)EmployeeBox : PasswordBox)
                .Focus();
            return;
        }

        // A recovery sign-in bypasses the level password by design, so it must never be
        // silent: warn in the log, and note that the DUT runs it produces carry the
        // reserved employee number rather than a real person.
        if (UserStore.IsBuiltInAdmin(result.Value.Id))
            _host.Log.Add(new LogEntry(LogLevel.Warning, DateTimeOffset.Now, "Login",
                "Signed in with the BUILT-IN RECOVERY ADMIN. Set a real Admin employee "
                + "and level password in Machine > Settings; runs made now are stamped "
                + $"'{UserStore.BuiltInAdminId}', not a person."));

        _host.Session.LogIn(result.Value);
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
