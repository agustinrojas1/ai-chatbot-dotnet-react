using System.Text;
using Chatbot.Core.Domain;
using Chatbot.Core.Knowledge;

namespace Chatbot.Infrastructure.Knowledge;

/// <summary>A knowledge markdown file: <c>SourcePath</c> is relative to the knowledge folder (<c>dental/cancellation-policy.md</c>).</summary>
public sealed record KnowledgeFile(Niche Niche, string SourcePath, string Content)
{
    /// <summary>File name without extension.</summary>
    public string Slug => Path.GetFileNameWithoutExtension(SourcePath);
}

/// <summary>Where the documents come from. The product reads the ones compiled into the assembly; tests give their own.</summary>
public interface IKnowledgeSource
{
    IReadOnlyList<KnowledgeFile> GetFiles();
}

/// <summary>
/// The files of <c>Seeding/knowledge/&lt;niche&gt;/*.md</c>, embedded in the assembly (csproj), so ingestion finds them
/// the same way under <c>dotnet run</c>, in the Docker image and in tests. The niche is the folder name.
/// </summary>
public sealed class EmbeddedKnowledgeSource : IKnowledgeSource
{
    private const string Prefix = "knowledge/";

    /// <summary>Folder name to niche. The folders are named after the product niches, not after the <see cref="Niche"/> enum.</summary>
    public static readonly IReadOnlyDictionary<string, Niche> Folders = new Dictionary<string, Niche>(StringComparer.OrdinalIgnoreCase)
    {
        ["realestate"] = Niche.RealEstate,
        ["dental"] = Niche.Clinic,
        ["restaurant"] = Niche.Restaurant,
    };

    public IReadOnlyList<KnowledgeFile> GetFiles()
    {
        var assembly = typeof(EmbeddedKnowledgeSource).Assembly;
        var files = new List<KnowledgeFile>();

        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            var sourcePath = name[Prefix.Length..].Replace('\\', '/');
            var folder = sourcePath.Split('/')[0];
            if (!Folders.TryGetValue(folder, out var niche))
            {
                throw new KnowledgeFormatException(sourcePath,
                    $"unknown knowledge folder '{folder}'; expected one of {string.Join(", ", Folders.Keys)}.");
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files.Add(new KnowledgeFile(niche, sourcePath, reader.ReadToEnd()));
        }

        return files;
    }
}
