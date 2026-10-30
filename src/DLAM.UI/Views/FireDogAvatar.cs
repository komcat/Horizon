using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DLAM.UI.Views;

/// <summary>
/// DLAM's mascot — FireDog's corgi with a flame for a tail, ported from FireDog
/// (<c>src/ui/Widgets.cpp</c>, <c>DrawFireDog</c>): the same 28 × 14 sprite rows, colours and
/// timings. The body is the still part; the ears, the flame and the legs are laid over it per
/// frame. The animation is a function of the mood and the time; a <see cref="DispatcherTimer"/>
/// samples it and redraws only when the frame changes. Pure decoration — no I/O.
///
/// Station moods map onto FireDog's: Awake → Idle (the flame flickers, the eye blinks now and
/// then), Working → Running (trotting, the flame streams back), Sleeping → Sleeping (faded, eye
/// shut, rising z's), Down → Down (red, ears down, a blinking "!").
/// </summary>
public sealed class FireDogAvatar : FrameworkElement, IStationMascot
{
    private const int W = 28;   // sprite size, in sprite pixels
    private const int H = 14;

    // '.' empty, 'O' body, 'F' flame, 'Y' flame tip, 'S' smoke, 'W' white (the Zzz and the '!').
    // The eye is the hole at (21, 4).
    private static readonly string[] Body =
    {
        "............................",
        "............................",
        "............................",
        "...................OOOOOOO..",
        "...................OO.OOOO..",
        "....OO.............OOOOOOOOO",
        "....OO.............OOOOOOOOO",
        "....OOOOOOOOOOOOOOOOOOOOOO..",
        "....OOOOOOOOOOOOOOOOOOOOO...",
        "....OOOOOOOOOOOOOOOOOOOOO...",
        "....OOOOOOOOOOOOOOOOOOOOO...",
        ".....OOOOOOOOOOOOOOOOOOO....",
        "............................",
        "............................",
    };

    private const int EarX = 19;   // ears: 3 rows from the top, from this column
    private const int LegY = 12;   // legs: the last 2 rows
    private const int EyeX = 21, EyeY = 4;

    private static readonly string[] EarsUp = { ".O....O..", "OO...OO..", "OOO.OOO.." };
    private static readonly string[] EarsDown = { ".........", ".........", "OOO..OOO." };

    // Over the empty sky above the back: a 'z' that rises (three positions), and the '!' of a DOWN.
    private static readonly string[] Z = { "WWW", ".W.", "WWW" };
    private static readonly (int X, int Y)[] ZPos = { (7, 4), (10, 2), (13, 0) };
    private static readonly string[] Bang = { "WW", "WW", "WW", "WW", "..", "WW" };
    private const int BangX = 11;

    // 5 × 5 from the top-left corner: calm A/B, streaming A/B, smoke A/B.
    private static readonly string[][] Flame =
    {
        new[] { "..Y..", ".YFY.", ".YFFY", "..FFY", "...FF" },
        new[] { ".Y...", "..YY.", ".YFFY", ".YFFY", "...FF" },
        new[] { "Y....", "YFY..", ".FFFY", "..FFF", "...FF" },
        new[] { ".....", "YY...", "YFFY.", ".FFFY", "..FFF" },
        new[] { "..S..", ".S...", "..S..", "...S.", "....." },
        new[] { ".S...", "..S..", "...S.", "..S..", "....." },
    };

    // Standing, trot A, trot B.
    private static readonly string[][] Legs =
    {
        new[] { ".....OO.OO.......OO.OO......", ".....OO.OO.......OO.OO......" },
        new[] { ".....OO.OO.......OO.OO......", "....OO...OO.....OO...OO....." },
        new[] { ".....OO.OO.......OO.OO......", "......OOOO.........OOOO....." },
    };

