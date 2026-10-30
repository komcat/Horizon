using System.Windows.Controls;

namespace Horizon.Station.UI.Views.Sections;

/// <summary>
/// Machine-page Camera section — grab / exposure controls plus an inline live
/// preview. The "Pop out" command opens a larger <see cref="DisplayWindow"/>
/// bound to the same view model, so both views share one frame pipeline.
/// </summary>
public partial class CameraSectionView : UserControl
{
    public CameraSectionView() => InitializeComponent();
}
