using System.Windows;
using Horizon.Workflow;

namespace Horizon.UI;

/// <summary>
/// WPF implementation of the Layer 2 operator-prompt contract. A workflow
/// <c>flow.askUser</c> step blocks on <see cref="ConfirmAsync"/> until the
/// operator answers a modal <see cref="OperatorPromptWindow"/>. Registered on
/// <see cref="IProcessService.OperatorPrompt"/> by the shell.
/// </summary>
public sealed class OperatorPrompt : IOperatorPrompt
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(false);

        // The runner calls in on a background thread — show the dialog on the UI thread.
        return dispatcher.InvokeAsync(() => Show(title, message, ct)).Task;
    }

    private static bool Show(string title, string message, CancellationToken ct)
    {
        var window = new OperatorPromptWindow(title, message)
        {
            Owner = Application.Current.MainWindow,
        };

        // A run cancellation closes the dialog (treated as a decline).
        using var registration = ct.Register(() =>
            window.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) window.Close(); }));

        var confirmed = window.ShowDialog();
        ct.ThrowIfCancellationRequested();   // surfaces a Stop as a cancellation
        return confirmed == true;
    }
}
