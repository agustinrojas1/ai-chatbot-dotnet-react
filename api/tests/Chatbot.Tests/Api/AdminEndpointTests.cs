using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chatbot.Core.Domain;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Api;

[Collection(PostgresCollection.Name)]
public class AdminEndpointTests(PostgresFixture postgres)
{
    private const string Key = "test-key";
    private const string Phone = "+54 11 5555-4444";

    private async Task<(WebApplicationFactory<Program> Factory, AgentWorld World)> StartAsync(string? key = Key, string? maskPhones = null)
    {
        var settings = new Dictionary<string, string> { ["Admin:Key"] = key ?? "" };
        if (maskPhones is not null)
            settings["Admin:MaskPhones"] = maskPhones;
        var (factory, world) = await ApiHost.StartAsync(postgres, new ScriptedChatClient(), settings);
        await SeedAsync(world);
        return (factory, world);
    }

    /// <summary>One slot booking at the clinic, one order at the restaurant and one handoff at each.</summary>
    private static async Task SeedAsync(AgentWorld world)
    {
        await using var db = world.NewDb();
        var clinic = await db.Businesses.SingleAsync(b => b.Niche == Niche.Clinic);
        var restaurant = await db.Businesses.SingleAsync(b => b.Niche == Niche.Restaurant);
        var slot = await db.AvailabilitySlots.Where(s => s.BusinessId == clinic.Id).OrderBy(s => s.StartsAt).FirstAsync();
        var offering = await db.Offerings.Where(o => o.BusinessId == restaurant.Id).FirstAsync();

        var clinicSession = await world.NewSessionAsync(Niche.Clinic);
        var restaurantSession = await world.NewSessionAsync(Niche.Restaurant);
        var now = DateTimeOffset.UtcNow;

        db.Bookings.AddRange(
            new Booking
            {
                BusinessId = clinic.Id, SessionId = clinicSession, SlotId = slot.Id, OfferingId = slot.OfferingId, CustomerName = "Ana Perez",
                CustomerPhone = Phone, Notes = "First visit", IdempotencyKey = "k1", CreatedAt = now.AddMinutes(-10),
            },
            new Booking
            {
                BusinessId = restaurant.Id, SessionId = restaurantSession, CustomerName = "Beto Diaz", CustomerPhone = "+54 11 6666-7777",
                Items = JsonSerializer.SerializeToElement(new[] { new { offeringId = offering.Id, qty = 2 } }), Total = offering.Price * 2,
                IdempotencyKey = "k2", CreatedAt = now,
            });
        db.HandoffRequests.AddRange(
            new HandoffRequest { SessionId = clinicSession, Reason = "wants a person", Summary = "Clinic summary", CreatedAt = now.AddMinutes(-5) },
            new HandoffRequest { SessionId = restaurantSession, Reason = "complaint", Summary = "Restaurant summary", CreatedAt = now });
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage Get(string url, string? key = Key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key is not null)
            request.Headers.Add("X-Admin-Key", key);
        return request;
    }

