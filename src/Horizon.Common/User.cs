using System.Security.Cryptography;

namespace Horizon.Common;

/// <summary>
/// An employee registered at the station. <see cref="Id"/> is the employee number
/// typed at sign-in; <see cref="Level"/> is what that employee is allowed to reach.
/// The sign-in password is <b>not</b> here — it belongs to the level (shared by
/// everyone at that level, see <see cref="UserStore.SignIn"/>), so an employee record
/// carries no secret and can be added or removed freely.
///
/// See <c>docs/product-and-traceability-design.md</c>. The record is both the access
/// identity and the traceability identity: it is stamped onto every DUT run.
/// </summary>
public sealed record User
{
    /// <summary>Employee number — typed at sign-in and stamped onto DUT runs. Required.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable name shown in the identity chip and the DUT record.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>What this employee may reach. The sign-in level must match this exactly.</summary>
    public AccessLevel Level { get; init; } = AccessLevel.Operator;

    /// <summary>
    /// Legacy per-user PIN, still used by the Horizon.UI login. FAAM.UI authenticates
    /// against the level password instead and ignores this.
    /// </summary>
    public string Pin { get; init; } = string.Empty;

    /// <summary>Inactive users cannot sign in but are kept for historical traceability.</summary>
    public bool Active { get; init; } = true;
}

/// <summary>
/// One level's shared sign-in password, stored as a PBKDF2 verifier rather than in
/// clear. An entry that is absent — or has an empty <see cref="Hash"/> — means the
/// level has <b>no</b> password set and signs in on the employee number alone.
/// </summary>
public sealed record LevelCredential
{
    /// <summary>Base64 random salt, unique per level.</summary>
    public string Salt { get; init; } = string.Empty;

    /// <summary>Base64 PBKDF2-SHA256 derived key. Empty = no password on this level.</summary>
    public string Hash { get; init; } = string.Empty;
}

/// <summary>
/// Loads / saves the station's operator directory (<c>users.json</c> in the config
/// directory — machine-global, shared by every front-end, alongside
/// <c>app_settings.json</c>): the employee records plus one password per
/// <see cref="AccessLevel"/>.
///
/// Sign-in (<see cref="SignIn"/>) takes all three fields the login screen collects —
/// level, employee number, level password — and only succeeds when the employee
/// exists, is active, is registered at exactly that level, and the level's password
/// matches.
///
/// <para><b>Upgrade from the pre-level directory.</b> A file written before levels
/// existed has no <c>SchemaVersion</c>; every user in it is promoted to
/// <see cref="AccessLevel.Admin"/> on first load, because those users had
/// unrestricted access before the gate was introduced. Locking the station down is
/// then an explicit act (demote people in Settings), never a silent side effect of
/// installing a new build that would otherwise lock everyone out of Settings.</para>
/// </summary>
public sealed class UserStore
{
    private const string FileName = "users";

    /// <summary>Current on-disk schema. 0 / absent = the pre-level directory.</summary>
    private const int CurrentSchema = 1;

    // PBKDF2 work factor. High enough to be worth doing, low enough that the login
    // dialog stays instant on a shop-floor PC.
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    private readonly string _dir;
    private UsersFile _file;

    public UserStore(string? configDir = null)
    {
        _dir = configDir ?? ConfigLoader.GetConfigDirectory();
        _file = Load();
    }

    /// <summary>All active users, in display order.</summary>
    public IReadOnlyList<User> ActiveUsers
        => _file.Users.Where(u => u.Active && !string.IsNullOrWhiteSpace(u.Id))
                      .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                      .ToList();

    /// <summary>Every user, active or not — the Settings directory editor's source.</summary>
    public IReadOnlyList<User> AllUsers
        => _file.Users.OrderBy(u => u.Level)
                      .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                      .ToList();

