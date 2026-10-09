using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;
using ChatClient = OpenAI.Chat.ChatClient;

namespace Chatbot.Tests.Llm;

public class GeminiThoughtSignaturePolicyTests
{
    private const string GeminiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
    private const string GroqUrl = "https://api.groq.com/openai/v1/";
    private const string Sentinel = GeminiThoughtSignaturePolicy.SkipValidatorSignature;

    private const string Completion = """{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}]}""";

    private static (ChatClient Client, RecordingHandler Handler) Build(string baseUrl, string responseBody = Completion, string contentType = "application/json")
    {
        var handler = new RecordingHandler(responseBody, contentType);
        return (new ChatClient("m", new ApiKeyCredential("k"), Options(baseUrl, handler)), handler);
    }

    // The same options the factory builds for a provider: endpoint + the policy (Gemini host only), over a recording transport.
    private static OpenAIClientOptions Options(string baseUrl, RecordingHandler handler)
    {
        var uri = new Uri(baseUrl);
        var options = new OpenAIClientOptions
        {
            Endpoint = uri,
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        GeminiThoughtSignaturePolicy.AddTo(options, uri);
        return options;
    }

    private static List<ChatMessage> ToolLoopHistory() =>
    [
        new(ChatRole.User, "Price of the loft?"),
        new(ChatRole.Assistant, [new FunctionCallContent("call_1", "search_catalog", new Dictionary<string, object?> { ["query"] = "loft ñandú 🏠" })]),
        new(ChatRole.Tool, [new FunctionResultContent("call_1", "[{\"name\":\"Loft\"}]")]),
    ];

    private static JsonArray ToolCalls(JsonNode body) =>
        body["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "assistant")!["tool_calls"]!.AsArray();

    private static async Task<JsonNode> SendProtocol(ChatClient client, RecordingHandler handler, string json)
    {
        await client.CompleteChatAsync(BinaryContent.Create(BinaryData.FromString(json)));
        return JsonNode.Parse(handler.Bodies.Single())!;
    }

    [Fact]
    public async Task Gemini_host_adds_the_sentinel_to_replayed_tool_calls()
    {
        var (client, handler) = Build(GeminiUrl);

        await client.AsIChatClient().GetResponseAsync(ToolLoopHistory());

        var call = ToolCalls(JsonNode.Parse(handler.Bodies.Single())!).Single()!;
        Assert.Equal(Sentinel, call["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
        // The rest of the call is intact.
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("search_catalog", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("loft ñandú 🏠", JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["query"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_host_signs_every_tool_call_of_parallel_calls()
    {
        var (client, handler) = Build(GeminiUrl);
        List<ChatMessage> history =
        [
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, [new FunctionCallContent("a", "search_catalog"), new FunctionCallContent("b", "check_availability")]),
            new(ChatRole.Tool, [new FunctionResultContent("a", "1"), new FunctionResultContent("b", "2")]),
        ];

        await client.AsIChatClient().GetResponseAsync(history);

        var calls = ToolCalls(JsonNode.Parse(handler.Bodies.Single())!);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(Sentinel, c!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Gemini_host_also_rewrites_streaming_requests()
    {
        var (client, handler) = Build(GeminiUrl, "data: [DONE]\n\n", "text/event-stream");

        await foreach (var _ in client.AsIChatClient().GetStreamingResponseAsync(ToolLoopHistory()))
        {
        }

        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(Sentinel, ToolCalls(body).Single()!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_providers_are_left_untouched()
    {
        var (client, handler) = Build(GroqUrl);

        await client.AsIChatClient().GetResponseAsync(ToolLoopHistory());

        var call = ToolCalls(JsonNode.Parse(handler.Bodies.Single())!).Single()!;
        Assert.Null(call["extra_content"]);
        Assert.DoesNotContain("thought_signature", handler.Bodies.Single());
    }

    [Fact]
    public async Task An_existing_real_signature_is_preserved()
    {
        var (client, handler) = Build(GeminiUrl);
        const string json = """
            {"model":"m","messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","tool_calls":[
                {"id":"a","type":"function","function":{"name":"f","arguments":"{}"},"extra_content":{"google":{"thought_signature":"REAL-SIGNATURE"}}},
                {"id":"b","type":"function","function":{"name":"g","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"a","content":"1"}]}
            """;

        var body = await SendProtocol(client, handler, json);

        var calls = ToolCalls(body);
        Assert.Equal("REAL-SIGNATURE", calls[0]!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
        Assert.Equal(Sentinel, calls[1]!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_extra_content_fields_are_kept_when_adding_the_signature()
    {
        var (client, handler) = Build(GeminiUrl);
        const string json = """
            {"model":"m","messages":[{"role":"assistant","tool_calls":[
              {"id":"a","type":"function","function":{"name":"f","arguments":"{}"},"extra_content":{"other":{"x":1}}}]}]}
            """;

        var body = await SendProtocol(client, handler, json);

        var call = ToolCalls(body).Single()!;
        Assert.Equal(1, call["extra_content"]!["other"]!["x"]!.GetValue<int>());
        Assert.Equal(Sentinel, call["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requests_without_tool_calls_are_sent_byte_for_byte()
    {
        var (client, handler) = Build(GeminiUrl);
        const string json = """{"model":"m",  "messages":[{"role":"user","content":"hola ñ"},{"role":"assistant","content":"tool_calls are mentioned here"}], "temperature":0.70}""";

        await client.CompleteChatAsync(BinaryContent.Create(BinaryData.FromString(json)));

        Assert.Equal(json, handler.Bodies.Single());
    }

    [Fact]
    public async Task Messages_other_than_assistant_tool_calls_keep_their_meaning()
    {
        var (client, handler) = Build(GeminiUrl);
        const string json = """
            {"model":"m","temperature":0.70,"messages":[
              {"role":"system","content":"sys"},
              {"role":"user","content":"hola ñ 🏠 \"quoted\""},
              {"role":"assistant","content":"plain answer"},
              {"role":"assistant","tool_calls":[{"id":"a","type":"function","function":{"name":"f","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"a","content":"result"}]}
            """;

        var sent = await SendProtocol(client, handler, json);

        var expected = JsonNode.Parse(json)!;
        expected["messages"]![3]!["tool_calls"]![0]!["extra_content"] = new JsonObject
        {
            ["google"] = new JsonObject { ["thought_signature"] = Sentinel },
        };
        Assert.True(JsonNode.DeepEquals(expected, sent), sent.ToJsonString());
        Assert.Contains("0.70", handler.Bodies.Single()); // numbers keep their original text
    }

    [Fact]
    public async Task Other_endpoints_are_not_touched()
    {
        var handler = new RecordingHandler("""{"object":"list","data":[],"model":"m","usage":{"prompt_tokens":1,"total_tokens":1}}""", "application/json");
        var client = new EmbeddingClient("m", new ApiKeyCredential("k"), Options(GeminiUrl, handler));
        const string json = """{"model":"m","input":"x","messages":[{"role":"assistant","tool_calls":[{"id":"a"}]}]}""";

        await client.GenerateEmbeddingsAsync(BinaryContent.Create(BinaryData.FromString(json)));

        Assert.Equal(json, handler.Bodies.Single());
    }

    private sealed class RecordingHandler(string responseBody, string contentType) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType),
            };
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType),
            };
        }
    }
}
