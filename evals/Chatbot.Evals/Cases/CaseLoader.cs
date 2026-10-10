using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chatbot.Evals.Checking;
using Chatbot.Infrastructure.Agent.Tools;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Chatbot.Evals.Cases;

/// <summary>Every problem found in the case files, each one as <c>file:line: message</c>.</summary>
internal sealed class CaseFormatException(IReadOnlyList<string> problems)
    : Exception("Invalid eval cases:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads <c>evals/cases/*.yaml</c>. The YAML is walked node by node instead of deserialized into classes so that a
/// typo (<c>expectToolCall</c>), an unknown tool or a wrong value fails with the file and line, before any money is
/// spent on a model. All problems of all files are reported at once.
/// </summary>
internal static partial class CaseLoader
{
    private static readonly string[] ToolNames =
    [
        AgentToolFactory.SearchCatalog, AgentToolFactory.GetOfferingDetails, AgentToolFactory.CheckAvailability,
        AgentToolFactory.CreateBooking, AgentToolFactory.HandoffToHuman, AgentToolFactory.SearchKnowledgeBase,
    ];

    private static readonly string[] ExpectationKeys =
    [
        "expectToolCalls", "expectAnyToolCall", "expectToolOrder", "forbidToolCalls", "expectLanguage", "forbidPhrases", "mustMention",
        "expectBooking", "forbidBooking", "expectBookingTotal", "expectHandoff", "expectRetrievalSources",
    ];

    /// <summary>The retrieval questions live in the cases folder but have their own format (see <see cref="Retrieval.RetrievalCaseLoader"/>).</summary>
    public const string RetrievalFileName = "rag-retrieval.yaml";

    public static IReadOnlyList<EvalCase> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new CaseFormatException([$"{directory}: cases directory not found."]);

        var files = Directory.GetFiles(directory, "*.yaml")
            .Where(f => !Path.GetFileName(f).Equals(RetrievalFileName, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            throw new CaseFormatException([$"{directory}: no *.yaml files."]);

        return ParseAll(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))));
    }

    /// <summary>Parses several files and checks what spans them (unique ids).</summary>
    public static IReadOnlyList<EvalCase> ParseAll(IEnumerable<(string Source, string Yaml)> files)
    {
        var problems = new List<string>();
        var cases = new List<EvalCase>();
        foreach (var (source, yaml) in files)
            cases.AddRange(Parse(source, yaml, problems));

        foreach (var duplicate in cases.GroupBy(c => c.Id).Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Last().Source}: duplicate case id '{duplicate.Key}' (also in {duplicate.First().Source}).");

        if (problems.Count > 0)
            throw new CaseFormatException(problems);
        return cases;
    }

    /// <summary>Parses one file, throwing <see cref="CaseFormatException"/> if it has any problem.</summary>
    public static IReadOnlyList<EvalCase> Parse(string source, string yaml)
    {
        var problems = new List<string>();
        var cases = Parse(source, yaml, problems);
        if (problems.Count > 0)
            throw new CaseFormatException(problems);
        return cases;
    }

    private static List<EvalCase> Parse(string source, string yaml, List<string> problems)
    {
        var cases = new List<EvalCase>();
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            problems.Add($"{source}:{ex.Start.Line}: invalid YAML: {ex.Message}");
            return cases;
        }
        catch (InvalidOperationException)
        {
            // The scanner of YamlDotNet fails like this on some unbalanced brackets and quotes, without a position.
            problems.Add($"{source}: invalid YAML syntax (check brackets, quotes and indentation).");
            return cases;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlSequenceNode root)
        {
            problems.Add($"{source}:1: the file must be a list of cases (each one starting with '- id: ...').");
            return cases;
        }

        var context = new Context(source, problems);
        foreach (var node in root.Children)
        {
            if (ReadCase(node, context) is { } evalCase)
                cases.Add(evalCase);
        }

        if (root.Children.Count == 0)
            problems.Add($"{source}:1: the file has no cases.");
        return cases;
    }

    private static EvalCase? ReadCase(YamlNode node, Context context)
    {
        if (node is not YamlMappingNode map)
        {
            context.Problem(node, "a case must be a mapping (id, category, niche, turns, ...).");
            return null;
        }

        var before = context.Problems.Count;
        var fields = Fields(map, context, ["id", "category", "niche", "turns", "rubric", .. ExpectationKeys]);

        var id = RequiredString(fields, "id", map, context);
        if (id is not null && !IdPattern().IsMatch(id))
            context.Problem(fields["id"], $"id '{id}' must be lowercase letters, digits and dashes.");

        var category = RequiredEnum(fields, "category", map, context, Category, Enum.GetValues<EvalCategory>().Select(Name));
        var niche = RequiredEnum(fields, "niche", map, context, Niche, Enum.GetValues<EvalNiche>().Select(Name));
        var expect = ReadExpectations(fields, map, context);
        var turns = ReadTurns(fields, map, context);
        var rubric = fields.TryGetValue("rubric", out var rubricNode) ? Text(rubricNode, context, "rubric") : null;

        if (context.Problems.Count > before || id is null || category is null || niche is null || turns is null)
            return null;

        if (expect.IsEmpty && turns.All(t => t.Expect.IsEmpty))
        {
            context.Problem(map, $"case '{id}' has no expectations, so it could never fail.");
            return null;
        }

        return new EvalCase(id, category.Value, niche.Value, turns, expect, rubric, $"{context.Source}:{map.Start.Line}");
    }

    private static List<EvalTurn>? ReadTurns(Dictionary<string, YamlNode> fields, YamlMappingNode owner, Context context)
    {
        if (!fields.TryGetValue("turns", out var node))
        {
            context.Problem(owner, "missing 'turns'.");
            return null;
        }

        if (node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
        {
            context.Problem(node, "'turns' must be a non-empty list of user messages.");
            return null;
        }

        var turns = new List<EvalTurn>();
        foreach (var item in sequence.Children)
        {
            switch (item)
            {
                case YamlScalarNode scalar:
                    if (Text(scalar, context, "turn") is { } say)
                        turns.Add(new EvalTurn(say, Expectations.None));
                    break;
                case YamlMappingNode turnMap:
                    var turnFields = Fields(turnMap, context, ["say", .. ExpectationKeys]);
                    var text = RequiredString(turnFields, "say", turnMap, context);
                    var expect = ReadExpectations(turnFields, turnMap, context);
                    if (text is not null)
                        turns.Add(new EvalTurn(text, expect));
                    break;
                default:
                    context.Problem(item, "a turn is either the user's text or a mapping with 'say' and expectations.");
                    break;
            }
        }

        return turns.Count == sequence.Children.Count ? turns : null;
    }

    private static Expectations ReadExpectations(Dictionary<string, YamlNode> fields, YamlMappingNode owner, Context context)
    {
        var toolCalls = fields.TryGetValue("expectToolCalls", out var callsNode) ? ReadToolCalls(callsNode, context) : [];
        var anyTool = fields.TryGetValue("expectAnyToolCall", out var anyNode) ? ReadToolNames(anyNode, context, "expectAnyToolCall") : [];
        if (anyTool.Count == 1)
            context.Problem(fields["expectAnyToolCall"], "expectAnyToolCall needs at least two tools to choose from (use expectToolCalls for one).");
        var forbidTools = fields.TryGetValue("forbidToolCalls", out var forbidNode) ? ReadToolNames(forbidNode, context, "forbidToolCalls") : [];
        var phrases = fields.TryGetValue("forbidPhrases", out var phrasesNode) ? ReadStrings(phrasesNode, context, "forbidPhrases") : [];
        var mentions = fields.TryGetValue("mustMention", out var mentionNode) ? ReadMentions(mentionNode, context) : [];
        var sources = fields.TryGetValue("expectRetrievalSources", out var sourcesNode) ? ReadStrings(sourcesNode, context, "expectRetrievalSources") : [];

        ReplyLanguage? language = null;
        if (fields.TryGetValue("expectLanguage", out var languageNode))
        {
            language = Text(languageNode, context, "expectLanguage")?.ToLowerInvariant() switch
            {
                "en" => ReplyLanguage.English,
                "es" => ReplyLanguage.Spanish,
                null => null,
                var other => Invalid<ReplyLanguage?>(languageNode, context, $"expectLanguage '{other}' must be 'en' or 'es'."),
            };
        }

        var inOrder = fields.TryGetValue("expectToolOrder", out var orderNode) && ReadBool(orderNode, context, "expectToolOrder") == true;
        if (inOrder && toolCalls.Count < 2)
            context.Problem(fields["expectToolOrder"], "expectToolOrder needs at least two expectToolCalls to order.");

        bool? expectBooking = fields.TryGetValue("expectBooking", out var expectBookingNode) ? ReadBool(expectBookingNode, context, "expectBooking") : null;
        bool? forbidBooking = fields.TryGetValue("forbidBooking", out var forbidBookingNode) ? ReadBool(forbidBookingNode, context, "forbidBooking") : null;
        if (expectBooking == true && forbidBooking == true)
            context.Problem(owner, "expectBooking and forbidBooking cannot both be true.");
        bool? booking = expectBooking == true ? true : forbidBooking == true ? false : null;

        decimal? total = null;
        if (fields.TryGetValue("expectBookingTotal", out var totalNode))
        {
            if (Text(totalNode, context, "expectBookingTotal") is { } totalText)
            {
                if (decimal.TryParse(totalText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                    total = parsed;
                else
                    context.Problem(totalNode, $"expectBookingTotal '{totalText}' is not a number.");
            }

            if (booking != true)
                context.Problem(totalNode, "expectBookingTotal needs expectBooking: true.");
        }

        bool? handoff = fields.TryGetValue("expectHandoff", out var handoffNode) ? ReadBool(handoffNode, context, "expectHandoff") : null;

        return new Expectations
        {
            ToolCalls = toolCalls,
            ToolsInOrder = inOrder,
            AnyToolCall = anyTool,
            ForbidToolCalls = forbidTools,
            Language = language,
            ForbidPhrases = phrases,
            MustMention = mentions,
            RetrievalSources = sources,
            Booking = booking,
            BookingTotal = total,
            Handoff = handoff,
        };
    }

    private static List<ToolCallExpectation> ReadToolCalls(YamlNode node, Context context)
    {
        var calls = new List<ToolCallExpectation>();
        if (node is not YamlSequenceNode sequence)
        {
            context.Problem(node, "expectToolCalls must be a list of '- name: <tool>'.");
            return calls;
        }

        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode map)
            {
                context.Problem(item, "an expectToolCalls entry must be a mapping with 'name' (and optionally 'args', 'count').");
                continue;
            }

            var fields = Fields(map, context, ["name", "args", "count"]);
            var name = RequiredString(fields, "name", map, context);
            if (name is not null)
                CheckToolName(name, fields["name"], context);

            JsonElement? args = null;
            if (fields.TryGetValue("args", out var argsNode))
            {
                if (argsNode is YamlMappingNode)
                    args = ToJson(argsNode);
                else
                    context.Problem(argsNode, "args must be a mapping with the arguments that must have been sent.");
            }

            int? count = null;
            if (fields.TryGetValue("count", out var countNode))
            {
                if (int.TryParse(Text(countNode, context, "count"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1)
                    count = parsed;
                else
                    context.Problem(countNode, "count must be a whole number of at least 1 (use forbidToolCalls for zero).");
            }

            if (name is not null)
                calls.Add(new ToolCallExpectation(name, args, count));
        }

        return calls;
    }

    private static List<string> ReadToolNames(YamlNode node, Context context, string key)
    {
        var names = ReadStrings(node, context, key);
        foreach (var name in names)
            CheckToolName(name, node, context);
        return names;
    }

    private static void CheckToolName(string name, YamlNode at, Context context)
    {
        if (!ToolNames.Contains(name))
            context.Problem(at, $"unknown tool '{name}'. Known tools: {string.Join(", ", ToolNames)}.");
    }

    private static List<MustMention> ReadMentions(YamlNode node, Context context)
    {
        var mentions = new List<MustMention>();
        if (node is not YamlSequenceNode sequence)
        {
            context.Problem(node, "mustMention must be a list.");
            return mentions;
        }

        foreach (var item in sequence.Children)
        {
            switch (item)
            {
                case YamlScalarNode scalar:
                    if (Text(scalar, context, "mustMention") is { } text)
                        mentions.Add(new MentionText([text]));
                    break;
                case YamlMappingNode map:
                    var fields = Fields(map, context, ["anyOf", "fromTool", "path"]);
                    if (fields.TryGetValue("anyOf", out var anyOf))
                    {
                        var options = ReadStrings(anyOf, context, "anyOf");
                        if (options.Count == 0)
                            context.Problem(anyOf, "anyOf needs at least one text.");
                        else if (fields.ContainsKey("fromTool") || fields.ContainsKey("path"))
                            context.Problem(map, "anyOf cannot be combined with fromTool/path.");
                        else
                            mentions.Add(new MentionText(options));
                    }
                    else if (RequiredString(fields, "fromTool", map, context) is { } tool)
                    {
                        CheckToolName(tool, fields["fromTool"], context);
                        if (RequiredString(fields, "path", map, context) is { } path)
                        {
                            if (JsonPath.IsValid(path))
                                mentions.Add(new MentionFromTool(tool, path));
                            else
                                context.Problem(fields["path"], $"path '{path}' is not valid. Use names separated by dots, with optional [n] or [*], like results[*].price.");
                        }
                    }

                    break;
                default:
                    context.Problem(item, "a mustMention entry is a text, '{ anyOf: [...] }' or '{ fromTool: <tool>, path: <path> }'.");
                    break;
            }
        }

        return mentions;
    }

    private static List<string> ReadStrings(YamlNode node, Context context, string key)
    {
        var values = new List<string>();
        if (node is YamlScalarNode single)
        {
            if (Text(single, context, key) is { } text)
                values.Add(text);
            return values;
        }

        if (node is not YamlSequenceNode sequence)
        {
            context.Problem(node, $"{key} must be a list of texts.");
            return values;
        }

        foreach (var item in sequence.Children)
        {
            if (item is YamlScalarNode scalar && Text(scalar, context, key) is { } text)
                values.Add(text);
            else if (item is not YamlScalarNode)
                context.Problem(item, $"every {key} entry must be a text.");
        }

        return values;
    }

    /// <summary>The mapping as a dictionary; reports keys that are not in <paramref name="allowed"/>.</summary>
    private static Dictionary<string, YamlNode> Fields(YamlMappingNode map, Context context, string[] allowed)
    {
        var fields = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var (keyNode, value) in map.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            if (!allowed.Contains(key))
                context.Problem(keyNode, $"unknown key '{key}'. Allowed here: {string.Join(", ", allowed)}.");
            else
                fields[key] = value;
        }

        return fields;
    }

    private static string? RequiredString(Dictionary<string, YamlNode> fields, string key, YamlMappingNode owner, Context context)
    {
        if (fields.TryGetValue(key, out var node))
            return Text(node, context, key);

        context.Problem(owner, $"missing '{key}'.");
        return null;
    }

    private static T? RequiredEnum<T>(
        Dictionary<string, YamlNode> fields, string key, YamlMappingNode owner, Context context, Func<string, T?> parse,
        IEnumerable<string> validNames)
        where T : struct
    {
        var text = RequiredString(fields, key, owner, context);
        if (text is null)
            return null;

        if (parse(text) is { } value)
            return value;

        context.Problem(fields[key], $"{key} '{text}' is not valid. Valid values: {string.Join(", ", validNames)}.");
        return null;
    }

    private static string? Text(YamlNode node, Context context, string what)
    {
        if (node is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value))
        {
            context.Problem(node, $"{what} must be a non-empty text.");
            return null;
        }

        return value.Trim();
    }

    private static bool? ReadBool(YamlNode node, Context context, string key)
    {
        if (node is YamlScalarNode { Value: { } value } && bool.TryParse(value, out var parsed))
            return parsed;

        context.Problem(node, $"{key} must be true or false.");
        return null;
    }

    private static T? Invalid<T>(YamlNode node, Context context, string message)
    {
        context.Problem(node, message);
        return default;
    }

    /// <summary>The YAML as JSON. Quoted scalars stay text; plain <c>true</c>, <c>12</c> and <c>1.5</c> become boolean and number.</summary>
    private static JsonElement ToJson(YamlNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, node);
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                writer.WriteStartObject();
                foreach (var (key, value) in map.Children)
                {
                    writer.WritePropertyName(((YamlScalarNode)key).Value ?? "");
                    Write(writer, value);
                }

                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case YamlScalarNode { Style: ScalarStyle.Plain, Value: { } plain } when bool.TryParse(plain, out var flag):
                writer.WriteBooleanValue(flag);
                break;
            case YamlScalarNode { Style: ScalarStyle.Plain, Value: { } plain } when decimal.TryParse(plain, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                writer.WriteNumberValue(number);
                break;
            case YamlScalarNode scalar:
                writer.WriteStringValue(scalar.Value);
                break;
        }
    }

    private static EvalCategory? Category(string text) => Normalize(text) switch
    {
        "language" => EvalCategory.Language,
        "search" => EvalCategory.Search,
        "availability" => EvalCategory.Availability,
        "booking" => EvalCategory.Booking,
        "order" => EvalCategory.Order,
        "notincatalog" => EvalCategory.NotInCatalog,
        "handoff" => EvalCategory.Handoff,
        "smalltalk" => EvalCategory.SmallTalk,
        "injection" => EvalCategory.Injection,
        "gibberish" => EvalCategory.Gibberish,
        "raganswer" => EvalCategory.RagAnswer,
        "ragunanswerable" => EvalCategory.RagUnanswerable,
        "ragmixed" => EvalCategory.RagMixed,
        "raginjection" => EvalCategory.RagInjection,
        _ => null,
    };

    private static EvalNiche? Niche(string text) => Normalize(text) switch
    {
        "realestate" => EvalNiche.RealEstate,
        "dental" => EvalNiche.Dental,
        "restaurant" => EvalNiche.Restaurant,
        _ => null,
    };

    /// <summary>Parses a category given on the command line with the same rules as the YAML.</summary>
    public static EvalCategory? ParseCategory(string text) => Category(text);

    public static EvalNiche? ParseNiche(string text) => Niche(text);

    /// <summary>The names as they are written in the YAML: <c>not_in_catalog</c>, <c>smallTalk</c>, <c>realestate</c>...</summary>
    public static string Name(EvalCategory category) => category switch
    {
        EvalCategory.NotInCatalog => "not_in_catalog",
        EvalCategory.SmallTalk => "smallTalk",
        EvalCategory.RagAnswer => "rag_answer",
        EvalCategory.RagUnanswerable => "rag_unanswerable",
        EvalCategory.RagMixed => "rag_mixed",
        EvalCategory.RagInjection => "rag_injection",
        _ => category.ToString().ToLowerInvariant(),
    };

    public static string Name(EvalNiche niche) => niche.ToString().ToLowerInvariant();

    private static string Normalize(string text) => text.Replace("_", "").Replace("-", "").ToLowerInvariant();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    private sealed class Context(string source, List<string> problems)
    {
        public string Source { get; } = source;
        public List<string> Problems { get; } = problems;

        public void Problem(YamlNode node, string message) => Problems.Add($"{Source}:{node.Start.Line}: {message}");
    }
}
