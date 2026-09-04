using Microsoft.Data.Sqlite;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Storage;

/// <summary>
/// The catalog: one SQLite file holding every volume, file and scan.
///
/// Deliberately a single portable file. The whole point of cataloguing by volume GUID is
/// that a disk which is not currently plugged in still participates in duplicate decisions;
/// that only works if the catalog outlives any particular machine state.
/// </summary>
public sealed class CatalogDatabase : IDisposable
{
    public const int SchemaVersion = 7;

    private readonly SqliteConnection _connection;

    public SqliteConnection Connection => _connection;
    public string Path { get; }

    private CatalogDatabase(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public static CatalogDatabase Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();

        Execute(connection,
            // WAL keeps readers (the UI, a report query) working while a scan writes.
            "PRAGMA journal_mode=WAL;" +
            // NORMAL is the right durability for a catalog: a crash can lose the last
            // transaction, and the frontier checkpoint simply re-walks that directory.
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA temp_store=MEMORY;" +
            "PRAGMA cache_size=-131072;" +   // 128 MB page cache
            "PRAGMA foreign_keys=ON;");

        var db = new CatalogDatabase(connection, full);
        db.Migrate();
        return db;
    }

    private void Migrate()
    {
        Execute(_connection, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        int current = GetSchemaVersion();
        if (current > SchemaVersion)
            throw new InvalidOperationException(
                $"Catalog schema v{current} is newer than this build (v{SchemaVersion}). Upgrade media-tool.");

        if (current == SchemaVersion) return;

        // SQLite makes DDL transactional, so a migration either lands whole or not at all.
        // Driven with raw BEGIN/COMMIT rather than BeginTransaction so the helper commands
        // below do not each have to be enlisted explicitly.
        Execute(_connection, "BEGIN");
        try
        {
            if (current < 1) ApplyV1();
            if (current < 2) ApplyV2();
            if (current < 3) ApplyV3();
            if (current < 4) ApplyV4();
            if (current < 5) ApplyV5();
            if (current < 6) ApplyV6();
            if (current < 7) ApplyV7();
            SetMeta("schema_version", SchemaVersion.ToString());
            Execute(_connection, "COMMIT");
        }
        catch
        {
            Execute(_connection, "ROLLBACK");
            throw;
        }
    }

    private void ApplyV1()
    {
        Execute(_connection, """
            CREATE TABLE volumes (
                volume_id        INTEGER PRIMARY KEY,
                volume_guid      TEXT    NOT NULL UNIQUE,
                serial_number    INTEGER,
                label            TEXT,
                file_system      TEXT,
                total_bytes      INTEGER,
                storage_kind     INTEGER NOT NULL DEFAULT 0,
                supports_file_id INTEGER NOT NULL DEFAULT 0,
                last_mount_point TEXT,
                last_seen_utc    TEXT
            );

            CREATE TABLE scans (
                scan_id        INTEGER PRIMARY KEY,
                volume_id      INTEGER NOT NULL REFERENCES volumes(volume_id),
                root_rel       TEXT    NOT NULL,
                status         TEXT    NOT NULL,
                started_utc    TEXT    NOT NULL,
                finished_utc   TEXT,
                dirs_visited   INTEGER NOT NULL DEFAULT 0,
                files_seen     INTEGER NOT NULL DEFAULT 0,
                files_accepted INTEGER NOT NULL DEFAULT 0,
                bytes_accepted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_scans_resume ON scans(volume_id, root_rel, status);

            -- Directories still owed. A scan resumes by reloading this set; a row is
            -- removed only in the same transaction that inserts its children, so the
            -- frontier can never lose a subtree.
            CREATE TABLE crawl_frontier (
                scan_id  INTEGER NOT NULL REFERENCES scans(scan_id) ON DELETE CASCADE,
                rel_dir  TEXT    NOT NULL,
                PRIMARY KEY (scan_id, rel_dir)
            ) WITHOUT ROWID;

            CREATE TABLE files (
                file_key      INTEGER PRIMARY KEY,
                volume_id     INTEGER NOT NULL REFERENCES volumes(volume_id),
                rel_path      TEXT    NOT NULL,
                name          TEXT    NOT NULL,
                ext           TEXT,
                size          INTEGER NOT NULL,
                mtime         INTEGER NOT NULL,   -- FILETIME ticks UTC
                ctime         INTEGER NOT NULL,
                attributes    INTEGER NOT NULL,
                file_id_low   INTEGER,            -- NTFS 128-bit file id, NULL on exFAT/FAT32
                file_id_high  INTEGER,
                last_scan_id  INTEGER NOT NULL,
                present       INTEGER NOT NULL DEFAULT 1
            );

            CREATE UNIQUE INDEX ux_files_path ON files(volume_id, rel_path);

            -- Identity that survives a rename or a move within the volume: the incremental
            -- rescan matches on this before it falls back to path.
            CREATE INDEX ix_files_fileid ON files(volume_id, file_id_low, file_id_high)
                WHERE file_id_low IS NOT NULL;

            -- Size is the first cut of the duplicate cascade: byte-identical files must
            -- agree on it, so groups of one are eliminated before any I/O happens.
            CREATE INDEX ix_files_size ON files(size) WHERE present = 1;
            """);
    }

    /// <summary>v2 adds the exact-duplicate cascade: a cheap probe hash and a full content hash.</summary>
    private void ApplyV2()
    {
        Execute(_connection, """
            -- xxHash64 of the 64KB head plus the 64KB tail. Only ever compared between files
            -- that already share a size, so the probe is consistent within any group.
            ALTER TABLE files ADD COLUMN partial_hash INTEGER;

            -- xxHash128 of the whole file, 16 bytes.
            ALTER TABLE files ADD COLUMN content_hash BLOB;

            -- The size and mtime the file had when it was hashed. A hash is only trusted
            -- while both still match, so an edited file is silently re-hashed rather than
            -- being matched against stale content.
            ALTER TABLE files ADD COLUMN hashed_size INTEGER;
            ALTER TABLE files ADD COLUMN hashed_mtime INTEGER;

            CREATE INDEX ix_files_partial ON files(size, partial_hash) WHERE partial_hash IS NOT NULL;
            CREATE INDEX ix_files_content ON files(content_hash) WHERE content_hash IS NOT NULL;
            """);
    }

    public int GetSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        var value = cmd.ExecuteScalar();
        return value is null ? 0 : int.Parse((string)value);
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES(@k,@v) " +
                          "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts or refreshes a volume row and returns its catalog id.</summary>
    public long UpsertVolume(VolumeInfo volume)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volumes (volume_guid, serial_number, label, file_system, total_bytes,
                                 storage_kind, supports_file_id, last_mount_point, last_seen_utc)
            VALUES (@guid, @serial, @label, @fs, @total, @kind, @fileid, @mount, @seen)
            ON CONFLICT(volume_guid) DO UPDATE SET
                serial_number    = excluded.serial_number,
                label            = excluded.label,
                file_system      = excluded.file_system,
                total_bytes      = excluded.total_bytes,
                storage_kind     = excluded.storage_kind,
                supports_file_id = excluded.supports_file_id,
                last_mount_point = excluded.last_mount_point,
                last_seen_utc    = excluded.last_seen_utc
            RETURNING volume_id;
            """;
        cmd.Parameters.AddWithValue("@guid", volume.VolumeGuid);
        cmd.Parameters.AddWithValue("@serial", volume.SerialNumber);
        cmd.Parameters.AddWithValue("@label", (object?)volume.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fs", (object?)volume.FileSystem ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@total", (long)volume.TotalBytes);
        cmd.Parameters.AddWithValue("@kind", (int)volume.StorageKind);
        cmd.Parameters.AddWithValue("@fileid", volume.SupportsFileId ? 1 : 0);
        cmd.Parameters.AddWithValue("@mount", (object?)volume.PrimaryMountPoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seen", DateTime.UtcNow.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>v3 adds the image tiers: exact-picture identity and the two perceptual hashes.</summary>
    private void ApplyV3()
    {
        Execute(_connection, """
            -- xxHash128 of the normalised pixel square plus the original dimensions. Equal
            -- values mean the same picture, whatever the container said about it.
            ALTER TABLE files ADD COLUMN pixel_hash BLOB;

            ALTER TABLE files ADD COLUMN dhash INTEGER;
            ALTER TABLE files ADD COLUMN phash INTEGER;

            -- 16x16 grayscale, 256 bytes. Lets a candidate pair be confirmed in RAM instead
            -- of by re-reading two originals off a spinning disk.
            ALTER TABLE files ADD COLUMN thumb16 BLOB;

            ALTER TABLE files ADD COLUMN img_width INTEGER;
            ALTER TABLE files ADD COLUMN img_height INTEGER;

            -- Spread of the thumbnail. Near-blank images share a perceptual hash by nature,
            -- so they are held back from clustering rather than allowed to form one huge
            -- false group.
            ALTER TABLE files ADD COLUMN contrast REAL;

            -- 0 = not decoded, 1 = decoded, 2 = decode failed. Failures are recorded so a
            -- re-run does not keep retrying the same unreadable files forever.
            ALTER TABLE files ADD COLUMN decode_state INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE files ADD COLUMN decoded_mtime INTEGER;

            CREATE INDEX ix_files_pixelhash ON files(pixel_hash) WHERE pixel_hash IS NOT NULL;
            CREATE INDEX ix_files_decode ON files(decode_state) WHERE present = 1;
            """);
    }

    /// <summary>
    /// v4 adds the evidence the keeper policy decides on, plus the record of what was
    /// actually moved. Metadata is read lazily — only files inside a duplicate group ever
    /// need it, which is a small fraction of the catalog.
    /// </summary>
    private void ApplyV4()
    {
        Execute(_connection, """
            ALTER TABLE files ADD COLUMN has_exif     INTEGER;
            ALTER TABLE files ADD COLUMN exif_tags    INTEGER;
            ALTER TABLE files ADD COLUMN date_taken   INTEGER;   -- unix seconds, UTC
            ALTER TABLE files ADD COLUMN camera       TEXT;
            ALTER TABLE files ADD COLUMN has_gps      INTEGER;
            ALTER TABLE files ADD COLUMN jpeg_quality INTEGER;
            ALTER TABLE files ADD COLUMN meta_bytes   INTEGER;
            ALTER TABLE files ADD COLUMN meta_state   INTEGER NOT NULL DEFAULT 0;

            -- Every file this tool has moved, and everything needed to move it back.
            -- Written before the move, confirmed after, so an interrupted run leaves a
            -- record of exactly what was in flight.
            CREATE TABLE actions (
                action_id      INTEGER PRIMARY KEY,
                batch_id       TEXT    NOT NULL,
                file_key       INTEGER NOT NULL,
                kind           TEXT    NOT NULL,   -- quarantine
                origin_volume  TEXT    NOT NULL,   -- volume GUID, so a replug does not lose it
                origin_rel     TEXT    NOT NULL,
                destination    TEXT    NOT NULL,
                size           INTEGER NOT NULL,
                content_hash   BLOB,
                kept_file_key  INTEGER,
                state          TEXT    NOT NULL,   -- planned | done | failed | undone
                message        TEXT,
                acted_utc      TEXT
            );
            CREATE INDEX ix_actions_batch ON actions(batch_id, state);
            """);
    }

    /// <summary>
    /// v5 adds sub-second capture time. Whole seconds are not enough to separate frames of
    /// a burst, which is exactly the case the clustering has to refuse.
    /// </summary>
    /// <summary>
    /// v7 remembers what a person decided while reviewing.
    ///
    /// Without it a review lived only in the window: hours of judgement on hundreds of
    /// clusters were lost by closing the app, and the Apply button then had nothing to act
    /// on. A decision is expensive to make and cheap to store, so it gets stored.
    ///
    /// The key is the cluster's membership, not its position in a list. Positions shift the
    /// moment the scope changes or a file moves; membership is what the person actually
    /// looked at, so a decision follows its cluster across runs and is dropped on the floor
    /// only if the cluster itself no longer exists.
    /// </summary>
    private void ApplyV7()
    {
        Execute(_connection, """
            CREATE TABLE review_decisions (
                cluster_key     TEXT    PRIMARY KEY,  -- derived from the member file keys
                keeper_file_key INTEGER NOT NULL,
                state           TEXT    NOT NULL,     -- confirmed | skipped
                decided_utc     TEXT    NOT NULL
            );
            """);
    }

    private void ApplyV5()
    {
        Execute(_connection, "ALTER TABLE files ADD COLUMN sub_sec INTEGER;");
    }

    /// <summary>
    /// v6 records where a capture date came from. A date that lives only in a sidecar is
    /// weaker evidence than one embedded in the file: the sidecar can be left behind by any
    /// copy or move, so a photo relying on it is one file operation from losing its history.
    /// </summary>
    private void ApplyV6()
    {
        Execute(_connection, """
            ALTER TABLE files ADD COLUMN sidecar_source TEXT;
            ALTER TABLE files ADD COLUMN sidecar_path TEXT;
            """);
    }

    /// <summary>
    /// A second connection to the same catalog, for reading while the main one writes.
    /// WAL allows that; sharing a single SqliteConnection across threads does not.
    /// </summary>
    public SqliteConnection OpenSecondaryConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
