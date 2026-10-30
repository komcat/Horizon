namespace Horizon.Station.UI.ViewModels.Sections;

/// <summary>
/// A group of head-specific settings on the Machine › Settings page (FAAM: the Run-page Scanning
/// buttons, the Start-correction flag). The page lists the head's panels after its own groups and
/// treats them like its own: <see cref="Load"/> on load / reload, <see cref="Validate"/> before
/// anything is written, <see cref="Save"/> with the page's Save button. The head registers a
/// <c>DataTemplate</c> for each panel type (its view); edits stay local until Save.
/// </summary>
public interface IHeadSettingsPanel
{
    /// <summary>Reads the panel's settings from disk (discarding unsaved edits).</summary>
    void Load();

    /// <summary>The first problem with the edits (shown as "Not saved — …"), or null when they can be saved.</summary>
    string? Validate();

    /// <summary>Writes the edits (already validated). Throws on I/O failure.</summary>
    void Save();

    /// <summary>The file(s) <see cref="Save"/> writes, for the page's "Saved to …" status (e.g. "faam_scan_panel.json").</summary>
    string SavedFiles { get; }
}
