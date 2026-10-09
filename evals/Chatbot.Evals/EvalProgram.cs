using System.Diagnostics;
using Chatbot.Evals.Cases;
using Chatbot.Evals.Reporting;
using Chatbot.Evals.Running;
using Microsoft.Extensions.Configuration;

namespace Chatbot.Evals;

/// <summary>
/// Entry point. Exit code 0: the MVP targets were met. 1: they were not. 2: the run could not start (bad arguments,
/// invalid case files, no model configured, no database).
/// </summary>
internal static class EvalProgram
{
    private const int TargetsMissed = 1;
    private const int CouldNotRun = 2;

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
        var cases = options.Select(CaseLoader.LoadDirectory(casesDirectory));
        if (cases.Count == 0)
            throw new UsageException("No case matches the filters.");

        var configuredConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (!options.Fake && MissingModelSettings(EvalConfiguration.Build(configuredConnection ?? "", options.Model)) is { } missing)
        {
            Console.Error.WriteLine($"No model is configured: {missing} is empty. Set Llm__Primary__ApiKey and Llm__Primary__Model (or LLM_PRIMARY_API_KEY and LLM_PRIMARY_MODEL), or run with --fake.");
            return CouldNotRun;
        }

        Console.WriteLine($"{cases.Count} case(s) x {options.Repeat} from {casesDirectory}. {(options.Fake ? "Model: scripted fake." : "Model: real.")}");
        Console.WriteLine(configuredConnection is null ? "Starting a PostgreSQL container..." : "Using ConnectionStrings__Postgres.");

        await using var database = await EvalDatabase.StartAsync(configuredConnection, ct);
        var configuration = EvalConfiguration.Build(database.ConnectionString, options.Model);
        await using var services = EvalHost.Build(configuration, options.Fake ? new FakeChatClient() : null);
        await EvalHost.PrepareDatabaseAsync(services, ct);

        var clock = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var results = await EvalRunner.RunAsync(services, cases, options.Repeat, (result, done, total) =>
            Console.WriteLine($"[{done}/{total}] {result.Transcript.Case.Id} {(result.Passed ? "PASS" : "FAIL")}"), ct);

        var summary = EvalSummary.Create(results);
        var info = new RunInfo(
            startedAt, clock.Elapsed, options.Fake, options.Repeat, options.Filter,
            options.Category is { } category ? CaseLoader.Name(category) : null,
            options.Niche is { } niche ? CaseLoader.Name(niche) : null);

        Console.WriteLine();
        ConsoleReport.Write(Console.Out, summary);

        var report = WriteReport(options.ResultsDirectory ?? Path.Combine(Path.GetDirectoryName(casesDirectory)!, "results"), summary, info);
        Console.WriteLine();
        Console.WriteLine($"Report: {report}");

        if (summary.TargetsMet)
            return 0;

        Console.WriteLine($"MVP targets (100% language, 100% forbidden phrases, >= 90% tool use) not met: {string.Join("; ", summary.Targets.Where(t => !t.Met).Select(t => t.Name))}.");
        return TargetsMissed;
    }

    private static string WriteReport(string directory, EvalSummary summary, RunInfo info)
    {
        Directory.CreateDirectory(directory);
        var name = MarkdownReport.FileName(info.StartedAt);
        var path = Path.Combine(directory, name);
        for (var attempt = 2; File.Exists(path); attempt++)
            path = Path.Combine(directory, Path.ChangeExtension(name, null) + $"-{attempt}.md");

        File.WriteAllText(path, MarkdownReport.Render(summary, info));
        return path;
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
