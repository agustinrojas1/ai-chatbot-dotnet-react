using Chatbot.Core.Knowledge;

namespace Chatbot.Tests.Knowledge;

public class ReciprocalRankFusionTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Lists(params string[][] lists) => [.. lists.Select(l => (IReadOnlyList<string>)l)];

    [Fact]
    public void Score_is_the_sum_of_one_over_k_plus_rank()
    {
        var fused = ReciprocalRankFusion.Fuse(Lists(["a", "b"], ["b", "c"]));

        Assert.Equal(["b", "a", "c"], fused.Select(f => f.Key));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, 12);
        Assert.Equal(1.0 / 61, fused[1].Score, 12);
        Assert.Equal(1.0 / 62, fused[2].Score, 12);
    }

    [Fact]
    public void An_item_found_by_one_ranking_only_is_kept_with_a_null_rank_in_the_other()
    {
        var fused = ReciprocalRankFusion.Fuse(Lists(["semantic-only"], ["lexical-only"]));

        Assert.Equal(2, fused.Count);
        var semanticOnly = fused.Single(f => f.Key == "semantic-only");
        Assert.Equal([1, null], semanticOnly.Ranks);
        Assert.Equal([null, 1], fused.Single(f => f.Key == "lexical-only").Ranks);
    }

    [Fact]
    public void Agreement_between_rankings_beats_a_single_first_place()
    {
        // "agreed" is 2nd in both lists; "lonely" is 1st in one list only.
        var fused = ReciprocalRankFusion.Fuse(Lists(["lonely", "agreed"], ["other", "agreed"]));

        Assert.Equal("agreed", fused[0].Key);
    }

    [Fact]
    public void Ties_go_to_the_better_best_rank_then_to_the_one_seen_first()
    {
        // a: rank 1 in list 0 only. b: rank 1 in list 1 only. Same score and same best rank: a was seen first.
        var tie = ReciprocalRankFusion.Fuse(Lists(["a"], ["b"]));
        Assert.Equal(["a", "b"], tie.Select(f => f.Key));
        Assert.Equal(tie[0].Score, tie[1].Score);

        // x has ranks (1, 3) and z (3, 1): same score, same best rank, x was seen first. y (2, 2) scores a hair less (2/62 < 1/61 + 1/63).
        var fused = ReciprocalRankFusion.Fuse(Lists(["x", "y", "z"], ["z", "y", "x"]));
        Assert.Equal(["x", "z", "y"], fused.Select(f => f.Key));
    }

    [Fact]
    public void Empty_rankings_give_an_empty_result_and_one_empty_ranking_is_ignored()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse(Lists([], [])));
        Assert.Equal(["a", "b"], ReciprocalRankFusion.Fuse(Lists(["a", "b"], [])).Select(f => f.Key));
    }

    [Fact]
    public void A_key_repeated_in_one_ranking_counts_once_at_its_first_position()
    {
        var fused = ReciprocalRankFusion.Fuse(Lists(["a", "b", "a"]));

        Assert.Equal(["a", "b"], fused.Select(f => f.Key));
        Assert.Equal(1.0 / 61, fused[0].Score, 12);
    }

    [Fact]
    public void The_result_is_deterministic_and_k_must_be_positive()
    {
        var input = Lists(["a", "b", "c", "d"], ["d", "c", "b", "a"]);

        Assert.Equal(ReciprocalRankFusion.Fuse(input).Select(f => f.Key), ReciprocalRankFusion.Fuse(input).Select(f => f.Key));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse(input, k: 0));
    }
}
