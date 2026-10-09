using System.Reflection;

namespace Chatbot.Infrastructure.Seeding;

/// <summary>Persona prompts live in <c>Seeding/Personas/*.md</c>, embedded as <c>Personas.&lt;file&gt;</c>.</summary>
internal static class PersonaPrompts
{
    public static string Load(string resourceName)
    {
        var assembly = typeof(PersonaPrompts).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded persona prompt '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
