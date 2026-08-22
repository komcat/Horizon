namespace Horizon.Common;

/// <summary>
/// What a signed-in operator is allowed to reach. Levels are <b>ranked</b>: a screen
/// or action declares the minimum level it needs, and anyone at or above it gets in
/// (compare with <c>&gt;=</c> — the numeric values are the ordering and must not be
/// reshuffled). Every user record carries exactly one level, and each level has its
/// own shared sign-in password held in <c>users.json</c>.
///
/// The station-floor mapping (FAAM.UI):
/// <list type="bullet">
///   <item><see cref="Operator"/> — Home + Run: execute the process on units.</item>
///   <item><see cref="Technician"/> — + Machine and Vision Profile: manual hardware
///     control and vision-recipe authoring.</item>
///   <item><see cref="Engineer"/> — + Engineer (sequence editor), Repository, Script
///     and Calibration: change what the machine does.</item>
///   <item><see cref="Admin"/> — + the Settings section: machine identity, the user
///     directory, and the level passwords themselves.</item>
/// </list>
/// This is a shop-floor access gate, not a security boundary: the app runs as the
/// logged-in Windows user and the config files are readable on the machine. It stops
/// the wrong person changing the process, not a determined attacker.
/// </summary>
public enum AccessLevel
{
    Operator = 0,
    Technician = 1,
    Engineer = 2,
    Admin = 3,
}
