using System.Net;
using Chatbot.Infrastructure.Agent;
using Chatbot.Infrastructure.Agent.Tools;
using Chatbot.Tests.Agent;
using Chatbot.Tests.Data;
using static Chatbot.Tests.Security.SecurityHost;

namespace Chatbot.Tests.Security;

/// <summary>The cost caps of SPEC 9 reach every model call, and a misconfigured cap cannot silently disappear.</summary>
[Collection(PostgresCollection.Name)]
public class CostCapTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_model_call_of_a_tool_loop_carries_the_output_token_cap_and_the_loop_is_bounded()
    {
        var llm = new ScriptedChatClient((_, _) => Turn.Call(AgentToolFactory.SearchCatalog, new() { ["query"] = "x" }));
        var world = await AgentWorld.CreateAsync(postgres, llm, new AgentOptions { MaxIterations = 3, MaxOutputTokens = 77 });

        await world.RunAsync(await world.NewSessionAsync(), "show me everything");

        Assert.Equal(4, llm.Calls.Count); // N tool rounds + the call that follows the last one, never more
        Assert.All(llm.Options, o => Assert.Equal(77, o!.MaxOutputTokens));
    }

    [Theory]
    [InlineData("Agent:MaxOutputTokens", "0")]
    [InlineData("Agent:MaxOutputTokens", "-5")]
    [InlineData("Agent:MaxIterations", "0")]
    [InlineData("Agent:MaxInputChars", "0")]
    public async Task A_zero_or_negative_agent_limit_is_refused_at_startup(string key, string value)
    {
        var world = await AgentWorld.CreateAsync(postgres, AlwaysOk());
        using var factory = Start(world, new Dictionary<string, string> { [key] = value });

        AssertFailsToStart(factory, "Agent limit");
    }

    [Fact]
    public async Task The_configured_agent_limits_reach_the_model_through_the_http_pipeline()
    {
        var llm = AlwaysOk();
        var world = await AgentWorld.CreateAsync(postgres, llm);
        using var factory = Start(world, new Dictionary<string, string> { ["Agent:MaxOutputTokens"] = "123" });

        var response = await SendAsync(factory.CreateClient(), await world.NewSessionAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(123, Assert.Single(llm.Options)!.MaxOutputTokens);
    }
}
