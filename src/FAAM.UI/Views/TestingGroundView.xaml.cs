using System.Windows.Controls;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer &gt; Testing Ground tab — a scratch surface for experimental alignment
/// routines that aren't yet promoted to a first-class page or workflow step. It
/// currently hosts the <see cref="SpiralRunPanel"/> (constant-pitch XY spiral
/// search); further widgets drop into the same <c>WidgetHost</c> WrapPanel.
/// The Raster Climb panel that used to sit here is DEPRECATED (do not use) and removed.
/// </summary>
public partial class TestingGroundView : UserControl, IStationPage
{
    private readonly SpiralRunPanel _spiral = new();

    public TestingGroundView(EngineHost host)
    {
        InitializeComponent();
        _spiral.Attach(host);
        SpiralHost.Content = _spiral;
    }

    /// <summary>Called when the tab / Engineer page is left — stops any in-flight experiment.</summary>
    /// <summary>Nothing runs until the operator starts a routine, so showing the tab starts nothing.</summary>
    public void Activate() { }

    public void Deactivate() => _spiral.Deactivate();
}
