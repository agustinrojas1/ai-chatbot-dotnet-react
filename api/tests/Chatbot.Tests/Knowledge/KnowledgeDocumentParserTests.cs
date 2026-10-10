using Chatbot.Core.Knowledge;

namespace Chatbot.Tests.Knowledge;

public class KnowledgeDocumentParserTests
{
    private const string Valid = """
        ---
        title: "Cancellation policy: appointments"
        language: es
        tags: [citas, cancelacion]
        updated: 2026-09-30
        ---

        ## Plazos

        Avisá con 24 horas.
        """;

    [Fact]
    public void Reads_every_field_and_the_body()
    {
        var doc = KnowledgeDocumentParser.Parse("dental/x.md", Valid);

        Assert.Equal("Cancellation policy: appointments", doc.Title); // quotes removed, the colon inside kept
        Assert.Equal("es", doc.Language);
        Assert.Equal(["citas", "cancelacion"], doc.Tags);
        Assert.Equal(new DateOnly(2026, 9, 30), doc.Updated);
        Assert.StartsWith("## Plazos", doc.Body);
    }

    [Fact]
    public void Tags_can_be_a_block_list_or_comma_separated_and_are_optional()
    {
        var block = KnowledgeDocumentParser.Parse("a.md", "---\ntitle: T\nlanguage: en\ntags:\n  - one\n  - 'two'\n---\nBody");
        var commas = KnowledgeDocumentParser.Parse("a.md", "---\ntitle: T\nlanguage: en\ntags: one, two, ONE\n---\nBody");
        var none = KnowledgeDocumentParser.Parse("a.md", "---\ntitle: T\nlanguage: en\n---\nBody");

        Assert.Equal(["one", "two"], block.Tags);
        Assert.Equal(["one", "two"], commas.Tags); // duplicates ignoring case are dropped
        Assert.Empty(none.Tags);
        Assert.Null(none.Updated);
    }

    [Fact]
    public void Windows_line_endings_a_bom_comments_and_unknown_keys_are_accepted()
    {
        var content = "\uFEFF---\r\n# a comment\r\ntitle: T\r\nlanguage: EN\r\nauthor: someone\r\n---\r\n\r\nBody\r\n";

        var doc = KnowledgeDocumentParser.Parse("a.md", content);

        Assert.Equal(("T", "en", "Body"), (doc.Title, doc.Language, doc.Body));
    }

    [Theory]
    [InlineData("No frontmatter here", "must start with a frontmatter")]
    [InlineData("", "must start with a frontmatter")]
    [InlineData("---\ntitle: T\nlanguage: en\nBody without closing", "not closed")]
    [InlineData("---\nlanguage: en\n---\nBody", "'title' is required")]
    [InlineData("---\ntitle: \"\"\nlanguage: en\n---\nBody", "'title' is required")]
    [InlineData("---\ntitle: T\n---\nBody", "'language' must be 'es' or 'en'")]
    [InlineData("---\ntitle: T\nlanguage: fr\n---\nBody", "'language' must be 'es' or 'en' (found 'fr')")]
    [InlineData("---\ntitle: T\nlanguage: en\nupdated: 30/09/2026\n---\nBody", "'updated' must be a date")]
    [InlineData("---\ntitle: T\ntitle: U\nlanguage: en\n---\nBody", "'title' appears twice")]
    [InlineData("---\ntitle: T\nlanguage en\n---\nBody", "expected 'key: value'")]
    [InlineData("---\n- orphan\ntitle: T\nlanguage: en\n---\nBody", "list item without a field")]
    [InlineData("---\ntitle: T\nlanguage: en\n---\n\n   \n", "no content after the frontmatter")]
    public void Malformed_documents_fail_with_an_error_that_names_the_file(string content, string expected)
    {
        var error = Assert.Throws<KnowledgeFormatException>(() => KnowledgeDocumentParser.Parse("dental/broken.md", content));

        Assert.StartsWith("dental/broken.md: ", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_horizontal_rule_inside_the_body_does_not_end_the_frontmatter_early()
    {
        var doc = KnowledgeDocumentParser.Parse("a.md", "---\ntitle: T\nlanguage: en\n---\nFirst\n\n---\n\nSecond");

        Assert.Contains("---", doc.Body);
        Assert.Contains("Second", doc.Body);
    }
}
