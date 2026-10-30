using System.Windows;

namespace Horizon.Station.UI.Views;

/// <summary>
/// A head-contributed tab on the Engineer page (FAAM: Find First Light, Testing Ground), shown
/// after the shared tabs. A tab whose <see cref="Content"/> is an <see cref="IStationPage"/> is
/// activated when selected and deactivated when left (and when the page is left).
/// </summary>
public sealed record EngineerTab(string Header, FrameworkElement Content);
