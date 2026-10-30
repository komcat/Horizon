using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Horizon.Station.UI;

/// <summary>One release — a <c>v*</c> git tag created by <c>deploy/release.ps1</c>.</summary>
public sealed record AppRelease(string Tag, string Commit, DateTimeOffset? Date, string Title);

/// <summary>
/// The running head's build identity and release history, read from the
/// <c>&lt;entry assembly&gt;.version_info.json</c> resource that the head's build embeds
/// (FAAM.UI: <c>build/version-info.ps1</c>, as <c>FAAM.UI.version_info.json</c>) — stations have no git.
/// When the resource is missing (built without git) it falls back to the assembly version,
/// with <see cref="FromGit"/> false.
/// </summary>
public sealed class AppVersionInfo
{
    public string Version { get; private init; } = "";
    public string Describe { get; private init; } = "";
    public string Commit { get; private init; } = "";
    public string ShortCommit { get; private init; } = "";
    public string Branch { get; private init; } = "";
    public DateTimeOffset? CommitDate { get; private init; }
    public string CommitSubject { get; private init; } = "";
    public bool Dirty { get; private init; }
    public int CommitsSinceRelease { get; private init; }
    public IReadOnlyList<AppRelease> Releases { get; private init; } = Array.Empty<AppRelease>();

    /// <summary>When this exe was built (its file time), or null if unknown.</summary>
    public DateTime? BuiltLocal { get; private init; }

    /// <summary>True when the history came from git at build time.</summary>
    public bool FromGit { get; private init; }

    /// <summary>Short identity for display: <c>v0.6.1 · abc1234</c>, with <c> *</c> for a dirty tree.</summary>
    public string Label => $"v{Version}" + (string.IsNullOrEmpty(ShortCommit) ? "" : $" · {ShortCommit}")
                         + (Dirty ? " *" : "");

    private static AppVersionInfo? _current;

    /// <summary>The running build's info (read once).</summary>
    public static AppVersionInfo Current => _current ??= Load();

    private static AppVersionInfo Load()
    {
        // The head executable carries the resource (and its version), not this library.
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly;
        var assemblyVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? asm.GetName().Version?.ToString() ?? "";
        // .NET appends "+<full sha>" to the informational version — keep just the release part.
        var plainVersion = assemblyVersion.Split('+')[0];
        DateTime? built = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) built = File.GetLastWriteTime(exe);
        }
        catch { /* unknown build time is fine */ }

        try
        {
            using var stream = asm.GetManifestResourceStream($"{asm.GetName().Name}.version_info.json");
            if (stream != null)
            {
                using var doc = JsonDocument.Parse(stream);
                var r = doc.RootElement;
                var releases = new List<AppRelease>();
                if (r.TryGetProperty("releases", out var rel))
                {
                    // ConvertTo-Json writes a lone object for a single-element array on some hosts.
                    var items = rel.ValueKind == JsonValueKind.Array ? rel.EnumerateArray().ToList()
                              : rel.ValueKind == JsonValueKind.Object ? new List<JsonElement> { rel }
                              : new List<JsonElement>();
                    foreach (var e in items)
                        releases.Add(new AppRelease(Str(e, "tag"), Str(e, "commit"), Date(e, "date"), Str(e, "title")));
                }
                return new AppVersionInfo
                {
                    Version = Str(r, "version") is { Length: > 0 } v ? v : plainVersion,
                    Describe = Str(r, "describe"),
                    Commit = Str(r, "commit"),
                    ShortCommit = Str(r, "shortCommit"),
                    Branch = Str(r, "branch"),
                    CommitDate = Date(r, "commitDate"),
                    CommitSubject = Str(r, "commitSubject"),
                    Dirty = r.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True,
                    CommitsSinceRelease = r.TryGetProperty("commitsSinceRelease", out var n) && n.TryGetInt32(out var c) ? c : 0,
                    Releases = releases,
                    BuiltLocal = built,
                    FromGit = true,
                };
            }
        }
        catch
        {
            // Malformed resource — fall through to the assembly version.
        }

        var sha = assemblyVersion.Contains('+') ? assemblyVersion.Split('+')[1] : "";
        return new AppVersionInfo
        {
            Version = plainVersion,
            Commit = sha,
            ShortCommit = sha.Length > 7 ? sha[..7] : sha,
            BuiltLocal = built,
        };
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? Date(JsonElement e, string name)
        => DateTimeOffset.TryParse(Str(e, name), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
