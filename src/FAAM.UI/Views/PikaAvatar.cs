using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FAAM.UI.Views;

/// <summary>What the header pika is doing — follows the station's OEE state.</summary>
public enum PikaMood
{
    /// <summary>Online, nothing executing (standby): sits and blinks now and then.</summary>
    Awake,
    /// <summary>A step / program is executing: hops and zaps continuously.</summary>
    Working,
    /// <summary>Idle (nothing executed for the idle threshold) or offline: eyes shut, Zzz.</summary>
    Sleeping,
    /// <summary>Station declared down: eyes shut on a red tile with a red "!".</summary>
    Down,
}

/// <summary>
/// The shell header mascot — a blocky pixel-art North American pika with a yellow
/// thunderbolt. The pika is a patient, precise forager that sets every stalk just so,
/// which is a fair stand-in for an active-alignment station; the bolt is the signal it
/// chases. The sprite is authored as ASCII rows (one char per block) and drawn as filled
/// squares; a slow <see cref="DispatcherTimer"/> steps the animation for the current
/// <see cref="Mood"/>. Pure decoration — no I/O, a few hundred rects per changed frame.
/// </summary>
public sealed class PikaAvatar : FrameworkElement
{
    // '#' fur · 'd' inner ear · 'o' eye · 'n' nose · 'p' cheek · '.' empty
    private static readonly string[] Body =
    {
        "..###....###..",
        ".#ddd#..#ddd#.",
        ".#ddd####ddd#.",
        "..##########..",
        ".############.",
        "##############",
        "###oo####oo###",
        "###oo####oo###",
        "#pp###nn###pp#",
        "##############",
        ".############.",
        "..##......##..",
    };

    private static readonly string[] Bolt =
    {
        "..zzz",
        ".zzz.",
        "zzzzz",
        "..zz.",
        ".zz..",
        ".z...",
    };

    private static readonly string[] SmallZ = { "zzz", ".z.", "zzz" };
    private static readonly string[] BigZ = { "zzzz", "..z.", ".z..", "zzzz" };
    private static readonly string[] Bang = { "zz", "zz", "zz", "zz", "..", "zz" };

    private const int BodyCols = 14;
    private const int SideCol = 15;            // one empty column between pika and the side glyph
    private const int BoltRow = 2;
    private const int Cols = SideCol + 5;
    private const int Rows = 13;               // 12 sprite rows + 1 row of hop headroom
    private const int EyeTopRow = 6;           // a blink / sleep closes this row of the eyes
    private const int LoopTicks = 50;          // 50 × 120 ms ≈ 6 s idle loop

    private static readonly Brush Fur = Frozen(0xF5, 0xC5, 0x18);
    private static readonly Brush InnerEar = Frozen(0xB8, 0x86, 0x0B);
    private static readonly Brush Eye = Frozen(0x1E, 0x1E, 0x1E);
    private static readonly Brush Nose = Frozen(0x5A, 0x3A, 0x1A);
    private static readonly Brush Cheek = Frozen(0xE8, 0x58, 0x4A);
    private static readonly Brush CheekZap = Frozen(0xFF, 0x2D, 0x2D);
    private static readonly Brush BoltYellow = Frozen(0xFF, 0xE4, 0x5C);
    private static readonly Brush BoltFlash = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush SleepZ = Frozen(0x90, 0xCA, 0xF9);
    private static readonly Brush BangRed = Frozen(0xFF, 0x40, 0x40);
    private static readonly Brush Tile = Frozen(0x1E, 0x1E, 0x1E);
    private static readonly Brush TileDown = Frozen(0x4A, 0x10, 0x10);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _tick;
    private int _state = -1;                   // packed frame; redraw only on change
    private PikaMood _mood = PikaMood.Awake;

    public PikaAvatar()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);   // crisp blocks, no seams
        SnapsToDevicePixels = true;
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        Step();   // initial frame
    }

    public PikaMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            Step();
        }
    }

    private int Phase => _tick % LoopTicks;

    private void Step()
    {
        _tick = (_tick + 1) % (LoopTicks * 8);
        bool working = _mood == PikaMood.Working;
        bool awake = _mood == PikaMood.Awake;
        bool eyesShut = _mood is PikaMood.Sleeping or PikaMood.Down
                     || (!working && awake && Phase is 8 or 9 or 31)
                     || (working && Phase is 20 or 21);
        bool hop = working && _tick % 8 is 0 or 1;
        bool zap = working;
        bool flash = zap && (_tick & 1) == 0;
        int zPhase = _mood == PikaMood.Sleeping ? (_tick / 5) % 4 : 0;

        int state = (eyesShut ? 1 : 0) | (hop ? 2 : 0) | (zap ? 4 : 0) | (flash ? 8 : 0)
                  | (zPhase << 4) | ((int)_mood << 8);
        if (state == _state) return;
        _state = state;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var mood = (PikaMood)((_state >> 8) & 0xF);
        bool eyesShut = (_state & 1) != 0, hop = (_state & 2) != 0, zap = (_state & 4) != 0, flash = (_state & 8) != 0;
        int zPhase = (_state >> 4) & 0x3;

        dc.DrawRoundedRectangle(mood == PikaMood.Down ? TileDown : Tile, null,
            new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);

        double cell = Math.Floor(Math.Min(ActualWidth / (Cols + 2), ActualHeight / (Rows + 1)));
        if (cell < 1) return;
        double ox = Math.Floor((ActualWidth - cell * Cols) / 2);
        double oy = Math.Floor((ActualHeight - cell * Rows) / 2);

        int top = hop ? 0 : 1;
        for (int r = 0; r < Body.Length; r++)
        {
            for (int c = 0; c < BodyCols; c++)
            {
                char ch = Body[r][c];
                if (eyesShut && r == EyeTopRow && ch == 'o') ch = '#';
                var brush = ch switch
                {
                    '#' => Fur,
                    'd' => InnerEar,
                    'o' => Eye,
                    'n' => Nose,
                    'p' => zap ? CheekZap : Cheek,
                    _ => null,
                };
                if (brush is not null)
                    dc.DrawRectangle(brush, null, new Rect(ox + c * cell, oy + (top + r) * cell, cell, cell));
            }
        }

        switch (mood)
        {
            case PikaMood.Working when zap:
                Glyph(dc, Bolt, SideCol, BoltRow, flash ? BoltFlash : BoltYellow, ox, oy, cell);
                break;
            case PikaMood.Sleeping:
                // A "z" drifting up and growing into a "Z", then starting over.
                var z = zPhase >= 2 ? BigZ : SmallZ;
                Glyph(dc, z, SideCol + (zPhase & 1), 8 - 2 * zPhase, SleepZ, ox, oy, cell);
                break;
            case PikaMood.Down:
                Glyph(dc, Bang, SideCol + 2, 3, BangRed, ox, oy, cell);
                break;
        }
    }

    private static void Glyph(DrawingContext dc, string[] glyph, int col, int row, Brush brush,
                              double ox, double oy, double cell)
    {
        for (int r = 0; r < glyph.Length; r++)
            for (int c = 0; c < glyph[r].Length; c++)
                if (glyph[r][c] == 'z' && row + r >= 0 && row + r < Rows)
                    dc.DrawRectangle(brush, null, new Rect(ox + (col + c) * cell, oy + (row + r) * cell, cell, cell));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
