using Chatbot.Core.Domain;
using Chatbot.Evals.Cases;
using Chatbot.Infrastructure.Knowledge;
using Chatbot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chatbot.Evals.Retrieval;

/// <summary>A passage of the top 4, reduced to what the metrics need.</summary>
internal sealed record RankedPassage(string SourceId, double? SemanticScore, bool AboveThreshold)
{
    public string Slug => SourceId[..SourceId.LastIndexOf('#')];
}

/// <summary>
/// What searching one question returned. <paramref name="DocumentLanguage"/> is the language of the first expected document
/// (null for unanswerable questions), which with the question language gives the language pair.
/// </summary>
internal sealed record RetrievalResult(
    RetrievalCase Case, ReplyLanguage? DocumentLanguage, IReadOnlyList<RankedPassage> Passages, bool ThresholdPassed,
    long EmbeddingTokens, string? Error)
{
    /// <summary>The searched ranking by document: slugs in order of their best chunk, each once.</summary>
    public IReadOnlyList<string> DocumentRanking => [.. Passages.Select(p => p.Slug).Distinct()];

    /// <summary>1-based rank of the best expected document among the documents of the top 4; null when none is there.</summary>
    public int? Rank
    {
        get
        {
            var ranking = DocumentRanking;
            for (var i = 0; i < ranking.Count; i++)
            {
                if (Case.ExpectSources.Contains(ranking[i], StringComparer.OrdinalIgnoreCase))
                    return i + 1;
            }

            return null;
        }
    }

    public bool Hit => Rank is not null;

    /// <summary>The search could not run (the embeddings provider failed): the question is left out of every metric.</summary>
    public bool NotRun => Error is not null;

    /// <summary>"es->en": the question language, then the language of the expected document.</summary>
    public string? Pair => DocumentLanguage is { } doc ? $"{Code(Case.Language)}->{Code(doc)}" : null;

    private static string Code(ReplyLanguage language) => language == ReplyLanguage.Spanish ? "es" : "en";
}

/// <summary>Runs <see cref="KnowledgeSearch"/> (no model, only the embeddings) for every question, scoped to the business of its niche.</summary>
internal static class RetrievalRunner
{
    public static async Task<IReadOnlyList<RetrievalResult>> RunAsync(
        IServiceProvider services, IReadOnlyList<RetrievalCase> cases, Action<RetrievalResult, int, int>? onResult, CancellationToken ct)
    {
        Dictionary<(EvalNiche, string), string> documents;
        Dictionary<EvalNiche, Guid> businesses;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatbotDbContext>();
            businesses = (await db.Businesses.AsNoTracking().ToListAsync(ct)).ToDictionary(b => ToEvalNiche(b.Niche), b => b.Id);
            var nicheOf = businesses.ToDictionary(b => b.Value, b => b.Key);
            documents = (await db.KnowledgeDocuments.AsNoTracking().ToListAsync(ct))
                .ToDictionary(d => (nicheOf[d.BusinessId], d.Slug), d => d.Language);
        }

        // A typo in a slug would read as a retrieval miss: stop before searching.
        var unknown = cases.SelectMany(c => c.ExpectSources.Select(s => (Case: c, Slug: s)))
            .Where(x => !documents.ContainsKey((x.Case.Niche, x.Slug)))
            .Select(x => $"{x.Case.Source}: question '{x.Case.Id}' expects document '{x.Slug}', which is not in the {CaseLoader.Name(x.Case.Niche)} knowledge base.")
            .ToList();
        if (unknown.Count > 0)
            throw new CaseFormatException(unknown);

        var results = new List<RetrievalResult>();
        foreach (var question in cases)
        {
            ReplyLanguage? documentLanguage = question.ExpectSources.Count == 0
                ? null
                : documents[(question.Niche, question.ExpectSources[0])] == "es" ? ReplyLanguage.Spanish : ReplyLanguage.English;

            RetrievalResult result;
            try
            {
                await using var scope = services.CreateAsyncScope();
                var search = scope.ServiceProvider.GetRequiredService<KnowledgeSearch>();
                var found = await search.SearchAsync(businesses[question.Niche], question.Question, ct);
                result = new RetrievalResult(
                    question, documentLanguage,
                    [.. found.Passages.Select(p => new RankedPassage(p.SourceId, p.SemanticScore, p.AboveThreshold))],
                    found.ThresholdPassed, found.EmbeddingTokens, null);
            }
            catch (EmbeddingException ex)
            {
                result = new RetrievalResult(question, documentLanguage, [], false, 0, ex.Message);
            }

            results.Add(result);
            onResult?.Invoke(result, results.Count, cases.Count);
        }

        return results;
    }

    private static EvalNiche ToEvalNiche(Niche niche) => niche switch
    {
        Niche.RealEstate => EvalNiche.RealEstate,
        Niche.Clinic => EvalNiche.Dental,
        _ => EvalNiche.Restaurant,
    };
}
