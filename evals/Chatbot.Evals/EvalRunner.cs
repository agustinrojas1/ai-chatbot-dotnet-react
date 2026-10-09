using Chatbot.Evals.Cases;
using Chatbot.Evals.Checking;
using Chatbot.Evals.Running;

namespace Chatbot.Evals;

/// <summary>Runs the selected cases one after the other and judges each run.</summary>
internal static class EvalRunner
{
    /// <param name="onResult">Called after every run with the result, the number of runs done and the total (progress output).</param>
    public static async Task<IReadOnlyList<CaseResult>> RunAsync(
        IServiceProvider services, IReadOnlyList<EvalCase> cases, int repeat, Action<CaseResult, int, int>? onResult, CancellationToken ct)
    {
        var runner = new ConversationRunner(services);
        var results = new List<CaseResult>();
        var total = cases.Count * repeat;

        foreach (var evalCase in cases)
        {
            for (var iteration = 1; iteration <= repeat; iteration++)
            {
                var transcript = await runner.RunAsync(evalCase, iteration, ct);
                var result = Evaluator.Evaluate(transcript);
                results.Add(result);
                onResult?.Invoke(result, results.Count, total);
            }
        }

        return results;
    }
}
