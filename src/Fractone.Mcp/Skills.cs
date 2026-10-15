using System.Reflection;
using ModelContextProtocol.Server;

namespace Fractone.Mcp
{
	// The plugin's skills, embedded from plugins/fractone/skills at build time, so installs without the plugin (NuGet or
	// the MCP Registry in Claude Desktop chat, Cursor, VS Code) still get them: as prompts the user can pick and as
	// resources an agent can read.
	internal sealed record Skill(string Name, string Description, string Text)
	{
		public const string UriPrefix = "fractone://skills/";

		public string Uri => UriPrefix + Name;

		private const string ResourcePrefix = "Fractone.Mcp.skills/";

		public static IReadOnlyList<Skill> All { get; } = Load();

		private static Skill[] Load()
		{
			Assembly assembly = typeof(Skill).Assembly;
			return [.. assembly.GetManifestResourceNames()
				.Where(resource => resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
				.Select(resource =>
				{
					using StreamReader reader = new(assembly.GetManifestResourceStream(resource)!);
					return Parse(reader.ReadToEnd());
				})
				.OrderBy(skill => skill.Name, StringComparer.Ordinal)];
		}

		// SKILL.md opens with YAML frontmatter; the skills keep name and description on one line each.
		internal static Skill Parse(string text)
		{
			string[] lines = text.ReplaceLineEndings("\n").Split('\n');
			if (lines[0].Trim() != "---") throw new InvalidDataException("A SKILL.md must open with frontmatter.");
			int end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
			if (end < 0) throw new InvalidDataException("A SKILL.md's frontmatter is not closed.");
			string Field(string key) => lines[1..end].Select(line => line.Trim())
				.FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..].Trim()
				?? throw new InvalidDataException($"A SKILL.md has no {key}.");
			return new Skill(Field("name"), Field("description"), text);
		}

		public static IEnumerable<McpServerPrompt> Prompts() => All.Select(skill =>
			McpServerPrompt.Create(() => skill.Text, new McpServerPromptCreateOptions { Name = skill.Name, Description = skill.Description }));

		public static IEnumerable<McpServerResource> Resources() => All.Select(skill =>
			McpServerResource.Create(() => skill.Text, new McpServerResourceCreateOptions
			{
				UriTemplate = skill.Uri,
				Name = skill.Name,
				Description = skill.Description,
				MimeType = "text/markdown"
			}));
	}
}
