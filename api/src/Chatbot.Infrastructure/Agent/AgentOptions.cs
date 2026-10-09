namespace Chatbot.Infrastructure.Agent;

/// <summary>Bound from the <c>Agent</c> configuration section. Limits that keep one message from costing too much (SPEC 7.4, 9).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Longest accepted user message, in characters.</summary>
    public int MaxInputChars { get; set; } = 1000;

    /// <summary>How many of the latest user/assistant messages are sent to the model.</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>Model round trips per user message (a tool call costs one extra round trip).</summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>Cap on the tokens the model may generate in one call.</summary>
    public int MaxOutputTokens { get; set; } = 800;
}
