using System.Globalization;
using System.Windows;

namespace FAAM.UI;

/// <summary>
/// Home › Version — a brief build identity: this build's release version, git id, branch
/// and build time, then every release (v* tag) with its date and git id. Read-only; the
/// data is <see cref="AppVersionInfo.Current"/>, embedded at build time.
/// </summary>
public partial class VersionHistoryWindow : Window
{
    private sealed record Row(string Tag, string DateText, string Commit, string Title);

    private readonly AppVersionInfo _info = AppVersionInfo.Current;

    public VersionHistoryWindow()
    {
        InitializeComponent();
        var i = _info;

        VersionText.Text = $"FAAM.UI  v{i.Version}";
        CommitText.Text = string.IsNullOrEmpty(i.ShortCommit)
            ? "git id unknown"
            : $"git {i.ShortCommit}" + (string.IsNullOrEmpty(i.Branch) ? "" : $"  ·  {i.Branch}")
              + (string.IsNullOrEmpty(i.Describe) ? "" : $"  ·  {i.Describe}");
        BuiltText.Text = string.Join("   ", new[]
        {
            i.BuiltLocal is { } b ? $"Built {b:yyyy-MM-dd HH:mm}" : null,
            i.CommitDate is { } c ? $"Committed {c.ToLocalTime():yyyy-MM-dd HH:mm}" : null,
            i.CommitsSinceRelease > 0 && i.Releases.Count > 0
                ? $"{i.CommitsSinceRelease} commit(s) after {i.Releases[0].Tag}" : null,
        }.Where(s => s != null));

        var notes = new List<string>();
        if (i.Dirty) notes.Add("Built with uncommitted changes — not an exact copy of the git id above.");
        if (!i.FromGit) notes.Add("Release history unavailable (this build was made without git).");
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n", notes);
            NoteText.Visibility = Visibility.Visible;
        }

        ReleaseGrid.ItemsSource = i.Releases
            .Select(r => new Row(r.Tag,
                r.Date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                r.Commit, r.Title))
            .ToList();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var i = _info;
        var text = $"FAAM.UI v{i.Version} ({(string.IsNullOrEmpty(i.Describe) ? i.ShortCommit : i.Describe)})"
                   + (string.IsNullOrEmpty(i.Commit) ? "" : $" commit {i.Commit}")
                   + (i.BuiltLocal is { } b ? $" built {b:yyyy-MM-dd HH:mm}" : "");
        try { Clipboard.SetText(text); } catch { /* clipboard busy — ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