    /// <summary>Finds a user by employee number (case-insensitive), or null.</summary>
    public User? Find(string id)
        => _file.Users.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the supplied PIN matches the user (or the user has no PIN). Horizon.UI login.</summary>
    public bool VerifyPin(string id, string pin)
    {
        var user = Find(id);
        if (user == null) return false;
        return string.IsNullOrEmpty(user.Pin) || string.Equals(user.Pin, pin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authenticates one sign-in attempt. Returns the matched <see cref="User"/>, or a
    /// failure whose message is safe to show on the login screen.
    /// </summary>
    public Result<User> SignIn(AccessLevel level, string employeeId, string password)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return Result<User>.Fail(ErrorCode.InvalidParameter, "Enter your employee number.");

        var user = Find(employeeId.Trim());

        // One message for "no such employee", "inactive" and "wrong level": which of the
        // three it was is not the operator's business, and saying so would let anyone
        // enumerate the directory from the login screen.
        if (user == null || !user.Active || user.Level != level)
            return Result<User>.Fail(ErrorCode.OperationFailed,
                "Employee number is not registered at that level.");

        if (!VerifyLevelPassword(level, password))
            return Result<User>.Fail(ErrorCode.OperationFailed, $"Incorrect {level} password.");

        return Result<User>.Ok(user);
    }

    /// <summary>True when <paramref name="level"/> has a password set (an unset level signs in freely).</summary>
    public bool HasLevelPassword(AccessLevel level)
        => _file.LevelPasswords.TryGetValue(level.ToString(), out var c) && !string.IsNullOrEmpty(c.Hash);

    /// <summary>Checks a password against a level's stored verifier. An unset level accepts anything.</summary>
    public bool VerifyLevelPassword(AccessLevel level, string password)
    {
        if (!HasLevelPassword(level)) return true;

        var cred = _file.LevelPasswords[level.ToString()];
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(cred.Salt);
            expected = Convert.FromBase64String(cred.Hash);
        }
        catch (FormatException)
        {
            return false;   // corrupt verifier — refuse rather than let everyone in
        }

        var actual = Derive(password ?? string.Empty, salt, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Sets (or, with an empty <paramref name="password"/>, clears) a level's shared
    /// password. Does not save — call <see cref="Save"/>.
    /// </summary>
    public void SetLevelPassword(AccessLevel level, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            _file.LevelPasswords.Remove(level.ToString());
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        _file.LevelPasswords[level.ToString()] = new LevelCredential
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt, KeyBytes)),
        };
    }

    /// <summary>
    /// Adds an employee. Fails on a blank or duplicate employee number. Does not save —
    /// call <see cref="Save"/>.
    /// </summary>
    public Result AddUser(User user)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.Id))
            return Result.Fail(ErrorCode.InvalidParameter, "Employee number is required.");
        if (Find(user.Id) != null)
            return Result.Fail(ErrorCode.InvalidParameter, $"Employee {user.Id} is already registered.");

        _file.Users.Add(user with
        {
            Id = user.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Id.Trim() : user.DisplayName.Trim(),
        });
        return Result.Ok();
    }

    /// <summary>
    /// Removes an employee from the directory. Past DUT runs keep the employee number
    /// they were stamped with, so history survives the deletion. Refuses to remove the
    /// last Admin — that would lock the station out of its own Settings. Does not save.
    /// </summary>
    public Result RemoveUser(string id)
    {
        var user = Find(id);
        if (user == null)
            return Result.Fail(ErrorCode.FileNotFound, $"Employee {id} is not registered.");

        if (user.Level == AccessLevel.Admin
            && _file.Users.Count(u => u.Level == AccessLevel.Admin && u.Active) <= 1)
            return Result.Fail(ErrorCode.OperationFailed,
                "This is the last Admin — add another before removing this one.");

        _file.Users.Remove(user);
        return Result.Ok();
    }

    /// <summary>Writes the directory to <c>users.json</c>.</summary>
    public void Save() => ConfigLoader.SaveJson(_dir, FileName, _file);

    /// <summary>Re-reads the directory from disk (re-seeding if absent).</summary>
    public void Reload() => _file = Load();

    private static byte[] Derive(string password, byte[] salt, int length)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, length);

    private UsersFile Load()
    {
        var file = ConfigLoader.LoadJson<UsersFile>(_dir, FileName);
        if (file == null) return Seed();
        return file.SchemaVersion < CurrentSchema ? Upgrade(file) : file;
    }

    /// <summary>
    /// Promotes a pre-level directory: everyone in it had unrestricted access before
    /// the gate existed, so everyone becomes Admin. Persisted immediately so the
    /// promotion happens once.
    /// </summary>
    private UsersFile Upgrade(UsersFile file)
    {
        var upgraded = new UsersFile
        {
            SchemaVersion = CurrentSchema,
            Users = file.Users.Select(u => u with { Level = AccessLevel.Admin }).ToList(),
            LevelPasswords = file.LevelPasswords,
        };
        ConfigLoader.SaveJson(_dir, FileName, upgraded);
        return upgraded;
    }

    /// <summary>
    /// First run: one Admin employee and no level passwords, so the station is usable
    /// out of the box and the first thing an Admin does is set the passwords.
    /// </summary>
    private UsersFile Seed()
    {
        var seeded = new UsersFile
        {
            SchemaVersion = CurrentSchema,
            Users = new List<User>
            {
                new() { Id = "0000", DisplayName = "Administrator", Level = AccessLevel.Admin },
            },
        };
        ConfigLoader.SaveJson(_dir, FileName, seeded);
        return seeded;
    }

    private sealed record UsersFile
    {
        /// <summary>0 / absent = written before access levels existed; see <see cref="Upgrade"/>.</summary>
        public int SchemaVersion { get; init; }

        public List<User> Users { get; init; } = new();

        /// <summary>Level name (<see cref="AccessLevel"/>) → its shared password verifier.</summary>
        public Dictionary<string, LevelCredential> LevelPasswords { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
