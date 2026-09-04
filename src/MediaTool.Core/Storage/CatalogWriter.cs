using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MediaTool.Core.Crawl;

namespace MediaTool.Core.Storage;

/// <summary>
/// The single writer draining the crawl channel into SQLite.
///
/// One writer, not many: SQLite serialises writes anyway, and funnelling through one
/// connection lets every batch be a real transaction with prepared statements reused
/// across it — the difference between ~2k and ~200k rows/sec.
///
/// Transactions are committed only on a directory boundary. Because files and the frontier
/// update for their directory arrive in order on the same channel, that boundary is exactly
/// where the catalog is consistent: either a directory's files and its frontier update are
/// both durable, or neither is and the directory is simply re-walked on resume.
/// </summary>
public sealed class CatalogWriter : IDisposable
{
    /// <summary>Rows per transaction. Large enough to amortise commit cost, small enough that a crash loses little.</summary>
    private const int CommitThreshold = 20_000;

    /// <summary>
    /// Also commit on a timer. On a slow HDD a directory-heavy tree can take minutes to
    /// produce 20k rows, and losing minutes of a scan to an unplugged disk is the exact
    /// failure this checkpoint exists to prevent.
    /// </summary>
    private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(5);

    private readonly SqliteConnection _connection;
    private readonly long _volumeId;
    private readonly long _scanId;

    private readonly SqliteCommand _upsertFile;
    private readonly SqliteCommand _deleteFrontier;
    private readonly SqliteCommand _insertFrontier;

    private SqliteTransaction? _transaction;
    private int _uncommitted;
    private readonly System.Diagnostics.Stopwatch _sinceCommit = new();

    public long FilesWritten { get; private set; }

    public CatalogWriter(CatalogDatabase db, long volumeId, long scanId)
    {
        _connection = db.Connection;
        _volumeId = volumeId;
        _scanId = scanId;

        _upsertFile = _connection.CreateCommand();
        _upsertFile.CommandText = """
            INSERT INTO files (volume_id, rel_path, name, ext, size, mtime, ctime, attributes,
                               file_id_low, file_id_high, last_scan_id, present)
            VALUES (@vol, @path, @name, @ext, @size, @mtime, @ctime, @attr, @idlo, @idhi, @scan, 1)
            ON CONFLICT(volume_id, rel_path) DO UPDATE SET
                name         = excluded.name,
                ext          = excluded.ext,
                size         = excluded.size,
                mtime        = excluded.mtime,
                ctime        = excluded.ctime,
                attributes   = excluded.attributes,
                file_id_low  = excluded.file_id_low,
                file_id_high = excluded.file_id_high,
                last_scan_id = excluded.last_scan_id,
                present      = 1;
            """;
        AddParams(_upsertFile, "@vol", "@path", "@name", "@ext", "@size", "@mtime", "@ctime",
                               "@attr", "@idlo", "@idhi", "@scan");

        _deleteFrontier = _connection.CreateCommand();
        _deleteFrontier.CommandText = "DELETE FROM crawl_frontier WHERE scan_id=@scan AND rel_dir=@dir";
        AddParams(_deleteFrontier, "@scan", "@dir");

        _insertFrontier = _connection.CreateCommand();
        _insertFrontier.CommandText =
            "INSERT OR IGNORE INTO crawl_frontier (scan_id, rel_dir) VALUES (@scan, @dir)";
        AddParams(_insertFrontier, "@scan", "@dir");
    }

    /// <summary>Drains the channel until the crawler closes it.</summary>
    public async Task RunAsync(ChannelReader<CrawlEvent> reader, CancellationToken ct)
    {
        BeginTransaction();
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (evt.IsDirectoryCompletion)
                {
                    WriteFrontierUpdate(evt.CompletedDirectory!, evt.DiscoveredDirectories!);

                    // Only here is the catalog in a consistent state, so only here do we commit.
                    if (_uncommitted >= CommitThreshold || _sinceCommit.Elapsed >= CommitInterval)
                        Checkpoint();
                }
                else if (evt.File is { } file)
                {
                    WriteFile(file);
                }
            }

            Commit();
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    private void WriteFile(FileRecord file)
    {
        var p = _upsertFile.Parameters;
        p["@vol"].Value = _volumeId;
        p["@path"].Value = file.RelativePath;
        p["@name"].Value = file.Name;
        p["@ext"].Value = (object?)file.Extension ?? DBNull.Value;
        p["@size"].Value = file.Size;
        p["@mtime"].Value = file.LastWriteTimeUtc;
        p["@ctime"].Value = file.CreationTimeUtc;
        p["@attr"].Value = (long)file.Attributes;
        // SQLite integers are signed; identity only needs the bits to round-trip.
        p["@idlo"].Value = file.HasFileId ? unchecked((long)file.FileIdLow) : DBNull.Value;
        p["@idhi"].Value = file.HasFileId ? unchecked((long)file.FileIdHigh) : DBNull.Value;
        p["@scan"].Value = _scanId;

        _upsertFile.ExecuteNonQuery();
        _uncommitted++;
        FilesWritten++;
    }

    private void WriteFrontierUpdate(string completedDir, IReadOnlyList<string> children)
    {
        _deleteFrontier.Parameters["@scan"].Value = _scanId;
        _deleteFrontier.Parameters["@dir"].Value = completedDir;
        _deleteFrontier.ExecuteNonQuery();

        foreach (string child in children)
        {
            _insertFrontier.Parameters["@scan"].Value = _scanId;
            _insertFrontier.Parameters["@dir"].Value = child;
            _insertFrontier.ExecuteNonQuery();
        }

        _uncommitted += 1 + children.Count;
    }

    private void BeginTransaction()
    {
        _transaction = _connection.BeginTransaction();
        _upsertFile.Transaction = _transaction;
        _deleteFrontier.Transaction = _transaction;
        _insertFrontier.Transaction = _transaction;
        _uncommitted = 0;
        _sinceCommit.Restart();
    }

    private void Checkpoint()
    {
        Commit();
        BeginTransaction();
    }

    private void Commit()
    {
        if (_transaction is null) return;
        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        _uncommitted = 0;
    }

    private void Rollback()
    {
        if (_transaction is null) return;
        try { _transaction.Rollback(); } catch { /* connection may already be gone */ }
        _transaction.Dispose();
        _transaction = null;
    }

    private static void AddParams(SqliteCommand cmd, params string[] names)
    {
        foreach (string n in names) cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value));
    }

    public void Dispose()
    {
        Rollback();
        _upsertFile.Dispose();
        _deleteFrontier.Dispose();
        _insertFrontier.Dispose();
    }
}
