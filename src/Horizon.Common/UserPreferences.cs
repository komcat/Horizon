using System.Text.Json;

namespace Horizon.Common;

public class UserPreferences
{
    public bool AutoOpenTopView { get; set; } = true;
    public bool ShowCrosshair { get; set; } = false;
    public bool ClickToMoveEnabled { get; set; } = false;
    public bool ShowChartOverlay { get; set; } = true;
    public string? LastSelectedCamera { get; set; }
    public string? LastSelectedMeasurementDevice { get; set; }
    public bool AutoStartMeasurementPolling { get; set; } = true;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static UserPreferences Load()
    {
        var dir = ConfigLoader.GetConfigDirectory();
        var prefs = ConfigLoader.LoadJson<UserPreferences>(dir, "user_preferences");
        return prefs ?? new UserPreferences();
    }

    public void Save()
    {
        var dir = ConfigLoader.GetConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "user_preferences.json");
        var json = JsonSerializer.Serialize(this, WriteOptions);
        File.WriteAllText(path, json);
    }
}
