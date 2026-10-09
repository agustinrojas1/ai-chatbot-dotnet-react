using Chatbot.Evals.Cases;

namespace Chatbot.Evals;

/// <summary>The command line is wrong. The message is shown to the user together with the usage.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>The options of <c>dotnet run --project evals/Chatbot.Evals -- [options]</c>.</summary>
internal sealed record EvalOptions
{
    public const string Usage = """
        Usage: dotnet run --project evals/Chatbot.Evals -- [options]

          --filter <text>     only cases whose id contains the text
          --category <name>   only one category (language, search, availability, booking, order,
                              not_in_catalog, handoff, smallTalk, injection, gibberish)
          --niche <name>      only one business (realestate, dental, restaurant)
          --model <name>      model of the primary provider (overrides Llm__Primary__Model)
          --repeat <k>        run every case k times (models are not deterministic)
          --fake              use a scripted fake model instead of a real one: proves the harness
                              runs offline, most cases fail on purpose
          --cases <dir>       directory with the *.yaml cases (default: evals/cases)
          --results <dir>     directory for the report (default: evals/results)

        Database: ConnectionStrings__Postgres if set, otherwise a throwaway PostgreSQL container (needs Docker).
        Model: Llm__Primary__ApiKey, Llm__Primary__BaseUrl and Llm__Primary__Model (or LLM_PRIMARY_*).
        """;

    public string? Filter { get; init; }
    public EvalCategory? Category { get; init; }
    public EvalNiche? Niche { get; init; }
    public string? Model { get; init; }
    public int Repeat { get; init; } = 1;
    public bool Fake { get; init; }
    public string? CasesDirectory { get; init; }
    public string? ResultsDirectory { get; init; }
    public bool Help { get; init; }

    public static EvalOptions Parse(IReadOnlyList<string> args)
    {
        var options = new EvalOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new UsageException($"{name} needs a value.");

            options = name switch
            {
                "--filter" => options with { Filter = Value() },
                "--category" => options with { Category = CaseLoader.ParseCategory(Value()) ?? throw new UsageException($"Unknown category '{args[i]}'.") },
                "--niche" => options with { Niche = CaseLoader.ParseNiche(Value()) ?? throw new UsageException($"Unknown niche '{args[i]}'.") },
                "--model" => options with { Model = Value() },
                "--repeat" => options with { Repeat = int.TryParse(Value(), out var k) && k >= 1 ? k : throw new UsageException("--repeat must be a whole number of at least 1.") },
                "--cases" => options with { CasesDirectory = Value() },
                "--results" => options with { ResultsDirectory = Value() },
                "--fake" => options with { Fake = true },
                "--help" or "-h" => options with { Help = true },
                _ => throw new UsageException($"Unknown option '{name}'."),
            };
        }

        return options;
    }

    /// <summary>The cases that match every filter given.</summary>
    public IReadOnlyList<EvalCase> Select(IEnumerable<EvalCase> cases) =>
        cases
            .Where(c => Filter is null || c.Id.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Where(c => Category is null || c.Category == Category)
            .Where(c => Niche is null || c.Niche == Niche)
            .ToList();
}
