using Horizon.Common;
using Horizon.Workflow;

namespace Horizon.Station.UI;

/// <summary>
/// The signed-in operator for this station — both the access gate and the
/// traceability stamp.
///
/// <para><b>Access.</b> Nothing in the shell works until somebody signs in:
/// <see cref="Has"/> answers "is the current sign-in at least this level?", and the
/// shell, the Home tiles, the jog rail and the DUT header all consult it (and
/// re-consult it on <see cref="Changed"/>). Logging out drops the station back to no
/// access without touching the hardware.</para>
///
/// <para><b>Traceability.</b> Login pushes an <see cref="OperatorContext"/> down to
/// Layer 2 carrying the employee number, the level they signed in at, and the
/// station's machine number + credential string, so every DUT run records who ran the
/// unit and on which machine, under what standing. The machine identity is read from
/// <see cref="AppSettings"/> at each login, so editing it in Settings takes effect on
/// the next sign-in rather than needing a restart.</para>
/// </summary>
public sealed class Session
{
    private readonly ProcessService _process;
    private readonly AppSettingsStore _settings;

    public Session(ProcessService process, AppSettingsStore settings)
    {
        _process = process;
        _settings = settings;
    }

    /// <summary>The signed-in operator, or null when nobody is logged in.</summary>
    public User? Operator { get; private set; }

    /// <summary>Machine / station number stamped onto DUT runs — the Settings value, or the PC name.</summary>
    public string StationId
        => string.IsNullOrWhiteSpace(_settings.Current.StationId)
            ? Environment.MachineName
            : _settings.Current.StationId;

    /// <summary>The station's credential string (its signature); empty when unsigned.</summary>
    public string MachineCredential => _settings.Current.MachineCredential ?? string.Empty;

    public bool IsLoggedIn => Operator != null;

    /// <summary>The level the current operator signed in at; null when logged out.</summary>
    public AccessLevel? Level => Operator?.Level;

    /// <summary>
    /// True when somebody is signed in at <paramref name="required"/> or above — the one
    /// question every gated screen and button asks. Logged out is always false, so the
    /// station is locked by default.
    /// </summary>
    public bool Has(AccessLevel required) => Operator != null && Operator.Level >= required;

    /// <summary>Raised after login or logout so the chrome and the gates can refresh.</summary>
    public event Action? Changed;

    /// <summary>Signs an operator in and stamps Layer 2 for traceability.</summary>
    public void LogIn(User user)
    {
        Operator = user;
        _process.SetOperator(new OperatorContext(
            user.Id, user.DisplayName, StationId, user.Level.ToString(), MachineCredential));
        Changed?.Invoke();
    }

    /// <summary>Signs the current operator out; the shell locks and runs are unattributed until the next login.</summary>
    public void LogOut()
    {
        Operator = null;
        _process.SetOperator(null);
        Changed?.Invoke();
    }
}
