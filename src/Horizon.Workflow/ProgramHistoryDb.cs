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
            CREATE INDEX IF NOT EXISTS ix_version_operator ON program_version(operator_id);";
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
