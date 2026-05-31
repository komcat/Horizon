namespace Horizon.Common;

/// <summary>
/// An operator known to the station. This is a <b>traceability</b> identity, not a
/// security principal: the <see cref="Pin"/> exists only to stop accidental
/// mis-selection of who is at the machine, and is stored in clear. Roles and real
/// authentication are intentionally out of scope (see
/// <c>docs/product-and-traceability-design.md</c>).
/// </summary>
public sealed record User
{
    /// <summary>Stable identifier stamped onto DUT runs (e.g. a badge number or login). Required.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable name shown in the operator picker.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Short PIN confirming the operator at the machine. Empty = no PIN required.</summary>
    public string Pin { get; init; } = string.Empty;

    /// <summary>Inactive users are hidden from the picker but kept for historical traceability.</summary>
    public bool Active { get; init; } = true;
}

/// <summary>
/// Loads / saves the station's operator directory (<c>users.json</c> in the config
/// directory — machine-global, shared by every front-end, alongside
/// <c>app_settings.json</c>). Seeds a single default operator on first run so the
/// login screen is never empty.
/// </summary>
public sealed class UserStore
{
    private const string FileName = "users";

    private readonly string _dir;
    private UsersFile _file;

    public UserStore(string? configDir = null)
    {
        _dir = configDir ?? ConfigLoader.GetConfigDirectory();
        _file = ConfigLoader.LoadJson<UsersFile>(_dir, FileName) ?? Seed();
    }

    /// <summary>All active operators, in display order.</summary>
    public IReadOnlyList<User> ActiveUsers
        => _file.Users.Where(u => u.Active && !string.IsNullOrWhiteSpace(u.Id))
                      .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                      .ToList();

    /// <summary>Finds an operator by id (case-insensitive), or null.</summary>
    public User? Find(string id)
        => _file.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the supplied PIN matches the operator (or the operator has no PIN).</summary>
    public bool VerifyPin(string id, string pin)
    {
        var user = Find(id);
        if (user == null) return false;
        return string.IsNullOrEmpty(user.Pin) || string.Equals(user.Pin, pin, StringComparison.Ordinal);
    }

    /// <summary>Writes the directory to <c>users.json</c>.</summary>
    public void Save() => ConfigLoader.SaveJson(_dir, FileName, _file);

    /// <summary>Re-reads the directory from disk (re-seeding if absent).</summary>
    public void Reload() => _file = ConfigLoader.LoadJson<UsersFile>(_dir, FileName) ?? Seed();

    private UsersFile Seed()
    {
        var seeded = new UsersFile
        {
            Users = new List<User> { new() { Id = "operator", DisplayName = "Operator", Pin = "" } },
        };
        // Persist immediately so the file exists and can be edited by hand.
        ConfigLoader.SaveJson(_dir, FileName, seeded);
        return seeded;
    }

    private sealed record UsersFile
    {
        public List<User> Users { get; init; } = new();
    }
}
