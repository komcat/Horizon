using System;
using System.Collections.Generic;
using Horizon.Common;
using Horizon.Engine;

namespace FAAM.UI;

/// <summary>One saved hexapod pose (all six axes) behind a Run-page Scanning position button.</summary>
public sealed class ScanAxes
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double U { get; set; }
    public double V { get; set; }
    public double W { get; set; }

    public static ScanAxes From(MotionPosition p) => new() { X = p.X, Y = p.Y, Z = p.Z, U = p.U, V = p.V, W = p.W };

    public MotionPosition ToPosition() => new(X, Y, Z, U, V, W);

    public bool IsValid()
        => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z)
           && double.IsFinite(U) && double.IsFinite(V) && double.IsFinite(W);

    public override string ToString()
        => $"X {X:0.####}  Y {Y:0.####}  Z {Z:0.####}  U {U:0.####}  V {V:0.####}  W {W:0.####}";
}

/// <summary>A position button: its caption and the saved pose (null = nothing saved yet).</summary>
public sealed class ScanPositionSlot
{
    public string Label { get; set; } = "";
    public ScanAxes? Position { get; set; }
}

/// <summary>
/// A script button: its caption and what it runs — a <see cref="ScriptPreset"/> by name (script +
/// arguments, from <see cref="ScriptPresetStore"/>), or else a bare <c>.moon</c> script (active
/// product) with no arguments. A set <see cref="Preset"/> wins.
/// </summary>
public sealed class ScanScriptSlot
{
    public string Label { get; set; } = "";
    public string Script { get; set; } = "";
    public string Preset { get; set; } = "";
}

/// <summary>The whole <c>faam_scan_panel.json</c> file.</summary>
public sealed class ScanPanelSettings
{
    public List<ScanPositionSlot> Positions { get; set; } = new();
    public List<ScanScriptSlot> Scripts { get; set; } = new();
}

/// <summary>
/// The Run-page Scanning panel's customisation — two hex-right position slots (Save1 / Save2
/// store the current pose, Move1 / Move2 move the hexapod there) and four script buttons (Scan1..Scan4,
/// 2×2: a click runs the assigned script preset or <c>.moon</c> script). Captions, saved poses and script
/// assignments are edited by right-click on the Run page or on Machine › Settings, and stored
/// machine-global in <c>faam_scan_panel.json</c>. Script names resolve in the active product.
/// </summary>
public static class ScanPanelStore
{
    /// <summary>The hexapod the position buttons save and move.</summary>
    public const string Device = "hex-right";

    /// <summary>Number of position buttons.</summary>
    public const int PositionSlotCount = 2;

    /// <summary>Number of script buttons (a 2×2 grid on the Run page).</summary>
    public const int ScriptSlotCount = 4;

    private const string FileName = "faam_scan_panel";

    /// <summary>Raised after every successful <see cref="Save"/> (on the saving thread).</summary>
    public static event Action? Changed;

    /// <summary>The saved settings, padded / trimmed to the slot counts (defaults when missing / unreadable).</summary>
    public static ScanPanelSettings Load()
    {
        ScanPanelSettings? s;
        try
        {
            s = ConfigLoader.LoadJson<ScanPanelSettings>(ConfigLoader.GetConfigDirectory(), FileName);
        }
        catch
        {
            s = null;   // unreadable → defaults
        }
        return Normalize(s ?? new ScanPanelSettings());
    }

    /// <summary>Overwrites the file and notifies listeners. Throws on I/O failure.</summary>
    public static void Save(ScanPanelSettings settings)
    {
        ConfigLoader.SaveJson(ConfigLoader.GetConfigDirectory(), FileName, Normalize(settings));
        Changed?.Invoke();
    }

    private static ScanPanelSettings Normalize(ScanPanelSettings s)
    {
        s.Positions ??= new List<ScanPositionSlot>();
        s.Scripts ??= new List<ScanScriptSlot>();
        while (s.Positions.Count < PositionSlotCount) s.Positions.Add(new ScanPositionSlot());
        while (s.Scripts.Count < ScriptSlotCount) s.Scripts.Add(new ScanScriptSlot());
        if (s.Positions.Count > PositionSlotCount) s.Positions.RemoveRange(PositionSlotCount, s.Positions.Count - PositionSlotCount);
        if (s.Scripts.Count > ScriptSlotCount) s.Scripts.RemoveRange(ScriptSlotCount, s.Scripts.Count - ScriptSlotCount);

        for (var i = 0; i < PositionSlotCount; i++)
        {
            var p = s.Positions[i] ??= new ScanPositionSlot();
            // The label names the Move button (Save{n} is the fixed caption above it); the old
            // default "Save{n}" is migrated to "Move{n}".
            p.Label = (p.Label ?? "").Trim();
            if (p.Label.Length == 0 || p.Label == $"Save{i + 1}") p.Label = $"Move{i + 1}";
            if (p.Position != null && !p.Position.IsValid()) p.Position = null;
        }

        for (var i = 0; i < ScriptSlotCount; i++)
        {
            var sc = s.Scripts[i] ??= new ScanScriptSlot();
            if (string.IsNullOrWhiteSpace(sc.Label)) sc.Label = $"Scan{i + 1}";
            sc.Label = sc.Label.Trim();
            sc.Script = (sc.Script ?? "").Trim();
            sc.Preset = (sc.Preset ?? "").Trim();
        }
        return s;
    }
}
