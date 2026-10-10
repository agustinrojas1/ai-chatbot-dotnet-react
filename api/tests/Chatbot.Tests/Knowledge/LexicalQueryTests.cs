using Chatbot.Core.Knowledge;

namespace Chatbot.Tests.Knowledge;

public class LexicalQueryTests
{
    [Theory]
    [InlineData("What is the cancellation fee?", "cancellation fee")]
    [InlineData("¿Aceptan OSDE 210?", "aceptan osde 210")]
    [InlineData("Do you accept USD 1,500 deposits?", "accept usd 1,500 deposits")]
    [InlineData("24-hour notice", "24-hour notice")]
    public void Keeps_the_content_words_so_that_postgres_ands_them(string question, string expected) =>
        Assert.Equal(expected, LexicalQuery.Build(question));

    [Fact]
    public void Operators_and_punctuation_from_the_user_never_reach_the_query()
    {
        Assert.Equal("fee refund quoted", LexicalQuery.Build("fee OR -refund \"quoted\" & | ! ("));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("¿Qué es lo que hay?")]
    [InlineData("?!?")]
    public void Nothing_but_common_words_gives_no_lexical_query(string question) => Assert.Null(LexicalQuery.Build(question));

    [Fact]
    public void Repeated_words_are_kept_once_and_the_size_is_bounded()
    {
        Assert.Equal("pets", LexicalQuery.Build("pets pets PETS"));
        var many = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"term{i}"));
        Assert.Equal(LexicalQuery.MaxTerms, LexicalQuery.Build(many)!.Split(' ').Length);
    }
}
