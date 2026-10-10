using System.Diagnostics;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Reporting;
using Chatbot.Evals.Retrieval;
using Chatbot.Evals.Running;
using Chatbot.Infrastructure.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Chatbot.Evals;

/// <summary>
/// Entry point. Exit code 0: the MVP targets were met. 1: they were not. 2: the run could not start (bad arguments,
/// invalid case files, no model configured, no database). 3: the targets were met by the runs that were judged, but
/// the provider refused some runs (quota), so the result is incomplete.
/// </summary>
internal static class EvalProgram
{
    private const int TargetsMissed = 1;
    private const int CouldNotRun = 2;
    private const int Incomplete = 3;

    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(args, cancellation.Token);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(EvalOptions.Usage);
            return CouldNotRun;
        }
        catch (CaseFormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return CouldNotRun;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return CouldNotRun;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var options = EvalOptions.Parse(args);
        if (options.Help)
        {
            Console.WriteLine(EvalOptions.Usage);
            return 0;
        }

        var casesDirectory = options.CasesDirectory ?? FindCasesDirectory();
        if (options.Retrieval)
            return await RunRetrievalAsync(options, casesDirectory, ct);

        var cases = options.Select(CaseLoader.LoadDirectory(casesDirectory));
        if (cases.Count == 0)
            throw new UsageException("No case matches the filters.");

        var configuredConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        var probe = EvalConfiguration.Build(configuredConnection ?? "", options.Model);
        if (!options.Fake && MissingModelSettings(probe) is { } missing)
        {
            Console.Error.WriteLine($"No model is configured: {missing} is empty. Set Llm__Primary__ApiKey and Llm__Primary__Model (or LLM_PRIMARY_API_KEY and LLM_PRIMARY_MODEL), or run with --fake.");
            return CouldNotRun;
        }

        // The knowledge base is only indexed (and so only needs the embeddings) when a selected case searches it.
        var needsKnowledge = cases.Any(c => c.Category is EvalCategory.RagAnswer or EvalCategory.RagUnanswerable or EvalCategory.RagMixed or EvalCategory.RagInjection);
        if (needsKnowledge && !options.Fake && MissingEmbeddingSettings(probe) is { } missingEmbeddings)
        {
            Console.Error.WriteLine($"The selected cases search the knowledge base but {missingEmbeddings} is empty. Set Embeddings__ApiKey (or EMBEDDINGS_API_KEY; it defaults to Llm__Primary__ApiKey), or run with --fake.");
            return CouldNotRun;
        }

        Console.WriteLine($"{cases.Count} case(s) x {options.Repeat} from {casesDirectory}. {(options.Fake ? "Model: scripted fake." : "Model: real.")}");
        Console.WriteLine(configuredConnection is null ? "Starting a PostgreSQL container..." : "Using ConnectionStrings__Postgres.");

        await using var database = await EvalDatabase.StartAsync(configuredConnection, ct);
        var configuration = EvalConfiguration.Build(database.ConnectionString, options.Model, fakeEmbeddings: options.Fake);
        var policy = options.Fake ? null : new ModelCallPolicy(TimeSpan.FromMilliseconds(options.MinIntervalMs), Notice: Console.WriteLine);
        await using var services = EvalHost.Build(
            configuration, options.Fake ? new FakeChatClient() : null, policy, options.Fake ? new FakeEmbeddingGenerator() : null);
        await EvalHost.PrepareDatabaseAsync(services, ct);
        if (needsKnowledge)
            PrintIngestion(await EvalHost.IngestKnowledgeAsync(services, ct));

        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var results = await EvalRunner.RunAsync(services, cases, options.Repeat, (result, done, total) =>
            Console.WriteLine($"[{done}/{total}] {result.Transcript.Case.Id} {(result.NotRun ? "NOT RUN" : result.Passed ? "PASS" : "FAIL")}"), ct);

        var summary = EvalSummary.Create(results);
        var info = NewRunInfo(options, startedAt, clock.Elapsed);

        Console.WriteLine();
        ConsoleReport.Write(Console.Out, summary);

        var report = WriteReport(ResultsDirectory(options, casesDirectory), MarkdownReport.FileName(info.StartedAt), MarkdownReport.Render(summary, info));
        Console.WriteLine();
        Console.WriteLine($"Report: {report}");

        if (summary.TargetsMet && summary.NotRun.Count == 0)
            return 0;

        if (summary.TargetsMet)
        {
            Console.WriteLine($"Incomplete: {summary.NotRun.Count} run(s) were refused by the provider (quota) and not judged. The targets are met by the other {summary.Runs}.");
            return Incomplete;
        }

        Console.WriteLine($"Targets not met: {string.Join("; ", summary.Targets.Where(t => !t.Met).Select(t => t.Name))}. (MVP: 100% language, 100% forbidden phrases, >= 90% tool use. M8: 100% unanswerable and injection cases.)");
        return TargetsMissed;
    }

    /// <summary>
    /// <c>--retrieval</c>: indexes the knowledge base with the real embeddings (or the fake ones) and measures the search alone.
    /// No chat model is involved, so no <c>Llm</c> key is needed.
    /// </summary>
    private static async Task<int> RunRetrievalAsync(EvalOptions options, string casesDirectory, CancellationToken ct)
    {
        var questions = RetrievalCaseLoader.LoadDirectory(casesDirectory)
            .Where(q => options.Filter is null || q.Id.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
            .Where(q => options.Niche is null || q.Niche == options.Niche)
            .ToList();
        if (questions.Count == 0)
            throw new UsageException("No retrieval question matches the filters.");

        var configuredConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (!options.Fake && MissingEmbeddingSettings(EvalConfiguration.Build(configuredConnection ?? "")) is { } missing)
        {
            Console.Error.WriteLine($"No embeddings are configured: {missing} is empty. Set Embeddings__ApiKey (or EMBEDDINGS_API_KEY; it defaults to Llm__Primary__ApiKey), or run with --fake.");
            return CouldNotRun;
        }

        Console.WriteLine($"{questions.Count} retrieval question(s) from {casesDirectory}. {(options.Fake ? "Embeddings: deterministic fake." : "Embeddings: real.")}");
        Console.WriteLine(configuredConnection is null ? "Starting a PostgreSQL container..." : "Using ConnectionStrings__Postgres.");

        await using var database = await EvalDatabase.StartAsync(configuredConnection, ct);
        var configuration = EvalConfiguration.Build(database.ConnectionString, fakeEmbeddings: options.Fake);
        await using var services = EvalHost.Build(configuration, new FakeChatClient(), null, options.Fake ? new FakeEmbeddingGenerator() : null);
        await EvalHost.PrepareDatabaseAsync(services, ct);
        PrintIngestion(await EvalHost.IngestKnowledgeAsync(services, ct));

        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var results = await RetrievalRunner.RunAsync(services, questions, (result, done, total) =>
            Console.WriteLine($"[{done}/{total}] {result.Case.Id} {(result.NotRun ? "NOT RUN" : result.Case.Unanswerable ? (result.ThresholdPassed ? "passed threshold" : "below threshold") : result.Rank is { } rank ? $"rank {rank}" : "MISS")}"), ct);

        var summary = new RetrievalSummary(results);
        var info = NewRunInfo(options, startedAt, clock.Elapsed);
        var embeddingModel = services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Model;

        Console.WriteLine();
        ConsoleReport.WriteRetrieval(Console.Out, summary);

        var name = Path.ChangeExtension(MarkdownReport.FileName(info.StartedAt), null) + "-retrieval.md";
        var report = WriteReport(ResultsDirectory(options, casesDirectory), name, MarkdownReport.RenderRetrieval(summary, info, embeddingModel));
        Console.WriteLine();
        Console.WriteLine($"Report: {report}");

        if (summary.TargetsMet && summary.NotRun.Count == 0)
            return 0;

        if (summary.TargetsMet)
        {
            Console.WriteLine($"Incomplete: {summary.NotRun.Count} question(s) could not be searched (embeddings provider) and are not in the metrics.");
            return Incomplete;
        }

        Console.WriteLine($"Retrieval targets not met: {string.Join("; ", summary.Targets.Where(t => !t.Met).Select(t => t.Name))} (recall@4 >= {RetrievalSummary.RecallTarget}, MRR >= {RetrievalSummary.MrrTarget}).");
        return TargetsMissed;
    }

    private static void PrintIngestion(IngestionReport report) =>
        Console.WriteLine($"Knowledge base: {report.Documents} documents ({report.Processed} indexed, {report.Skipped} unchanged), {report.Chunks} chunks, {report.EmbeddingTokens:N0} embedding tokens{(report.TokensEstimated ? " (estimated)" : "")}, ${report.CostUsd.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}.");

    private static RunInfo NewRunInfo(EvalOptions options, DateTimeOffset startedAt, TimeSpan duration) => new(
        startedAt, duration, options.Fake, options.Repeat, options.Filter,
        options.Category is { } category ? CaseLoader.Name(category) : null,
        options.Niche is { } niche ? CaseLoader.Name(niche) : null);

    private static string ResultsDirectory(EvalOptions options, string casesDirectory) =>
        options.ResultsDirectory ?? Path.Combine(Path.GetDirectoryName(casesDirectory)!, "results");

    private static string WriteReport(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        for (var attempt = 2; File.Exists(path); attempt++)
            path = Path.Combine(directory, Path.ChangeExtension(name, null) + $"-{attempt}.md");

        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The first embeddings setting that is missing (they default to the primary LLM provider), or null.</summary>
    private static string? MissingEmbeddingSettings(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Embeddings:ApiKey"]) && string.IsNullOrWhiteSpace(configuration["Llm:Primary:ApiKey"]))
            return "Embeddings:ApiKey";
        if (string.IsNullOrWhiteSpace(configuration["Embeddings:BaseUrl"]) && string.IsNullOrWhiteSpace(configuration["Llm:Primary:BaseUrl"]))
            return "Embeddings:BaseUrl";
        return null;
    }

    /// <summary>The first of the primary provider settings that is missing, or null.</summary>
    private static string? MissingModelSettings(IConfiguration configuration) =>
        new[] { "Llm:Primary:ApiKey", "Llm:Primary:Model", "Llm:Primary:BaseUrl" }.FirstOrDefault(key => string.IsNullOrWhiteSpace(configuration[key]));

    /// <summary>Walks up from the working directory (and from the binaries) to the first <c>evals/cases</c>.</summary>
    private static string FindCasesDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "evals", "cases");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        throw new UsageException("Could not find evals/cases. Run from the repository or pass --cases <dir>.");
    }
}
