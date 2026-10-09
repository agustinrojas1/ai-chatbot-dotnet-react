using Chatbot.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Tests.Llm;

public class LlmClientFactoryTests
{
    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddLlm(configuration).BuildServiceProvider();
    }

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "gemini")]
    [InlineData("https://api.groq.com/openai/v1", "groq")]
    [InlineData("https://openrouter.ai/api/v1", "openrouter.ai")]
    public void Provider_label_comes_from_the_host(string baseUrl, string expected)
    {
        Assert.Equal(expected, LlmClientFactory.ProviderLabel(new Uri(baseUrl)));
    }

    [Fact]
    public void Registers_a_keyed_fallback_client_bound_from_configuration()
    {
        using var provider = Provider(new()
        {
            ["Llm:Primary:BaseUrl"] = "https://generativelanguage.googleapis.com/v1beta/openai/",
            ["Llm:Primary:ApiKey"] = "key-1",
            ["Llm:Primary:Model"] = "gemini-model",
            ["Llm:Fallback:BaseUrl"] = "https://api.groq.com/openai/v1",
            ["Llm:Fallback:ApiKey"] = "key-2",
            ["Llm:Fallback:Model"] = "groq-model",
        });

        var client = provider.GetRequiredKeyedService<IChatClient>(LlmClientFactory.ServiceKey);

        Assert.IsType<FallbackChatClient>(client);
        Assert.Same(client, provider.GetRequiredKeyedService<IChatClient>(LlmClientFactory.ServiceKey));
        Assert.Null(provider.GetService<IChatClient>()); // only the keyed registration exists; decorators add the unkeyed one
    }

    [Fact]
    public async Task Missing_primary_key_does_not_fail_at_startup_but_on_first_use()
    {
        using var provider = Provider([]);

        var client = provider.GetRequiredKeyedService<IChatClient>(LlmClientFactory.ServiceKey);

        var error = await Assert.ThrowsAsync<LlmNotConfiguredException>(() => client.GetResponseAsync("hi"));
        Assert.Contains("Llm:Primary:ApiKey", error.Message);
        await Assert.ThrowsAsync<LlmNotConfiguredException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync("hi"))
            {
            }
        });
    }

    [Fact]
    public void Fallback_without_api_key_is_ignored()
    {
        using var provider = Provider(new()
        {
            ["Llm:Primary:BaseUrl"] = "https://api.groq.com/openai/v1",
            ["Llm:Primary:ApiKey"] = "key-1",
            ["Llm:Primary:Model"] = "m",
            ["Llm:Fallback:BaseUrl"] = "https://api.groq.com/openai/v1",
            ["Llm:Fallback:ApiKey"] = "",
            ["Llm:Fallback:Model"] = "",
        });

        Assert.IsType<FallbackChatClient>(provider.GetRequiredKeyedService<IChatClient>(LlmClientFactory.ServiceKey));
    }

    [Fact]
    public void Invalid_base_url_is_reported_clearly_when_the_client_is_first_resolved()
    {
        using var provider = Provider(new()
        {
            ["Llm:Primary:BaseUrl"] = "not a url",
            ["Llm:Primary:ApiKey"] = "key-1",
            ["Llm:Primary:Model"] = "m",
        });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeyedService<IChatClient>(LlmClientFactory.ServiceKey));
        Assert.Contains("BaseUrl", error.Message);
    }
}
