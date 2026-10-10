using Chatbot.Core.Knowledge;

namespace Chatbot.Tests.Knowledge;

public class ChunkerTests
{
    private static string Words(int count, string prefix = "word") => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    [Fact]
    public void One_chunk_per_heading_section_with_the_title_and_heading_as_context()
    {
        var body = """
            ## Cancellations

            Cancel at least 24 hours before.

            ## Late arrivals

            After 15 minutes the slot is released.
            """;

        var chunks = Chunker.Split("Appointment policy", body);

        Assert.Equal(2, chunks.Count);
        Assert.Equal([0, 1], chunks.Select(c => c.Ordinal));
        Assert.Equal(["Cancellations", "Late arrivals"], chunks.Select(c => c.Heading));
        Assert.Equal("Appointment policy > Cancellations\nCancel at least 24 hours before.", chunks[0].Text);
        Assert.StartsWith("Appointment policy > Late arrivals\n", chunks[1].Text);
    }

    [Fact]
    public void Nested_headings_are_a_path_and_a_heading_that_repeats_the_title_is_dropped()
    {
        var body = """
            # Pets

            Intro text.

            ## Dogs

            ### Size limits

            Up to 25 kg.

            ## Cats

            Always welcome.
            """;

        var chunks = Chunker.Split("Pets", body);

        Assert.Equal(["Pets", "Dogs > Size limits", "Cats"], chunks.Select(c => c.Heading));
        Assert.Equal("Pets\nIntro text.", chunks[0].Text); // no "Pets > Pets"
        Assert.StartsWith("Pets > Dogs > Size limits\n", chunks[1].Text);
    }

    [Fact]
    public void Text_before_the_first_heading_belongs_to_the_title_and_empty_sections_are_dropped()
    {
        var body = "Opening hours apply to everyone.\n\n## Empty section\n\n## Deliveries\n\nDaily until 23:00.";

        var chunks = Chunker.Split("Hours", body);

        Assert.Equal(["Hours", "Deliveries"], chunks.Select(c => c.Heading));
        Assert.Equal([0, 1], chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void A_hash_line_inside_a_code_fence_is_not_a_heading()
    {
        var body = "## Setup\n\nRun:\n\n```\n# not a heading\nls\n```\n\nDone.";

        var chunks = Chunker.Split("Doc", body);

        var chunk = Assert.Single(chunks);
        Assert.Contains("# not a heading", chunk.Text);
    }

    [Fact]
    public void A_long_section_is_split_into_pieces_near_the_target_size_that_never_exceed_the_maximum()
    {
        var options = new ChunkerOptions(TargetTokens: 100, MaxTokens: 125, OverlapTokens: 15);
        var paragraphs = Enumerable.Range(0, 12).Select(i => $"Paragraph {i}. " + Words(30, $"p{i}w"));
        var body = "## Long\n\n" + string.Join("\n\n", paragraphs);

        var chunks = Chunker.Split("Doc", body, options);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens, $"chunk {c.Ordinal} has {c.TokenCount} tokens"));
        Assert.All(chunks, c => Assert.StartsWith("Doc > Long\n", c.Text));
        Assert.All(chunks, c => Assert.Equal("Long", c.Heading));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));

        // Nothing is lost: every word of the section is in at least one piece.
        var all = string.Join(' ', chunks.Select(c => c.Text));
        for (var i = 0; i < 12; i++)
            Assert.Contains($"p{i}w29", all);
    }

    [Fact]
    public void Consecutive_pieces_overlap_by_the_tail_of_the_previous_one()
    {
        var options = new ChunkerOptions(TargetTokens: 100, MaxTokens: 125, OverlapTokens: 20);
        var body = "## Long\n\n" + Words(400);

        var chunks = Chunker.Split("Doc", body, options);

        Assert.True(chunks.Count > 2);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previous = chunks[i - 1].Text["Doc > Long\n".Length..];
            var current = chunks[i].Text["Doc > Long\n".Length..];
            var firstWord = current.Split(' ')[0];
            var at = previous.LastIndexOf(firstWord, StringComparison.Ordinal);
            Assert.True(at >= 0, $"piece {i} does not start inside piece {i - 1}");
            Assert.StartsWith(previous[at..], current); // the whole tail of the previous piece reappears at the start
            Assert.InRange(previous.Length - at, 1, options.OverlapTokens * Chunker.CharsPerToken);
        }
    }

    [Fact]
    public void Zero_overlap_gives_disjoint_pieces()
    {
        var options = new ChunkerOptions(TargetTokens: 100, MaxTokens: 125, OverlapTokens: 0);
        var chunks = Chunker.Split("Doc", "## Long\n\n" + Words(400), options);

        var words = chunks.SelectMany(c => c.Text["Doc > Long\n".Length..].Split(' ')).ToList();
        Assert.Equal(words.Count, words.Distinct().Count());
    }

    [Fact]
    public void A_single_word_longer_than_the_budget_is_cut_instead_of_looping()
    {
        var options = new ChunkerOptions(TargetTokens: 50, MaxTokens: 60, OverlapTokens: 5);

        var chunks = Chunker.Split("Doc", "## Blob\n\n" + new string('x', 2000), options);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= options.MaxTokens));
    }

    [Fact]
    public void The_same_input_always_gives_the_same_chunks()
    {
        var body = "## A\n\n" + Words(500) + "\n\n## B\n\nShort one.\n\n## C\n\n" + Words(300, "c");

        var first = Chunker.Split("Doc", body);
        var second = Chunker.Split("Doc", body);

        Assert.Equal(first, second);
        Assert.Equal(Enumerable.Range(0, first.Count), first.Select(c => c.Ordinal));
    }

    [Fact]
    public void Windows_line_endings_give_the_same_chunks()
    {
        var body = "## A\n\nOne.\n\n## B\n\nTwo.";

        Assert.Equal(Chunker.Split("Doc", body), Chunker.Split("Doc", body.Replace("\n", "\r\n")));
    }

    [Fact]
    public void Invalid_sizes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Chunker.Split("Doc", "x", new ChunkerOptions(TargetTokens: 100, MaxTokens: 50)));
        Assert.Throws<ArgumentException>(() => Chunker.Split("Doc", "x", new ChunkerOptions(TargetTokens: 100, MaxTokens: 150, OverlapTokens: 100)));
    }

    [Fact]
    public void Token_estimate_is_four_characters_per_token_rounded_up()
    {
        Assert.Equal(0, Chunker.EstimateTokens(""));
        Assert.Equal(1, Chunker.EstimateTokens("abcd"));
        Assert.Equal(2, Chunker.EstimateTokens("abcde"));
    }
}
