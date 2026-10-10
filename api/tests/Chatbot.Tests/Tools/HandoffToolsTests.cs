using Chatbot.Core.Domain;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace Chatbot.Tests.Tools;

[Collection(PostgresCollection.Name)]
public class HandoffToolsTests(PostgresFixture postgres)
{
    private const string Args = """{"reason":"Customer asked for a person","summary":"Wants to negotiate the rent of the Palermo flat."}""";

    [Fact]
    public async Task Handoff_CreatesOpenRequestMovesSessionAndSetsContext()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.HandoffToHuman, Args);

        Assert.False(result.GetProperty("alreadyRequested").GetBoolean());
        Assert.Equal("Customer asked for a person", session.Context.HandoffReason);

        await using var db = world.NewDb();
        var request = await db.HandoffRequests.SingleAsync();
        Assert.Equal(result.GetProperty("handoffId").GetGuid(), request.Id);
        Assert.Equal(session.Context.SessionId, request.SessionId);
        Assert.Equal(HandoffStatus.Open, request.Status);
        Assert.Equal("Customer asked for a person", request.Reason);
        Assert.Equal("Wants to negotiate the rent of the Palermo flat.", request.Summary);
        Assert.Equal(SessionStatus.AwaitingHuman, (await db.ChatSessions.SingleAsync(s => s.Id == session.Context.SessionId)).Status);
    }

    [Fact]
    public async Task Handoff_IsIdempotentWhileARequestIsOpen()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var first = await session.CallAsync(AgentToolFactory.HandoffToHuman, Args);
        var second = await session.CallAsync(AgentToolFactory.HandoffToHuman, """{"reason":"Again","summary":"Same thing"}""");

        Assert.True(second.GetProperty("alreadyRequested").GetBoolean());
        Assert.Equal(first.GetProperty("handoffId").GetGuid(), second.GetProperty("handoffId").GetGuid());
        Assert.Equal("Customer asked for a person", session.Context.HandoffReason);
        await using var db = world.NewDb();
        Assert.Equal(1, await db.HandoffRequests.CountAsync());
    }

    [Theory]
    [InlineData("""{"summary":"x"}""", "invalid_reason")]
    [InlineData("""{"reason":"  ","summary":"x"}""", "invalid_reason")]
    [InlineData("""{"reason":"x"}""", "invalid_summary")]
    public async Task Handoff_InvalidArguments_ReturnReadableError(string arguments, string code)
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var session = await world.NewSessionAsync(world.A);

        var result = await session.CallAsync(AgentToolFactory.HandoffToHuman, arguments);

        Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.Null(session.Context.HandoffReason);
        await using var db = world.NewDb();
        Assert.Empty(await db.HandoffRequests.ToListAsync());
        Assert.Equal(SessionStatus.Active, (await db.ChatSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Handoff_SessionOfAnotherBusiness_IsNotFound()
    {
        var world = await ToolWorld.CreateAsync(postgres);
        await using var real = await world.NewSessionAsync(world.B);
        // Tools of business A pointing at a session that belongs to business B.
        await using var db = world.NewDb();
        var context = new ToolContext(world.A.Id, real.Context.SessionId, world.A.Timezone);
        var tools = new Session(db, context, AgentToolFactory.Create(db, context, ToolWorld.Clock, world.NewKnowledge(db)));

        var result = await tools.CallAsync(AgentToolFactory.HandoffToHuman, Args);

        Assert.Equal("session_not_found", result.GetProperty("code").GetString());
        Assert.Empty(await db.HandoffRequests.ToListAsync());
    }
}
