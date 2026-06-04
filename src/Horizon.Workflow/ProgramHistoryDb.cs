using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Horizon.Workflow;

/// <summary>
/// SQLite-backed program revision history / audit store — a single machine-global
/// database (<c>%APPDATA%\Horizon\history.db</c>) across all products. Each
/// <see cref="ProcessService.SaveProgram"/> appends an operator-stamped snapshot and
/// a flattened, UID-keyed row per block / parameter, so history is queryable
/// (e.g. "every change to block UID X"). See
/// <c>docs/program-history-sqlite-design.md</c>.
///
/// The full <c>snapshot_json</c> is kept per revision so the snapshot diff and exact
/// restore still work; the normalized <c>block_revision</c> / <c>block_param</c>
/// tables exist purely for querying.
/// </summary>
public sealed class ProgramHistoryDb
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connString;
    private readonly bool _available;   // false if the DB could not be opened — history degrades to a no-op

    public ProgramHistoryDb(string? dbPath = null)
    {
        dbPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Horizon", "history.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            EnsureSchema();
            _available = true;
        }
        catch
        {
            _available = false;   // never let a history-store fault break startup
            return;
        }
        ImportLegacyJsonOnce();
    }

    /// <summary>
    /// Appends a new operator-stamped revision of <paramref name="snapshot"/> and
    /// returns it. If the store is unavailable the version is returned but not
    /// persisted (so the caller's logging still works).
    /// </summary>
    public ProgramVersion Append(string product, WorkflowProgram snapshot, OperatorContext? op)
    {
        if (!_available)
            return new ProgramVersion
            {
                Version = 1, SavedUtc = DateTime.UtcNow,
                OperatorId = op?.UserId, OperatorName = op?.DisplayName, StationId = op?.StationId,
                Snapshot = snapshot,
            };

        using var conn = Open();
        var version = new ProgramVersion
        {
            Version = NextVersion(conn, product, snapshot.Name),
            SavedUtc = DateTime.UtcNow,
            OperatorId = op?.UserId,
            OperatorName = op?.DisplayName,
            StationId = op?.StationId,
            Snapshot = snapshot,
        };
        InsertVersion(conn, product, version);
        return version;
    }

    /// <summary>Reads a program's full revision history (oldest version first).</summary>
    public ProgramHistory Load(string product, string program)
    {
        var history = new ProgramHistory { Program = program };
        if (!_available) return history;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT version, saved_utc, operator_id, operator_name, station_id, snapshot_json
              FROM program_version WHERE product=$p AND program=$g ORDER BY version";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$g", program);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var snapshot = JsonSerializer.Deserialize<WorkflowProgram>(reader.GetString(5), JsonOptions)
                           ?? new WorkflowProgram { Name = program };
            history.Versions.Add(new ProgramVersion
            {
                Version = reader.GetInt32(0),
                SavedUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OperatorId = reader.IsDBNull(2) ? null : reader.GetString(2),
                OperatorName = reader.IsDBNull(3) ? null : reader.GetString(3),
                StationId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Snapshot = snapshot,
            });
        }
        return history;
    }

    /// <summary>
    /// Cross-program query over the normalized block/param tables. Each result row is
    /// one parameter value of a block in a revision matching the (AND-combined)
    /// filters. Newest first, capped at 2000 rows.
    /// </summary>
    public IReadOnlyList<HistoryRow> Query(HistoryQuery q)
    {
        var rows = new List<HistoryRow>();
        if (!_available) return rows;

        var where = new List<string>();
        void Filter(string? value, string sql) { if (!string.IsNullOrWhiteSpace(value)) where.Add(sql); }
        Filter(q.BlockUid, "p.block_uid = $uid");
        Filter(q.ParamKey, "p.key = $key");
        Filter(q.Device, "r.device = $device");
        Filter(q.OperatorId, "v.operator_id = $op");
        Filter(q.Product, "v.product = $product");
        Filter(q.Program, "v.program = $program");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT v.product, v.program, v.version, v.saved_utc, v.operator_name,
                     p.block_uid, r.op, r.device, p.key, p.value
              FROM block_param p
              JOIN program_version v ON v.id = p.version_id
              JOIN block_revision r ON r.version_id = p.version_id AND r.block_uid = p.block_uid"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY v.saved_utc DESC, v.program, p.key LIMIT 2000";

        if (!string.IsNullOrWhiteSpace(q.BlockUid)) cmd.Parameters.AddWithValue("$uid", q.BlockUid);
        if (!string.IsNullOrWhiteSpace(q.ParamKey)) cmd.Parameters.AddWithValue("$key", q.ParamKey);
        if (!string.IsNullOrWhiteSpace(q.Device)) cmd.Parameters.AddWithValue("$device", q.Device);
        if (!string.IsNullOrWhiteSpace(q.OperatorId)) cmd.Parameters.AddWithValue("$op", q.OperatorId);
        if (!string.IsNullOrWhiteSpace(q.Product)) cmd.Parameters.AddWithValue("$product", q.Product);
        if (!string.IsNullOrWhiteSpace(q.Program)) cmd.Parameters.AddWithValue("$program", q.Program);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new HistoryRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? "" : reader.GetString(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9)));
        return rows;
    }

    // --- decks ---

    /// <summary>
    /// Appends a new operator-stamped revision of <paramref name="snapshot"/> (keyed by
    /// its <see cref="WorkflowDeck.Uid"/>) and returns it. Unavailable store → returned
    /// but not persisted.
    /// </summary>
    public DeckVersion AppendDeck(string product, WorkflowDeck snapshot, OperatorContext? op)
    {
        if (!_available)
            return new DeckVersion
            {
                Version = 1, SavedUtc = DateTime.UtcNow,
                OperatorId = op?.UserId, OperatorName = op?.DisplayName, StationId = op?.StationId,
                Snapshot = snapshot,
            };

        using var conn = Open();
        var version = new DeckVersion
        {
            Version = NextDeckVersion(conn, product, snapshot.Uid),
            SavedUtc = DateTime.UtcNow,
            OperatorId = op?.UserId,
            OperatorName = op?.DisplayName,
            StationId = op?.StationId,
            Snapshot = snapshot,
        };
        InsertDeckVersion(conn, product, version);
        return version;
    }

    /// <summary>Reads a deck's full revision history by UID (oldest version first).</summary>
    public DeckHistory LoadDeckHistory(string product, string deckUid)
    {
        var history = new DeckHistory { DeckUid = deckUid };
        if (!_available || string.IsNullOrEmpty(deckUid)) return history;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT version, saved_utc, operator_id, operator_name, station_id, snapshot_json, deck_name
              FROM deck_version WHERE product=$p AND deck_uid=$u ORDER BY version";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$u", deckUid);

        using var reader = cmd.ExecuteReader();
        string name = string.Empty;
        while (reader.Read())
        {
            var snapshot = JsonSerializer.Deserialize<WorkflowDeck>(reader.GetString(5), JsonOptions) ?? new WorkflowDeck();
            name = reader.IsDBNull(6) ? name : reader.GetString(6);
            history.Versions.Add(new DeckVersion
            {
                Version = reader.GetInt32(0),
                SavedUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OperatorId = reader.IsDBNull(2) ? null : reader.GetString(2),
                OperatorName = reader.IsDBNull(3) ? null : reader.GetString(3),
                StationId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Snapshot = snapshot,
            });
        }
        return history with { DeckName = name };
    }

    /// <summary>
    /// Cross-deck query over the normalized entry table. Each result row is one entry of
    /// a deck in a revision matching the (AND-combined) filters. Newest first, capped at 2000.
    /// </summary>
    public IReadOnlyList<DeckHistoryRow> QueryDecks(DeckHistoryQuery q)
    {
        var rows = new List<DeckHistoryRow>();
        if (!_available) return rows;

        var where = new List<string>();
        void Filter(string? value, string sql) { if (!string.IsNullOrWhiteSpace(value)) where.Add(sql); }
        Filter(q.DeckUid, "v.deck_uid = $duid");
        Filter(q.EntryUid, "e.entry_uid = $euid");
        Filter(q.Program, "e.program = $program");
        Filter(q.OperatorId, "v.operator_id = $op");
        Filter(q.Product, "v.product = $product");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT v.product, v.deck_name, v.version, v.saved_utc, v.operator_name,
                     e.entry_uid, e.position, e.program, e.auto, e.delay_seconds, e.condition
              FROM deck_entry_revision e
              JOIN deck_version v ON v.id = e.version_id"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY v.saved_utc DESC, v.deck_name, e.position LIMIT 2000";

        if (!string.IsNullOrWhiteSpace(q.DeckUid)) cmd.Parameters.AddWithValue("$duid", q.DeckUid);
        if (!string.IsNullOrWhiteSpace(q.EntryUid)) cmd.Parameters.AddWithValue("$euid", q.EntryUid);
        if (!string.IsNullOrWhiteSpace(q.Program)) cmd.Parameters.AddWithValue("$program", q.Program);
        if (!string.IsNullOrWhiteSpace(q.OperatorId)) cmd.Parameters.AddWithValue("$op", q.OperatorId);
        if (!string.IsNullOrWhiteSpace(q.Product)) cmd.Parameters.AddWithValue("$product", q.Product);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new DeckHistoryRow(
                reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt32(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                reader.IsDBNull(10) ? "" : reader.GetString(10)));
        return rows;
    }

    private static int NextDeckVersion(SqliteConnection conn, string product, string deckUid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM deck_version WHERE product=$p AND deck_uid=$u";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$u", deckUid);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Inserts a deck revision + its entry rows. Idempotent on (product, deck_uid, version).</summary>
    private static void InsertDeckVersion(SqliteConnection conn, string product, DeckVersion v)
    {
        using var tx = conn.BeginTransaction();

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText =
                @"INSERT OR IGNORE INTO deck_version
                  (product, deck_uid, deck_name, version, saved_utc, operator_id, operator_name, station_id, snapshot_json)
                  VALUES ($product,$uid,$name,$version,$saved,$opid,$opname,$station,$json)";
            ins.Parameters.AddWithValue("$product", product);
            ins.Parameters.AddWithValue("$uid", v.Snapshot.Uid);
            ins.Parameters.AddWithValue("$name", v.Snapshot.Name);
            ins.Parameters.AddWithValue("$version", v.Version);
            ins.Parameters.AddWithValue("$saved", v.SavedUtc.ToString("o", CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("$opid", (object?)v.OperatorId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$opname", (object?)v.OperatorName ?? DBNull.Value);
            ins.Parameters.AddWithValue("$station", (object?)v.StationId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(v.Snapshot, JsonOptions));
            ins.ExecuteNonQuery();
        }

        long versionId;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id FROM deck_version WHERE product=$p AND deck_uid=$u AND version=$v";
            sel.Parameters.AddWithValue("$p", product);
            sel.Parameters.AddWithValue("$u", v.Snapshot.Uid);
            sel.Parameters.AddWithValue("$v", v.Version);
            versionId = Convert.ToInt64(sel.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        for (int i = 0; i < v.Snapshot.Entries.Count; i++)
        {
            var e = v.Snapshot.Entries[i];
            if (string.IsNullOrEmpty(e.Uid)) continue;
            using var er = conn.CreateCommand();
            er.Transaction = tx;
            er.CommandText =
                @"INSERT OR IGNORE INTO deck_entry_revision
                  (version_id, entry_uid, position, program, enabled, auto, delay_seconds, condition)
                  VALUES ($v,$uid,$pos,$prog,$en,$auto,$delay,$cond)";
            er.Parameters.AddWithValue("$v", versionId);
            er.Parameters.AddWithValue("$uid", e.Uid);
            er.Parameters.AddWithValue("$pos", i + 1);
            er.Parameters.AddWithValue("$prog", e.Program);
            er.Parameters.AddWithValue("$en", e.Enabled ? 1 : 0);
            er.Parameters.AddWithValue("$auto", e.Auto ? 1 : 0);
            er.Parameters.AddWithValue("$delay", e.DelaySeconds);
            er.Parameters.AddWithValue("$cond", e.Condition ?? string.Empty);
            er.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // --- vision profiles ---

    /// <summary>
    /// Appends a new operator-stamped revision of <paramref name="snapshot"/> (keyed by
    /// its <see cref="VisionProfile.Uid"/>) and returns it. Unavailable store → returned
    /// but not persisted.
    /// </summary>
    public VisionProfileVersion AppendVisionProfile(string product, VisionProfile snapshot, OperatorContext? op)
    {
        if (!_available)
            return new VisionProfileVersion
            {
                Version = 1, SavedUtc = DateTime.UtcNow,
                OperatorId = op?.UserId, OperatorName = op?.DisplayName, StationId = op?.StationId,
                Snapshot = snapshot,
            };

        using var conn = Open();
        var version = new VisionProfileVersion
        {
            Version = NextVisionVersion(conn, product, snapshot.Uid),
            SavedUtc = DateTime.UtcNow,
            OperatorId = op?.UserId,
            OperatorName = op?.DisplayName,
            StationId = op?.StationId,
            Snapshot = snapshot,
        };

        using var ins = conn.CreateCommand();
        ins.CommandText =
            @"INSERT OR IGNORE INTO vision_version
              (product, profile_uid, profile_name, version, saved_utc, operator_id, operator_name, station_id, template_uid, snapshot_json)
              VALUES ($product,$uid,$name,$version,$saved,$opid,$opname,$station,$tpl,$json)";
        ins.Parameters.AddWithValue("$product", product);
        ins.Parameters.AddWithValue("$uid", snapshot.Uid);
        ins.Parameters.AddWithValue("$name", snapshot.Name);
        ins.Parameters.AddWithValue("$version", version.Version);
        ins.Parameters.AddWithValue("$saved", version.SavedUtc.ToString("o", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("$opid", (object?)version.OperatorId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$opname", (object?)version.OperatorName ?? DBNull.Value);
        ins.Parameters.AddWithValue("$station", (object?)version.StationId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$tpl", (object?)snapshot.Template?.Uid ?? DBNull.Value);
        ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, JsonOptions));
        ins.ExecuteNonQuery();
        return version;
    }

    /// <summary>Reads a vision profile's full revision history by UID (oldest version first).</summary>
    public VisionProfileHistory LoadVisionHistory(string product, string profileUid)
    {
        var history = new VisionProfileHistory { ProfileUid = profileUid };
        if (!_available || string.IsNullOrEmpty(profileUid)) return history;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT version, saved_utc, operator_id, operator_name, station_id, snapshot_json, profile_name
              FROM vision_version WHERE product=$p AND profile_uid=$u ORDER BY version";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$u", profileUid);

        using var reader = cmd.ExecuteReader();
        string name = string.Empty;
        while (reader.Read())
        {
            var snapshot = JsonSerializer.Deserialize<VisionProfile>(reader.GetString(5), JsonOptions) ?? new VisionProfile();
            name = reader.IsDBNull(6) ? name : reader.GetString(6);
            history.Versions.Add(new VisionProfileVersion
            {
                Version = reader.GetInt32(0),
                SavedUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                OperatorId = reader.IsDBNull(2) ? null : reader.GetString(2),
                OperatorName = reader.IsDBNull(3) ? null : reader.GetString(3),
                StationId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Snapshot = snapshot,
            });
        }
        return history with { ProfileName = name };
    }

    private static int NextVisionVersion(SqliteConnection conn, string product, string profileUid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM vision_version WHERE product=$p AND profile_uid=$u";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$u", profileUid);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // --- internals ---

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
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS program_version (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                product TEXT NOT NULL, program TEXT NOT NULL, version INTEGER NOT NULL,
                saved_utc TEXT NOT NULL,
                operator_id TEXT, operator_name TEXT, station_id TEXT,
                snapshot_json TEXT NOT NULL,
                UNIQUE(product, program, version));
            CREATE TABLE IF NOT EXISTS block_revision (
                version_id INTEGER NOT NULL REFERENCES program_version(id) ON DELETE CASCADE,
                block_uid TEXT NOT NULL, op TEXT, device TEXT, lane TEXT, name TEXT,
                enabled INTEGER, stage_index INTEGER,
                PRIMARY KEY(version_id, block_uid));
            CREATE TABLE IF NOT EXISTS block_param (
                version_id INTEGER NOT NULL REFERENCES program_version(id) ON DELETE CASCADE,
                block_uid TEXT NOT NULL, key TEXT NOT NULL, value TEXT,
                PRIMARY KEY(version_id, block_uid, key));
            CREATE INDEX IF NOT EXISTS ix_block_rev_uid ON block_revision(block_uid);
            CREATE INDEX IF NOT EXISTS ix_block_rev_device ON block_revision(device);
            CREATE INDEX IF NOT EXISTS ix_block_param_uid ON block_param(block_uid, key);
            CREATE INDEX IF NOT EXISTS ix_version_program ON program_version(product, program);
            CREATE INDEX IF NOT EXISTS ix_version_operator ON program_version(operator_id);

            CREATE TABLE IF NOT EXISTS deck_version (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                product TEXT NOT NULL, deck_uid TEXT NOT NULL, deck_name TEXT NOT NULL, version INTEGER NOT NULL,
                saved_utc TEXT NOT NULL,
                operator_id TEXT, operator_name TEXT, station_id TEXT,
                snapshot_json TEXT NOT NULL,
                UNIQUE(product, deck_uid, version));
            CREATE TABLE IF NOT EXISTS deck_entry_revision (
                version_id INTEGER NOT NULL REFERENCES deck_version(id) ON DELETE CASCADE,
                entry_uid TEXT NOT NULL, position INTEGER, program TEXT, enabled INTEGER,
                auto INTEGER, delay_seconds REAL, condition TEXT,
                PRIMARY KEY(version_id, entry_uid));
            CREATE INDEX IF NOT EXISTS ix_deck_ver ON deck_version(product, deck_uid);
            CREATE INDEX IF NOT EXISTS ix_deck_ver_operator ON deck_version(operator_id);
            CREATE INDEX IF NOT EXISTS ix_deck_entry_uid ON deck_entry_revision(entry_uid);
            CREATE INDEX IF NOT EXISTS ix_deck_entry_program ON deck_entry_revision(program);

            CREATE TABLE IF NOT EXISTS vision_version (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                product TEXT NOT NULL, profile_uid TEXT NOT NULL, profile_name TEXT NOT NULL, version INTEGER NOT NULL,
                saved_utc TEXT NOT NULL,
                operator_id TEXT, operator_name TEXT, station_id TEXT,
                template_uid TEXT,
                snapshot_json TEXT NOT NULL,
                UNIQUE(product, profile_uid, version));
            CREATE INDEX IF NOT EXISTS ix_vision_ver ON vision_version(product, profile_uid);
            CREATE INDEX IF NOT EXISTS ix_vision_ver_operator ON vision_version(operator_id);";
        cmd.ExecuteNonQuery();
    }

    private static int NextVersion(SqliteConnection conn, string product, string program)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM program_version WHERE product=$p AND program=$g";
        cmd.Parameters.AddWithValue("$p", product);
        cmd.Parameters.AddWithValue("$g", program);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Inserts a revision + its flattened block / param rows. Idempotent on (product, program, version).</summary>
    private static void InsertVersion(SqliteConnection conn, string product, ProgramVersion v)
    {
        using var tx = conn.BeginTransaction();

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText =
                @"INSERT OR IGNORE INTO program_version
                  (product, program, version, saved_utc, operator_id, operator_name, station_id, snapshot_json)
                  VALUES ($product,$program,$version,$saved,$opid,$opname,$station,$json)";
            ins.Parameters.AddWithValue("$product", product);
            ins.Parameters.AddWithValue("$program", v.Snapshot.Name);
            ins.Parameters.AddWithValue("$version", v.Version);
            ins.Parameters.AddWithValue("$saved", v.SavedUtc.ToString("o", CultureInfo.InvariantCulture));
            ins.Parameters.AddWithValue("$opid", (object?)v.OperatorId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$opname", (object?)v.OperatorName ?? DBNull.Value);
            ins.Parameters.AddWithValue("$station", (object?)v.StationId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(v.Snapshot, JsonOptions));
            ins.ExecuteNonQuery();
        }

        long versionId;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id FROM program_version WHERE product=$p AND program=$g AND version=$v";
            sel.Parameters.AddWithValue("$p", product);
            sel.Parameters.AddWithValue("$g", v.Snapshot.Name);
            sel.Parameters.AddWithValue("$v", v.Version);
            versionId = Convert.ToInt64(sel.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // Already has block rows (idempotent re-import) → nothing more to do.
        if (!BlockRowsExist(conn, tx, versionId))
        {
            foreach (var row in Flatten(v.Snapshot))
            {
                using (var br = conn.CreateCommand())
                {
                    br.Transaction = tx;
                    br.CommandText =
                        @"INSERT OR IGNORE INTO block_revision
                          (version_id, block_uid, op, device, lane, name, enabled, stage_index)
                          VALUES ($v,$uid,$op,$dev,$lane,$name,$en,$stage)";
                    br.Parameters.AddWithValue("$v", versionId);
                    br.Parameters.AddWithValue("$uid", row.Uid);
                    br.Parameters.AddWithValue("$op", row.Op);
                    br.Parameters.AddWithValue("$dev", row.Device);
                    br.Parameters.AddWithValue("$lane", row.Lane);
                    br.Parameters.AddWithValue("$name", row.Name);
                    br.Parameters.AddWithValue("$en", row.Enabled ? 1 : 0);
                    br.Parameters.AddWithValue("$stage", row.StageIndex);
                    br.ExecuteNonQuery();
                }
                foreach (var (key, value) in row.Params)
                {
                    using var bp = conn.CreateCommand();
                    bp.Transaction = tx;
                    bp.CommandText =
                        @"INSERT OR IGNORE INTO block_param (version_id, block_uid, key, value)
                          VALUES ($v,$uid,$k,$val)";
                    bp.Parameters.AddWithValue("$v", versionId);
                    bp.Parameters.AddWithValue("$uid", row.Uid);
                    bp.Parameters.AddWithValue("$k", key);
                    bp.Parameters.AddWithValue("$val", (object?)value ?? DBNull.Value);
                    bp.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }

    private static bool BlockRowsExist(SqliteConnection conn, SqliteTransaction tx, long versionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM block_revision WHERE version_id=$v LIMIT 1";
        cmd.Parameters.AddWithValue("$v", versionId);
        return cmd.ExecuteScalar() != null;
    }

    private sealed record BlockRow(
        string Uid, string Op, string Device, string Lane, string Name,
        bool Enabled, int StageIndex, IReadOnlyDictionary<string, string> Params);

    /// <summary>Flattens a program to its leaf blocks (parallel branches expanded), with stage + lane.</summary>
    private static List<BlockRow> Flatten(WorkflowProgram program)
    {
        var rows = new List<BlockRow>();
        for (int i = 0; i < program.Steps.Count; i++)
        {
            var s = program.Steps[i];
            if (s.Op.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase) && s.Branches.Count > 0)
            {
                foreach (var b in s.Branches)
                    foreach (var bs in b.Steps)
                        if (!string.IsNullOrEmpty(bs.Id))
                            rows.Add(new BlockRow(bs.Id, bs.Op, bs.Device, b.Lane, bs.Name, bs.Enabled, i, bs.Parameters));
            }
            else if (!string.IsNullOrEmpty(s.Id))
            {
                rows.Add(new BlockRow(s.Id, s.Op, s.Device, s.Device, s.Name, s.Enabled, i, s.Parameters));
            }
        }
        return rows;
    }

    // --- one-time import of the legacy JSON history ---

    private void ImportLegacyJsonOnce()
    {
        if (GetMeta("json_imported") == "1") return;
        try
        {
            var productsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Horizon", "products");
            if (Directory.Exists(productsRoot))
            {
                using var conn = Open();
                foreach (var productDir in Directory.GetDirectories(productsRoot))
                {
                    var product = Path.GetFileName(productDir);
                    var historyDir = Path.Combine(productDir, "processes", "history");
                    if (!Directory.Exists(historyDir)) continue;

                    foreach (var file in Directory.GetFiles(historyDir, "*.json"))
                    {
                        try
                        {
                            var h = JsonSerializer.Deserialize<ProgramHistory>(File.ReadAllText(file), JsonOptions);
                            if (h == null) continue;
                            foreach (var v in h.Versions)
                                InsertVersion(conn, product, v);   // idempotent
                        }
                        catch { /* skip an unreadable history file */ }
                    }
                }
            }
        }
        catch { /* best-effort import — never block startup */ }
        SetMeta("json_imported", "1");
    }

    private string? GetMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k,$v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
