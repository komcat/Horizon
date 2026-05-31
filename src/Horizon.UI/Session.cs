using Horizon.Common;
using Horizon.Workflow;

namespace Horizon.UI;

/// <summary>
/// The signed-in operator for this station. Holds the current <see cref="User"/>
/// for display and pushes an <see cref="OperatorContext"/> down to Layer 2 so every
/// DUT run is stamped with who ran it and on which station. Traceability only — no
/// access control (see <c>docs/product-and-traceability-design.md</c>).
/// </summary>
public sealed class Session
{
    private readonly ProcessService _process;

    public Session(ProcessService process, string stationId)
    {
        _process = process;
        StationId = stationId;
    }

    /// <summary>The signed-in operator, or null when nobody is logged in.</summary>
    public User? Operator { get; private set; }

    /// <summary>Station / machine identity stamped onto DUT runs.</summary>
    public string StationId { get; }

    public bool IsLoggedIn => Operator != null;

    /// <summary>Raised after login or logout so the chrome can refresh.</summary>
    public event Action? Changed;

    /// <summary>Signs an operator in and stamps Layer 2 for traceability.</summary>
    public void LogIn(User user)
    {
        Operator = user;
        _process.SetOperator(new OperatorContext(user.Id, user.DisplayName, StationId));
        Changed?.Invoke();
    }

    /// <summary>Signs the current operator out; subsequent runs are unattributed until the next login.</summary>
    public void LogOut()
    {
        Operator = null;
        _process.SetOperator(null);
        Changed?.Invoke();
    }
}
