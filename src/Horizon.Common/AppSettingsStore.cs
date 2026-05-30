namespace Horizon.Common;

/// <summary>
/// Loads, holds, and persists <see cref="AppSettings"/> (<c>app_settings.json</c>
/// in the config directory). A single instance is shared per front-end via its
/// EngineHost. <see cref="Current"/> is replaced wholesale on update / reload;
/// <see cref="Changed"/> fires so live consumers (e.g. the camera scale) refresh.
/// </summary>
public sealed class AppSettingsStore
{
    private const string FileName = "app_settings";

    private readonly string _dir;
    private AppSettings _current;

    public AppSettingsStore(string? configDir = null)
    {
        _dir = configDir ?? ConfigLoader.GetConfigDirectory();
        _current = ConfigLoader.LoadJson<AppSettings>(_dir, FileName) ?? new AppSettings();
    }

    /// <summary>The current settings. Replaced by <see cref="Update"/> / <see cref="Reload"/>.</summary>
    public AppSettings Current => _current;

    /// <summary>Raised after <see cref="Current"/> changes (update or reload).</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>Replaces the in-memory settings. Does not persist — call <see cref="Save"/>.</summary>
    public void Update(AppSettings settings)
    {
        _current = settings;
        Changed?.Invoke(_current);
    }

    /// <summary>Writes <see cref="Current"/> to <c>app_settings.json</c>.</summary>
    public void Save() => ConfigLoader.SaveJson(_dir, FileName, _current);

    /// <summary>Re-reads from disk (defaults if absent), replacing <see cref="Current"/>.</summary>
    public void Reload()
    {
        _current = ConfigLoader.LoadJson<AppSettings>(_dir, FileName) ?? new AppSettings();
        Changed?.Invoke(_current);
    }
}
