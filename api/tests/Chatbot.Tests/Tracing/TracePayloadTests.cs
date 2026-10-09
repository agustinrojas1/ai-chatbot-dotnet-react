using System.Text.Json;
using Chatbot.Infrastructure.Agent.Tracing;

namespace Chatbot.Tests.Tracing;

public class TracePayloadTests
{
    [Fact]
    public void Null_stays_null_and_small_payloads_are_kept()
    {
        Assert.Null(TracePayload.Sanitize(null));
        var kept = TracePayload.Sanitize(new { query = "cleaning", maxPrice = 100 })!.Value;
        Assert.Equal("cleaning", kept.GetProperty("query").GetString());
        Assert.Equal(100, kept.GetProperty("maxPrice").GetInt32());
    }

    [Fact]
    public void Phone_numbers_are_masked_in_every_string_and_the_plus_is_not_escaped()
    {
        var payload = new
        {
            customerPhone = "+54 11 5555-4444",
            messages = new[] { new { text = "call me at +54 11 5555-4444 please" } },
            asNumber = 5491155554444L,
        };

        var raw = TracePayload.Sanitize(payload)!.Value.GetRawText();

        Assert.Contains("+54 11 ****-4444", raw);
        Assert.DoesNotContain("5555", raw);
        Assert.DoesNotContain("5491155554444", raw);
    }

    [Fact]
    public void Oversized_payloads_become_a_marked_preview_of_at_most_the_limit()
    {
        var huge = new { query = new string('a', 10_000) };

        var stored = TracePayload.Sanitize(huge)!.Value;

        Assert.Equal(JsonValueKind.String, stored.ValueKind);
        var preview = stored.GetString()!;
        Assert.EndsWith(TracePayload.TruncatedMarker, preview);
        Assert.Equal(TracePayload.MaxChars + TracePayload.TruncatedMarker.Length, preview.Length);
    }

    [Fact]
    public void A_phone_cut_by_the_truncation_does_not_leak()
    {
        // The number straddles the limit: masking runs before the cut, so the preview holds the masked text.
        var text = new string('x', TracePayload.MaxChars - 40) + " +54 11 5555-4444 " + new string('y', 200);

        var preview = TracePayload.Sanitize(new { text })!.Value.GetString()!;

        Assert.DoesNotContain("5555", preview);
    }

    [Fact]
    public void Clip_does_not_split_a_surrogate_pair()
    {
        var text = new string('a', 9) + "😀" + "tail";

        var clipped = TracePayload.Clip(text, 10);

        Assert.Equal(new string('a', 9) + TracePayload.TruncatedMarker, clipped);
    }
}