    [Theory]
    [InlineData("/api/admin/bookings")]
    [InlineData("/api/admin/handoffs")]
    public async Task Missing_or_wrong_key_is_401(string url)
    {
        var (factory, _) = await StartAsync();
        using var host = factory;
        var client = factory.CreateClient();

        var missing = await client.SendAsync(Get(url, key: null));
        var wrong = await client.SendAsync(Get(url, key: "nope"));
        var prefix = await client.SendAsync(Get(url, key: "test-ke"));

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, prefix.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Ana Perez", await wrong.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/admin/bookings")]
    [InlineData("/api/admin/handoffs")]
    public async Task A_blank_configured_key_disables_the_endpoints_with_503_even_for_an_empty_header(string url)
    {
        var (factory, _) = await StartAsync(key: "");
        using var host = factory;
        var client = factory.CreateClient();

        var withKey = await client.SendAsync(Get(url, key: Key));
        var empty = await client.SendAsync(Get(url, key: ""));
        var none = await client.SendAsync(Get(url, key: null));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, empty.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.StatusCode);
        Assert.Contains("Admin:Key", await withKey.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bookings_are_listed_newest_first_with_masked_phones_by_default()
    {
        var (factory, world) = await StartAsync();
        using var host = factory;

        var response = await factory.CreateClient().SendAsync(Get("/api/admin/bookings"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(["Beto Diaz", "Ana Perez"], items.Select(i => i.GetProperty("customerName").GetString()));
        Assert.Equal(["restaurant", "dental"], items.Select(i => i.GetProperty("niche").GetString()));
        Assert.Equal(["+54 11 ****-7777", "+54 11 ****-4444"], items.Select(i => i.GetProperty("customerPhone").GetString()));

        var order = items[0];
        Assert.Equal("PendingConfirmation", order.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("slotStartsAt").ValueKind);
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());
        Assert.True(order.GetProperty("total").GetDecimal() > 0);
        Assert.NotEqual(Guid.Empty, order.GetProperty("sessionId").GetGuid());

        var visit = items[1];
        Assert.Equal(JsonValueKind.String, visit.GetProperty("slotStartsAt").ValueKind);
        Assert.Equal("First visit", visit.GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.Null, visit.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Null, visit.GetProperty("total").ValueKind);
        Assert.True(visit.TryGetProperty("offeringName", out _));
        Assert.True(visit.TryGetProperty("createdAt", out _));

        // The database keeps the full number either way.
        await using var db = world.NewDb();
        Assert.Contains(await db.Bookings.Select(b => b.CustomerPhone).ToListAsync(), p => p == Phone);
    }

    [Fact]
    public async Task Phones_are_returned_in_full_only_when_masking_is_switched_off()
    {
        var (factory, _) = await StartAsync(maskPhones: "false");
        using var host = factory;
        var client = factory.CreateClient();

        var withKey = await (await client.SendAsync(Get("/api/admin/bookings"))).Content.ReadFromJsonAsync<JsonElement>();
        var withoutKey = await client.SendAsync(Get("/api/admin/bookings", key: null));

        Assert.Equal([Phone, "+54 11 6666-7777"], withKey.EnumerateArray().Select(i => i.GetProperty("customerPhone").GetString()).Order());
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.DoesNotContain("5555", await withoutKey.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Phones_inside_free_text_are_masked_with_the_phone_masking_on_and_kept_when_it_is_off()
    {
        const string leak = "Luca Rossi, +1 555 222 3344, wants a person";
        foreach (var maskPhones in new[] { "true", "false" })
        {
            var (factory, world) = await StartAsync(maskPhones: maskPhones);
            using var host = factory;
            await using (var db = world.NewDb())
            {
                var booking = await db.Bookings.SingleAsync(b => b.IdempotencyKey == "k2");
                booking.Notes = "Call +1 555 222 3344 after 6";
                booking.Items = JsonSerializer.SerializeToElement(new[] { new { offeringId = Guid.NewGuid(), qty = 1, options = "call 555-222-3344" } });
                db.HandoffRequests.Add(new HandoffRequest { SessionId = booking.SessionId, Reason = "phone +1 555 222 3344", Summary = leak, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
                await db.SaveChangesAsync();
            }

            var client = factory.CreateClient();
            // Parsed, because the response writer escapes "+" as \u002B.
            var bookings = (await (await client.SendAsync(Get("/api/admin/bookings?niche=restaurant"))).Content.ReadFromJsonAsync<JsonElement>())[0];
            var handoffs = (await (await client.SendAsync(Get("/api/admin/handoffs?niche=restaurant"))).Content.ReadFromJsonAsync<JsonElement>())
                .EnumerateArray().Select(h => (Reason: h.GetProperty("reason").GetString(), Summary: h.GetProperty("summary").GetString())).First(h => h.Reason!.StartsWith("phone"));
            var options = bookings.GetProperty("items")[0].GetProperty("options").GetString();

            if (maskPhones == "true")
            {
                // The same masking as customerPhone: the middle digits go, the last four stay.
                foreach (var text in new[] { bookings.GetProperty("notes").GetString(), options, handoffs.Reason, handoffs.Summary })
                {
                    Assert.DoesNotContain("222", text);
                    Assert.Contains("3344", text);
                    Assert.Contains("***", text);
                }

                Assert.StartsWith("Luca Rossi, ", handoffs.Summary);
                Assert.EndsWith(", wants a person", handoffs.Summary);
            }
            else
            {
                Assert.Equal("Call +1 555 222 3344 after 6", bookings.GetProperty("notes").GetString());
                Assert.Equal("call 555-222-3344", options);
                Assert.Equal(leak, handoffs.Summary);
            }
        }
    }

    [Theory]
    [InlineData("dental", "Ana Perez")]
    [InlineData("restaurant", "Beto Diaz")]
    public async Task Bookings_can_be_filtered_by_niche(string niche, string customer)
    {
        var (factory, _) = await StartAsync();
        using var host = factory;

        var items = await (await factory.CreateClient().SendAsync(Get($"/api/admin/bookings?niche={niche}"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal([customer], items.EnumerateArray().Select(i => i.GetProperty("customerName").GetString()));
    }

    [Fact]
    public async Task An_empty_niche_list_and_an_unknown_niche_are_told_apart()
    {
        var (factory, _) = await StartAsync();
        using var host = factory;
        var client = factory.CreateClient();

        var realEstate = await (await client.SendAsync(Get("/api/admin/bookings?niche=realestate"))).Content.ReadFromJsonAsync<JsonElement>();
        var unknown = await client.SendAsync(Get("/api/admin/bookings?niche=bakery"));

        Assert.Equal(0, realEstate.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Handoffs_are_listed_and_filtered_by_niche()
    {
        var (factory, _) = await StartAsync();
        using var host = factory;
        var client = factory.CreateClient();

        var all = await (await client.SendAsync(Get("/api/admin/handoffs"))).Content.ReadFromJsonAsync<JsonElement>();
        var dental = await (await client.SendAsync(Get("/api/admin/handoffs?niche=dental"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["complaint", "wants a person"], all.EnumerateArray().Select(i => i.GetProperty("reason").GetString()));
        var item = Assert.Single(dental.EnumerateArray());
        Assert.Equal("dental", item.GetProperty("niche").GetString());
        Assert.Equal("Clinic summary", item.GetProperty("summary").GetString());
        Assert.Equal("Open", item.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, item.GetProperty("sessionId").GetGuid());
        Assert.NotEqual(Guid.Empty, item.GetProperty("id").GetGuid());
        Assert.True(item.TryGetProperty("createdAt", out _));
    }
}
