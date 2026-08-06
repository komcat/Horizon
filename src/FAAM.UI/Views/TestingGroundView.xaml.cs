using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer &gt; Testing Ground tab — a scratch surface for experimental alignment
/// routines that aren't yet promoted to a first-class page or workflow step. It
/// currently hosts the <see cref="SpiralRunPanel"/> (constant-pitch XY spiral
/// search); further widgets drop into the same <c>WidgetHost</c> WrapPanel.
/// </summary>
public partial class TestingGroundView : UserControl
{
    private readonly SpiralRunPanel _spiral = new();

    public TestingGroundView(EngineHost host)
    {
        InitializeComponent();
        _spiral.Attach(host);
        SpiralHost.Content = _spiral;
    }

    /// <summary>Called when the Testing Ground tab is selected. No background work to start yet.</summary>
    public void Activate() { }

    /// <summary>Called when the tab / Engineer page is left — stops any in-flight experiment.</summary>
    public void Deactivate() => _spiral.Deactivate();
}
