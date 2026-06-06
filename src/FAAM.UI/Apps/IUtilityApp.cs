using System.Windows;

namespace FAAM.UI.Apps;

/// <summary>
/// Run-state of a utility app — drives its home-grid badge. Independent of whether
/// the app is the foreground (visible) screen: an app reports <see cref="Running"/>
/// or <see cref="Connected"/> while the operator is looking at a different app (the
/// "phone" springboard keeps apps alive in the background). See
/// <c>docs/utility-app-launcher-design.md</c> ("two independent axes").
/// </summary>
public enum AppRunState
{
    /// <summary>Not running / idle — default badge.</summary>
    Idle,
    /// <summary>Routine is executing (e.g. a scan / climb / correction) — yellow.</summary>
    Running,
    /// <summary>A passive connection is up (SiPhOG streaming) — green.</summary>
    Connected,
    /// <summary>The last run errored — red.</summary>
    Error,
}

/// <summary>
/// One utility app hosted by the Run-page springboard (<c>UtilityAppHost</c>). A thin
/// adapter over an existing panel: it adds the launcher metadata (id / title / glyph)
/// and a uniform, observable run-state, leaving the panel itself unchanged. The host
/// shows <see cref="Content"/> in its app area and a tile bound to <see cref="State"/>
/// / <see cref="StatusDetail"/> on the home grid.
///
/// Phase 1 is navigation + status only. Run control (<c>StartAsync</c>/<c>StopAsync</c>)
/// and the Layer 2 → Layer 3 command seam arrive in Phase 2 — see the design doc.
/// </summary>
public interface IUtilityApp
{
    /// <summary>Stable identifier used by command blocks (Phase 2): "ucorrection","faupre","hillclimb","siphog".</summary>
    string Id { get; }
    /// <summary>Display title shown on the home tile and the app-view header.</summary>
    string Title { get; }
    /// <summary>Short text glyph for the home tile (real icons are Phase 3).</summary>
    string Glyph { get; }
    /// <summary>The app's content — the existing panel, shown in the host's app area.</summary>
    UIElement Content { get; }
    /// <summary>Live run-state for the home badge. Raises <see cref="System.ComponentModel.INotifyPropertyChanged"/>.</summary>
    AppRunState State { get; }
    /// <summary>Human-readable status line for the badge / header (e.g. "Scanning…", "Disconnected").</summary>
    string? StatusDetail { get; }
}
