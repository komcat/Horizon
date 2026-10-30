using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Horizon.Engine;
using Horizon.Workflow;

namespace Horizon.Station.UI;

/// <summary>
/// Read-only revision history of a vision profile: each save is an operator-stamped
/// snapshot. Selecting a revision shows its fields, with values that changed from the
/// previous revision highlighted (e.g. <c>method: ncc → circle</c>).
/// </summary>
public partial class VisionHistoryWindow : Window
{
    private readonly VisionProfileHistory _history;

    public VisionHistoryWindow(EngineHost host, string profileUid, string profileName)
    {
        InitializeComponent();
        _history = host.Process.GetVisionProfileHistory(profileUid)
                   ?? new VisionProfileHistory { ProfileUid = profileUid, ProfileName = profileName };

        var name = string.IsNullOrEmpty(_history.ProfileName) ? profileName : _history.ProfileName;
        HeaderText.Text = $"History — '{name}'   ({_history.Versions.Count} revision(s))";

        VersionList.ItemsSource = BuildRows();
        if (VersionList.Items.Count > 0)
            VersionList.SelectedIndex = 0;
        else
            DiffHeader.Text = "No saved revisions yet.";
    }

    private List<VersionRow> BuildRows()
    {
        var rows = new List<VersionRow>();
        for (int i = _history.Versions.Count - 1; i >= 0; i--)   // newest first
        {
            var v = _history.Versions[i];
            rows.Add(new VersionRow(
                $"v{v.Version}   ·   {v.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   ·   {Who(v)}", i, v));
        }
        return rows;
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionRow row)
        {
            DiffList.ItemsSource = null;
            return;
        }

        var after = Fields(row.Version.Snapshot);
        var before = row.Index > 0
            ? Fields(_history.Versions[row.Index - 1].Snapshot).ToDictionary(x => x.Field, x => x.Value)
            : null;

        var diffRows = new List<DiffRow>();
        int changes = 0;
        foreach (var (field, value) in after)
        {
            string? old = null;
            bool known = before != null && before.TryGetValue(field, out old);
            bool changed = known && !string.Equals(old, value, StringComparison.Ordinal);
            if (changed) changes++;
            diffRows.Add(new DiffRow(field, changed ? $"{old}  →  {value}" : value, changed));
        }
        DiffList.ItemsSource = diffRows;

        DiffHeader.Text = row.Index == 0
            ? $"v{row.Version.Version} — initial revision · by {Who(row.Version)} · station {row.Version.StationId ?? "—"}"
            : $"v{row.Version.Version} vs v{_history.Versions[row.Index - 1].Version} · {changes} change(s) · " +
              $"by {Who(row.Version)} · station {row.Version.StationId ?? "—"}";
    }

    /// <summary>Flattens a profile into ordered (field, display value) pairs for the diff list.</summary>
    private static List<(string Field, string Value)> Fields(VisionProfile p)
    {
        var d = new List<(string, string)>
        {
            ("position", $"{Empty(p.Device)} / {Empty(p.PositionName)}"),
            ("camera", Empty(p.CameraId)),
            ("exposure", p.AutoExposure ? "auto" : $"{p.ExposureUs:0} µs"),
            ("lighting", string.IsNullOrEmpty(p.LightingDevice) ? "—" : $"{p.LightingDevice} = {p.LightingIntensity}"),
            ("method", Empty(p.DetectionMethod)),
            ("filters", p.Filters is { Count: > 0 } ? string.Join("  →  ", p.Filters.Select(FilterSummary)) : "(none)"),
            ("search ROI", p.SearchRoi is { } r ? $"x{r.X} y{r.Y}  {r.Width}×{r.Height}" : "(whole image)"),
        };

        switch ((p.DetectionMethod ?? "ncc").ToLowerInvariant())
        {
            case "circle":
                d.Add(("circle", $"r {p.Circle.MinRadius:0}..{p.Circle.MaxRadius:0}  circ ≥ {p.Circle.MinCircularity:0.##}"));
                break;
            case "line":
                d.Add(("line", $"min length {p.Line.MinLength:0}  ·  {p.Line.Direction}"));
                break;
            default: // ncc
                d.Add(("min score", p.MinScore.ToString("0.00", CultureInfo.InvariantCulture)));
                d.Add(("angle range", $"±{p.NccAngleRange:0}°"));
                d.Add(("template", p.Template is { } t
                    ? $"{ShortUid(t.Uid)}  {t.Width}×{t.Height}  anchor ({t.AnchorX:0},{t.AnchorY:0})  rot {t.Rotation:0}°"
                    : "(none)"));
                break;
        }
        return d;
    }

    private static string FilterSummary(VisionFilter f) => f.Kind switch
    {
        VisionFilterKind.BinaryThreshold => $"binary[{f.ThresholdMin}..{f.ThresholdMax}]",
        VisionFilterKind.Gaussian => $"gauss[{f.GaussMask}]",
        VisionFilterKind.CannyEdge => $"canny[{f.ThresholdMin}/{f.ThresholdMax} α{f.Alpha:0.0}]",
        _ => "none",
    };

    private static string Empty(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    private static string Who(VisionProfileVersion v)
        => string.IsNullOrEmpty(v.OperatorName) ? (v.OperatorId ?? "—") : v.OperatorName;

    private static string ShortUid(string uid) => uid.Length <= 14 ? uid : uid[..14] + "…";

    private sealed record VersionRow(string Header, int Index, VisionProfileVersion Version);

    private sealed record DiffRow(string Field, string Detail, bool Changed);
}
