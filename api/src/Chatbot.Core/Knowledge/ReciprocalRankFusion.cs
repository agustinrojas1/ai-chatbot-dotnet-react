namespace Chatbot.Core.Knowledge;

/// <summary>An item of the fused ranking: its RRF score and its 1-based rank in every input ranking (null where it was absent).</summary>
public sealed record FusedHit<TKey>(TKey Key, double Score, IReadOnlyList<int?> Ranks);

/// <summary>
/// Reciprocal Rank Fusion (Cormack et al., 2009): <c>score(d) = sum over rankings of 1 / (k + rank(d))</c>. It merges rankings
/// whose scores cannot be compared (a cosine similarity and a <c>ts_rank</c>) by using only the positions. An item found by one
/// ranking only still scores, so a hit that one retriever misses is not lost. Pure and deterministic.
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>The constant of the original paper and of the pgvector hybrid-search example: it keeps one first place from outweighing agreement between rankings.</summary>
    public const int DefaultK = 60;

    /// <summary>
    /// The fused ranking, best first. Ties (equal score) go to the item with the better best rank, then to the one seen first
    /// (earlier ranking, earlier position). A key repeated inside one ranking counts once, at its first position.
    /// </summary>
    public static IReadOnlyList<FusedHit<TKey>> Fuse<TKey>(IReadOnlyList<IReadOnlyList<TKey>> rankings, int k = DefaultK)
        where TKey : notnull
    {
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be greater than zero.");

        var scores = new Dictionary<TKey, double>();
        var ranks = new Dictionary<TKey, int?[]>();
        var firstSeen = new List<TKey>();

        for (var list = 0; list < rankings.Count; list++)
        {
            var position = 0;
            foreach (var key in rankings[list])
            {
                position++;
                if (!ranks.TryGetValue(key, out var itemRanks))
                {
                    itemRanks = new int?[rankings.Count];
                    ranks[key] = itemRanks;
                    scores[key] = 0;
                    firstSeen.Add(key);
                }

                if (itemRanks[list] is not null)
                    continue; // repeated in the same ranking: the first position counts

                itemRanks[list] = position;
                scores[key] += 1.0 / (k + position);
            }
        }

        return [.. firstSeen
            .Select((key, order) => (Key: key, Order: order))
            .OrderByDescending(x => scores[x.Key])
            .ThenBy(x => ranks[x.Key].Min(r => r ?? int.MaxValue))
            .ThenBy(x => x.Order)
            .Select(x => new FusedHit<TKey>(x.Key, scores[x.Key], ranks[x.Key]))];
    }
}
