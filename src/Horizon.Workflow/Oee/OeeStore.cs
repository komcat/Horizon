using Microsoft.Data.Sqlite;

namespace Horizon.Workflow.Oee;

/// <summary>
/// The machine-global OEE event store — <c>%APPDATA%\Horizon\oee.db</c> (SQLite), shared by
/// every Horizon head on the station. Four interval/event tables, times as UTC unix ms:
/// <list type="bullet">
/// <item><c>session</c> — a head was online (<c>started</c> → <c>ended</c>; <c>last_seen</c> is a
/// heartbeat so a crash still leaves a usable end).</item>
/// <item><c>run</c> — something was executing (program / deck / recipe / script / Run-page step).</item>
/// <item><c>downtime</c> — an engineer / technician declared the station down, and who brought it back up.</item>
/// <item><c>unit</c> — a finished DUT with its verdict (quality).</item>
/// </list>
/// Like <see cref="ProgramHistoryDb"/>, a store that cannot be opened degrades to a no-op
/// rather than breaking startup. Not thread-safe on its own — <see cref="OeeTracker"/>
/// serialises writes on one background writer and reads open their own connection.
/// </summary>
public sealed class OeeStore
{
    private readonly string _connString;

    public OeeStore(string? dbPath = null)
    {
        dbPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "oee.db");
        DbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            EnsureSchema();
            Available = true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Available = false;
        }
    }

    public string DbPath { get; }

    /// <summary>False when the database could not be opened — every call then no-ops / returns empty.</summary>
    public bool Available { get; }

    // --- writes ---

    public void OpenSession(string id, DateTime at, string app, string? station)
        => Exec("INSERT INTO session(id, started, last_seen, ended, app, station) VALUES($id, $t, $t, NULL, $app, $st)",
            ("$id", id), ("$t", Ms(at)), ("$app", app), ("$st", station));

    public void Heartbeat(string id, DateTime at)
        => Exec("UPDATE session SET last_seen = $t WHERE id = $id", ("$id", id), ("$t", Ms(at)));

    public void CloseSession(string id, DateTime at)
        => Exec("UPDATE session SET last_seen = $t, ended = $t WHERE id = $id", ("$id", id), ("$t", Ms(at)));

    public void OpenRun(string id, string sessionId, DateTime at, string kind, string? name)
        => Exec("INSERT INTO run(id, session_id, started, ended, kind, name) VALUES($id, $s, $t, NULL, $k, $n)",
            ("$id", id), ("$s", sessionId), ("$t", Ms(at)), ("$k", kind), ("$n", name));

    public void CloseRun(string id, DateTime at)
        => Exec("UPDATE run SET ended = $t WHERE id = $id", ("$id", id), ("$t", Ms(at)));

    public void OpenDowntime(OeeDowntime d)
        => Exec("INSERT INTO downtime(id, started, ended, down_by, up_by, reason, up_note, station) " +
                "VALUES($id, $t, NULL, $by, NULL, $r, NULL, $st)",
            ("$id", d.Id), ("$t", Ms(d.Start)), ("$by", d.DownBy), ("$r", d.Reason), ("$st", d.Station));

    public void CloseDowntime(string id, DateTime at, string upBy, string? note)
        => Exec("UPDATE downtime SET ended = $t, up_by = $by, up_note = $n WHERE id = $id",
            ("$id", id), ("$t", Ms(at)), ("$by", upBy), ("$n", note));

    public void AddUnit(OeeUnit u)
        => Exec("INSERT INTO unit(at, serial, product, verdict) VALUES($t, $s, $p, $v)",
            ("$t", Ms(u.At)), ("$s", u.Serial), ("$p", u.Product), ("$v", u.Verdict.ToString()));

    /// <summary>
    /// Closes what a previous process left open (crash / kill): its sessions end at their last
    /// heartbeat, and their runs end there too. Open downtime is deliberately kept — the
    /// station stays down across restarts until someone brings it up.
    /// </summary>
    public void RecoverDangling()
        => Exec("""
            UPDATE run SET ended = MAX(started, COALESCE((SELECT s.last_seen FROM session s WHERE s.id = run.session_id), started))
                WHERE ended IS NULL;
            UPDATE session SET ended = last_seen WHERE ended IS NULL;
            """);

    // --- reads ---

    /// <summary>The downtime still open (station currently down), or null.</summary>
    public OeeDowntime? GetOpenDowntime()
        => QueryDowntimes("WHERE ended IS NULL ORDER BY started DESC LIMIT 1", Array.Empty<(string, object?)>())
            .FirstOrDefault();

    /// <summary>
    /// Online sessions overlapping the window. An open session reports NULL end only when it
    /// is <paramref name="liveSessionId"/> (ongoing → now); any other open one ends at its last heartbeat.
    /// </summary>
    public IReadOnlyList<OeeInterval> Sessions(DateTime from, DateTime to, string? liveSessionId)
        => Intervals("session", "started",
            "CASE WHEN id = $live THEN ended ELSE COALESCE(ended, last_seen) END",
            from, to, liveSessionId);

    public IReadOnlyList<OeeInterval> Runs(DateTime from, DateTime to)
        => Intervals("run", "started", "ended", from, to);

    /// <summary>Downtime as plain intervals (for the calculator).</summary>
    public IReadOnlyList<OeeInterval> DownIntervals(DateTime from, DateTime to)
        => Intervals("downtime", "started", "ended", from, to);

    public IReadOnlyList<OeeDowntime> Downtimes(DateTime from, DateTime to)
        => QueryDowntimes("WHERE started < $to AND (ended IS NULL OR ended > $from) ORDER BY started",
            new (string, object?)[] { ("$from", Ms(from)), ("$to", Ms(to)) });

    public IReadOnlyList<OeeUnit> Units(DateTime from, DateTime to)
    {
        var list = new List<OeeUnit>();
        if (!Available) return list;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT at, serial, product, verdict FROM unit WHERE at >= $from AND at < $to ORDER BY at";
        cmd.Parameters.AddWithValue("$from", Ms(from));
        cmd.Parameters.AddWithValue("$to", Ms(to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var verdict = Enum.TryParse<DutVerdict>(r.GetString(3), out var v) ? v : DutVerdict.Fail;
            list.Add(new OeeUnit(FromMs(r.GetInt64(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), verdict));
        }
        return list;
    }

    // --- internals ---

    /// <summary>Intervals of <paramref name="table"/> overlapping [from, to); an open one (NULL end) overlaps anything after its start.</summary>
    private IReadOnlyList<OeeInterval> Intervals(
        string table, string startExpr, string endExpr, DateTime from, DateTime to, string? live = null)
    {
        var list = new List<OeeInterval>();
        if (!Available) return list;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT s, e FROM (SELECT {startExpr} AS s, {endExpr} AS e FROM {table}) " +
                          "WHERE s < $to AND (e IS NULL OR e > $from) ORDER BY s";
        cmd.Parameters.AddWithValue("$from", Ms(from));
        cmd.Parameters.AddWithValue("$to", Ms(to));
        cmd.Parameters.AddWithValue("$live", (object?)live ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OeeInterval(FromMs(r.GetInt64(0)), r.IsDBNull(1) ? null : FromMs(r.GetInt64(1))));
        return list;
    }

    private IReadOnlyList<OeeDowntime> QueryDowntimes(string where, (string, object?)[] args)
    {
        var list = new List<OeeDowntime>();
        if (!Available) return list;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, started, ended, down_by, up_by, reason, up_note, station FROM downtime " + where;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OeeDowntime(
                r.GetString(0), FromMs(r.GetInt64(1)), r.IsDBNull(2) ? null : FromMs(r.GetInt64(2)),
                r.GetString(3), Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7)));
        return list;
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        if (!Available) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS session (
                id TEXT PRIMARY KEY, started INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                ended INTEGER NULL, app TEXT NOT NULL, station TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_session_started ON session(started);
            CREATE TABLE IF NOT EXISTS run (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, started INTEGER NOT NULL,
                ended INTEGER NULL, kind TEXT NOT NULL, name TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_run_started ON run(started);
            CREATE TABLE IF NOT EXISTS downtime (
                id TEXT PRIMARY KEY, started INTEGER NOT NULL, ended INTEGER NULL,
                down_by TEXT NOT NULL, up_by TEXT NULL, reason TEXT NULL, up_note TEXT NULL, station TEXT NULL);
            CREATE TABLE IF NOT EXISTS unit (
                at INTEGER NOT NULL, serial TEXT NOT NULL, product TEXT NULL, verdict TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_unit_at ON unit(at);
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
