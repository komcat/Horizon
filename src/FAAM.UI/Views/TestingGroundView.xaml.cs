using System.Windows.Controls;
using FAAM.UI.Siphog;

namespace FAAM.UI.Views;

/// <summary>
/// Engineer &gt; Testing Ground tab — a scratch surface for experimental alignment
/// routines that aren't yet promoted to a first-class page or workflow step. It
/// currently hosts the <see cref="SpiralRunPanel"/> (constant-pitch XY spiral
/// search) and the <see cref="RasterClimbPanel"/> (Z-stepped XY raster peak-hold);
/// further widgets drop into the same <c>WidgetHost</c> WrapPanel.
/// </summary>
public partial class TestingGroundView : UserControl
{
    private readonly SpiralRunPanel _spiral = new();
    private readonly RasterClimbPanel _raster = new();

    public TestingGroundView(EngineHost host)
    {
        InitializeComponent();
        _spiral.Attach(host);
        _raster.Attach(host);
        SpiralHost.Content = _spiral;
        RasterHost.Content = _raster;
    }

    /// <summary>
    /// Supplies the shared SiPhOG client to the widgets that need its command channel
    /// (the raster climb's SLED guard) or its target SAG power. Called by the shell once
    /// the Run page has built the connection.
    /// </summary>
    public void AttachSiphog(SiphogClient siphog) => _raster.AttachSiphog(siphog);

    /// <summary>Called when the Testing Ground tab is selected — refreshes live readouts.</summary>
    public void Activate() => _raster.Activate();

    /// <summary>Called when the tab / Engineer page is left — stops any in-flight experiment.</summary>
    public void Deactivate()
    {
        _spiral.Deactivate();
        _raster.Deactivate();
    }
}
