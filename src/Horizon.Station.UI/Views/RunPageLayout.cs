using Horizon.Common;

namespace Horizon.Station.UI.Views;

/// <summary>
/// The Run page's saved layout — the camera column width the operator dragged the splitter to,
/// in <c>%APPDATA%\Horizon\config\{file}.json</c> (<c>{ "cameraColumnWidth": 900 }</c>, null = Auto).
/// Each head names its own file (FAAM: <c>faam_run_layout</c>).
/// </summary>
public static class RunPageLayout
{
    private sealed class Layout
    {
        public double? CameraColumnWidth { get; set; }
    }

    /// <summary>The saved width, or null (Auto / none saved / unreadable — <paramref name="log"/> says why).</summary>
    public static double? LoadCameraWidth(string file, Action<string> log)
    {
        try
        {
            var w = ConfigLoader.LoadJson<Layout>(ConfigLoader.GetConfigDirectory(), file)?.CameraColumnWidth;
            return w is double v && double.IsFinite(v) ? v : null;
        }
        catch (Exception ex)
        {
            log($"Run layout: could not load {file}.json — {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves the width (null = back to Auto).</summary>
    public static void SaveCameraWidth(string file, double? width, Action<string> log)
    {
        try
        {
            ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), file, new Layout { CameraColumnWidth = width });
        }
        catch (Exception ex)
        {
            log($"Run layout: could not save {file}.json — {ex.Message}");
        }
    }
}
