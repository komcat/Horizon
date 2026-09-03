using Horizon.Common;

namespace FAAM.UI;

/// <summary>
/// The one place that says which <see cref="AccessLevel"/> each FAAM screen needs.
/// Both the Home launcher (which tiles are clickable) and <c>ShellWindow</c> (which
/// navigations are honoured) read these constants, so a tile can never be enabled for
/// a screen the shell would refuse — or the reverse.
///
/// The shell re-checks on navigation rather than trusting the tile: the tiles are a
/// courtesy to the operator, the shell check is the actual gate.
///
/// <para><b>Deliberately ungated:</b> the STOP button on the jog rail and the safety
/// overlay's Acknowledge. Motion must be stoppable by whoever is standing at the
/// machine, signed in or not — a lock that can swallow an emergency stop is a worse
/// hazard than the one it prevents.</para>
/// </summary>
public static class AccessPolicy
{
    /// <summary>Run the process on units. The floor's day-to-day screen.</summary>
    public const AccessLevel Run = AccessLevel.Operator;

    /// <summary>Manual hardware control (Machine page) — moves axes and toggles outputs by hand.</summary>
    public const AccessLevel Machine = AccessLevel.Technician;

    /// <summary>Author a per-position vision recipe (the Vision Profile tab).</summary>
    public const AccessLevel VisionProfile = AccessLevel.Technician;

    /// <summary>The Vision Alignment page (Vision Profile + Gripped FAU tabs); the Gripped tab is gated further by <see cref="GrippedObject"/>.</summary>
    public const AccessLevel VisionAlignment = VisionProfile;

    /// <summary>The Gantt sequence editor — changes what the machine does.</summary>
    public const AccessLevel Engineer = AccessLevel.Engineer;

    /// <summary>Program / deck version browse + checkout.</summary>
    public const AccessLevel Repository = AccessLevel.Engineer;

    /// <summary>Write and run <c>.moon</c> scripts against live hardware.</summary>
    public const AccessLevel Script = AccessLevel.Engineer;

    /// <summary>Camera → needle calibration; rewrites a stored offset the process depends on.</summary>
    public const AccessLevel Calibration = AccessLevel.Engineer;

    /// <summary>
    /// The Machine page's Device Registration section (tool ↔ camera). Like calibration it
    /// rewrites a machine-global offset that camera→tool / tool→camera moves depend on.
    /// </summary>
    public const AccessLevel Registration = AccessLevel.Engineer;

    /// <summary>The Gripped FAU tab of Vision Alignment — teaches the object held by the registered gripper (per product).</summary>
    public const AccessLevel GrippedObject = AccessLevel.Engineer;

    /// <summary>The Part Alignment tab of Vision Alignment — fiducials + local objects that locate the part on the fixture (per product).</summary>
    public const AccessLevel PartAlignment = AccessLevel.Engineer;

    /// <summary>
    /// The Machine page's Settings section — machine identity, the user directory and
    /// the level passwords. Admin only: this is the screen that grants access.
    /// </summary>
    public const AccessLevel Settings = AccessLevel.Admin;

    /// <summary>Manual jogging from the shell rail (STOP stays available regardless).</summary>
    public const AccessLevel Jog = AccessLevel.Operator;

    /// <summary>
    /// Switching the active product. Operator-level on purpose: the floor runs whatever
    /// unit arrives next, and re-scoping to an existing product changes nothing about it.
    /// </summary>
    public const AccessLevel ProductSwitch = AccessLevel.Operator;

    /// <summary>
    /// Creating a product ("Save As…" deep-copies positions, vision profiles and
    /// programs into a new one). That is authoring a variation, not running one.
    /// </summary>
    public const AccessLevel ProductCreate = AccessLevel.Engineer;
}
