using System.Text.RegularExpressions;
using Chatbot.Evals.Cases;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Chatbot.Evals.Retrieval;

/// <summary>
/// One question for the retrieval eval. <paramref name="ExpectSources"/> are document slugs; a case with none is
/// <paramref name="Unanswerable"/>: no document has the answer, so it only measures the relevance threshold.
/// </summary>
internal sealed record RetrievalCase(
    string Id, EvalNiche Niche, ReplyLanguage Language, string Question, IReadOnlyList<string> ExpectSources, bool Unanswerable, string Source);

/// <summary>
/// Reads <c>evals/cases/rag-retrieval.yaml</c>: a list of <c>{ id, niche, language, question, expectSources | unanswerable }</c>.
/// Like <see cref="CaseLoader"/> it reports every problem with file and line.
/// </summary>
internal static partial class RetrievalCaseLoader
{
    private static readonly string[] Keys = ["id", "niche", "language", "question", "expectSources", "unanswerable"];

    public static IReadOnlyList<RetrievalCase> LoadDirectory(string casesDirectory)
    {
        var path = Path.Combine(casesDirectory, CaseLoader.RetrievalFileName);
        if (!File.Exists(path))
            throw new CaseFormatException([$"{path}: retrieval questions file not found."]);
        return Parse(CaseLoader.RetrievalFileName, File.ReadAllText(path));
    }

    public static IReadOnlyList<RetrievalCase> Parse(string source, string yaml)
    {
        var problems = new List<string>();
        var cases = new List<RetrievalCase>();
        void Problem(YamlNode node, string message) => problems.Add($"{source}:{node.Start.Line}: {message}");

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new CaseFormatException([$"{source}:{ex.Start.Line}: invalid YAML: {ex.Message}"]);
        }
        catch (InvalidOperationException)
        {
            throw new CaseFormatException([$"{source}: invalid YAML syntax (check brackets, quotes and indentation)."]);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlSequenceNode { Children.Count: > 0 } root)
            throw new CaseFormatException([$"{source}:1: the file must be a non-empty list of questions (each one starting with '- id: ...')."]);

        foreach (var node in root.Children)
        {
            if (node is not YamlMappingNode map)
            {
                Problem(node, "a question must be a mapping (id, niche, language, question, expectSources).");
                continue;
            }

            var fields = new Dictionary<string, YamlNode>();
            foreach (var (keyNode, value) in map.Children)
            {
                var key = (keyNode as YamlScalarNode)?.Value ?? "";
                if (Keys.Contains(key))
                    fields[key] = value;
                else
                    Problem(keyNode, $"unknown key '{key}'. Allowed here: {string.Join(", ", Keys)}.");
            }

            string? Text(string key)
            {
                if (!fields.TryGetValue(key, out var field))
                {
                    Problem(map, $"missing '{key}'.");
                    return null;
                }

                if (field is YamlScalarNode { Value: { } value } && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
                Problem(field, $"{key} must be a non-empty text.");
                return null;
            }

            var before = problems.Count;
            var id = Text("id");
            if (id is not null && !IdPattern().IsMatch(id))
                Problem(fields["id"], $"id '{id}' must be lowercase letters, digits and dashes.");
            var question = Text("question");

            EvalNiche? niche = null;
            if (Text("niche") is { } nicheText)
            {
                niche = CaseLoader.ParseNiche(nicheText);
                if (niche is null)
                    Problem(fields["niche"], $"niche '{nicheText}' is not valid. Valid values: realestate, dental, restaurant.");
            }

            ReplyLanguage? language = null;
            if (Text("language") is { } languageText)
            {
                language = languageText.ToLowerInvariant() switch { "en" => ReplyLanguage.English, "es" => ReplyLanguage.Spanish, _ => null };
                if (language is null)
                    Problem(fields["language"], $"language '{languageText}' must be 'en' or 'es'.");
            }

            var unanswerable = false;
            if (fields.TryGetValue("unanswerable", out var unanswerableNode))
            {
                if (unanswerableNode is YamlScalarNode { Value: { } flag } && bool.TryParse(flag, out var parsed))
                    unanswerable = parsed;
                else
                    Problem(unanswerableNode, "unanswerable must be true or false.");
            }

            var sources = new List<string>();
            if (fields.TryGetValue("expectSources", out var sourcesNode))
            {
                if (sourcesNode is YamlSequenceNode sequence && sequence.Children.All(c => c is YamlScalarNode { Value: { Length: > 0 } }))
                    sources.AddRange(sequence.Children.Select(c => ((YamlScalarNode)c).Value!.Trim()));
                else
                    Problem(sourcesNode, "expectSources must be a list of document slugs.");
            }

            if (unanswerable && sources.Count > 0)
                Problem(map, "a question cannot be unanswerable and have expectSources.");
            if (!unanswerable && sources.Count == 0 && !fields.ContainsKey("expectSources"))
                Problem(map, "give expectSources (document slugs) or mark the question 'unanswerable: true'.");

            if (problems.Count == before && id is not null && question is not null && niche is not null && language is not null)
                cases.Add(new RetrievalCase(id, niche.Value, language.Value, question, sources, unanswerable, $"{source}:{map.Start.Line}"));
        }

        foreach (var duplicate in cases.GroupBy(c => c.Id).Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Last().Source}: duplicate question id '{duplicate.Key}'.");

        if (problems.Count > 0)
            throw new CaseFormatException(problems);
        return cases;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