    private static readonly Brush BodyNormal = Frozen(217, 119, 87);
    private static readonly Brush BodySleepy = Frozen(165, 120, 105);
    private static readonly Brush BodyDown = Frozen(200, 50, 45);
    private static readonly Brush FlameRed = Frozen(230, 70, 40);
    private static readonly Brush FlameTip = Frozen(255, 200, 60);
    private static readonly Brush Smoke = Frozen(130, 130, 130);
    private static readonly Brush White = Frozen(235, 235, 245);
    private static readonly Brush Tile = Frozen(0x1E, 0x1E, 0x1E);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MascotMood _mood = MascotMood.Awake;
    private Frame _frame;
    private bool _drawn;

    public FireDogAvatar()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);   // crisp blocks, no seams
        SnapsToDevicePixels = true;
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        Step();   // initial frame
    }

    public string MascotName => "FireDog";

    public MascotMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            Step();
        }
    }

    /// <summary>One animation frame — everything the drawing depends on.</summary>
    private readonly record struct Frame(Brush Fur, int FlameIndex, int LegsIndex, bool EarsLow, bool Blink, int Zs, bool ShowBang);

    private void Step()
    {
        var frame = Compute(_mood, _clock.Elapsed.TotalSeconds);
        if (_drawn && frame == _frame) return;
        _frame = frame;
        _drawn = true;
        InvalidateVisual();
    }

    /// <summary>FireDog's <c>DrawFireDog</c> state logic for the station moods.</summary>
    private static Frame Compute(MascotMood mood, double t)
    {
        return mood switch
        {
            MascotMood.Working => new Frame(BodyNormal, 2 + Beat(t, 0.10), 1 + Beat(t, 0.15), false, false, 0, false),
            MascotMood.Sleeping => new Frame(BodySleepy, Beat(t, 0.6), 0, false, true,
                                             1 + (int)(t % 1.8 / 0.6), false),
            MascotMood.Down => new Frame(BodyDown, Beat(t, 0.25), 0, true, false, 0, Beat(t, 0.5) == 0),
            _ => new Frame(BodyNormal, Beat(t, 0.25), 0, false, t % 3.7 < 0.12, 0, false),
        };
    }

    /// <summary>0 / 1 alternating every <paramref name="period"/> seconds.</summary>
    private static int Beat(double t, double period) => (int)(t / period) & 1;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRoundedRectangle(Tile, null, new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);

        double px = Math.Floor(Math.Min(ActualWidth / (W + 2), ActualHeight / (H + 2)));
        if (px < 1) return;
        var o = new Point(Math.Floor((ActualWidth - px * W) / 2), Math.Floor((ActualHeight - px * H) / 2));
        var f = _frame;

        Layer(dc, o, px, Body, 0, 0, f.Fur);
        Layer(dc, o, px, f.EarsLow ? EarsDown : EarsUp, EarX, 0, f.Fur);
        Layer(dc, o, px, Flame[f.FlameIndex], 0, 0, f.Fur);
        Layer(dc, o, px, Legs[f.LegsIndex], 0, LegY, f.Fur);
        if (f.Blink)
            dc.DrawRectangle(f.Fur, null, new Rect(o.X + EyeX * px, o.Y + EyeY * px, px, px));
        for (int i = 0; i < f.Zs && i < ZPos.Length; i++)
            Layer(dc, o, px, Z, ZPos[i].X, ZPos[i].Y, f.Fur);
        if (f.ShowBang)
            Layer(dc, o, px, Bang, BangX, 0, f.Fur);
    }

    private static void Layer(DrawingContext dc, Point o, double px, string[] rows, int x0, int y0, Brush body)
    {
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                var brush = rows[y][x] switch
                {
                    'O' => body,
                    'F' => FlameRed,
                    'Y' => FlameTip,
                    'S' => Smoke,
                    'W' => White,
                    _ => null,
                };
                if (brush != null)
                    dc.DrawRectangle(brush, null, new Rect(o.X + (x0 + x) * px, o.Y + (y0 + y) * px, px, px));
            }
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
