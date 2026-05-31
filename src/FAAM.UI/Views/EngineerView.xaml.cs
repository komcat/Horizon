using System.Windows.Controls;
using System.Windows.Threading;
using FAAM.UI.ViewModels;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a non-coder visual process-sequence editor (command
/// palette, flow-chart of step blocks, per-block parameter panel). Builds, saves
/// and runs Layer 2 <c>.hwf</c> programs via the <see cref="ProcessEditorViewModel"/>.
/// </summary>
public partial class EngineerView : UserControl
{
    private readonly ProcessEditorViewModel _vm;

    public EngineerView(EngineHost host)
    {
        InitializeComponent();
        _vm = new ProcessEditorViewModel(host);
        DataContext = _vm;

        // Keep the newest log line in view. OnLog marshals to the UI thread before
        // mutating Log, so CollectionChanged fires here on the dispatcher — but
        // ScrollIntoView must be DEFERRED: calling it inside the CollectionChanged
        // notification re-enters the items pipeline (container generation) while the
        // tree walk is in progress and throws. Background priority runs it after.
        _vm.Log.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var n = LogList.Items.Count;
                if (n > 0)
                    LogList.ScrollIntoView(LogList.Items[n - 1]);
            }));
    }

    /// <summary>Subscribes the run/log hooks and refreshes the program list — called by the shell on show.</summary>
    public void Activate() => _vm.Activate();

    /// <summary>Detaches the run/log hooks — called by the shell when leaving the page.</summary>
    public void Deactivate() => _vm.Deactivate();
}
