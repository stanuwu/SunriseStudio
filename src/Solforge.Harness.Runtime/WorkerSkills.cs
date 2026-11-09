using System.Text;
using Solforge.Harness;
using Solforge.Harness.Shared;

namespace Solforge.Harness.Runtime;

/** Read-only skill discovery for embedded workers; no write or shell permission is added. */
public static class WorkerSkills
{
    public static IReadOnlyList<string> Roots(string project, string helper, HarnessSettings settings)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(Path.GetDirectoryName(helper)!, "ai-runtime", "node_modules", "@deepseek-ai", "dsh-experimental-solforge-vault-memory", "skills"),
            Path.Combine(project, ".solforge", "skills"), Path.Combine(project, ".agents", "skills"),
            Path.Combine(home, ".claude", "skills"), Path.Combine(home, ".agents", "skills"), Path.Combine(home, ".codex", "skills")
        };
        roots.AddRange(SharedHarnessResources.Inspect(settings, project).SkillDirectories);
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Index(IEnumerable<string> roots)
    {
        var text = new StringBuilder("Before doing a task by hand, check for a matching skill or available MCP tool. Read the matching SKILL.md first.\nSkills (read-only paths):\n");
        int count = 0;
        foreach (string root in roots)
        {
            IEnumerable<string> directories;
            try { directories = Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string directory in directories)
            {
                if (count >= 200 || text.Length >= 32000) return text.ToString();
                string path = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(path)) continue;
                string description = "";
                try
                {
                    using var reader = new StreamReader(path);
                    for (int line = 0; line < 20 && reader.ReadLine() is { } value; line++)
                        if (value.StartsWith("description:", StringComparison.Ordinal)) { description = value[12..].Trim(); break; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if (description.Length > 140) description = description[..140];
                text.Append("- ").Append(Path.GetFileName(directory)).Append(": ").Append(description).Append(" (").Append(path).AppendLine(")");
                count++;
            }
        }
        return text.ToString();
    }
}
