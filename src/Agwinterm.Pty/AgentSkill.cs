using System.IO;

namespace Agwinterm.Pty;

public static class AgentSkill
{
    private const string ReferencePrefix = "Agwinterm.Skill.references.";

    public static string SkillMarkdown { get; } = ReadResource("Agwinterm.Skill.SKILL.md");
    internal static IReadOnlyDictionary<string, string> References { get; } =
        typeof(AgentSkill).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ReferencePrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[ReferencePrefix.Length..], ReadResource);

    private static string ReadResource(string name)
    {
        using var stream = typeof(AgentSkill).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded skill resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void InstallTo(string dir)
    {
        var references = Path.Combine(dir, "references");
        Directory.CreateDirectory(references);
        foreach (var (name, text) in References)
            File.WriteAllText(Path.Combine(references, name), text);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), SkillMarkdown);
    }

    public static string Install()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int written = 0;
        foreach (var agent in new[] { ".claude", ".codex" })
        {
            try
            {
                InstallTo(Path.Combine(home, agent, "skills", "agwinterm"));
                written++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return $"installed agent skill to {written} location(s) (~/.claude/skills/agwinterm, ~/.codex/skills/agwinterm)";
    }
}
