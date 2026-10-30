using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Horizon.Station.UI.Views;

/// <summary>
/// A generic, show/hide yes-no prompt that floats over a camera surface (the Run-page main
/// feed or the Zoom2× inset). A banner (white text on black) sits top-centre; a green
/// affirmative button bottom-left and a red negative button bottom-right. The centre stays
/// click-through, so MoveClick / jog on the feed still works while the prompt is up.
///
/// Two ways to consume it:
///  • event style (<see cref="ICapturePromptSurface"/>): <see cref="Show"/> a prompt and
///    react to <see cref="Affirmed"/> / <see cref="Negated"/> (the consumer hides it). This
///    is how the alignment panel mirrors its per-point Capture button onto the feed.
///  • await style: <see cref="ConfirmAsync"/> returns true/false and auto-hides on answer.
///
/// Hidden by default; <see cref="Show"/> reveals it.
/// </summary>
public partial class CameraPromptOverlay : UserControl, ICapturePromptSurface
{
    private TaskCompletionSource<bool>? _pending;

    /// <summary>Raised when the operator presses the affirmative (green) button.</summary>
    public event Action? Affirmed;

    /// <summary>Raised when the operator presses the negative (red) button.</summary>
    public event Action? Negated;

    /// <summary>Raised on either answer (<c>true</c> = affirmative).</summary>
    public event Action<bool>? Answered;

    /// <summary>True while the prompt is visible.</summary>
    public bool IsShown => Root.Visibility == Visibility.Visible;

    public CameraPromptOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Compact mode for a small surface (the Zoom2× inset): no banner, and small buttons
    /// tucked into the lower-left / lower-right corners so the image stays visible.
    /// </summary>
    public bool Compact
    {
        get => BannerBox.Visibility != Visibility.Visible;
        set
        {
            BannerBox.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            foreach (var b in new[] { YesButton, NoButton })
            {
                b.FontSize = value ? 12 : 20;
                b.MinWidth = value ? 64 : 168;
                b.Height = value ? 26 : 68;
                b.Margin = new Thickness(value ? 4 : 10);
            }
        }
    }

    /// <summary>Shows the banner with the given text and button labels (marshals to the UI thread).</summary>
    public void Show(string text, string affirmText = "Yes", string negativeText = "No")
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Show(text, affirmText, negativeText)); return; }
        BannerText.Text = text;
        YesButton.Content = affirmText;
        NoButton.Content = negativeText;
        Root.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the prompt (marshals to the UI thread).</summary>
    public void Hide()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Hide); return; }
        Root.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Await-style prompt: shows the banner and completes with <c>true</c> (affirmative) /
    /// <c>false</c> (negative or cancellation), then hides. The <see cref="Affirmed"/> /
    /// <see cref="Negated"/> events still fire for any event-style listeners.
    /// </summary>
    public Task<bool> ConfirmAsync(string text, string affirmText, string negativeText, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = ct.Register(() => { Hide(); tcs.TrySetResult(false); });
        tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        _pending = tcs;
        Show(text, affirmText, negativeText);
        return tcs.Task;
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        var p = _pending;
        _pending = null;
        if (p != null) { p.TrySetResult(true); Hide(); }   // await-style: auto-hide
        Answered?.Invoke(true);
        Affirmed?.Invoke();
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        var p = _pending;
        _pending = null;
        if (p != null) { p.TrySetResult(false); Hide(); }
        Answered?.Invoke(false);
        Negated?.Invoke();
    }
}
