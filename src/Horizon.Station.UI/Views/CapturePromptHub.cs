namespace Horizon.Station.UI.Views;

/// <summary>
/// A fan-out <see cref="ICapturePromptSurface"/> that mirrors one logical prompt onto
/// several real overlays at once — the Run-page main feed plus any open camera pop-out
/// (<see cref="DisplayWindow"/>). <see cref="Show"/> / <see cref="Hide"/> are forwarded to
/// every registered member; a member's <see cref="ICapturePromptSurface.Affirmed"/> /
/// <see cref="ICapturePromptSurface.Negated"/> is re-raised once to the single consumer
/// (the alignment panel), so answering on <em>either</em> surface drives the same run.
///
/// A late-registered member (a pop-out opened while a prompt is already up) immediately
/// catches up to the current prompt, and Remove hides the member it drops. The hub itself
/// is the surface passed to <c>AlignmentPanel.SetCapturePromptSurface</c>; it lives on
/// <see cref="EngineHost"/> so both the Run page and the pop-out window can reach it.
/// </summary>
public sealed class CapturePromptHub : ICapturePromptSurface
{
    private readonly List<ICapturePromptSurface> _members = new();
    private bool _shown;
    private string _text = "";
    private string _affirm = "Yes";
    private string _negative = "No";

    /// <summary>True while a prompt is up — the operator is being asked something (tower light blinks yellow).</summary>
    public bool IsShown => _shown;

    /// <summary>Raised when the operator presses the affirmative button on any member surface.</summary>
    public event Action? Affirmed;

    /// <summary>Raised when the operator presses the negative button on any member surface.</summary>
    public event Action? Negated;

    /// <summary>
    /// Registers an overlay. Idempotent. If a prompt is currently shown the new member is
    /// brought up to date immediately (so a pop-out opened mid-prompt still displays it).
    /// </summary>
    public void Add(ICapturePromptSurface surface)
    {
        if (surface == null || _members.Contains(surface)) return;
        _members.Add(surface);
        surface.Affirmed += OnMemberAffirmed;
        surface.Negated += OnMemberNegated;
        if (_shown) surface.Show(_text, _affirm, _negative);
        else surface.Hide();
    }

    /// <summary>Unregisters an overlay and hides it (e.g. a pop-out window closing).</summary>
    public void Remove(ICapturePromptSurface surface)
    {
        if (surface == null || !_members.Remove(surface)) return;
        surface.Affirmed -= OnMemberAffirmed;
        surface.Negated -= OnMemberNegated;
        surface.Hide();
    }

    public void Show(string text, string affirmText, string negativeText)
    {
        _shown = true;
        _text = text;
        _affirm = affirmText;
        _negative = negativeText;
        foreach (var m in _members) m.Show(text, affirmText, negativeText);
    }

    public void Hide()
    {
        _shown = false;
        foreach (var m in _members) m.Hide();
    }

    // Re-raise once to the single consumer. The consumer (alignment panel) then drives the
    // next Show / Hide through the hub, keeping every member in lock-step — this mirrors the
    // old single-surface wiring exactly, just fanned out.
    private void OnMemberAffirmed() => Affirmed?.Invoke();
    private void OnMemberNegated() => Negated?.Invoke();
}
