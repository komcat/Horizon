namespace Horizon.Station.UI.Views;

/// <summary>
/// A show/hide prompt surface (banner text + an affirmative and a negative button) that a
/// routine can drive to ask the operator a yes/no question on top of the camera feed. The
/// <see cref="CameraPromptOverlay"/> implements it over the Run-page main feed / Zoom2×
/// inset; a consumer (e.g. the alignment panel) shows a prompt and reacts to
/// <see cref="Affirmed"/> / <see cref="Negated"/>, then hides it.
/// </summary>
public interface ICapturePromptSurface
{
    /// <summary>Shows the banner with the given text and button labels.</summary>
    void Show(string text, string affirmText, string negativeText);

    /// <summary>Hides the prompt (the default state).</summary>
    void Hide();

    /// <summary>Raised when the operator presses the affirmative (green) button.</summary>
    event Action? Affirmed;

    /// <summary>Raised when the operator presses the negative button.</summary>
    event Action? Negated;
}
