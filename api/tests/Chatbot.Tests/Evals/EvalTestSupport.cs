using System.Text.Json;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Running;

namespace Chatbot.Tests.Evals;

/// <summary>Builds cases (from YAML, the way the loader does) and recorded conversations without a model or a database.</summary>
internal static class EvalTestSupport
{
    /// <summary>
    /// One case with the given turns (each one is YAML: plain text, a quoted text, or a mapping on one line) and
    /// the lines of <paramref name="extra"/> added at case level.
    /// </summary>
    public static EvalCase Case(string extra, params string[] turns)
    {
        var yaml = "- id: test-case\n  category: booking\n  niche: dental\n  turns:\n"
            + string.Concat((turns.Length == 0 ? ["\"hello\""] : turns).Select(t => $"    - {t}\n"))
            + string.Concat(extra.Split('\n').Select(line => $"  {line}\n"));
        return CaseLoader.Parse("test.yaml", yaml).Single();
    }

    public static TurnResult Turn(string reply, params ToolCallRecord[] calls) =>
        new() { User = "", Reply = reply, ToolCalls = calls, TokensIn = 100, TokensOut = 20, Model = "test-model", Provider = "test" };

    public static ToolCallRecord Call(string name, string? argumentsJson = null, string? resultJson = null) =>
        new(name, Json(argumentsJson), Json(resultJson));

    public static JsonElement? Json(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    public static Transcript Run(EvalCase evalCase, params TurnResult[] turns) =>
        new(evalCase, 1, [.. turns.Select((t, i) => t with { User = evalCase.Turns[i].Say })], []);

    /// <summary>The <c>evals/cases</c> directory of the repository, found from the test binaries.</summary>
    public static string ShippedCasesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "evals", "cases");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("evals/cases not found above " + AppContext.BaseDirectory);
    }
}
