using System.Security.Cryptography;
using System.Text;

namespace MediaTool.Core.Storage;

public enum ReviewDecisionState
{
    Confirmed,
    Skipped,
}

public sealed record ReviewDecision(string ClusterKey, long KeeperFileKey, ReviewDecisionState State);

/// <summary>
/// What a person decided while reviewing, kept between sessions.
///
/// Reviewing a large library is hours of work spread over days, and every one of those
/// decisions is a judgement no machine could make. Holding them in the window meant closing
/// it threw them away — and because Apply only ever acted on confirmed clusters, the loss
/// was silent: the button simply went back to saying there was nothing to do.
///
/// Clusters are identified by who is in them rather than by where they sit in a list. A list
/// re-orders whenever the scope changes; the set of files a person looked at does not. That
/// also means a decision correctly stops applying once the cluster it was about is gone.
/// </summary>
public sealed class ReviewDecisions
{
    private readonly CatalogDatabase _db;

    public ReviewDecisions(CatalogDatabase db) => _db = db;

    /// <summary>
    /// A stable name for a cluster, derived from its members.
    ///
    /// Order-independent: the same files reviewed in a different order are the same cluster.
    /// </summary>
    public static string KeyFor(IEnumerable<long> fileKeys)
    {
        var ordered = fileKeys.Distinct().OrderBy(k => k).ToArray();
        if (ordered.Length == 0) throw new ArgumentException("A cluster has at least one file.", nameof(fileKeys));

        var text = new StringBuilder();
        foreach (long key in ordered) text.Append(key).Append(',');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..32];
    }

    public void Save(string clusterKey, long keeperFileKey, ReviewDecisionState state)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_decisions (cluster_key, keeper_file_key, state, decided_utc)
            VALUES ($k, $keeper, $state, $when)
            ON CONFLICT(cluster_key) DO UPDATE SET
                keeper_file_key = excluded.keeper_file_key,
                state           = excluded.state,
                decided_utc     = excluded.decided_utc
            """;
        cmd.Parameters.AddWithValue("$k", clusterKey);
        cmd.Parameters.AddWithValue("$keeper", keeperFileKey);
        cmd.Parameters.AddWithValue("$state", state.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets one decision, for when a reviewer changes their mind.</summary>
    public void Forget(string clusterKey)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM review_decisions WHERE cluster_key = $k";
        cmd.Parameters.AddWithValue("$k", clusterKey);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, ReviewDecision> LoadAll()
    {
        var result = new Dictionary<string, ReviewDecision>(StringComparer.Ordinal);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT cluster_key, keeper_file_key, state FROM review_decisions";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            var state = reader.GetString(2) == "skipped" ? ReviewDecisionState.Skipped : ReviewDecisionState.Confirmed;
            result[key] = new ReviewDecision(key, reader.GetInt64(1), state);
        }

        return result;
    }

    public int Count()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM review_decisions";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
