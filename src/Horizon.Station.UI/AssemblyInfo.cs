using System.Windows;
using System.Windows.Markup;

// Resources live in this assembly; there is no generic theme dictionary to look up.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

// One XAML namespace for every public station-UI namespace, so a head writes
// xmlns:st="urn:horizon-station-ui" instead of one clr-namespace per folder.
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.Mvvm")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.Telemetry")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.Process")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.ViewModels")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.ViewModels.Sections")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.ViewModels.Timeline")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.Views")]
[assembly: XmlnsDefinition("urn:horizon-station-ui", "Horizon.Station.UI.Views.Sections")]
[assembly: XmlnsPrefix("urn:horizon-station-ui", "st")]
