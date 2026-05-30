using System.Windows;
using System.Windows.Controls;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI.Views;

/// <summary>
/// The Engineer page — a placeholder canvas for engineering tools, reached from
/// the home launcher's Engineer tile.
///
/// Currently hosts a TEMPORARY motion-firing test: set gantry-main to 1 mm/s and
/// round-trip it PIC → FAU → PIC (coordinates resolved from the active product's
/// named positions). Remove the test button + <see cref="OnGantryTestClick"/>
/// once motion is verified on the bench.
/// </summary>
public partial class EngineerView : UserControl
{
    private const string Device = "gantry-main";
    private const double TestVelocity = 1.0;   // mm/s

    private readonly EngineHost _host;

    public EngineerView(EngineHost host)
    {
        _host = host;
        InitializeComponent();
    }

    private async void OnGantryTestClick(object sender, RoutedEventArgs e)
    {
        GantryTestButton.IsEnabled = false;
        try
        {
            // Resolve the two named positions from the active (FAAM) product.
            var pic = _host.Process.GetPosition(Device, "PIC");
            var fau = _host.Process.GetPosition(Device, "FAU");
            if (pic == null || fau == null)
            {
                SetStatus(LogLevel.Warning, "PIC/FAU not taught in the FAAM product — teach them first.");
                return;
            }

            // 1 mm/s for the whole round trip (set directly, not per-position).
            SetStatus(LogLevel.Info, $"Setting {Device} velocity → {TestVelocity} mm/s…");
            var vel = await _host.Engine.Motion.SetDeviceVelocityAsync(Device, TestVelocity);
            if (!vel.IsSuccess) { SetStatus(LogLevel.Warning, $"Velocity set failed: {vel.Message}"); return; }

            SetStatus(LogLevel.Info, "Moving → FAU…");
            var toFau = await _host.Engine.Motion.MoveToPositionAsync(Device, fau.Coordinates);
            if (!toFau.IsSuccess) { SetStatus(LogLevel.Warning, $"Move → FAU failed: {toFau.Message}"); return; }

            SetStatus(LogLevel.Info, "Moving → PIC…");
            var toPic = await _host.Engine.Motion.MoveToPositionAsync(Device, pic.Coordinates);
            if (!toPic.IsSuccess) { SetStatus(LogLevel.Warning, $"Move → PIC failed: {toPic.Message}"); return; }

            SetStatus(LogLevel.Info, $"Round trip complete: PIC → FAU → PIC @ {TestVelocity} mm/s.");
        }
        catch (Exception ex)
        {
            SetStatus(LogLevel.Error, $"Test error: {ex.Message}");
        }
        finally
        {
            GantryTestButton.IsEnabled = true;
        }
    }

    /// <summary>Updates the on-page status line and mirrors it into the shared engine log.</summary>
    private void SetStatus(LogLevel level, string message)
    {
        StatusText.Text = message;
        _host.Log.Add(new LogEntry(level, DateTimeOffset.Now, "Engineer", message));
    }
}
