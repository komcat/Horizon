using System.Windows;
using System.Windows.Controls;

namespace Horizon.Station.UI.Views;

/// <summary>What the station mascot is doing — follows the station's OEE state (and startup).</summary>
public enum MascotMood
{
    /// <summary>Online, nothing executing (standby): at rest, blinks now and then.</summary>
    Awake,
    /// <summary>A step / program is executing (or the hardware is connecting): busy animation.</summary>
    Working,
    /// <summary>Idle (nothing executed for the idle threshold) or offline: faded, eyes shut, Zzz.</summary>
    Sleeping,
    /// <summary>Station declared down (or startup failed): red, a white "!".</summary>
    Down,
}

/// <summary>
/// A head's mascot — pure decoration that animates its <see cref="Mood"/>. Implemented by a
/// <see cref="FrameworkElement"/> (FAAM: <see cref="PikaAvatar"/>; DLAM: the FireDog corgi) and
/// chosen by <see cref="StationApp.Mascot"/>.
/// </summary>
public interface IStationMascot
{
    /// <summary>Shown in the shell header tooltip ("Pika — station running").</summary>
    string MascotName { get; }

    MascotMood Mood { get; set; }
}

/// <summary>
/// Placeholder the shared windows put where the mascot goes (shell header, startup screen):
/// it holds the head's mascot (<see cref="StationApp.Mascot"/>) and forwards <see cref="Mood"/>.
/// </summary>
public sealed class MascotHost : Decorator
{
    private readonly IStationMascot _mascot;

    public MascotHost()
    {
        _mascot = StationApp.Mascot();
        Child = _mascot as UIElement
            ?? throw new InvalidOperationException("StationApp.Mascot must create a UIElement.");
    }

    /// <summary>The mascot's name (for tooltips).</summary>
    public string MascotName => _mascot.MascotName;

    public MascotMood Mood
    {
        get => _mascot.Mood;
        set => _mascot.Mood = value;
    }
}
