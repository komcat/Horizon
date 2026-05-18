using System.Windows;

namespace Horizon.UI;

/// <summary>
/// Pop-out live camera view — a larger window bound to the same
/// <c>CameraSectionViewModel</c> as the Machine-page Camera section, so it
/// shares the one frame pipeline. Owned by the shell window, so it closes with it.
/// </summary>
public partial class DisplayWindow : Window
{
    public DisplayWindow() => InitializeComponent();
}
